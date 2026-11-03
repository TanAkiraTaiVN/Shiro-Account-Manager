using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Shiro.Classes;
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
        public void TestCandidatesParallel_FindsValidKeyAmongstCandidates()
        {
            byte[] validKey = new byte[32];
            Random.Shared.NextBytes(validKey);

            byte[] dummy1 = new byte[32];
            Random.Shared.NextBytes(dummy1);
            byte[] dummy2 = new byte[32];
            Random.Shared.NextBytes(dummy2);

            byte[] encrypted = Cryptography.Encrypt("{\"accounts\":[]}", validKey);

            var candidates = new List<byte[]> { dummy1, validKey, dummy2 };
            byte[]? found = VaultKeyRecoveryService.TestCandidatesParallel(encrypted, candidates);

            Assert.Equal(validKey, found);

            // No valid candidate
            var noMatches = new List<byte[]> { dummy1, dummy2 };
            Assert.Null(VaultKeyRecoveryService.TestCandidatesParallel(encrypted, noMatches));
        }

        [Fact]
        public void ExecuteRecoveryCli_NoPayload_ReturnsExitCodeTwo()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-CliTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    int code = VaultKeyRecoveryService.ExecuteRecoveryCli();
                    Assert.Equal(2, code);
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

        [Fact]
        public void MergeAllKnownAccountsCli_DeduplicatesAndMergesAccountsCorrectly()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-MergeTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    // Create test accounts
                    var initialAccounts = new List<Account>
                    {
                        new Account { Username = "PlayerA", SecurityToken = "tokenA", UserID = 100 },
                        new Account { Username = "PlayerB", Password = "PassB" }
                    };

                    byte[] key = StoragePaths.GetOrCreateVaultKey();
                    byte[] serialized = AccountDataSerializer.Serialize(initialAccounts, includePasswords: true);
                    byte[] encrypted = Cryptography.Encrypt(serialized.AsSpan(), key);
                    File.WriteAllBytes(StoragePaths.AccountDataFile, encrypted);

                    // Add a legacy DPAPI backup file with extra information for PlayerB and PlayerC
                    var backupAccounts = new List<Account>
                    {
                        new Account { Username = "PlayerB", SecurityToken = "tokenB_recovered", UserID = 200 },
                        new Account { Username = "PlayerC", SecurityToken = "tokenC", UserID = 300 }
                    };
                    byte[] backupSerialized = AccountDataSerializer.Serialize(backupAccounts, includePasswords: true);
                    byte[] legacyDpapiEntropy = new byte[]
                    {
                        0x52, 0x4f, 0x42, 0x4c, 0x4f, 0x58, 0x20, 0x41, 0x43, 0x43, 0x4f, 0x55, 0x4e, 0x54,
                        0x20, 0x4d, 0x41, 0x4e, 0x41, 0x47, 0x45, 0x52, 0x20, 0x7c, 0x20, 0x3a, 0x29, 0x20,
                        0x7c, 0x20, 0x42, 0x52, 0x4f, 0x55, 0x47, 0x48, 0x54, 0x20, 0x54, 0x4f, 0x20, 0x59,
                        0x4f, 0x55, 0x20, 0x42, 0x55, 0x59, 0x20, 0x69, 0x63, 0x33, 0x77, 0x30, 0x6c, 0x66
                    };
                    byte[] backupProtected = ProtectedData.Protect(backupSerialized, legacyDpapiEntropy, DataProtectionScope.CurrentUser);
                    File.WriteAllBytes(StoragePaths.AccountDataFile + ".previous", backupProtected);

                    int exitCode = VaultKeyRecoveryService.MergeAllKnownAccountsCli();
                    Assert.Equal(0, exitCode);

                    // Verify merged results
                    byte[] mergedEncrypted = File.ReadAllBytes(StoragePaths.AccountDataFile);
                    byte[] decrypted = Cryptography.Decrypt(mergedEncrypted, key);
                    var mergedList = AccountDataSerializer.Deserialize(decrypted, out _);

                    Assert.Equal(3, mergedList.Count);
                    var playerB = mergedList.Find(a => a.Username == "PlayerB");
                    Assert.NotNull(playerB);
                    Assert.Equal("tokenB_recovered", playerB.SecurityToken);
                    Assert.Equal("PassB", playerB.Password);
                    Assert.Equal(200, playerB.UserID);
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
