#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Shiro.Services
{
    internal static class VaultKeyRecoveryService
    {
        private static void TraceLog(string message)
        {
            try
            {
                AppServices.Logger?.Info($"[VaultKeyRecoveryService] {message}");
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[VaultKeyRecoveryService] TraceLog: {ex.Message}"); }
        }

        public static byte[]? TryRecoverInProcess(byte[] encryptedAccountData)
        {
            if (encryptedAccountData == null || !Cryptography.IsEncrypted(encryptedAccountData))
                return null;

            foreach (string candidateFile in GetCandidateKeyFiles())
            {
                byte[]? raw = null;
                byte[]? unwrapped = null;
                bool returned = false;
                try
                {
                    if (!File.Exists(candidateFile))
                        continue;

                    raw = File.ReadAllBytes(candidateFile);
                    unwrapped = TryExtractKeyFromFileBytes(raw);
                    if (unwrapped != null && CanDecrypt(encryptedAccountData, unwrapped))
                    {
                        PersistRecoveredVaultKey(unwrapped);
                        TraceLog($"Recovered valid vault key from '{candidateFile}'.");
                        returned = true;
                        return unwrapped;
                    }
                }
                catch (Exception ex)
                {
                    TraceLog($"Candidate key file '{candidateFile}' check failed: {ex.Message}");
                }
                finally
                {
                    if (raw != null)
                        CryptographicOperations.ZeroMemory(raw);
                    if (!returned && unwrapped != null)
                        CryptographicOperations.ZeroMemory(unwrapped);
                }
            }

            return null;
        }

        private static IEnumerable<string> GetCandidateKeyFiles()
        {
            string keyFile = StoragePaths.VaultKeyFile;
            yield return keyFile + ".bak";
            yield return keyFile + ".previous";
            yield return keyFile + ".backup";
            yield return Path.Combine(StoragePaths.BaseDirectory, "vault.key");
            yield return Path.Combine(StoragePaths.BaseDirectory, "vault.key.bak");
        }

        internal static byte[]? TryExtractKeyFromFileBytes(byte[] raw)
        {
            if (raw == null || raw.Length == 0)
                return null;

            byte[] header = StoragePaths.DpapiKeyHeader;
            if (raw.Length > header.Length &&
                raw.AsSpan(0, header.Length).SequenceEqual(header))
            {
                byte[]? blob = null;
                byte[]? key = null;
                bool returned = false;
                try
                {
                    blob = raw.AsSpan(header.Length).ToArray();
                    key = ProtectedData.Unprotect(blob, StoragePaths.DpapiKeyEntropy, DataProtectionScope.CurrentUser);
                    if (key != null && key.Length == 32)
                    {
                        returned = true;
                        return key;
                    }
                }
                catch (Exception ex)
                {
                    TraceLog($"DPAPI unprotect candidate key failed: {ex.Message}");
                }
                finally
                {
                    if (blob != null)
                        CryptographicOperations.ZeroMemory(blob);
                    if (!returned && key != null)
                        CryptographicOperations.ZeroMemory(key);
                }
            }
            else if (raw.Length == 32)
            {
                return raw.AsSpan().ToArray();
            }

            return null;
        }

        internal static bool CanDecrypt(byte[] encryptedAccountData, byte[] candidateKey)
        {
            if (candidateKey == null || candidateKey.Length != 32)
                return false;

            try
            {
                byte[] plain = Cryptography.Decrypt(encryptedAccountData, candidateKey);
                try
                {
                    return plain != null && plain.Length > 2;
                }
                finally
                {
                    if (plain != null)
                        CryptographicOperations.ZeroMemory(plain);
                }
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[VaultKeyRecoveryService] CanDecrypt failed", ex);
                return false;
            }
        }

        private static void PersistRecoveredVaultKey(byte[] key)
        {
            StoragePaths.SaveRecoveredVaultKey(key);
        }
    }
}
