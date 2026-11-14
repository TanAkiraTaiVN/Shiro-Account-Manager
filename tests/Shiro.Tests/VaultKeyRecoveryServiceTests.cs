using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StoragePathsCollection")]
    public class VaultKeyRecoveryServiceTests
    {
        [Fact]
        public void TryExtractKeyFromFileBytes_ValidatesAndUnprotectsCorrectly()
        {
            // Null or empty
            Assert.Null(VaultKeyRecoveryService.TryExtractKeyFromFileBytes(null!));
            Assert.Null(VaultKeyRecoveryService.TryExtractKeyFromFileBytes(Array.Empty<byte>()));

            // Raw 32-byte key
            byte[] raw32 = new byte[32];
            Random.Shared.NextBytes(raw32);
            byte[]? extractedRaw = VaultKeyRecoveryService.TryExtractKeyFromFileBytes(raw32);
            Assert.Equal(raw32, extractedRaw);

            // Invalid length (not 32, not DPAPI)
            byte[] invalidLen = new byte[25];
            Assert.Null(VaultKeyRecoveryService.TryExtractKeyFromFileBytes(invalidLen));

            // DPAPI-wrapped key
            byte[] dpapiHeader = Encoding.ASCII.GetBytes("SHIRO_DPAPI_KEY_V1");
            byte[] dpapiEntropy = Encoding.ASCII.GetBytes("Shiro.VaultKey.v1");
            byte[] protectedBlob = ProtectedData.Protect(raw32, dpapiEntropy, DataProtectionScope.CurrentUser);

            byte[] wrapped = new byte[dpapiHeader.Length + protectedBlob.Length];
            Buffer.BlockCopy(dpapiHeader, 0, wrapped, 0, dpapiHeader.Length);
            Buffer.BlockCopy(protectedBlob, 0, wrapped, dpapiHeader.Length, protectedBlob.Length);

            byte[]? extractedDpapi = VaultKeyRecoveryService.TryExtractKeyFromFileBytes(wrapped);
            Assert.Equal(raw32, extractedDpapi);
        }

        [Fact]
        public void CanDecrypt_ValidatesKeyLengthAndDecryptionSuccess()
        {
            byte[] validKey = new byte[32];
            Random.Shared.NextBytes(validKey);

            byte[] wrongKey = new byte[32];
            Random.Shared.NextBytes(wrongKey);

            byte[] payload = Encoding.UTF8.GetBytes("[{\"Username\":\"TestKeyRecoveryUser\"}]");
            byte[] encrypted = Cryptography.Encrypt(payload.AsSpan(), validKey);

            // Null or invalid length key
            Assert.False(VaultKeyRecoveryService.CanDecrypt(encrypted, null!));
            Assert.False(VaultKeyRecoveryService.CanDecrypt(encrypted, new byte[16]));
            Assert.False(VaultKeyRecoveryService.CanDecrypt(encrypted, new byte[64]));

            // Valid key matches
            Assert.True(VaultKeyRecoveryService.CanDecrypt(encrypted, validKey));

            // Wrong key fails
            Assert.False(VaultKeyRecoveryService.CanDecrypt(encrypted, wrongKey));

            // Corrupt data fails
            byte[] corruptData = new byte[100];
            Assert.False(VaultKeyRecoveryService.CanDecrypt(corruptData, validKey));
        }

        [Fact]
        public void TryRecoverInProcess_RecoversFromCandidateBackupKeyFile()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-RecoverTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    byte[] validKey = new byte[32];
                    Random.Shared.NextBytes(validKey);
                    byte[] payload = Encoding.UTF8.GetBytes("[{\"Username\":\"BackupKeyUser\"}]");
                    byte[] encrypted = Cryptography.Encrypt(payload.AsSpan(), validKey);

                    File.WriteAllBytes(StoragePaths.VaultKeyFile + ".bak", validKey);

                    byte[]? recovered = VaultKeyRecoveryService.TryRecoverInProcess(encrypted);
                    Assert.Equal(validKey, recovered);
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); }
                    catch (IOException) { }
                }
            }
        }
    }
}
