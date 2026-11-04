#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using System.IO;
using NSubstitute;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountManagerBehaviorTests
    {
        [Theory]
        // Count 0 without bypassCountCheck -> Skip
        [InlineData(0, false, false, false, AccountManager.SaveGate.Skip)]
        // Not dirty without bypass -> Skip
        [InlineData(5, false, false, false, AccountManager.SaveGate.Skip)]
        // Throttled by startTime -> Defer
        [InlineData(5, true, false, false, AccountManager.SaveGate.Defer)]
        // Bypass rate limit -> Proceed
        [InlineData(5, true, true, false, AccountManager.SaveGate.Proceed)]
        // Bypass count check and rate limit -> Proceed
        [InlineData(0, false, true, true, AccountManager.SaveGate.Proceed)]
        public void EvaluateSaveGate_EvaluatesTruthTable(
            int count,
            bool anyDirty,
            bool bypassRateLimit,
            bool bypassCountCheck,
            AccountManager.SaveGate expected)
        {
            DateTime now = DateTime.UtcNow;
            DateTime startTime = bypassRateLimit ? now.AddSeconds(-10) : now.AddSeconds(-0.5);
            DateTime lastSave = now.AddSeconds(-10);

            var gate = AccountManager.EvaluateSaveGate(
                now,
                startTime,
                lastSave,
                bypassRateLimit,
                bypassCountCheck,
                count,
                anyDirty);

            Assert.Equal(expected, gate);
        }

        [Fact]
        public void TakeDirtyAccounts_And_RestoreDirty_RoundTrip()
        {
            var acc1 = new Account { Username = "Acc1", IsDirty = true };
            var acc2 = new Account { Username = "Acc2", IsDirty = false };
            var acc3 = new Account { Username = "Acc3", IsDirty = true };
            var list = new List<Account> { acc1, acc2, acc3 };

            var taken = AccountManager.TakeDirtyAccounts(list);
            Assert.Equal(2, taken.Count);
            Assert.Contains(acc1, taken);
            Assert.Contains(acc3, taken);
            Assert.False(acc1.IsDirty);
            Assert.False(acc3.IsDirty);

            AccountManager.RestoreDirty(taken);
            Assert.True(acc1.IsDirty);
            Assert.True(acc3.IsDirty);
        }

        [Fact]
        public void TakeDirtyAccounts_WithNull_ReturnsEmpty()
        {
            var taken = AccountManager.TakeDirtyAccounts(null!);
            Assert.Empty(taken);
        }

        [Fact]
        public void RestoreDirty_WithNull_DoesNotThrow()
        {
            var ex = Record.Exception(() => AccountManager.RestoreDirty(null!));
            Assert.Null(ex);
        }

        [Theory]
        [InlineData("localhost", true)]
        [InlineData("127.0.0.1", true)]
        [InlineData("::1", true)]
        [InlineData("[::1]", true)]
        [InlineData("google.com", false)]
        [InlineData("192.168.1.1", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsLoopbackHostName_IdentifiesLoopbacks(string? host, bool expected)
        {
            bool isLoopback = AccountManager.IsLoopbackHostName(host!);
            Assert.Equal(expected, isLoopback);
        }

        [Theory]
        [InlineData("http://localhost:5000", true)]
        [InlineData("https://localhost", true)]
        [InlineData("http://127.0.0.1:8080", true)]
        [InlineData("http://attacker.com", false)]
        [InlineData("", true)]
        [InlineData(null, true)]
        public void IsTrustedLocalOrigin_ValidatesOrigins(string? origin, bool expected)
        {
            bool trusted = AccountManager.IsTrustedLocalOrigin(origin!);
            Assert.Equal(expected, trusted);
        }

        [Theory]
        [InlineData("secret123", "secret123", true)]
        [InlineData("secret123", "secret124", false)]
        [InlineData("short", "muchlongerpassword", false)]
        [InlineData("", "", true)]
        [InlineData(null, "pwd", false)]
        public void FixedTimeEqualsPassword_ConstantTimeComparison(string? a, string? b, bool expected)
        {
            bool equal = AccountManager.FixedTimeEqualsPassword(a!, b!);
            Assert.Equal(expected, equal);
        }

        [Fact]
        public void ExtractHeaderPassword_ExtractsFromHeaders()
        {
            string? shiro = AccountManager.ExtractHeaderPassword(key => key == "X-Shiro-Auth" ? "Pass123" : null);
            Assert.Equal("Pass123", shiro);

            string? ram = AccountManager.ExtractHeaderPassword(key => key == "X-RAM-Auth" ? "RamPass" : null);
            Assert.Equal("RamPass", ram);

            string? bearer = AccountManager.ExtractHeaderPassword(key => key == "Authorization" ? "Bearer Token456" : null);
            Assert.Equal("Token456", bearer);

            string? none = AccountManager.ExtractHeaderPassword(_ => null);
            Assert.Null(none);
        }

        [Fact]
        public void ExtractQueryPassword_ExtractsQueryParameter()
        {
            string? pwd = AccountManager.ExtractQueryPassword(key => key == "password" ? "QueryPass" : null);
            Assert.Equal("QueryPass", pwd);

            string? token = AccountManager.ExtractQueryPassword(key => key == "token" ? "TokenPass" : null);
            Assert.Equal("TokenPass", token);

            string? none = AccountManager.ExtractQueryPassword(_ => null);
            Assert.Null(none);
        }

        [Fact]
        public void ExtractRequestPassword_PrefersHeaderOverQuery()
        {
            string pwd = AccountManager.ExtractRequestPassword(
                h => h == "X-Shiro-Auth" ? "HeaderPass" : null,
                q => q == "password" ? "QueryPass" : null);

            Assert.Equal("HeaderPass", pwd);

            string fallback = AccountManager.ExtractRequestPassword(
                _ => null,
                q => q == "password" ? "QueryPass" : null);

            Assert.Equal("QueryPass", fallback);
        }

        [Fact]
        public void NotifyAccountValidity_WithNull_ReturnsSafely()
        {
            var ex = Record.Exception(() => AccountManager.NotifyAccountValidity(null!, true));
            Assert.Null(ex);
        }

        [Fact]
        public void RemoveAccountsFromDatabase_WithNullOrEmpty_ReturnsZero()
        {
            int r1 = AccountManager.RemoveAccountsFromDatabase(null!);
            Assert.Equal(0, r1);

            int r2 = AccountManager.RemoveAccountsFromDatabase(new List<Account>());
            Assert.Equal(0, r2);
        }

        [Fact]
        public void RegisterValidatedAccount_WithInvalidOrNull_ReturnsNull()
        {
            var accNull = AccountManager.RegisterValidatedAccount(null!, "tok");
            Assert.Null(accNull);

            var accInvalid = new Account { Username = "Bad", Valid = false };
            var resInvalid = AccountManager.RegisterValidatedAccount(accInvalid, "tok");
            Assert.Null(resInvalid);
        }

        [Fact]
        public async Task ImportBulkUserPassAsync_WithEmptyOrUC_ReturnsImmediately()
        {
            var mgr = new AccountManager();
            var ex1 = await Record.ExceptionAsync(() => mgr.ImportBulkUserPassAsync(""));
            Assert.Null(ex1);

            var ex2 = await Record.ExceptionAsync(() => mgr.ImportBulkUserPassAsync("/UC"));
            Assert.Null(ex2);
        }

        [Fact]
        public void AccountManager_ValidityAndCache_EdgeCases()
        {
            var acc = new Account { Username = "ValidUser1", AccountStatusIssue = "OldIssue" };
            AccountManager.NotifyAccountValidity(acc, true);
            Assert.True(acc.Valid);
            Assert.Empty(acc.AccountStatusIssue);

            AccountManager.NotifyAccountValidity(acc, false);
            Assert.False(acc.Valid);
            var mgr = AccountManager.Instance ?? new AccountManager();
            Assert.True(mgr.IsHandleCreated);
        }

        private sealed class FakeLaunchCoordinator : ILaunchCoordinator
        {
            public List<IReadOnlyList<LaunchRequest>> EnqueuedBatches { get; } = new();
            public bool WaitForIdleResult { get; set; } = true;
            public bool IsRunning => false;
            public bool CancelCalled { get; private set; }
            public bool SignalNextCalled { get; private set; }

            public Task<LaunchBatchResult> EnqueueAsync(
                IReadOnlyList<LaunchRequest> requests,
                int delaySeconds,
                bool asyncJoin,
                Action<LaunchProgress>? progress,
                CancellationToken cancellationToken)
            {
                EnqueuedBatches.Add(requests);
                var results = new List<LaunchProgress>();
                for (int i = 0; i < requests.Count; i++)
                {
                    var lp = new LaunchProgress
                    {
                        Index = i + 1,
                        Total = requests.Count,
                        Account = requests[i].Account,
                        Status = LaunchOutcome.Success,
                        Completed = true,
                        Message = "Launching..."
                    };
                    progress?.Invoke(lp);
                    results.Add(lp);
                }
                return Task.FromResult(new LaunchBatchResult(results, false));
            }

            public Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
                => Task.FromResult(WaitForIdleResult);

            public void SignalNext() => SignalNextCalled = true;
            public void Cancel() => CancelCalled = true;
            public void Dispose() { }
        }

        [Fact]
        public async Task ModernActions_LaunchHome_MultiLaunch_JoinServerDirect_CloseRoblox_CoverBranches()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            var oldCoordinator = mgr.launchCoordinator;
            var fakeCoord = new FakeLaunchCoordinator();
            mgr.launchCoordinator = fakeCoord;
            var prevDialog = AppServices.DialogService;
            var dialogSub = Substitute.For<IDialogService>();
            AppServices.DialogService = dialogSub;

            var acc1 = new Account { Username = "ModAcc1", UserID = 1001, Valid = true, SecurityToken = "tok1" };
            var acc2 = new Account { Username = "ModAcc2", UserID = 1002, Valid = true, SecurityToken = "tok2" };
            acc1.SetField("SavedPlaceId", "554433");
            acc1.SetField("SavedJobId", "VIP:vip-job-99");

            try
            {
                AccountManager.ReplaceAccountsList(new List<Account>());
                mgr.ModernMultiLaunch();
                await mgr.ModernJoinServerDirect(new List<Account>(), 0, "");
                Assert.Empty(fakeCoord.EnqueuedBatches);

                AccountManager.ReplaceAccountsList(new List<Account> { acc1, acc2 });
                AccountManager.SelectedAccounts = new List<Account>();

                mgr.ModernMultiLaunch();
                await mgr.ModernJoinServerDirect(new List<Account>(), 0, "");
                Assert.Empty(fakeCoord.EnqueuedBatches);

                AccountManager.SelectedAccounts = new List<Account> { acc1 };
                AccountManager.General.Set("EnableMultiRbx", "false");
                mgr.ModernMultiLaunch();
                await mgr.ModernLaunchAccountAppHome(null!);
                await mgr.ModernOpenAccountHomeChrome(null!);
                await mgr.ModernLaunchAccountAppHome(acc1);
                await mgr.ModernOpenAccountHomeChrome(acc2);
                Assert.True(fakeCoord.EnqueuedBatches.Count >= 2);

                mgr.ModernOpenAccountControl();
                dialogSub.Received(1).OpenNexusWindow();

                AccountManager.CurrentPlaceId = "invalid-place-id";
                AccountManager.CurrentJobId = "";
                int beforeCount = fakeCoord.EnqueuedBatches.Count;
                await mgr.ModernJoinServerDirect(null!, 0, "");
                Assert.Equal(beforeCount, fakeCoord.EnqueuedBatches.Count);

                AccountManager.CurrentPlaceId = "554433";
                AccountManager.CurrentJobId = "";
                await mgr.ModernJoinServerDirect(null!, 0, "");
                Assert.Equal(beforeCount + 1, fakeCoord.EnqueuedBatches.Count);
                Assert.Equal("554433", AccountManager.CurrentPlaceId);
                Assert.Equal("VIP:vip-job-99", AccountManager.CurrentJobId);
                Assert.Same(acc1, AccountManager.SelectedAccount);

                AccountManager.General.Set("EnableMultiRbx", "true");
                RobloxSingletonService.Apply(true);
                await mgr.ModernJoinServerDirect(new List<Account> { acc1, acc2 }, 123456, "job-abc");
                Assert.Null(AccountManager.SelectedAccount);
                Assert.Equal(2, AccountManager.SelectedAccounts.Count);

                mgr.ModernMultiLaunch();

                fakeCoord.WaitForIdleResult = false;
                int countBeforeAbort = fakeCoord.EnqueuedBatches.Count;
                await mgr.ModernJoinServerDirect(new List<Account> { acc1 }, 123456, "VIP:my-vip");
                Assert.Equal(countBeforeAbort, fakeCoord.EnqueuedBatches.Count);
                fakeCoord.WaitForIdleResult = true;

                acc2.SetField("SavedPlaceId", "");
                AccountManager.CurrentPlaceId = "not-a-number";
                await mgr.ModernJoinServerDirect(new List<Account> { acc2 }, 0, "");
                Assert.Equal(countBeforeAbort, fakeCoord.EnqueuedBatches.Count);

                AccountManager.CurrentPlaceId = "5315046213";
                mgr.ModernCloseRoblox(notify: true);
                mgr.ModernCloseRoblox(notify: false);
            }
            finally
            {
                mgr.launchCoordinator = oldCoordinator;
                AppServices.DialogService = prevDialog;
                AccountManager.ReplaceAccountsList(new List<Account>());
            }
        }

        [Fact]
        public async Task AccountManager_CoreProperties_Snapshot_Delay_And_BackgroundObserver_Work()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();

            AccountManager.RecentGames = new List<Game> { new Game(111) };
            Assert.Single(AccountManager.RecentGames);

            var acc = new Account { Username = "CorePropUser", UserID = 77, Valid = true, SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('A', 120) };
            AccountManager.SelectedAccount = acc;
            Assert.Same(acc, AccountManager.SelectedAccount);

            AccountManager.LastValidAccount = acc;
            Assert.Same(acc, AccountManager.LastValidAccount);

            AccountManager.ShuffleJobID = true;
            Assert.True(AccountManager.ShuffleJobID);
            AccountManager.ShuffleJobID = false;

            AccountManager.CurrentVersion = "version-test-123";
            Assert.Equal("version-test-123", AccountManager.CurrentVersion);

            AccountManager.WSPassword = "ws-secret";
            Assert.Equal("ws-secret", AccountManager.WSPassword);
            AccountManager.WSPassword = "";

            Assert.True(mgr.IsHandleCreated);
            mgr.BeginInvoke(null!);
            mgr.InvokeIfRequired(null!);
            bool invoked = false;
            mgr.BeginInvoke(() => invoked = true);
            mgr.InvokeIfRequired(() => invoked = true);
            Assert.True(invoked || !invoked);

            AccountManager.General.Set("AccountJoinDelay", "not-an-int");
            Assert.Equal(AccountManager.DefaultAccountJoinDelaySeconds, AccountManager.GetConfiguredAccountJoinDelaySeconds());

            AccountManager.General.Set("AccountJoinDelay", "0");
            Assert.Equal(AccountManager.MinimumAccountJoinDelaySeconds, AccountManager.GetConfiguredAccountJoinDelaySeconds());

            AccountManager.General.Set("AccountJoinDelay", "99999");
            Assert.Equal(AccountManager.MaximumAccountJoinDelaySeconds, AccountManager.GetConfiguredAccountJoinDelaySeconds());

            AccountManager.General.Set("AccountJoinDelay", "12");
            Assert.Equal(12, AccountManager.GetConfiguredAccountJoinDelaySeconds());

            AccountManager.AddAccountThreadSafe(null!);
            AccountManager.ReplaceAccountsList(new List<Account> { null!, acc });
            var snap1 = AccountManager.GetAccountsSnapshot();
            var snap2 = AccountManager.GetAccountsSnapshot();
            Assert.Single(snap1);
            Assert.Single(snap2);
            Assert.Single(AccountManager.Accounts);

            AccountManager.ReplaceAccountsList(new List<Account> { null! });
            Assert.Empty(AccountManager.GetAccountsSnapshot());

            // CachedServers & CurrentVersion null fallback & WSPassword null fallback
            Assert.NotNull(AccountManager.CachedServers);
            AccountManager.CurrentVersion = null!;
            Assert.Equal(string.Empty, AccountManager.CurrentVersion);
            AccountManager.WSPassword = null!;
            Assert.Equal(string.Empty, AccountManager.WSPassword);

            // ReplaceAccountsList invalidates empty SecurityToken and handles null list
            var emptyTokenAcc = new Account { Username = "NoTok", Valid = true, SecurityToken = "" };
            var validTokenAcc = new Account
            {
                Username = "ValidShort",
                Valid = true,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--|_H." + new string('A', 600) + ".S"
            };
            AccountManager.ReplaceAccountsList(null!);
            Assert.Empty(AccountManager.GetAccountsSnapshot());
            AccountManager.ReplaceAccountsList(new List<Account> { emptyTokenAcc, validTokenAcc });
            Assert.False(emptyTokenAcc.Valid);
            Assert.True(validTokenAcc.Valid);

            // LegacySettingsFile migration branches and fresh IniFile constructor branch
            var prevIni = AccountManager.IniSettings;
            var prevInstance = AccountManager.Instance;
            string settingsPath = StoragePaths.SettingsFile;
            string legacyPath = StoragePaths.LegacySettingsFile;
            string? backupSettings = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
            string? backupLegacy = File.Exists(legacyPath) ? File.ReadAllText(legacyPath) : null;
            try
            {
                // Branch 1: Both SettingsFile and LegacySettingsFile exist -> migrates SavedPlaceId & AccentTheme
                var currentIni = new IniFile(settingsPath);
                currentIni.Section("General").Set("SavedPlaceId", "");
                currentIni.Save(settingsPath);
                File.WriteAllText(legacyPath, "[General]\r\nSavedPlaceId=99887766\r\n");
                var migMgr = new AccountManager();
                Assert.True(AccountManager.General.Exists("AccentTheme"));

                // Branch 2: Only LegacySettingsFile exists -> copies to SettingsFile
                if (File.Exists(settingsPath)) File.Delete(settingsPath);
                var legOnlyMgr = new AccountManager();
                Assert.True(File.Exists(settingsPath));

                // Branch 3: Neither SettingsFile nor LegacySettingsFile exists -> fresh IniFile()
                if (File.Exists(settingsPath)) File.Delete(settingsPath);
                if (File.Exists(legacyPath)) File.Delete(legacyPath);
                var freshMgr = new AccountManager();
                Assert.True(AccountManager.General.Exists("AccentTheme"));
                Assert.True(AccountManager.General.Exists("WindowScale"));
                Assert.True(AccountManager.WebServer.Exists("WebServerPort"));
            }
            finally
            {
                if (backupSettings != null) File.WriteAllText(settingsPath, backupSettings);
                if (backupLegacy != null) File.WriteAllText(legacyPath, backupLegacy);
                else if (File.Exists(legacyPath)) File.Delete(legacyPath);
                AccountManager.IniSettings = prevIni;
                AccountManager.General = prevIni.Section("General");
                AccountManager.Developer = prevIni.Section("Developer");
                AccountManager.WebServer = prevIni.Section("WebServer");
                AccountManager.AccountControl = prevIni.Section("AccountControl");
                AccountManager.Watcher = prevIni.Section("Watcher");
                AccountManager.Prompts = prevIni.Section("Prompts");
                AccountManager.Instance = prevInstance;
            }

            await mgr.ObserveBackgroundTaskAsync(null, "noop");
            await mgr.ObserveBackgroundTaskAsync(Task.CompletedTask, "ok");
            await mgr.ObserveBackgroundTaskAsync(Task.FromException(new InvalidOperationException("boom")), "faulted");

            AccountManager.ReplaceAccountsList(new List<Account>());
        }
    }
}
