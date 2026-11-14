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
        private static readonly byte[] DpapiKeyHeader = Encoding.ASCII.GetBytes("SHIRO_DPAPI_KEY_V1");
        private static readonly byte[] DpapiKeyEntropy = Encoding.ASCII.GetBytes("Shiro.VaultKey.v1");

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
                try
                {
                    if (!File.Exists(candidateFile))
                        continue;

                    byte[] raw = File.ReadAllBytes(candidateFile);
                    byte[]? unwrapped = TryExtractKeyFromFileBytes(raw);
                    if (unwrapped != null && CanDecrypt(encryptedAccountData, unwrapped))
                    {
                        PersistRecoveredVaultKey(unwrapped);
                        TraceLog($"Recovered valid vault key from '{candidateFile}'.");
                        return unwrapped;
                    }
                }
                catch (Exception ex)
                {
                    TraceLog($"Candidate key file '{candidateFile}' check failed: {ex.Message}");
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

            if (raw.Length > DpapiKeyHeader.Length &&
                raw.AsSpan(0, DpapiKeyHeader.Length).SequenceEqual(DpapiKeyHeader))
            {
                try
                {
                    byte[] blob = raw.AsSpan(DpapiKeyHeader.Length).ToArray();
                    byte[] key = ProtectedData.Unprotect(blob, DpapiKeyEntropy, DataProtectionScope.CurrentUser);
                    if (key != null && key.Length == 32)
                        return key;
                }
                catch (Exception ex)
                {
                    TraceLog($"DPAPI unprotect candidate key failed: {ex.Message}");
                }
            }
            else if (raw.Length == 32)
            {
                return raw;
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
