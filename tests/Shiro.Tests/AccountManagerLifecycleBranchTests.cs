#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountManagerLifecycleBranchTests
    {
        private sealed class StaticSettingsSnapshot
        {
            public AccountManager? Instance { get; init; }
            public IniFile IniSettings { get; init; } = null!;
            public IniSection General { get; init; } = null!;
            public IniSection Developer { get; init; } = null!;
            public IniSection WebServer { get; init; } = null!;
            public IniSection AccountControl { get; init; } = null!;
            public IniSection Watcher { get; init; } = null!;
            public IniSection Prompts { get; init; } = null!;

            public static StaticSettingsSnapshot Capture()
            {
                var mgr = AccountManager.Instance ?? new AccountManager();
                return new StaticSettingsSnapshot
                {
                    Instance = mgr,
                    IniSettings = AccountManager.IniSettings,
                    General = AccountManager.General,
                    Developer = AccountManager.Developer,
                    WebServer = AccountManager.WebServer,
                    AccountControl = AccountManager.AccountControl,
                    Watcher = AccountManager.Watcher,
                    Prompts = AccountManager.Prompts
                };
            }

            public void Restore()
            {
                AccountManager.IniSettings = IniSettings;
                AccountManager.General = General;
                AccountManager.Developer = Developer;
                AccountManager.WebServer = WebServer;
                AccountManager.AccountControl = AccountControl;
                AccountManager.Watcher = Watcher;
                AccountManager.Prompts = Prompts;
                AccountManager.Instance = Instance;
                if (Instance != null)
                {
                    AccountManager.EnsureBackendInitialized();
                }
            }
        }

        [Fact]
        public void EnsureBackendInitialized_WhenInstanceIsNull_CreatesAndInitializesInstance()
        {
            var snap = StaticSettingsSnapshot.Capture();
            bool prevSuppress = AccountManager.SuppressExitOnShutdown;
            AccountManager? created = null;

            try
            {
                AccountManager.SuppressExitOnShutdown = true;
                AccountManager.Instance = null;

                AccountManager.EnsureBackendInitialized();
                created = AccountManager.Instance;
                Assert.NotNull(created);
                Assert.False(created!.IsDisposed);
            }
            finally
            {
                if (created != null && !ReferenceEquals(created, snap.Instance))
                {
                    created.Dispose();
                }
                snap.Restore();
                AccountManager.SuppressExitOnShutdown = prevSuppress;
            }
        }

        [Fact]
        public async Task InitializeBackendServices_CoversMissingIniKeysAndValidWebServerPort()
        {
            var snap = StaticSettingsSnapshot.Capture();
            bool prevSuppress = AccountManager.SuppressExitOnShutdown;
            string prevPlaceId = AccountManager.CurrentPlaceId;

            AccountManager? temp1 = null;
            AccountManager? temp2 = null;
            try
            {
                AccountManager.SuppressExitOnShutdown = true;

                // 1. Missing SavedPlaceId + missing WebServerPort (with WebServer disabled to avoid port 7963 collision)
                temp1 = new AccountManager();
                AccountManager.General.RemoveProperty("SavedPlaceId");
                AccountManager.WebServer.RemoveProperty("WebServerPort");
                AccountManager.Developer.Set("EnableWebServer", "false");
                AccountManager.General.Set("ShowPresence", "false");
                AccountManager.General.Set("AutoCookieRefresh", "false");
                temp1.InitializeBackendServices();
                Assert.Equal("5315046213", AccountManager.CurrentPlaceId);

                // 2. Valid numeric WebServerPort + missing WebServerPort fallback when EnableWebServer=true
                int freePort;
                using (var probe = new TcpListener(IPAddress.Loopback, 0))
                {
                    probe.Start();
                    freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
                }

                temp2 = new AccountManager();
                AccountManager.Developer.Set("EnableWebServer", "true");
                AccountManager.WebServer.Set("WebServerPort", freePort.ToString());
                AccountManager.WebServer.Set("AllowExternalConnections", "false");
                temp2.InitializeBackendServices();
                await Task.Delay(50);
                await temp2.StopWebServerAsync();
                Assert.False(temp2.IsDisposed);

                // 3. Also exercise missing WebServerPort with EnableWebServer=true
                using var temp3 = new AccountManager();
                AccountManager.Developer.Set("EnableWebServer", "true");
                AccountManager.WebServer.RemoveProperty("WebServerPort");
                temp3.InitializeBackendServices();
                await Task.Delay(50);
                await temp3.StopWebServerAsync();
            }
            finally
            {
                temp1?.Dispose();
                temp2?.Dispose();
                AccountManager.CurrentPlaceId = prevPlaceId;
                snap.Restore();
                AccountManager.SuppressExitOnShutdown = prevSuppress;
            }
        }

        [Fact]
        public void InitializePostStartup_CoversMissingPropertiesAndDefaultIntervals()
        {
            var snap = StaticSettingsSnapshot.Capture();
            bool prevSuppress = AccountManager.SuppressExitOnShutdown;

            AccountManager? tempMgr = null;
            try
            {
                AccountManager.SuppressExitOnShutdown = true;
                tempMgr = new AccountManager();

                // Pass A: all optional General keys removed -> exercises Exists(...) == false arms
                AccountManager.General.RemoveProperty("AutoCloseRobloxOnLaunch");
                AccountManager.General.Set("EnableMultiRbx", "false");
                AccountManager.General.RemoveProperty("EnableAntiAfk");
                AccountManager.General.RemoveProperty("AutoTrimMemory");
                AccountManager.General.Set(AppSettingsKeys.General.AggressiveRamTrimming, "false");
                AccountManager.General.RemoveProperty(AppSettingsKeys.General.AutoMuteBackgroundClients);
                AccountManager.General.RemoveProperty(AppSettingsKeys.General.MaxTotalRobloxCpuPercent);
                AccountManager.General.Set("UseEmbeddedBrowser", "true");
                AccountManager.AccountControl.Set("StartOnLaunch", "false");

                tempMgr.InitializePostStartup();
                Assert.False(AccountManager.General.Exists("AutoCloseRobloxOnLaunch"));

                // Pass B: EnableAntiAfk=true without AntiAfkIntervalMinutes, AggressiveRamTrimming=true without AutoTrimIntervalMinutes, cpuCap=0
                AccountManager.General.Set("EnableAntiAfk", "true");
                AccountManager.General.RemoveProperty("AntiAfkIntervalMinutes");
                AccountManager.General.RemoveProperty("AutoTrimMemory");
                AccountManager.General.Set(AppSettingsKeys.General.AggressiveRamTrimming, "true");
                AccountManager.General.RemoveProperty("AutoTrimIntervalMinutes");
                AccountManager.General.Set(AppSettingsKeys.General.MaxTotalRobloxCpuPercent, "0");

                tempMgr.InitializePostStartup();
                Assert.Equal(0, AccountManager.General.Get<int>(AppSettingsKeys.General.MaxTotalRobloxCpuPercent, -1));
            }
            finally
            {
                AntiAfkService.Stop();
                MemoryOptimizer.ConfigureAutoTrim(false);
                RobloxAudioService.ConfigureSmartMute(false);
                tempMgr?.Dispose();
                snap.Restore();
                AccountManager.SuppressExitOnShutdown = prevSuppress;
            }
        }

        [Fact]
        public async Task OfferResumeReloginQueue_And_RestartWebServerCatch_CoverRemainingBranches()
        {
            var snap = StaticSettingsSnapshot.Capture();
            var prevDialogs = AppServices.DialogService;
            var dialogSub = Substitute.For<IDialogService>();
            AppServices.DialogService = dialogSub;
            bool prevSuppress = AccountManager.SuppressExitOnShutdown;
            int prevDelay = AccountManager.OfferResumeDelayMilliseconds;
            AccountManager.OfferResumeDelayMilliseconds = 1;
            AccountManager.SuppressExitOnShutdown = true;

            AccountManager? tempMgr = null;
            try
            {
                tempMgr = new AccountManager();

                // 1. Empty ReloginQueueStore -> hits pendingIds.Count == 0 early return
                ReloginQueueStore.Clear();
                await tempMgr.OfferResumeReloginQueue();
                Assert.Empty(ReloginQueueStore.Load());

                // 2. Non-empty ReloginQueueStore where candidates are either Valid (with non-empty token) or IsBannedOrLocked -> pending.Count == 0 clears store
                var validAcc = new Account
                {
                    Username = "AlreadyValid",
                    UserID = 9101,
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('A', 120),
                    Valid = true
                };
                var bannedAcc = new Account { Username = "BannedUser", UserID = 9102, Valid = false, AccountStatusIssue = "Banned" };
                AccountManager.ReplaceAccountsList(new List<Account> { validAcc, bannedAcc });
                ReloginQueueStore.Save(new long[] { 9101, 9102, 9103 });

                await tempMgr.OfferResumeReloginQueue();
                Assert.Empty(ReloginQueueStore.Load());

                // 3. RestartWebServerAsync with invalid port -> throws and hits catch cleanly
                await tempMgr.RestartWebServerAsync(-1, allowExternal: false);
                Assert.False(tempMgr.IsDisposed);

                // 4. Start on a free port and let ShutdownBackend stop non-null AltManagerWS
                int freePort;
                using (var probe = new TcpListener(IPAddress.Loopback, 0))
                {
                    probe.Start();
                    freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
                }
                await tempMgr.RestartWebServerAsync(freePort, allowExternal: false);
                tempMgr.Dispose();
                await tempMgr.StopWebServerAsync();
                Assert.True(tempMgr.IsDisposed);
            }
            finally
            {
                tempMgr?.Dispose();
                AppServices.DialogService = prevDialogs;
                ReloginQueueStore.Clear();
                AccountManager.ReplaceAccountsList(new List<Account>());
                AccountManager.OfferResumeDelayMilliseconds = prevDelay;
                snap.Restore();
                AccountManager.SuppressExitOnShutdown = prevSuppress;
            }
        }
    }
}
