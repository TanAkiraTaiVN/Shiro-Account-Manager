#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class PerformanceAndStaleJobIdRegressionTests
    {
        [Fact]
        public void Win32PlatformOptions_PrefersRedirectionSurface_OverWinUiCompositionSpinLoop()
        {
            Win32PlatformOptions opts = Program.CreateWin32PlatformOptions();
            Assert.NotNull(opts.CompositionMode);
            Assert.NotEmpty(opts.CompositionMode);
            Assert.Equal(Win32CompositionMode.RedirectionSurface, opts.CompositionMode[0]);
            Assert.DoesNotContain(Win32CompositionMode.WinUIComposition, opts.CompositionMode);
        }

        [Fact]
        public void MemoryOptimizer_AutoLowerPriority_DefaultsFalse_AndNeverThrottlesSingleClient()
        {
            var mock = Substitute.For<ISettingsService>();
            mock.Get<bool>(AppSettingsKeys.General.AutoLowerProcessPriority, false).Returns(false);

            using (AppServices.Override<ISettingsService>(mock))
            {
                Assert.False(MemoryOptimizer.IsAutoLowerPriorityActive());

                mock.Get<bool>(AppSettingsKeys.General.AutoLowerProcessPriority, false).Returns(true);
                Assert.Equal(OperatingSystem.IsWindows(), MemoryOptimizer.IsAutoLowerPriorityActive());
            }

            Assert.False(MemoryOptimizer.ShouldApplyBackgroundFocusThrottle(0));
            Assert.False(MemoryOptimizer.ShouldApplyBackgroundFocusThrottle(1));
            Assert.True(MemoryOptimizer.ShouldApplyBackgroundFocusThrottle(2));

            if (!OperatingSystem.IsWindows())
                return;

            var prevPidsOverride = RobloxProcessService.TestGetRunningRobloxPidsOverride;
            using var self = Process.GetCurrentProcess();
            var origPrio = self.PriorityClass;
            try
            {
                mock.Get<bool>(AppSettingsKeys.General.AutoLowerProcessPriority, false).Returns(true);
                using var scope = AppServices.Override<ISettingsService>(mock);
                self.PriorityClass = ProcessPriorityClass.Normal;
                MemoryOptimizer.DisableEcoQoS(self.Handle);
                MemoryOptimizer.SetProcessMemoryPriorityNative(self.Handle, isForeground: true);

                // Simulate another app (PID + 99999) stealing focus while only 1 Roblox client is running.
                MemoryOptimizer.EvaluateWithPids(foregroundPid: (uint)(self.Id + 99999), runningPids: new List<int> { self.Id });

                self.Refresh();
                Assert.Equal(ProcessPriorityClass.Normal, self.PriorityClass);
            }
            finally
            {
                RobloxProcessService.TestGetRunningRobloxPidsOverride = prevPidsOverride;
                self.PriorityClass = origPrio;
                MemoryOptimizer.DisableEcoQoS(self.Handle);
                MemoryOptimizer.SetProcessMemoryPriorityNative(self.Handle, isForeground: true);
            }
        }

        [Fact]
        public void SettingsViewModel_AggressiveRamTrim_DefaultsFalse_WhenKeyMissing()
        {
            var ini = new IniFile();
            var vm = new SettingsViewModel(
                new AppSettings(),
                ini["General"],
                Substitute.For<ISettingsSideEffects>(),
                Substitute.For<IDialogService>(),
                TimeSpan.FromMilliseconds(10),
                Substitute.For<IAppLogger>());

            vm.Load();
            Assert.False(vm.AggressiveRamTrim);

            ini["General"].Set(AppSettingsKeys.General.AggressiveRamTrimming, "true");
            vm.Load();
            Assert.True(vm.AggressiveRamTrim);
        }

        [Fact]
        public async Task DiscordRpcService_WhenNoPipeExists_ReturnsFastWithBackoffAndSingleDebugLog()
        {
            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            var prevOverride = DiscordRpcService.TestAvailablePipesOverride;
            DiscordRpcService.TestAvailablePipesOverride = () => Array.Empty<int>();
            try
            {
                using var rpc = new DiscordRpcService(logger, settings);
                var sw = Stopwatch.StartNew();
                bool first = await rpc.TryConnectForTestAsync(CancellationToken.None);
                bool second = await rpc.TryConnectForTestAsync(CancellationToken.None);
                sw.Stop();

                Assert.False(first);
                Assert.False(second);
                Assert.True(sw.ElapsedMilliseconds < 1000);

                logger.Received(1).Debug(Arg.Is<string>(s => s.Contains("No active Discord IPC pipe", StringComparison.Ordinal)));
                logger.DidNotReceive().Debug(Arg.Any<string>(), Arg.Any<Exception>());

                Assert.Equal(30000, DiscordRpcService.ComputeNextReconnectDelayMs(15000));
                Assert.Equal(60000, DiscordRpcService.ComputeNextReconnectDelayMs(30000));
                Assert.Equal(60000, DiscordRpcService.ComputeNextReconnectDelayMs(60000));
            }
            finally
            {
                DiscordRpcService.TestAvailablePipesOverride = prevOverride;
            }
        }

        [Fact]
        public void ServerBrowserStateService_CurrentJobId_IgnoresAndCleansStaleSavedJobId()
        {
            var ini = new IniFile();
            var general = ini["General"];
            general.Set("SavedJobId", "b2f6dddd-2e6a-40f7-8531-679849d0ac32");

            var state = new ServerBrowserStateService(general);
            Assert.Equal(string.Empty, state.CurrentJobId);

            state.CurrentJobId = "live-job-1";
            Assert.Equal("live-job-1", state.CurrentJobId);
            Assert.False(general.Exists("SavedJobId"));

            state.CurrentJobId = string.Empty;
            Assert.Equal(string.Empty, state.CurrentJobId);
        }

        [Fact]
        public void ResolveJoinJobId_RespectsEmptyDashboardBox_AndPreventsCrossPlaceSavedJobLeak()
        {
            var acc = new Account { Username = "User1", UserID = 1 };
            acc.SetField("SavedPlaceId", "111");
            acc.SetField("SavedJobId", "dead-job-guid");
            var accounts = new[] { acc };

            // UI textbox is attached and empty => must NOT fall back to session or account SavedJobId.
            Assert.Equal(string.Empty, AccountManager.ResolveJoinJobId(
                explicitJobId: "",
                dashboardJobId: "",
                placeId: 111,
                targetAccounts: accounts,
                sessionCurrentJobId: "stale-session-job"));

            // UI textbox has explicit value => honor it.
            Assert.Equal("chosen-server", AccountManager.ResolveJoinJobId(
                explicitJobId: "",
                dashboardJobId: " chosen-server ",
                placeId: 111,
                targetAccounts: accounts,
                sessionCurrentJobId: "stale-session-job"));

            // Headless / no UI textbox (null) => use session job if present, else SavedJobId only when placeId matches.
            Assert.Equal("stale-session-job", AccountManager.ResolveJoinJobId("", null, 111, accounts, "stale-session-job"));
            Assert.Equal("dead-job-guid", AccountManager.ResolveJoinJobId("", null, 111, accounts, ""));
            Assert.Equal(string.Empty, AccountManager.ResolveJoinJobId("", null, 222, accounts, ""));
        }

        [Fact]
        public void PatchSettings_WhenUnlockFpsDisabled_PreservesInGameFramerateCap()
        {
            string tempXml = Path.Combine(Path.GetTempPath(), $"Shiro_KeepFps_{Guid.NewGuid():N}.xml");
            var ini = new IniFile();
            var general = ini["General"];
            general.Set("UnlockFPS", "false");
            general.Set("EnablePotatoMode", "false");

            try
            {
                File.WriteAllText(tempXml, @"<roblox><Item class=""UserGameSettings""><Properties><int name=""FramerateCap"">144</int></Properties></Item></roblox>");
                using var scope = AppServices.Override<ISettingsService>(general);
                ClientSettingsPatcher.PatchSettings(settingsPathOverride: tempXml);

                string content = File.ReadAllText(tempXml);
                Assert.Contains(@"<int name=""FramerateCap"">144</int>", content);
            }
            finally
            {
                if (File.Exists(tempXml))
                    File.Delete(tempXml);
            }
        }
    }
}
