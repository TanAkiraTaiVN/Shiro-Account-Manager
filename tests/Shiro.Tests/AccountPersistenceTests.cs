using System;
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
                    try { Directory.Delete(tempDir, true); }
                    catch (IOException) { }
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
                    try { Directory.Delete(tempDir, true); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public void LoadAccountsFromStore_PreservesBanStatusIssueAndDoesNotConvertBannedToExpired()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-BanPersistTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    byte[] vaultKey = StoragePaths.GetOrCreateVaultKey();
                    var savedAccounts = new List<Account>
                    {
                        new Account
                        {
                            Username = "BannedAcc1",
                            UserID = 1001,
                            SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.|_TOK1",
                            Valid = false,
                            AccountStatusIssue = "Ban 7 ngày"
                        },
                        new Account
                        {
                            Username = "BannedAcc2",
                            UserID = 1002,
                            SecurityToken = "",
                            Valid = false,
                            AccountStatusIssue = "Bị khóa / Ban"
                        }
                    };

                    byte[] serialized = AccountDataSerializer.Serialize(savedAccounts, includePasswords: true);
                    byte[] encrypted = Cryptography.Encrypt(serialized.AsSpan(), vaultKey);
                    File.WriteAllBytes(StoragePaths.AccountDataFile, encrypted);

                    AccountManager.EnsureBackendInitialized();
                    bool unlocked = AccountManager.LoadAccountsFromStore();
                    Assert.True(unlocked);

                    var loaded = AccountManager.Accounts;
                    Assert.Equal(2, loaded.Count);

                    Assert.False(loaded[0].Valid);
                    Assert.True(loaded[0].IsBannedOrLocked);
                    Assert.Equal("Ban 7 ngày", loaded[0].AccountStatusIssue);

                    Assert.False(loaded[1].Valid);
                    Assert.True(loaded[1].IsBannedOrLocked);
                    Assert.Equal("Bị khóa / Ban", loaded[1].AccountStatusIssue);
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
        public void PromoteBackup_PreservesExistingOld()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-PromoteBackupTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    string path = StoragePaths.AccountDataFile;
                    string oldPath = path + ".old";
                    string backupPath = path + ".backup";

                    File.WriteAllText(path, "A");
                    File.WriteAllText(oldPath, "OLD");
                    File.WriteAllText(backupPath, "B");

                    var now = new DateTime(2026, 11, 1, 22, 0, 0);
                    string preservedPath = AccountManager.PromoteBackupToPrimary(path, now);

                    Assert.Equal("B", File.ReadAllText(path));
                    Assert.Equal("OLD", File.ReadAllText(oldPath));
                    Assert.Equal("A", File.ReadAllText(path + ".old.20261101220000"));
                    Assert.Equal(path + ".old.20261101220000", preservedPath);
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
        public void PromoteBackup_TimestampCollision_AppendsCounter()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-CollisionTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    string path = StoragePaths.AccountDataFile;
                    string backupPath = path + ".backup";
                    var now = new DateTime(2026, 11, 1, 22, 0, 0);
                    string collisionPath = path + ".old.20261101220000";

                    File.WriteAllText(path, "CURRENT_DATA");
                    File.WriteAllText(backupPath, "BACKUP_DATA");
                    File.WriteAllText(collisionPath, "EXISTING_COLLISION");

                    string preservedPath = AccountManager.PromoteBackupToPrimary(path, now);

                    Assert.Equal(path + ".old.20261101220000.1", preservedPath);
                    Assert.Equal("BACKUP_DATA", File.ReadAllText(path));
                    Assert.Equal("EXISTING_COLLISION", File.ReadAllText(collisionPath));
                    Assert.Equal("CURRENT_DATA", File.ReadAllText(path + ".old.20261101220000.1"));
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
        public void PromoteBackup_BackupMoveFails_RollsBackPrimaryFile()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-RollbackTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    string path = StoragePaths.AccountDataFile;
                    string backupPath = path + ".backup";
                    var now = new DateTime(2026, 11, 1, 22, 0, 0);

                    File.WriteAllText(path, "PRIMARY_DATA");
                    File.WriteAllText(backupPath, "BACKUP_DATA");

                    // Lock the backup file exclusively so File.Move fails
                    using (var fs = new FileStream(backupPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        Assert.Throws<IOException>(() => AccountManager.PromoteBackupToPrimary(path, now));
                    }

                    // Verify primary file was rolled back and is not missing
                    Assert.True(File.Exists(path));
                    Assert.Equal("PRIMARY_DATA", File.ReadAllText(path));
                    Assert.False(File.Exists(path + ".old.20261101220000"));
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
        public void TryLoadFromBackup_EmptyOrNullData_ReturnsNotAttempted()
        {
            var mgr = new AccountManager();
            Assert.Equal(BackupLoadResult.NotAttempted, mgr.TryLoadFromBackup(Array.Empty<byte>()));
            Assert.Equal(BackupLoadResult.NotAttempted, mgr.TryLoadFromBackup(null!));
        }
    }
}
