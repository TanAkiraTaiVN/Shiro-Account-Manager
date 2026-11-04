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
using Shiro.Classes;
using Shiro.Http;
using Shiro.Tests.TestInfrastructure;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountManagerLifecycleAndAccountsBehaviorTests : IDisposable
    {
        public void Dispose()
        {
            AppSettings.General.Set("EnableMultiRbx", "true");
            RobloxSingletonService.Apply(true);
        }

        [Fact]
        public void AccountManager_UpdateAccountValidity_NullAccount_DoesNotThrow()
        {
            var exception = Record.Exception(() =>
            {
                AccountManager.NotifyAccountValidity(null!, true);
                AccountManager.NotifyAccountValidity(null!, false);
            });
            Assert.Null(exception);

            var acc = new Account
            {
                Username = "ValidityTestUser",
                Valid = false,
                AccountStatusIssue = "LockedOut"
            };

            AccountManager.NotifyAccountValidity(acc, true);
            Assert.True(acc.Valid);
            Assert.Equal(string.Empty, acc.AccountStatusIssue);

            AccountManager.NotifyAccountValidity(acc, false);
            Assert.False(acc.Valid);
        }

        [Fact]
        public async Task AccountManager_GetUserIDAsync_EmptyOrWhitespace_ReturnsFalse()
        {
            var resNull = await AccountManager.GetUserIDAsync(null!);
            Assert.False(resNull.Success);
            Assert.Equal(-1, resNull.UserId);
            Assert.Null(resNull.Response);

            var resEmpty = await AccountManager.GetUserIDAsync("");
            Assert.False(resEmpty.Success);
            Assert.Equal(-1, resEmpty.UserId);

            var resWhitespace = await AccountManager.GetUserIDAsync("   ");
            Assert.False(resWhitespace.Success);
            Assert.Equal(-1, resWhitespace.UserId);
        }

        [Fact]
        public async Task AccountManager_GetUserIDAsync_KnownAccountInList_ReturnsCachedAndKnownId()
        {
            var known = new Account
            {
                Username = "KnownListUser",
                UserID = 887766,
                SecurityToken = "test_token"
            };

            lock (AccountManager.AccountListLock)
            {
                AccountManager.AccountsList.Add(known);
            }

            try
            {
                var (success1, uid1, resp1) = await AccountManager.GetUserIDAsync("KnownListUser");
                Assert.True(success1);
                Assert.Equal(887766, uid1);
                Assert.Null(resp1);

                // Call again to verify in-memory cache lookup
                var (success2, uid2, resp2) = await AccountManager.GetUserIDAsync("KnownListUser");
                Assert.True(success2);
                Assert.Equal(887766, uid2);
                Assert.Null(resp2);
            }
            finally
            {
                lock (AccountManager.AccountListLock)
                {
                    AccountManager.AccountsList.Remove(known);
                }
            }
        }

        [Fact]
        public void AccountManager_RegisterValidatedAccount_And_Remove_Behaviors()
        {
            // Invalid account -> returns null
            var invalid = new Account { Valid = false, Username = "InvalidUser" };
            var regInvalid = AccountManager.RegisterValidatedAccount(invalid, "token", saveImmediate: false);
            Assert.Null(regInvalid);

            // Valid account -> registered
            var valid = new Account
            {
                Username = "ValidRegUser",
                UserID = 998877,
                Valid = true,
                SecurityToken = "InitialToken"
            };

            var registered = AccountManager.RegisterValidatedAccount(valid, "InitialToken", "InitialPass", saveImmediate: false);
            Assert.NotNull(registered);
            Assert.Equal("InitialPass", registered.Password);

            try
            {
                // Registering again updates existing record
                var updated = new Account
                {
                    Username = "ValidRegUser",
                    UserID = 998877,
                    Valid = true,
                    SecurityToken = "UpdatedToken"
                };

                var secondReg = AccountManager.RegisterValidatedAccount(updated, "UpdatedToken", "UpdatedPass", saveImmediate: false);
                Assert.NotNull(secondReg);
                Assert.Equal("UpdatedToken", secondReg.SecurityToken);
                Assert.Equal("UpdatedPass", secondReg.Password);

                // Remove account from database
                int removedCount = AccountManager.RemoveAccountsFromDatabase(new[] { secondReg });
                Assert.Equal(1, removedCount);
            }
            finally
            {
                lock (AccountManager.AccountListLock)
                {
                    AccountManager.AccountsList.Remove(valid);
                }
            }
        }

        [Fact]
        public void AccountManager_PersistenceAndEncryption_MethodsExecuteSafely()
        {
            var exEnc = Record.Exception(() => AccountManager.IsDatabasePasswordEncrypted());
            Assert.Null(exEnc);

            if (AccountManager.Instance == null)
            {
                bool loaded = AccountManager.LoadAccountsFromStore();
                Assert.False(loaded);
            }
        }

        [Fact]
        public void AccountManager_RemoveAccounts_ValidationEdgeCases()
        {
            bool removedNull = AccountManager.RemoveAccount(null!);
            Assert.False(removedNull);

            int countNull = AccountManager.RemoveAccountsFromDatabase(null!);
            Assert.Equal(0, countNull);

            int countEmpty = AccountManager.RemoveAccountsFromDatabase(Array.Empty<Account>());
            Assert.Equal(0, countEmpty);
        }

        [Fact]
        public void AccountManager_LaunchSignals_ExecuteSafely()
        {
            var exception = Record.Exception(() =>
            {
                if (AccountManager.Instance != null)
                {
                    AccountManager.Instance.NextAccount();
                    AccountManager.Instance.CancelLaunching();
                }
            });
            Assert.Null(exception);
        }

        [Fact]
        public void AccountManager_MultiRobloxAndPresence_Behaviors()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            var ex = Record.Exception(() =>
            {
                bool multiResult = mgr.UpdateMultiRoblox();
                Assert.Equal(multiResult, multiResult);

                bool tryMulti = mgr.TryEnableMultiRoblox(promptIfRobloxRunning: false, notify: false);
                Assert.Equal(tryMulti, tryMulti);

                mgr.RestartPresenceTimer();
                mgr.ModernRefreshPresence();
                mgr.SchedulePresenceRefresh(null!);
                mgr.SchedulePresenceRefresh(new Account { UserID = -1 });
                mgr.SchedulePresenceRefresh(new Account { UserID = 987654 });
            });
            Assert.Null(ex);
        }

        [Fact]
        public async Task AccountManager_ImportBulkUserPassAsync_InputValidation()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();

            // Null or empty combos
            await mgr.ImportBulkUserPassAsync(null!);
            await mgr.ImportBulkUserPassAsync("");
            await mgr.ImportBulkUserPassAsync("/UC");

            // Combos without valid delimiter
            string status = "";
            await mgr.ImportBulkUserPassAsync("invalid_no_separator\nanother_bad_line", msg => status = msg);
// trailing line removed

            // Canceled token breaks loop with validCount == 0
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            await mgr.ImportBulkUserPassAsync("validUser:validPass", msg => status = msg, cts.Token);
            var prevOverride = BrowserService.TestSignInOverride;
            try
            {
                BrowserService.TestSignInOverride = (user, pass, slot, submit, token) =>
                {
                    if (user == "failUser") throw new InvalidOperationException("bad credentials");
                    if (user == "cancelUser") throw new OperationCanceledException(token);
                    return Task.FromResult<Account?>(new Account { Username = user ?? "u", Valid = true });
                };

                await mgr.ImportBulkUserPassAsync("okUser1:pass1\nokUser2|pass2\nokUser3\tpass3", msg => status = msg);
                Assert.Contains("3", status);

                await mgr.ImportBulkUserPassAsync("okUser1:pass1\nfailUser:pass2\ncancelUser:pass3", msg => status = msg);
                Assert.Contains("failUser", status);
            }
            finally
            {
                BrowserService.TestSignInOverride = prevOverride;
            }
        }

        [Fact]
        public void MakeSafeFileName_SanitizesInvalidCharsAndEdgeCases()
        {
            Assert.Equal("fallback", AccountManager.MakeSafeFileName("", "fallback"));
            Assert.Equal("fallback", AccountManager.MakeSafeFileName("   ", "fallback"));
            Assert.Equal("fallback", AccountManager.MakeSafeFileName("...", "fallback"));
            Assert.Equal("cleanName", AccountManager.MakeSafeFileName("cleanName", "fallback"));
            Assert.Equal("cleanName", AccountManager.MakeSafeFileName("  cleanName... ", "fallback"));
            Assert.Equal("bad_name_file", AccountManager.MakeSafeFileName("bad:name?file.", "fallback"));
            string longInvalid = new string('?', 300);
            Assert.Equal(new string('_', 300), AccountManager.MakeSafeFileName(longInvalid, "fallback"));
        }

        [Fact]
        public async Task GetUserIDAsync_HttpExecution_And_RefreshStaleCookiesAsync_CoverBranches()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            var prevUsersClient = AccountManager.UsersClient;
            var handler = new FakeHttpHandler();
            AccountManager.UsersClient = new RestClient(new System.Net.Http.HttpClient(handler), "https://users.roblox.com/");

            try
            {
                AccountManager.ClearResolvedUserIdsCacheForTesting();

                handler.Enqueue(HttpStatusCode.OK, "{\"data\":[{\"id\":445566,\"name\":\"HttpResolvedUser\"}]}");
                var (ok1, id1, _) = await AccountManager.GetUserIDAsync("HttpResolvedUser");
                Assert.True(ok1);
                Assert.Equal(445566, id1);

                var (okCached, idCached, _) = await AccountManager.GetUserIDAsync("HttpResolvedUser");
                Assert.True(okCached);
                Assert.Equal(445566, idCached);

                handler.Enqueue(HttpStatusCode.OK, "{\"data\":[]}");
                var (okEmpty, idEmpty, _) = await AccountManager.GetUserIDAsync("UnknownUserX");
                Assert.False(okEmpty);
                Assert.Equal(-1, idEmpty);

                handler.Enqueue(HttpStatusCode.OK, "{not-json");
                var (okBadJson, _, _) = await AccountManager.GetUserIDAsync("BadJsonUser");
                Assert.False(okBadJson);

                AccountManager.ReplaceAccountsList(new List<Account>());
                await mgr.RefreshStaleCookiesAsync(CancellationToken.None);

                var skippedAcc = new Account
                {
                    Username = "SkippedRefresh",
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('B', 120),
                    Valid = true,
                    LastAttemptedRefresh = DateTime.MinValue
                };
                skippedAcc.SetField("NoCookieRefresh", "true");

                var staleAcc = new Account
                {
                    Username = "StaleCandidate",
                    SecurityToken = "short_invalid_token",
                    Valid = false,
                    LastAttemptedRefresh = DateTime.MinValue
                };

                AccountManager.ReplaceAccountsList(new List<Account> { skippedAcc, staleAcc });
                await mgr.RefreshStaleCookiesAsync(CancellationToken.None);
                Assert.NotEqual(DateTime.MinValue, staleAcc.LastAttemptedRefresh);
            }
            finally
            {
                AccountManager.UsersClient = prevUsersClient!;
                AccountManager.ReplaceAccountsList(new List<Account>());
            }
        }

        [Fact]
        public async Task Persistence_Save_Load_Backup_Promote_And_ResetEncryption_CoverBranches()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroPersistTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string tempAccFile = Path.Combine(tempDir, "AccountData.json");
            string tempBackupsDir = Path.Combine(tempDir, "Backups");

            var prevStore = AccountManager.accountStore;
            var prevDialogs = AppServices.DialogService;
            var prevPrompt = AccountManager.TestPasswordPromptAsync;
            var dialogSub = Substitute.For<IDialogService>();
            AppServices.DialogService = dialogSub;
            AccountManager.accountStore = new AccountStore(tempAccFile, tempBackupsDir);

            try
            {
                File.WriteAllText(tempAccFile, "old-primary-1");
                File.WriteAllText(tempAccFile + ".backup", "backup-content-1");
                DateTime fixedNow = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
                string preserved1 = AccountManager.PromoteBackupToPrimary(tempAccFile, fixedNow);
                Assert.True(File.Exists(preserved1));
                Assert.Equal("backup-content-1", File.ReadAllText(tempAccFile));

                File.WriteAllText(tempAccFile + ".backup", "backup-content-2");
                string preserved2 = AccountManager.PromoteBackupToPrimary(tempAccFile, fixedNow);
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
                await AccountManager.SaveAccountsAsync(BypassRateLimit: true, BypassCountCheck: true);
                Assert.True(File.Exists(tempAccFile));
                Assert.False(AccountManager.IsDatabasePasswordEncrypted());

                acc.IsDirty = true;
                AccountManager.SaveAccounts(BypassRateLimit: false, BypassCountCheck: true);

                bool unlocked = AccountManager.LoadAccountsFromStore();
                Assert.True(unlocked);
                Assert.Single(AccountManager.GetAccountsSnapshot());

                byte[] encryptedBytes = File.ReadAllBytes(tempAccFile);
                Assert.Equal(BackupLoadResult.NotAttempted, mgr.TryLoadFromBackup(Array.Empty<byte>()));
                var backupRestored = mgr.TryLoadFromBackup(encryptedBytes);
                Assert.Equal(BackupLoadResult.Restored, backupRestored);

                byte[] plainSerialized = AccountDataSerializer.Serialize(new[] { acc }, includePasswords: true);
                dialogSub.ShowConfirmation(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
                Assert.Equal(BackupLoadResult.NotAttempted, mgr.TryLoadFromBackup(plainSerialized));

                // Plaintext opt-in file allows unencrypted backup to decode as Loaded -> Restored
                string optInFile = StoragePaths.GetUserFile("NoEncryption.IUnderstandTheRisks.iautamor");
                File.WriteAllText(optInFile, "opt-in");
                try
                {
                    dialogSub.ShowConfirmation(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
                    Assert.Equal(BackupLoadResult.Restored, mgr.TryLoadFromBackup(plainSerialized));
                }
                finally
                {
                    if (File.Exists(optInFile)) File.Delete(optInFile);
                }

                dialogSub.ShowConfirmation(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
                Assert.Equal(BackupLoadResult.Failed, mgr.TryLoadFromBackup(new byte[] { 1, 2, 3, 4 }));

                File.WriteAllBytes(tempAccFile, new byte[] { 0xFF, 0xFE, 0xFD, 0xFC });
                AccountManager.ReplaceAccountsList(new List<Account>());
                mgr.LoadAccounts(null, out bool corruptUnlocked);
                Assert.False(corruptUnlocked);
                Assert.True(File.Exists(tempAccFile + ".bak"));

                AccountManager.ReplaceAccountsList(new List<Account> { acc });
                await mgr.ResetEncryptionAsync(manualReset: false);
                mgr.ResetEncryption(manualReset: false);

                AccountManager.TestPasswordPromptAsync = (_, _) => Task.FromResult<string?>("/UC");
                await mgr.ResetEncryptionAsync(manualReset: true);

                AccountManager.TestPasswordPromptAsync = (_, _) => Task.FromResult<string?>("short");
                await mgr.ResetEncryptionAsync(manualReset: true);
                dialogSub.Received().ShowWarning(Arg.Any<string>(), Arg.Any<string>());

                AccountManager.TestPasswordPromptAsync = (_, _) => Task.FromResult<string?>("StrongMasterPass123!");
                await mgr.ResetEncryptionAsync(manualReset: true);
                Assert.True(AccountManager.IsDatabasePasswordEncrypted());

                // Encrypted backup locked with master password -> NeedsMasterPassword -> PromotedNeedsPassword or NotAttempted
                byte[] masterEncryptedBytes = File.ReadAllBytes(tempAccFile);
                dialogSub.ShowConfirmation(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
                Assert.Equal(BackupLoadResult.NotAttempted, mgr.TryLoadFromBackup(masterEncryptedBytes));

                File.WriteAllBytes(tempAccFile + ".backup", masterEncryptedBytes);
                dialogSub.ShowConfirmation(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
                Assert.Equal(BackupLoadResult.PromotedNeedsPassword, mgr.TryLoadFromBackup(masterEncryptedBytes));

                byte[] wrongHash = SHA512.HashData(Encoding.UTF8.GetBytes("WrongPassword999!"));
                mgr.LoadAccounts(wrongHash, out bool wrongUnlocked);
                Assert.False(wrongUnlocked);

                byte[] rightHash = SHA512.HashData(Encoding.UTF8.GetBytes("StrongMasterPass123!"));
                mgr.LoadAccounts(rightHash, out bool rightUnlocked);
                Assert.True(rightUnlocked);

                AccountManager.TestPasswordPromptAsync = (_, _) => Task.FromResult<string?>("");
                await mgr.ResetEncryptionAsync(manualReset: true);
                Assert.False(AccountManager.IsDatabasePasswordEncrypted());

                AccountManager.QueueDeferredSave();
            }
            finally
            {
                AccountManager.TestPasswordPromptAsync = prevPrompt;
                AccountManager.accountStore = prevStore;
                AppServices.DialogService = prevDialogs;
                AccountManager.ReplaceAccountsList(new List<Account>());
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore cleanup error */ }
            }
        }

        [Fact]
        public async Task Lifecycle_InitializePostStartup_AutoCookieRefresh_WebServer_And_OfferResume_CoverBranches()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            var prevDialogs = AppServices.DialogService;
            var dialogSub = Substitute.For<IDialogService>();
            AppServices.DialogService = dialogSub;
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
                dialogSub.ShowConfirmation(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
                await mgr.OfferResumeReloginQueue();
                dialogSub.Received(1).OpenFastLoginDialog(pendingAcc1);

                ReloginQueueStore.Save(new long[] { 701, 702 });
                dialogSub.ShowConfirmation(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
                await mgr.OfferResumeReloginQueue();
                dialogSub.Received(1).OpenFastLoginDialogBatch(Arg.Any<IReadOnlyList<Account>>());

                ReloginQueueStore.Save(new long[] { 701 });
                dialogSub.ShowConfirmation(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
                await mgr.OfferResumeReloginQueue();
                Assert.Empty(ReloginQueueStore.Load());

                AccountManager.General.Set("AutoCloseRobloxOnLaunch", "false");
                AccountManager.General.Set("EnableMultiRbx", "false");
                AccountManager.General.Set("EnableAntiAfk", "false");
                AccountManager.General.Set("AutoTrimMemory", "false");
                AccountManager.General.Set("UseEmbeddedBrowser", "true");
                AccountManager.AccountControl.Set("StartOnLaunch", "false");
                mgr.InitializePostStartup();

                // Exercise true branches of InitializePostStartup + EnsureBackendInitialized
                AccountManager.EnsureBackendInitialized();
                mgr.InitializeBackendServices(); // second call hits early return
                AccountManager.General.Set("EnableAntiAfk", "true");
                AccountManager.General.Set("AntiAfkIntervalMinutes", "5");
                AccountManager.General.Set("AutoTrimMemory", "true");
                AccountManager.General.Set("AutoTrimIntervalMinutes", "2");
                AccountManager.General.Set(AppSettingsKeys.General.AutoMuteBackgroundClients, "true");
                AccountManager.General.Set(AppSettingsKeys.General.MaxTotalRobloxCpuPercent, "50");
                AccountManager.AccountControl.Set("StartOnLaunch", "true");
                mgr.InitializePostStartup();

                AntiAfkService.Stop();
                MemoryOptimizer.ConfigureAutoTrim(false);
                RobloxAudioService.ConfigureSmartMute(false);
                AccountManager.General.Set("EnableAntiAfk", "false");
                AccountManager.General.Set("AutoTrimMemory", "false");
                AccountManager.General.Set(AppSettingsKeys.General.AutoMuteBackgroundClients, "false");
                AccountManager.General.Set(AppSettingsKeys.General.MaxTotalRobloxCpuPercent, "0");
                AccountManager.AccountControl.Set("StartOnLaunch", "false");

                AccountManager.SuppressExitOnShutdown = true;
                var tempMgr = new AccountManager();
                AccountManager.General.Set("ShuffleJobId", "true");
                AccountManager.General.Set("AutoCookieRefresh", "true");
                AccountManager.General.Set("ShowPresence", "false");
                AccountManager.Developer.Set("EnableWebServer", "false");
                tempMgr.InitializeBackendServices();
                tempMgr.Close();
                tempMgr.Dispose();
                Assert.True(tempMgr.IsDisposed);
                tempMgr.ShutdownBackend();

                // Second temp instance with AutoCookieRefresh=false, ShowPresence=true, EnableWebServer=true, and old updater files + Update dir
                string oldAu1 = Path.Combine(Directory.GetCurrentDirectory(), "Auto Update.exe");
                string oldAu2 = Path.Combine(Directory.GetCurrentDirectory(), "AU.exe");
                string updateDir = Path.Combine(Environment.CurrentDirectory, "Update");
                File.WriteAllText(oldAu1, "dummy");
                File.WriteAllText(oldAu2, "dummy");
                Directory.CreateDirectory(updateDir);
                File.WriteAllText(Path.Combine(updateDir, "temp.txt"), "x");

                var tempMgr2 = new AccountManager();
                AccountManager.General.Set("ShuffleJobId", "false");
                AccountManager.General.Set("AutoCookieRefresh", "false");
                AccountManager.General.Set("ShowPresence", "true");
                AccountManager.Developer.Set("EnableWebServer", "true");
                AccountManager.WebServer.Set("WebServerPort", (49152 + Random.Shared.Next(1000, 9000)).ToString());
                tempMgr2.InitializeBackendServices();
                AccountManager.Developer.Set("EnableWebServer", "false");
                Assert.False(File.Exists(oldAu1));
                Assert.False(File.Exists(oldAu2));
                Assert.False(Directory.Exists(updateDir));

                // Also cover InitializePostStartup with AutoCloseRobloxOnLaunch=true, EnableMultiRbx=true, UseEmbeddedBrowser=false
                AccountManager.General.Set("AutoCloseRobloxOnLaunch", "true");
                AccountManager.General.Set("EnableMultiRbx", "true");
                AccountManager.General.Set("UseEmbeddedBrowser", "false");
                tempMgr2.InitializePostStartup();
                AccountManager.General.Set("AutoCloseRobloxOnLaunch", "false");
                tempMgr2.Dispose();

                AccountManager.Instance = mgr;
            }
            finally
            {
                AccountManager.SuppressExitOnShutdown = false;
                AccountManager.OfferResumeDelayMilliseconds = prevDelay;
                AppServices.DialogService = prevDialogs;
                ReloginQueueStore.Clear();
                AccountManager.ReplaceAccountsList(new List<Account>());
            }
// trailing line removed
        }
    }
}
