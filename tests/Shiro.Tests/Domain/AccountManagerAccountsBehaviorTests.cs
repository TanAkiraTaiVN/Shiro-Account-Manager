#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Http;
using Shiro.Tests.TestInfrastructure;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public partial class AccountManagerLifecycleAndAccountsBehaviorTests
    {
        [Fact]
        public async Task Persistence_Save_Load_Backup_Promote_And_ResetEncryption_CoverBranches()
        {
            var mgr = AccountPersistenceService.Default;
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroPersistTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string tempAccFile = Path.Combine(tempDir, "AccountData.json");
            string tempBackupsDir = Path.Combine(tempDir, "Backups");

            var prevStore = AccountPersistenceService.Default.Store;
            var prevPrompt = AccountPersistenceService.Default.TestPasswordPromptAsync;
            var dialogSub = Substitute.For<IDialogService>();
            using var _dialogScope = AppServices.Override<IDialogService>(dialogSub);
            AccountPersistenceService.Default.Store = new AccountStore(tempAccFile, tempBackupsDir);

            try
            {
                File.WriteAllText(tempAccFile, "old-primary-1");
                File.WriteAllText(tempAccFile + ".backup", "backup-content-1");
                DateTime fixedNow = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
                string preserved1 = AccountPersistenceService.Default.PromoteBackupToPrimary(tempAccFile, fixedNow);
                Assert.True(File.Exists(preserved1));
                Assert.Equal("backup-content-1", File.ReadAllText(tempAccFile));

                File.WriteAllText(tempAccFile + ".backup", "backup-content-2");
                string preserved2 = AccountPersistenceService.Default.PromoteBackupToPrimary(tempAccFile, fixedNow);
                Assert.EndsWith(".1", preserved2);
                Assert.Equal("backup-content-2", File.ReadAllText(tempAccFile));

                File.Delete(tempAccFile);

                var acc = new Account
                {
                    Username = "PersistUser1",
                    UserID = 123987,
                    Valid = true,
                    IsDirty = true,
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('C', 120),
                    LastUse = DateTime.UtcNow
                };
                AccountManager.ReplaceAccountsList(new List<Account> { acc });
                await AccountPersistenceService.Default.SaveAccountsAsync(bypassRateLimit: true, bypassCountCheck: true);
                Assert.True(File.Exists(tempAccFile));
                Assert.False(AccountPersistenceService.Default.IsDatabasePasswordEncrypted());

                acc.IsDirty = true;
                AccountPersistenceService.Default.SaveAccounts(bypassRateLimit: false, bypassCountCheck: true);

                bool unlocked = AccountPersistenceService.Default.LoadAccountsFromStore();
                Assert.True(unlocked);
                Assert.Single(AppServices.AccountRepository.GetAll());

                byte[] encryptedBytes = File.ReadAllBytes(tempAccFile);
                Assert.Equal(BackupLoadResult.NotAttempted, await mgr.TryLoadFromBackupAsync(Array.Empty<byte>()));
                var backupRestored = await mgr.TryLoadFromBackupAsync(encryptedBytes);
                Assert.Equal(BackupLoadResult.Restored, backupRestored);

                byte[] plainSerialized = AccountDataSerializer.Serialize(new[] { acc }, includePasswords: true);
                dialogSub.ShowConfirmationAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(false));
                Assert.Equal(BackupLoadResult.NotAttempted, await mgr.TryLoadFromBackupAsync(plainSerialized));

                // Plaintext opt-in file allows unencrypted backup to decode as Loaded -> Restored
                string optInFile = StoragePaths.GetUserFile("NoEncryption.IUnderstandTheRisks.iautamor");
                File.WriteAllText(optInFile, "opt-in");
                try
                {
                    dialogSub.ShowConfirmationAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
                    Assert.Equal(BackupLoadResult.Restored, await mgr.TryLoadFromBackupAsync(plainSerialized));
                }
                finally
                {
                    if (File.Exists(optInFile)) File.Delete(optInFile);
                }

                dialogSub.ShowConfirmationAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
                Assert.Equal(BackupLoadResult.Failed, await mgr.TryLoadFromBackupAsync(new byte[] { 1, 2, 3, 4 }));

                File.WriteAllBytes(tempAccFile, new byte[] { 0xFF, 0xFE, 0xFD, 0xFC });
                AccountManager.ReplaceAccountsList(new List<Account>());
                mgr.LoadAccounts(null, out bool corruptUnlocked);
                Assert.False(corruptUnlocked);
                Assert.False(File.Exists(tempAccFile + ".bak"));
                Assert.NotEmpty(Directory.GetFiles(tempDir, Path.GetFileName(tempAccFile) + ".corrupt-*.bak"));

                AccountManager.ReplaceAccountsList(new List<Account> { acc });
                await mgr.ResetEncryptionAsync(manualReset: false);
                mgr.ResetEncryption(manualReset: false);

                AccountPersistenceService.Default.TestPasswordPromptAsync = (_, _) => Task.FromResult<string?>("/UC");
                await mgr.ResetEncryptionAsync(manualReset: true);

                AccountPersistenceService.Default.TestPasswordPromptAsync = (_, _) => Task.FromResult<string?>("short");
                await mgr.ResetEncryptionAsync(manualReset: true);
                dialogSub.Received().ShowWarning(Arg.Any<string>(), Arg.Any<string>());

                AccountPersistenceService.Default.TestPasswordPromptAsync = (_, _) => Task.FromResult<string?>("StrongMasterPass123!");
                await mgr.ResetEncryptionAsync(manualReset: true);
                Assert.True(AccountPersistenceService.Default.IsDatabasePasswordEncrypted());

                // Encrypted backup locked with master password -> NeedsMasterPassword -> PromotedNeedsPassword or NotAttempted
                byte[] masterEncryptedBytes = File.ReadAllBytes(tempAccFile);
                dialogSub.ShowConfirmationAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(false));
                Assert.Equal(BackupLoadResult.NotAttempted, await mgr.TryLoadFromBackupAsync(masterEncryptedBytes));

                File.WriteAllBytes(tempAccFile + ".backup", masterEncryptedBytes);
                dialogSub.ShowConfirmationAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
                Assert.Equal(BackupLoadResult.PromotedNeedsPassword, await mgr.TryLoadFromBackupAsync(masterEncryptedBytes));

                byte[] wrongHash = SHA512.HashData(Encoding.UTF8.GetBytes("WrongPassword999!"));
                mgr.LoadAccounts(wrongHash, out bool wrongUnlocked);
                Assert.False(wrongUnlocked);

                byte[] rightHash = SHA512.HashData(Encoding.UTF8.GetBytes("StrongMasterPass123!"));
                mgr.LoadAccounts(rightHash, out bool rightUnlocked);
                Assert.True(rightUnlocked);

                AccountPersistenceService.Default.TestPasswordPromptAsync = (_, _) => Task.FromResult<string?>("");
                await mgr.ResetEncryptionAsync(manualReset: true);
                Assert.False(AccountPersistenceService.Default.IsDatabasePasswordEncrypted());

                AccountPersistenceService.Default.QueueDeferredSave();
            }
            finally
            {
                AccountPersistenceService.Default.TestPasswordPromptAsync = prevPrompt;
                AccountPersistenceService.Default.Store = prevStore;
                AccountManager.ReplaceAccountsList(new List<Account>());
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore cleanup error */ }
            }
        }

        [Fact]
        public async Task Lifecycle_InitializePostStartup_AutoCookieRefresh_WebServer_And_OfferResume_CoverBranches()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            var dialogSub = Substitute.For<IDialogService>();
            using var _dialogScope = AppServices.Override<IDialogService>(dialogSub);
            int prevDelay = AccountManager.OfferResumeDelayMilliseconds;
            AccountManager.OfferResumeDelayMilliseconds = 1;

            try
            {
                mgr.SetAutoCookieRefreshEnabled(true);
                mgr.SetAutoCookieRefreshEnabled(true); // already non-null
                Assert.NotNull(mgr.AutoCookieRefresh);
                mgr.SetAutoCookieRefreshEnabled(false);
                mgr.SetAutoCookieRefreshEnabled(false); // already null
                Assert.Null(mgr.AutoCookieRefresh);

                await mgr.StopWebServerAsync();
                int testPort = 49152 + Random.Shared.Next(1000, 9000);
                await mgr.RestartWebServerAsync(testPort, allowExternal: false);
                await mgr.StopWebServerAsync();
                await mgr.RestartWebServerAsync(-1, allowExternal: false); // invalid port -> catch block sets AltManagerWS = null

                var pendingAcc1 = new Account { Username = "Pending1", UserID = 701, Valid = false };
                var pendingAcc2 = new Account { Username = "Pending2", UserID = 702, Valid = false };
                AccountManager.ReplaceAccountsList(new List<Account> { pendingAcc1, pendingAcc2 });

                ReloginQueueStore.Save(new long[] { 701 });
                dialogSub.ShowConfirmationAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
                await mgr.OfferResumeReloginQueue();
                dialogSub.Received(1).OpenFastLoginDialog(pendingAcc1);

                ReloginQueueStore.Save(new long[] { 701, 702 });
                dialogSub.ShowConfirmationAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
                await mgr.OfferResumeReloginQueue();
                dialogSub.Received(1).OpenFastLoginDialogBatch(Arg.Any<IReadOnlyList<Account>>());

                ReloginQueueStore.Save(new long[] { 701 });
                dialogSub.ShowConfirmationAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(false));
                await mgr.OfferResumeReloginQueue();
                Assert.Empty(ReloginQueueStore.Load());

                AppSettings.General.Set("AutoCloseRobloxOnLaunch", "false");
                AppSettings.General.Set("EnableMultiRbx", "false");
                AppSettings.General.Set("EnableAntiAfk", "false");
                AppSettings.General.Set("AutoTrimMemory", "false");
                mgr.InitializePostStartup();

                // Exercise true branches of InitializePostStartup + EnsureBackendInitialized
                AccountManager.EnsureBackendInitialized();
                mgr.InitializeBackendServices(); // second call hits early return
                AppSettings.General.Set("EnableAntiAfk", "true");
                AppSettings.General.Set("AntiAfkIntervalMinutes", "5");
                AppSettings.General.Set("AutoTrimMemory", "true");
                AppSettings.General.Set("AutoTrimIntervalMinutes", "2");
                AppSettings.General.Set(AppSettingsKeys.General.AutoMuteBackgroundClients, "true");
                AppSettings.General.Set(AppSettingsKeys.General.MaxTotalRobloxCpuPercent, "50");
                mgr.InitializePostStartup();

                AntiAfkService.Stop();
                MemoryOptimizer.ConfigureAutoTrim(false);
                RobloxAudioService.ConfigureSmartMute(false);
                AppSettings.General.Set("EnableAntiAfk", "false");
                AppSettings.General.Set("AutoTrimMemory", "false");
                AppSettings.General.Set(AppSettingsKeys.General.AutoMuteBackgroundClients, "false");
                AppSettings.General.Set(AppSettingsKeys.General.MaxTotalRobloxCpuPercent, "0");

                AccountManager.SuppressExitOnShutdown = true;
                string prevPlaceId = AppServices.ServerBrowserState.CurrentPlaceId;
                AppServices.ServerBrowserState.CurrentPlaceId = string.Empty;
                var tempMgr = new AccountManager();
                AppSettings.General.Set("ShuffleJobId", "true");
                AppSettings.General.Set("AutoCookieRefresh", "true");
                AppSettings.General.Set("ShowPresence", "false");
                AppSettings.Developer.Set("EnableWebServer", "false");
                tempMgr.InitializeBackendServices();
                tempMgr.Close();
                tempMgr.Dispose();
                Assert.True(tempMgr.IsDisposed);
                tempMgr.ShutdownBackend();
                AppServices.ServerBrowserState.CurrentPlaceId = prevPlaceId;

                // Second temp instance with AutoCookieRefresh=false, ShowPresence=true, EnableWebServer=true
                var tempMgr2 = new AccountManager();
                AppSettings.General.Set("ShuffleJobId", "false");
                AppSettings.General.Set("AutoCookieRefresh", "false");
                AppSettings.General.Set("ShowPresence", "true");
                AppSettings.Developer.Set("EnableWebServer", "true");
                AppSettings.WebServer.Set("WebServerPort", "invalid_numeric_port");
                AppSettings.WebServer.Set("AllowExternalConnections", "true");
                tempMgr2.InitializeBackendServices();
                Assert.Null(tempMgr2.AutoCookieRefresh);
                Assert.NotNull(tempMgr2.PresenceTimer);
                AppSettings.Developer.Set("EnableWebServer", "false");
                AppSettings.WebServer.Set("AllowExternalConnections", "false");

                // Also cover InitializePostStartup with AutoCloseRobloxOnLaunch=true, EnableMultiRbx=true
                AppSettings.General.Set("AutoCloseRobloxOnLaunch", "true");
                AppSettings.General.Set("EnableMultiRbx", "true");
                tempMgr2.InitializePostStartup();
                AppSettings.General.Set("AutoCloseRobloxOnLaunch", "false");
                tempMgr2.Dispose();

                AccountManager.Instance = mgr;
            }
            finally
            {
                AccountManager.SuppressExitOnShutdown = false;
                AccountManager.OfferResumeDelayMilliseconds = prevDelay;
                ReloginQueueStore.Clear();
                AccountManager.ReplaceAccountsList(new List<Account>());
            }
        }
    }
}
