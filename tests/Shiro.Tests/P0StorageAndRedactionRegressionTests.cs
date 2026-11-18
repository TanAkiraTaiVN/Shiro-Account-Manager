#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public partial class P0StorageAndRedactionRegressionTests
    {
        private static void CleanupTempDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { }
            }
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        [Fact]
        public async Task ResetEncryptionAsync_RemoveMasterPassword_WithoutPreExistingVaultKey_ProvisionsKeyAndUnlocksOnReload()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-P0-VaultUnlock-" + Guid.NewGuid().ToString("N"));
            var dialogSub = Substitute.For<IDialogService>();
            var prevLastValid = AppServices.SelectionState.LastValidAccount;

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                using var dialogScope = AppServices.Override<IDialogService>(dialogSub);
                StoragePaths.ReloadVaultKeyCache();

                byte[] masterHash = SHA512.HashData(Encoding.UTF8.GetBytes("MasterSecret123!"));
                var original = new Account { Username = "VaultUnlockUser", UserID = 5001, Password = "pw1", Valid = true };
                byte[] serialized = AccountDataSerializer.Serialize(new[] { original }, includePasswords: true);
                File.WriteAllBytes(StoragePaths.AccountDataFile, Cryptography.Encrypt(serialized, masterHash));
                Assert.False(File.Exists(StoragePaths.VaultKeyFile));

                var svc = new AccountPersistenceService(new AccountRepository());
                svc.LoadAccounts(masterHash, out bool unlocked);
                Assert.True(unlocked);
                Assert.False(File.Exists(StoragePaths.VaultKeyFile));

                svc.TestPasswordPromptAsync = (_, _) => Task.FromResult<string?>(string.Empty);
                await svc.ResetEncryptionAsync(manualReset: true);

                Assert.True(File.Exists(StoragePaths.VaultKeyFile));
                Assert.True(File.Exists(StoragePaths.VaultKeyFile + ".bak"));
                Assert.False(svc.IsDatabasePasswordEncrypted());
                dialogSub.Received(1).ShowInfo(LocalizationService.Get("AccountManager.SwitchedToDefaultVault"), Arg.Any<string>());

                StoragePaths.ReloadVaultKeyCache();
                var reloadedRepo = new AccountRepository();
                var reloadedSvc = new AccountPersistenceService(reloadedRepo);
                reloadedSvc.LoadAccounts(null, out bool reloadedUnlocked);

                Assert.True(reloadedUnlocked);
                Assert.False(reloadedSvc.IsVaultLockedPendingMasterPassword);
                Assert.Single(reloadedRepo.GetAll());
                Assert.Equal("VaultUnlockUser", reloadedRepo.GetAll()[0].Username);
                Assert.Equal("pw1", reloadedRepo.GetAll()[0].Password);
            }
            finally
            {
                AppServices.SelectionState.LastValidAccount = prevLastValid;
                StoragePaths.ReloadVaultKeyCache();
                CleanupTempDir(tempDir);
            }
        }

        [Fact]
        public async Task ResetEncryptionAsync_WhitespaceOnlyPassword_FailsValidationWithoutClearingMasterPassword()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-P0-Whitespace-" + Guid.NewGuid().ToString("N"));
            var dialogSub = Substitute.For<IDialogService>();
            var prevLastValid = AppServices.SelectionState.LastValidAccount;

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                using var dialogScope = AppServices.Override<IDialogService>(dialogSub);
                StoragePaths.ReloadVaultKeyCache();

                byte[] masterHash = SHA512.HashData(Encoding.UTF8.GetBytes("KeepMasterPass99!"));
                var original = new Account { Username = "WhitespaceUser", UserID = 5002, Valid = true };
                byte[] serialized = AccountDataSerializer.Serialize(new[] { original }, includePasswords: true);
                File.WriteAllBytes(StoragePaths.AccountDataFile, Cryptography.Encrypt(serialized, masterHash));

                var repo = new AccountRepository();
                var svc = new AccountPersistenceService(repo);
                svc.LoadAccounts(masterHash, out bool unlocked);
                Assert.True(unlocked);

                svc.TestPasswordPromptAsync = (_, _) => Task.FromResult<string?>("        ");
                await svc.ResetEncryptionAsync(manualReset: true);

                dialogSub.Received(1).ShowWarning(LocalizationService.Get("AccountManager.MasterPasswordMinLength"), Arg.Any<string>());
                dialogSub.DidNotReceive().ShowInfo(Arg.Any<string>(), Arg.Any<string>());
                Assert.False(File.Exists(StoragePaths.VaultKeyFile));
                Assert.True(svc.IsDatabasePasswordEncrypted());

                repo.GetAll()[0].IsDirty = true;
                svc.SaveAccounts(bypassRateLimit: true, bypassCountCheck: true);

                var verifySvc = new AccountPersistenceService(new AccountRepository());
                verifySvc.LoadAccounts(null, out bool unlockedWithoutPass);
                Assert.False(unlockedWithoutPass);
                verifySvc.LoadAccounts(masterHash, out bool unlockedWithPass);
                Assert.True(unlockedWithPass);
            }
            finally
            {
                AppServices.SelectionState.LastValidAccount = prevLastValid;
                StoragePaths.ReloadVaultKeyCache();
                CleanupTempDir(tempDir);
            }
        }

        [Fact]
        public void LoadOrCreateVaultKeyAtPath_WhenWriteFails_FailsClosedAndThrows()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-P0-FailClosed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string blockerFile = Path.Combine(tempDir, "not-a-directory");
                File.WriteAllText(blockerFile, "blocker");
                Assert.Throws<IOException>(() => StoragePaths.LoadOrCreateVaultKeyAtPath(Path.Combine(blockerFile, "vault.key")));

                string readOnlyKeyFile = Path.Combine(tempDir, "readonly.key");
                File.WriteAllBytes(readOnlyKeyFile, new byte[] { 1, 2, 3 });
                File.SetAttributes(readOnlyKeyFile, FileAttributes.ReadOnly);
                Exception? ex = Record.Exception(() => StoragePaths.LoadOrCreateVaultKeyAtPath(readOnlyKeyFile));
                Assert.NotNull(ex);
                Assert.True(ex is IOException or UnauthorizedAccessException);
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }

        [Fact]
        public void SaveRecoveredVaultKey_ZeroesPreviousCachedVaultKeyBuffer()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-P0-ZeroPrev-" + Guid.NewGuid().ToString("N"));

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                StoragePaths.ReloadVaultKeyCache();

                byte[] previousRef = StoragePaths.GetOrCreateVaultKey();
                Assert.Equal(32, previousRef.Length);
                Assert.Contains(previousRef, b => b != 0);

                byte[] recoveredKey = RandomNumberGenerator.GetBytes(32);
                recoveredKey[0] ^= 0xFF;

                StoragePaths.SaveRecoveredVaultKey(recoveredKey);

                Assert.All(previousRef, b => Assert.Equal(0, b));
                byte[] currentCached = StoragePaths.GetOrCreateVaultKey();
                Assert.NotSame(previousRef, currentCached);
                Assert.Equal(recoveredKey, currentCached);
            }
            finally
            {
                StoragePaths.ReloadVaultKeyCache();
                CleanupTempDir(tempDir);
            }
        }

        [Fact]
        public void EnsureVaultKeyForUnlockedVaultCopy_CustomDirectory_AndCachedPath_WorkAndFailClosed()
        {
            string activeDir = Path.Combine(Path.GetTempPath(), "Shiro-P0-Active-" + Guid.NewGuid().ToString("N"));
            string customDir = Path.Combine(Path.GetTempPath(), "Shiro-P0-Custom-" + Guid.NewGuid().ToString("N"));

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(activeDir);
                StoragePaths.ReloadVaultKeyCache();

                byte[] activeKey = StoragePaths.EnsureVaultKeyForUnlockedVaultCopy();
                byte[] cachedCopy = StoragePaths.EnsureVaultKeyForUnlockedVaultCopy();
                Assert.Equal(32, activeKey.Length);
                Assert.Equal(activeKey, cachedCopy);
                Assert.NotSame(activeKey, cachedCopy);

                Directory.CreateDirectory(customDir);
                byte[] customKey = StoragePaths.EnsureVaultKeyForUnlockedVaultCopy(customDir);
                Assert.Equal(32, customKey.Length);
                Assert.True(File.Exists(Path.Combine(customDir, "vault.key")));
                Assert.Equal(activeKey, StoragePaths.GetOrCreateVaultKey());

                string blocker = Path.Combine(customDir, "blocker-file");
                File.WriteAllText(blocker, "x");
                Assert.Throws<IOException>(() => StoragePaths.EnsureVaultKeyForUnlockedVaultCopy(blocker));
            }
            finally
            {
                StoragePaths.ReloadVaultKeyCache();
                CleanupTempDir(activeDir);
                CleanupTempDir(customDir);
            }
        }

        [Fact]
        public async Task ResetEncryptionAsync_WhenLockedOrSaveFails_PreservesMasterPasswordAndState()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-P0-LockGuard-" + Guid.NewGuid().ToString("N"));
            var dialogSub = Substitute.For<IDialogService>();
            var prevLastValid = AppServices.SelectionState.LastValidAccount;

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                using var dialogScope = AppServices.Override<IDialogService>(dialogSub);
                StoragePaths.ReloadVaultKeyCache();

                byte[] masterHash = SHA512.HashData(Encoding.UTF8.GetBytes("LockedMasterPass1!"));
                var acc = new Account { Username = "LockedGuardUser", UserID = 5003, Valid = true };
                File.WriteAllBytes(StoragePaths.AccountDataFile,
                    Cryptography.Encrypt(AccountDataSerializer.Serialize(new[] { acc }, true), masterHash));

                var svc = new AccountPersistenceService(new AccountRepository());
                svc.LoadAccounts(null, out bool unlockedBefore);
                Assert.False(unlockedBefore);
                Assert.True(svc.IsVaultLockedPendingMasterPassword);

                await svc.ResetEncryptionAsync(manualReset: true);
                dialogSub.Received(1).ShowWarning(Arg.Is<string>(s => s.Contains("locked", StringComparison.OrdinalIgnoreCase)), Arg.Any<string>());

                svc.LoadAccounts(masterHash, out bool unlockedNow);
                Assert.True(unlockedNow);
                svc.TestPasswordPromptAsync = (_, _) => Task.FromResult<string?>(string.Empty);

                using (var lockFile = new FileStream(StoragePaths.AccountDataFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Exception? saveEx = await Record.ExceptionAsync(() => svc.ResetEncryptionAsync(manualReset: true));
                    Assert.NotNull(saveEx);
                    Assert.True(saveEx is IOException or UnauthorizedAccessException);
                }

                // Master password hash was rolled back in memory so disk and memory remain consistent.
                Assert.True(svc.IsDatabasePasswordEncrypted());
                svc.SaveAccounts(bypassRateLimit: true, bypassCountCheck: true);
                var verifySvc = new AccountPersistenceService(new AccountRepository());
                verifySvc.LoadAccounts(null, out bool unlockedWithoutPass);
                Assert.False(unlockedWithoutPass);
                verifySvc.LoadAccounts(masterHash, out bool stillMasterUnlocked);
                Assert.True(stillMasterUnlocked);
            }
            finally
            {
                AppServices.SelectionState.LastValidAccount = prevLastValid;
                StoragePaths.ReloadVaultKeyCache();
                CleanupTempDir(tempDir);
            }
        }

        private static byte[] BuildLegacyArrayJsonBytes(string username, long userId)
            => Encoding.UTF8.GetBytes($"[{{\"Username\":\"{username}\",\"UserID\":{userId}}}]");

        [Fact]
        public void SaveAccounts_WhenDiskWriteFails_PreservesIsDirtyUntilWriteSucceeds()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-P0-DirtyOrder-" + Guid.NewGuid().ToString("N"));
            var dialogSub = Substitute.For<IDialogService>();
            var settingsSub = Substitute.For<ISettingsService>();
            bool? observedDirtyBeforeWrite = null;

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                using var dialogScope = AppServices.Override<IDialogService>(dialogSub);
                StoragePaths.ReloadVaultKeyCache();

                var repo = new AccountRepository();
                var acc = new Account { Username = "DirtyPersistUser", UserID = 6001, Alias = "Initial", IsDirty = true };
                repo.ReplaceAll(new[] { acc });

                var svc = new AccountPersistenceService(repo)
                {
                    StartTime = DateTime.Now.AddMinutes(-5),
                    LastSaveTime = DateTime.Now.AddMinutes(-5)
                };
                svc.SaveAccounts(bypassRateLimit: true, bypassCountCheck: true);
                Assert.False(acc.IsDirty);

                acc.Alias = "PendingWrite";
                Assert.True(acc.IsDirty);
                svc.LastSaveTime = DateTime.Now.AddMinutes(-5);

                settingsSub.Exists("SavePasswords").Returns(_ =>
                {
                    observedDirtyBeforeWrite = acc.IsDirty;
                    return false;
                });

                using (AppServices.Override<ISettingsService>(settingsSub))
                {
                    using (var lockStream = new FileStream(StoragePaths.AccountDataFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        Exception? writeEx = Record.Exception(() => svc.SaveAccounts(bypassRateLimit: true, bypassCountCheck: true));
                        Assert.NotNull(writeEx);
                        Assert.True(writeEx is IOException or UnauthorizedAccessException);
                    }

                    Assert.True(observedDirtyBeforeWrite);
                    Assert.True(acc.IsDirty);

                    svc.SaveAccounts(bypassRateLimit: false, bypassCountCheck: false);
                    Assert.False(acc.IsDirty);
                }

                var reloadRepo = new AccountRepository();
                var reloadSvc = new AccountPersistenceService(reloadRepo);
                reloadSvc.LoadAccounts(null, out bool reloaded);
                Assert.True(reloaded);
                Assert.Equal("PendingWrite", Assert.Single(reloadRepo.GetAll()).Alias);
            }
            finally
            {
                StoragePaths.ReloadVaultKeyCache();
                CleanupTempDir(tempDir);
            }
        }

        [Fact]
        public void LoadAccounts_WhenPrimaryCorrupt_PreservesExistingBakAndWritesQuarantineFile()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-P0-CorruptBak-" + Guid.NewGuid().ToString("N"));
            var dialogSub = Substitute.For<IDialogService>();
            var prevLastValid = AppServices.SelectionState.LastValidAccount;

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                using var dialogScope = AppServices.Override<IDialogService>(dialogSub);
                StoragePaths.ReloadVaultKeyCache();

                byte[] sentinelBak = Encoding.UTF8.GetBytes("EXISTING_VALID_BAK_SENTINEL");
                byte[] corruptPrimary = Encoding.UTF8.GetBytes("CORRUPT_PRIMARY_PAYLOAD");
                string bakPath = StoragePaths.AccountDataFile + ".bak";
                File.WriteAllBytes(bakPath, sentinelBak);
                File.WriteAllBytes(StoragePaths.AccountDataFile, corruptPrimary);

                var svc = new AccountPersistenceService(new AccountRepository());
                svc.LoadAccounts();

                Assert.Equal(sentinelBak, File.ReadAllBytes(bakPath));
                string pattern = Path.GetFileName(StoragePaths.AccountDataFile) + ".corrupt-*.bak";
                string[] quarantineFiles = Directory.GetFiles(tempDir, pattern);
                Assert.Single(quarantineFiles);
                Assert.Equal(corruptPrimary, File.ReadAllBytes(quarantineFiles[0]));
            }
            finally
            {
                AppServices.SelectionState.LastValidAccount = prevLastValid;
                StoragePaths.ReloadVaultKeyCache();
                CleanupTempDir(tempDir);
            }
        }

        [Fact]
        public void AccountFileDecoder_Decode_WithNonNullMasterPassword_DistinguishesTruncatedCorruptFromWrongPassword()
        {
            byte[] masterHash = SHA512.HashData(Encoding.UTF8.GetBytes("CorrectMaster99!"));
            byte[] wrongHash = SHA512.HashData(Encoding.UTF8.GetBytes("WrongMaster99!"));
            var keys = new AccountDecodeKeys(
                GetVaultKeyCopy: () => Array.Empty<byte>(),
                TryRecoverVaultKey: null,
                UnprotectDpapi: _ => throw new CryptographicException("disabled"),
                PlaintextOptIn: false);

            byte[] truncated = Cryptography.ShiroVaultHeaderV3.Concat(new byte[] { 1, 2, 3, 4 }).ToArray();
            var corruptResult = AccountFileDecoder.Decode(truncated, masterHash, keys);
            Assert.Equal(AccountDecodeStatus.Corrupt, corruptResult.Status);
            Assert.IsType<InvalidDataException>(corruptResult.Error);

            byte[] validPayload = Cryptography.Encrypt(
                AccountDataSerializer.Serialize(new[] { new Account { Username = "EncUser", UserID = 6002 } }, true),
                masterHash);
            var wrongPassResult = AccountFileDecoder.Decode(validPayload, wrongHash, keys);
            Assert.Equal(AccountDecodeStatus.NeedsMasterPassword, wrongPassResult.Status);
        }

        [Fact]
        public void TryRestoreReadableBackup_PrefersNewestValidBackupOverOlderLargerBackup()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-P0-BackupRank-" + Guid.NewGuid().ToString("N"));
            var dialogSub = Substitute.For<IDialogService>();
            var prevLastValid = AppServices.SelectionState.LastValidAccount;

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                using var dialogScope = AppServices.Override<IDialogService>(dialogSub);
                StoragePaths.ReloadVaultKeyCache();

                byte[] vaultKey = StoragePaths.GetOrCreateVaultKey();
                var olderAccounts = Enumerable.Range(1, 5)
                    .Select(i => new Account { Username = "OldAcc" + i, UserID = 7000 + i })
                    .ToList();
                var newerAccounts = new List<Account>
                {
                    new Account { Username = "NewAcc1", UserID = 8001 },
                    new Account { Username = "NewAcc2", UserID = 8002 }
                };

                string olderPath = Path.Combine(StoragePaths.BackupsDirectory, "AccountData_20261115_120000.json.bak");
                string newerPath = StoragePaths.AccountDataFile + ".backup";
                File.WriteAllBytes(olderPath, Cryptography.EncryptWithRandomKey(AccountDataSerializer.Serialize(olderAccounts, true), vaultKey));
                File.WriteAllBytes(newerPath, Cryptography.EncryptWithRandomKey(AccountDataSerializer.Serialize(newerAccounts, true), vaultKey));
                File.SetLastWriteTimeUtc(olderPath, DateTime.UtcNow.AddDays(-1));
                File.SetLastWriteTimeUtc(newerPath, DateTime.UtcNow.AddMinutes(-1));
                File.WriteAllBytes(StoragePaths.AccountDataFile, Encoding.UTF8.GetBytes("CORRUPT_PRIMARY"));

                var repo = new AccountRepository();
                var svc = new AccountPersistenceService(repo) { StartTime = DateTime.Now.AddMinutes(-5) };

                bool restored = svc.TryRestoreReadableBackup();

                Assert.True(restored);
                Assert.Equal(2, repo.GetAll().Count);
                Assert.Equal(new[] { "NewAcc1", "NewAcc2" }, repo.GetAll().Select(a => a.Username).ToArray());
            }
            finally
            {
                AppServices.SelectionState.LastValidAccount = prevLastValid;
                StoragePaths.ReloadVaultKeyCache();
                CleanupTempDir(tempDir);
            }
        }

        [Fact]
        public async Task LoadAccounts_LocksOutConcurrentSaveWhileLocked_AndReentersSaveForMigration()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-P0-LoadLock-" + Guid.NewGuid().ToString("N"));
            var dialogSub = Substitute.For<IDialogService>();
            var prevLastValid = AppServices.SelectionState.LastValidAccount;

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                using var dialogScope = AppServices.Override<IDialogService>(dialogSub);
                StoragePaths.ReloadVaultKeyCache();

                byte[] vaultKey = StoragePaths.GetOrCreateVaultKey();
                byte[] legacyPayload = BuildLegacyArrayJsonBytes("LegacyArrUser", 9001);
                File.WriteAllBytes(StoragePaths.AccountDataFile, Cryptography.EncryptWithRandomKey(legacyPayload, vaultKey));

                var migrationRepo = new AccountRepository();
                var migrationSvc = new AccountPersistenceService(migrationRepo) { StartTime = DateTime.Now.AddMinutes(-5) };
                migrationSvc.LoadAccounts(null, out bool migratedUnlocked);
                Assert.True(migratedUnlocked);
                Assert.Equal("LegacyArrUser", Assert.Single(migrationRepo.GetAll()).Username);

                byte[] masterHash = SHA512.HashData(Encoding.UTF8.GetBytes("LockedConcurrentPass1!"));
                byte[] wrongHash = SHA512.HashData(Encoding.UTF8.GetBytes("WrongConcurrentPass2!"));
                var lockedAcc = new Account { Username = "LockedVaultUser", UserID = 9002, Valid = true };
                File.WriteAllBytes(StoragePaths.AccountDataFile,
                    Cryptography.Encrypt(AccountDataSerializer.Serialize(new[] { lockedAcc }, true), masterHash));

                var lockRepo = new AccountRepository();
                var lockSvc = new AccountPersistenceService(lockRepo) { StartTime = DateTime.Now.AddMinutes(-5) };
                lockSvc.LoadAccounts(null, out bool initialUnlocked);
                Assert.False(initialUnlocked);
                Assert.True(lockSvc.IsVaultLockedPendingMasterPassword);

                Task loadTask = Task.Run(() => lockSvc.LoadAccounts(wrongHash, out _));
                while (!loadTask.IsCompleted)
                {
                    lockSvc.SaveAccounts(bypassRateLimit: true, bypassCountCheck: true);
                    await Task.Yield();
                }
                await loadTask;

                Assert.True(lockSvc.IsVaultLockedPendingMasterPassword);
                var verifyRepo = new AccountRepository();
                var verifySvc = new AccountPersistenceService(verifyRepo);
                verifySvc.LoadAccounts(masterHash, out bool finalUnlocked);
                Assert.True(finalUnlocked);
                Assert.Equal("LockedVaultUser", Assert.Single(verifyRepo.GetAll()).Username);
            }
            finally
            {
                AppServices.SelectionState.LastValidAccount = prevLastValid;
                StoragePaths.ReloadVaultKeyCache();
                CleanupTempDir(tempDir);
            }
        }

        [Fact]
        public async Task ResetEncryptionAsync_WhenTestPasswordPromptNull_InvokesShowSecretPromptAsync()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-P0-SecretReset-" + Guid.NewGuid().ToString("N"));
            var dialogSub = Substitute.For<IDialogService>();
            dialogSub.ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult("/UC"));
            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                using var dialogScope = AppServices.Override<IDialogService>(dialogSub);
                StoragePaths.ReloadVaultKeyCache();

                var svc = new AccountPersistenceService(new AccountRepository())
                {
                    TestPasswordPromptAsync = null
                };
                await svc.ResetEncryptionAsync(manualReset: true);

                Assert.False(svc.IsDatabasePasswordEncrypted());
                await dialogSub.Received(1).ShowSecretPromptAsync(
                    LocalizationService.Get("AccountManager.EnterNewPasswordPrompt"),
                    LocalizationService.Get("AccountManager.ChangeDatabasePassword"),
                    Arg.Any<string>());
                await dialogSub.DidNotReceiveWithAnyArgs().ShowInputPromptAsync(default!, default!, default!);
            }
            finally
            {
                StoragePaths.ReloadVaultKeyCache();
                CleanupTempDir(tempDir);
            }
        }
    }
}
