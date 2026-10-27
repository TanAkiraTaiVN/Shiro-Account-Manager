using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;
using Xunit;

namespace RBX_Alt_Manager.Tests
{
    [Collection("StoragePathsCollection")]
    public class AccountPersistenceTests
    {
        [Fact]
        public void IsDatabasePasswordEncrypted_ReturnsExpectedState()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-PersistTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    // No file exists -> returns false
                    Assert.False(AccountManager.IsDatabasePasswordEncrypted());

                    // Plaintext file exists -> returns false
                    File.WriteAllText(StoragePaths.AccountDataFile, "[]");
                    Assert.False(AccountManager.IsDatabasePasswordEncrypted());

                    // Encrypted with default vault key -> returns false (since default key can decrypt it)
                    byte[] vaultKey = StoragePaths.GetOrCreateVaultKey();
                    byte[] encWithVault = Cryptography.Encrypt("[]", vaultKey);
                    File.WriteAllBytes(StoragePaths.AccountDataFile, encWithVault);
                    Assert.False(AccountManager.IsDatabasePasswordEncrypted());

                    // Encrypted with an unknown custom password -> returns true
                    byte[] customPassword = Encoding.UTF8.GetBytes("CustomUserPassword999!");
                    byte[] encWithCustom = Cryptography.Encrypt("[]", customPassword);
                    File.WriteAllBytes(StoragePaths.AccountDataFile, encWithCustom);
                    Assert.True(AccountManager.IsDatabasePasswordEncrypted());
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void TryRestoreReadableBackup_RestoresFromBackupWhenPrimaryCorrupt()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-RestoreTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    byte[] vaultKey = StoragePaths.GetOrCreateVaultKey();

                    var backupAccounts = new List<Account>
                    {
                        new Account { Username = "BackupRestoredUser", UserID = 777888 }
                    };
                    byte[] serialized = AccountDataSerializer.Serialize(backupAccounts, includePasswords: true);
                    byte[] encrypted = Cryptography.Encrypt(serialized.AsSpan(), vaultKey);

                    // Write primary file as corrupted
                    File.WriteAllText(StoragePaths.AccountDataFile, "CORRUPTED_TRUNCATED_GARBAGE");

                    // Write valid backup file
                    string backupFile = StoragePaths.AccountDataFile + ".backup";
                    File.WriteAllBytes(backupFile, encrypted);

                    var store = new AccountStore(StoragePaths.AccountDataFile, StoragePaths.BackupsDirectory);
                    bool restored = store.RestoreHistoricalBackup(backupFile);
                    Assert.True(restored);

                    byte[] data = store.Read();
                    Assert.NotNull(data);

                    // Check that data read matches restored backup
                    byte[] decrypted = Cryptography.Decrypt(data, vaultKey);
                    var loaded = AccountDataSerializer.Deserialize(decrypted, out _);
                    Assert.Single(loaded);
                    Assert.Equal("BackupRestoredUser", loaded[0].Username);
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }
}
