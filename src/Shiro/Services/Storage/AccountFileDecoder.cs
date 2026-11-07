#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Shiro.Classes;

namespace Shiro.Services
{
    public enum AccountDecodeStatus
    {
        Empty,
        Loaded,
        NeedsMasterPassword,
        Corrupt
    }

    public sealed record AccountDecodeResult(
        AccountDecodeStatus Status,
        List<Account> Accounts,
        bool NeedsMigration,
        bool LegacyPlaintext,
        Exception? Error);

    public sealed record AccountDecodeKeys(
        Func<byte[]> GetVaultKeyCopy,
        Func<byte[], byte[]?>? TryRecoverVaultKey,
        Func<byte[], (byte[] Plain, bool LegacyScope)> UnprotectDpapi,
        bool PlaintextOptIn);

    public static class AccountFileDecoder
    {
        public static AccountDecodeResult Decode(byte[] data, byte[]? masterPassword, AccountDecodeKeys keys)
        {
            ArgumentNullException.ThrowIfNull(keys);

            if (data == null || data.Length == 0)
            {
                return new AccountDecodeResult(AccountDecodeStatus.Empty, new List<Account>(), false, false, null);
            }

            if (Cryptography.IsEncrypted(data))
            {
                bool legacyCryptoHeader = Cryptography.IsLegacyHeader(data);

                if (masterPassword != null)
                {
                    byte[]? decrypted = null;
                    try
                    {
                        decrypted = Cryptography.Decrypt(data, masterPassword);
                        var accounts = AccountDataSerializer.Deserialize(decrypted, out bool needsMigration);
                        return new AccountDecodeResult(AccountDecodeStatus.Loaded, accounts, needsMigration || legacyCryptoHeader, false, null);
                    }
                    catch (Exception ex)
                    {
                        return new AccountDecodeResult(AccountDecodeStatus.NeedsMasterPassword, new List<Account>(), false, false, ex);
                    }
                    finally
                    {
                        if (decrypted != null)
                        {
                            CryptographicOperations.ZeroMemory(decrypted);
                        }
                    }
                }

                byte[]? decryptedVault = null;
                Exception? failureEx = null;

                if (keys.GetVaultKeyCopy != null)
                {
                    byte[]? vaultKey = null;
                    try
                    {
                        vaultKey = keys.GetVaultKeyCopy();
                        if (vaultKey != null && vaultKey.Length > 0)
                        {
                            try
                            {
                                decryptedVault = Cryptography.Decrypt(data, vaultKey);
                            }
                            catch (Exception ex)
                            {
                                failureEx = ex;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failureEx ??= ex;
                    }
                    finally
                    {
                        if (vaultKey != null)
                        {
                            CryptographicOperations.ZeroMemory(vaultKey);
                        }
                    }
                }

                if (decryptedVault == null && keys.TryRecoverVaultKey != null)
                {
                    byte[]? recoveredKey = null;
                    try
                    {
                        recoveredKey = keys.TryRecoverVaultKey(data);
                        if (recoveredKey != null && recoveredKey.Length > 0)
                        {
                            try
                            {
                                decryptedVault = Cryptography.Decrypt(data, recoveredKey);
                            }
                            catch (Exception ex)
                            {
                                failureEx ??= ex;
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(recoveredKey);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failureEx ??= ex;
                    }
                }

                if (decryptedVault != null)
                {
                    try
                    {
                        var accounts = AccountDataSerializer.Deserialize(decryptedVault, out bool needsMigration);
                        return new AccountDecodeResult(AccountDecodeStatus.Loaded, accounts, needsMigration || legacyCryptoHeader, false, null);
                    }
                    catch (Exception ex)
                    {
                        return new AccountDecodeResult(AccountDecodeStatus.Corrupt, new List<Account>(), false, false, ex);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(decryptedVault);
                    }
                }

                return new AccountDecodeResult(AccountDecodeStatus.NeedsMasterPassword, new List<Account>(), false, false, failureEx);
            }

            // Unencrypted / DPAPI / Plaintext branch
            Exception? dpapiEx = null;
            if (keys.UnprotectDpapi != null)
            {
                byte[]? plain = null;
                try
                {
                    bool legacyScope;
                    (plain, legacyScope) = keys.UnprotectDpapi(data);
                    var accounts = AccountDataSerializer.Deserialize(plain, out bool dpapiNeedsMigration);
                    bool needsMigration = dpapiNeedsMigration || legacyScope;
                    return new AccountDecodeResult(AccountDecodeStatus.Loaded, accounts, needsMigration, false, null);
                }
                catch (Exception ex)
                {
                    dpapiEx = ex;
                }
                finally
                {
                    if (plain != null)
                    {
                        CryptographicOperations.ZeroMemory(plain);
                    }
                }
            }

            if (keys.PlaintextOptIn)
            {
                try
                {
                    var accounts = AccountDataSerializer.Deserialize(data, out bool plaintextNeedsMigration);
                    return new AccountDecodeResult(AccountDecodeStatus.Loaded, accounts, true, true, null);
                }
                catch (Exception ex)
                {
                    return new AccountDecodeResult(AccountDecodeStatus.Corrupt, new List<Account>(), false, false, ex);
                }
            }

            return new AccountDecodeResult(
                AccountDecodeStatus.Corrupt,
                new List<Account>(),
                false,
                false,
                dpapiEx ?? new InvalidDataException("The account file could not be decrypted. Plaintext recovery is disabled unless the legacy opt-in marker is present."));
        }
    }
}
