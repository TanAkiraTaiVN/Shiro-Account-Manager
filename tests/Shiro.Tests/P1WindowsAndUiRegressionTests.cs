#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P1WindowsAndUiRegressionTests : IDisposable
    {
        private readonly bool _origStreamerEnabled = StreamerModeService.IsEnabled;
        private readonly bool _origStreamerAutoDetect = StreamerModeService.AutoDetectEnabled;
        private readonly bool _origSingletonEnabled = RobloxSingletonService.IsEnabled;

        public void Dispose()
        {
            StreamerModeService.IsEnabled = _origStreamerEnabled;
            StreamerModeService.AutoDetectEnabled = _origStreamerAutoDetect;
            AntiAfkService.Stop();
            MemoryOptimizer.ConfigureAutoTrim(false, 3);
            RobloxBackgroundThrottlerService.Instance.IsEnabled = false;
            RobloxSingletonService.ResetForTest(_origSingletonEnabled);
            RobloxJobObjectService.Close();
        }

        [Fact]
        public async Task HighG1_MainWindowStartupPrompts_RunSequentiallyWithoutOverlappingModal()
        {
            bool firstActive = false;
            bool secondStartedDuringFirst = false;
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<string>();

            var runTask = MainWindow.RunStartupPromptsSequentiallyAsync(
                async () =>
                {
                    firstActive = true;
                    order.Add("disclaimer-start");
                    await releaseFirst.Task;
                    order.Add("disclaimer-end");
                    firstActive = false;
                },
                () =>
                {
                    if (firstActive) secondStartedDuringFirst = true;
                    order.Add("vault-prompt");
                    return Task.CompletedTask;
                });

            await Task.Delay(30);
            releaseFirst.SetResult();
            await runTask;

            Assert.False(secondStartedDuringFirst);
            Assert.Equal(new[] { "disclaimer-start", "disclaimer-end", "vault-prompt" }, order);
        }

        [Fact]
        public void HighG5_StreamerModeService_InitializeAppliesSettingAndReadsLiveIsActiveOnOpen()
        {
            var ini = new IniFile();
            var gen = ini["General"];
            gen.Set(AppSettingsKeys.General.StreamerModeEnabled, "true");
            gen.Set(AppSettingsKeys.General.StreamerModeAutoDetect, "false");
            using var scope = AppServices.Override<ISettingsService>(gen);

            bool? eventState = null;
            Action<bool> onState = s => eventState = s;
            StreamerModeService.StateChanged += onState;
            try
            {
                StreamerModeService.InitializeForTest();
                Assert.True(StreamerModeService.IsEnabled);
                Assert.True(StreamerModeService.IsActive);
                Assert.True(eventState);

                // Even if captured as false prior to Window.Opened, Opened must inspect live IsActive
                Assert.True(StreamerModeService.ShouldApplyActiveOnOpened(capturedActive: false));
            }
            finally
            {
                StreamerModeService.StateChanged -= onState;
            }
        }

        [Fact]
        public void HighG6_AntiAfkService_HiddenFailureBacksOff_AndForegroundFallbackRunsWhenEnabled()
        {
            var now = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
            int fgCalls = 0;

            var hiddenFailed = AntiAfkService.ProcessClientPulseForTest(
                hWnd: new IntPtr(101),
                isHidden: true,
                isForegroundWindow: false,
                allowFocusSteal: false,
                nowUtc: now,
                intervalMinutes: 8,
                postPulse: _ => false,
                sendForegroundSpace: _ => { fgCalls++; return true; },
                previousFailures: 0);

            Assert.False(hiddenFailed.Succeeded);
            Assert.Equal(1, hiddenFailed.Failures);
            Assert.True(hiddenFailed.NextDueUtc > now.AddMinutes(1));

            var bgWithFallback = AntiAfkService.ProcessClientPulseForTest(
                hWnd: new IntPtr(102),
                isHidden: false,
                isForegroundWindow: false,
                allowFocusSteal: true,
                nowUtc: now,
                intervalMinutes: 8,
                postPulse: _ => true,
                sendForegroundSpace: _ => { fgCalls++; return true; },
                previousFailures: 0);

            Assert.True(bgWithFallback.Succeeded);
            Assert.Equal(1, fgCalls);
        }

        [Fact]
        public void HighG8_MemoryOptimizer_UsesThrottlerPriority_AndHideRespectsAggressiveTrimSetting()
        {
            RobloxBackgroundThrottlerService.Instance.IsEnabled = true;
            RobloxBackgroundThrottlerService.Instance.TargetBackgroundFps = 10;

            uint bgPrio = MemoryOptimizer.ResolveTrimTargetPriorityClass(isForeground: false);
            Assert.Equal(RobloxProcessOptimizer.IDLE_PRIORITY_CLASS, bgPrio);

            var ini = new IniFile();
            var gen = ini["General"];
            gen.Set(AppSettingsKeys.General.AggressiveRamTrimming, "false");
            using var scope = AppServices.Override<ISettingsService>(gen);
            Assert.False(RobloxWindowService.ShouldTrimOnHide());

            gen.Set(AppSettingsKeys.General.AggressiveRamTrimming, "true");
            Assert.True(RobloxWindowService.ShouldTrimOnHide());
        }

        [Fact]
        public void HighG9_HighG10_HighG11_RobloxProcessAndWatcher_LogSplittingAndDisposalAndKillCondition()
        {
            // HIGH-G11: trailing '\n' must not emit trailing empty element or erase LastLine
            string[] lines = RobloxProcess.SplitCompletedLogLines("DataModel::stop(1a)\n");
            Assert.Equal(new[] { "DataModel::stop(1a)" }, lines);

            var state = new RobloxLogSessionState
            {
                CurrentDataModel = 0x1a,
                LastLine = "2026-11-16T00:00:00Z [FLog::Output] DataModel::stop(1a)"
            };
            var emptyOutcome = RobloxProcess.AdvanceLogSessionState(string.Empty, ref state, verifyDataModel: true, exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.None, emptyOutcome);
            Assert.False(string.IsNullOrEmpty(state.LastLine));

            // HIGH-G10: multicast LogFileRead must continue to subscriber #2 if subscriber #1 throws ObjectDisposedException
            bool secondCalled = false;
            EventHandler<EventArgs> broken = (_, _) => throw new ObjectDisposedException("LogStream");
            EventHandler<EventArgs> healthy = (_, _) => secondCalled = true;
            EventHandler<EventArgs> combined = broken + healthy;
            RobloxProcess.RaiseLogFileReadSafely(combined, null, EventArgs.Empty);
            Assert.True(secondCalled);

            // HIGH-G10: KillProcess permission when IgnoreExistingProcesses == false must allow kill even at LastPosition == 0
            Assert.True(RobloxProcess.ShouldAllowKill(ignoreExistingProcesses: false, startedBeforeWatcher: true, lastPosition: 0));
            Assert.False(RobloxProcess.ShouldAllowKill(ignoreExistingProcesses: true, startedBeforeWatcher: true, lastPosition: 0));

            // HIGH-G9: RobloxProcess implements IDisposable & ClearAndDisposeTrackedProcesses empties list safely
            Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(RobloxProcess)));
            RobloxWatcher.ClearAndDisposeTrackedProcesses();
            Assert.Empty(RobloxWatcher.Instances);
        }

        [Fact]
        public void HighG12_HighG13_JobObjectCloseJoinsThread_AndSingletonFastToggleIsSafe()
        {
            RobloxJobObjectService.EnsureJobHandle();
            RobloxJobObjectService.Close();
            Assert.False(RobloxJobObjectService.IsIocpThreadAliveForTest);

            bool origSingletonEnabled = RobloxSingletonService.IsEnabled;
            int disableStatusEvents = 0;
            Action countStatus = () => disableStatusEvents++;
            RobloxSingletonService.Stop();
            try
            {
                RobloxSingletonService.Apply(true);
                RobloxSingletonService.Apply(false);
                RobloxSingletonService.Apply(true);
                bool heldAfterReenable = RobloxSingletonService.MutexHeld;
                bool accessDeniedAfterReenable = RobloxSingletonService.AccessDenied;
                disableStatusEvents = 0;
                RobloxSingletonService.StatusChanged += countStatus;
                try
                {
                    RobloxSingletonService.Apply(false);
                }
                finally
                {
                    RobloxSingletonService.StatusChanged -= countStatus;
                }
                Assert.True(heldAfterReenable || accessDeniedAfterReenable);
                Assert.False(RobloxSingletonService.MutexHeld);
                Assert.Equal(1, disableStatusEvents);
                Assert.Equal("Multi-Instance Disabled", RobloxSingletonService.StatusText);
            }
            finally
            {
                RobloxSingletonService.ResetForTest(origSingletonEnabled);
            }
        }

        [Fact]
        public void HighG14_CloseAccountRobloxProcessCore_FailsClosedOnMissingOrMismatchedCli_AndClearsPid()
        {
            int killedPid = 0;
            var acc = new Account
            {
                Username = "ReusedPidUser",
                LastLaunchedProcessId = Environment.ProcessId,
                BrowserTrackerID = "expected-tracker-999"
            };

            bool closed = RobloxProcessService.CloseAccountRobloxProcessCore(
                acc,
                isPidAlive: _ => true,
                getCommandLineByPid: _ => null, // CLI unreadable (reused/elevated) -> must FAIL CLOSED
                getRunningPids: () => new List<int>(),
                killPid: pid => killedPid = pid);

            Assert.False(closed);
            Assert.Equal(0, killedPid);
            Assert.Equal(0, acc.LastLaunchedProcessId);
        }

        [Fact]
        public void Medium_WindowsAndViewModel_FixesWorkEndToEnd()
        {
            // ForegroundWindowService must not set WINEVENT_SKIPOWNPROCESS (0x0002)
            Assert.Equal(0u, ForegroundWindowService.WinEventHookFlags);

            // WindowBackdropService must not apply legacy Mica when BackdropType.None on 21H2
            Assert.False(WindowBackdropService.ShouldApplyLegacyMica(WindowBackdropService.BackdropType.None, osBuild: 22000));
            Assert.True(WindowBackdropService.ShouldApplyLegacyMica(WindowBackdropService.BackdropType.MainWindow, osBuild: 22000));

            // Throttler prunes exited PIDs
            var throttler = RobloxBackgroundThrottlerService.Instance;
            throttler.ApplyThrottleState(55501, isForeground: false);
            throttler.PruneExitedProcesses(new[] { 99999 });
            Assert.False(throttler.IsProcessThrottled(55501));

            // WindowArranger does not permanently cache empty tracker on transient CLI read failure
            int cliCalls = 0;
            string t1 = RobloxWindowArrangerService.ResolveTrackerForPidWithCacheForTest(
                77701,
                _ => { cliCalls++; return null; });
            string t2 = RobloxWindowArrangerService.ResolveTrackerForPidWithCacheForTest(
                77701,
                _ => { cliCalls++; return "\"RobloxPlayerBeta.exe\" -b 123456"; });
            Assert.Equal(string.Empty, t1);
            Assert.Equal("123456", t2);
            Assert.Equal(2, cliCalls);

            // MainViewModel.SavePlaceIdToAccountCommand saves SavedPlaceId even when OnSavePlaceIdToAccountRequested is null
            var acc = new Account { Username = "PlaceSaveUser", Valid = true };
            var accVm = new AccountViewModel(acc);
            using var repoScope = AppServices.Override(Substitute.For<IAccountRepository>());
            using var vm = new MainViewModel();
            vm.PlaceId = "1818";
            vm.JobId = "srv-42";
            vm.SavePlaceIdToAccountCommand.Execute(accVm);
            Assert.Equal("1818", acc.GetField("SavedPlaceId"));
        }

        [Fact]
        public void MemoryOptimizer_SoloClient_RestoresFullPerformanceEvenWhenUnfocused()
        {
            Assert.True(MemoryOptimizer.ShouldRestoreFullPerformanceOnSolo(1));
            Assert.False(MemoryOptimizer.ShouldRestoreFullPerformanceOnSolo(2));
        }

        [Fact]
        public void AntiAfkService_Interval18WithMaxJitter_CapsDueAt1140Seconds()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.Equal(now.AddSeconds(1140), AntiAfkService.ComputeNextDueUtc(now, 18, 120));
            Assert.Equal(now.AddSeconds(1080), AntiAfkService.ComputeNextDueUtc(now, 50, 0));
        }

        [Fact]
        public void Win32TrayHelper_IsAllowedTrayClass_RejectsArbitrarySubstringClasses()
        {
            Assert.True(Win32TrayHelper.IsAllowedTrayClass("Shell_TrayWnd"));
            Assert.True(Win32TrayHelper.IsAllowedTrayClass("NotifyIconOverflowWindow"));
            Assert.False(Win32TrayHelper.IsAllowedTrayClass("RobloxAppTrayPreview"));
        }

        [Fact]
        public void ProcessExtensions_TryValidateUnicodeStringSlice_RejectsOddOrOutOfBoundsPointers()
        {
            Assert.False(ProcessExtensions.TryValidateUnicodeStringSlice(0x1000, 64, 5, 0x1010));
            Assert.False(ProcessExtensions.TryValidateUnicodeStringSlice(0x1000, 64, 32, 0x1030));
            Assert.True(ProcessExtensions.TryValidateUnicodeStringSlice(0x1000, 64, 16, 0x1010));
        }

        [Fact]
        public void ForegroundWindowService_StaleSequence_IsRejected()
        {
            Assert.False(ForegroundWindowService.ShouldCommitForegroundSequence(4, 5));
            Assert.True(ForegroundWindowService.ShouldCommitForegroundSequence(5, 5));
        }

        [Fact]
        public void ProcessIdentity_WhenCreationTicksDiffer_DetectsPidReuseAcrossCaches()
        {
            Assert.False(RobloxProcessIdentityHelper.IsCreationMatch(expectedTicks: 1000L, actualTicks: 2000L));
            Assert.True(RobloxProcessIdentityHelper.IsCreationMatch(expectedTicks: 1000L, actualTicks: 1000L));
            Assert.False(RobloxProcessIdentityHelper.IsCreationMatch(expectedTicks: 0L, actualTicks: 0L));

            RobloxWindowArrangerService.ClearTrackerCacheForTest();
            try
            {
                int cliReads = 0;
                string first = RobloxWindowArrangerService.ResolveTrackerForPidWithCacheForTest(
                    88701,
                    _ => { cliReads++; return "\"RobloxPlayerBeta.exe\" -b 111"; },
                    _ => 1000L);
                string hit = RobloxWindowArrangerService.ResolveTrackerForPidWithCacheForTest(
                    88701,
                    _ => { cliReads++; return "\"RobloxPlayerBeta.exe\" -b 999"; },
                    _ => 1000L);
                string recycled = RobloxWindowArrangerService.ResolveTrackerForPidWithCacheForTest(
                    88701,
                    _ => { cliReads++; return "\"RobloxPlayerBeta.exe\" -b 222"; },
                    _ => 2000L);

                Assert.Equal("111", first);
                Assert.Equal("111", hit);
                Assert.Equal("222", recycled);
                Assert.Equal(2, cliReads);
            }
            finally
            {
                RobloxWindowArrangerService.ClearTrackerCacheForTest();
            }
        }

        [Fact]
        public void BuildMainWindowFilter_RejectsToolWindowAndImeUtilityClasses()
        {
            Assert.False(RobloxProcessIdentityHelper.ShouldSelectAsMainWindow(0, 0x00000080u, "WINDOWSCLIENT".AsSpan()));
            Assert.False(RobloxProcessIdentityHelper.ShouldSelectAsMainWindow(0x40000000u, 0, "WINDOWSCLIENT".AsSpan()));
            Assert.False(RobloxProcessIdentityHelper.ShouldSelectAsMainWindow(0, 0, "IME".AsSpan()));
            Assert.False(RobloxProcessIdentityHelper.ShouldSelectAsMainWindow(0, 0, "MSCTFIME UI".AsSpan()));
            Assert.False(RobloxProcessIdentityHelper.ShouldSelectAsMainWindow(0, 0, "Default IME".AsSpan()));
            Assert.False(RobloxProcessIdentityHelper.ShouldSelectAsMainWindow(0, 0, "Chrome_WidgetWin_0".AsSpan()));
            Assert.True(RobloxProcessIdentityHelper.ShouldSelectAsMainWindow(0, 0, "WINDOWSCLIENT".AsSpan()));
        }

        [Fact]
        public void RobloxWatcher_RecycledPid_EvictsSeenAndMetadataCache_AndKillNativeSuppressesReconnect()
        {
            RobloxWatcher.ClearAndDisposeTrackedProcesses();
            var prevPidsOverride = RobloxProcessService.TestGetRunningRobloxPidsOverride;
            var svc = AutoReconnectService.Instance;
            bool prevEnabled = svc.IsEnabled;
            svc.Clear();

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/c \"more >nul\"",
                RedirectStandardInput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            Assert.NotNull(proc);

            try
            {
                RobloxWatcher.SeedProcessCacheForTest(88101, "old-trk", isGameClient: true, creationTicks: 1000L);
                Assert.True(RobloxWatcher.TryGetValidatedMetadataForTest(88101, 1000L, out string sameTracker));
                Assert.Equal("old-trk", sameTracker);
                Assert.False(RobloxWatcher.TryGetValidatedMetadataForTest(88101, 2000L, out _));
                lock (RobloxWatcher.InstanceRegistryLock)
                {
                    Assert.DoesNotContain(88101, RobloxWatcher.Seen);
                }

                svc.IsEnabled = true;
                var acc = new Account { Username = "KillNativeSuppressUser", UserID = 88102 };
                svc.RegisterSession(acc, 1818, processId: proc!.Id, nowUtcOverride: DateTime.UtcNow.AddMinutes(-1));

                RobloxWatcher.KillNative(proc.Id, "test memory kill");
                Assert.True(proc.WaitForExit(3000));
                Assert.True(svc.TryGetAccountSession(acc, out var session));
                Assert.True(session!.IsSuppressed);
                Assert.False(session.IsActive);
            }
            finally
            {
                RobloxProcessService.TestGetRunningRobloxPidsOverride = prevPidsOverride;
                svc.Clear();
                svc.IsEnabled = prevEnabled;
                RobloxWatcher.ClearAndDisposeTrackedProcesses();
                try { if (!proc!.HasExited) proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            }
        }

        [Fact]
        public void RobloxWatcher_ZeroTickRace_ReQueriesAndAbortsIfZero_AndPruneCleansSeenAndCreationTicks()
        {
            RobloxWatcher.ClearAndDisposeTrackedProcesses();
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/c \"more >nul\"",
                RedirectStandardInput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            })!;
            try
            {
                Assert.False(RobloxWatcher.TryRegisterSeenProcess(proc.Id, "trk0", currentCreationTicks: 0, _ => 0));
                Assert.DoesNotContain(proc.Id, RobloxWatcher.Seen);
                Assert.False(RobloxWatcher.HasSeenCreationTicksForTest(proc.Id));
                Assert.Empty(RobloxWatcher.Instances);

                Assert.True(RobloxWatcher.TryRegisterSeenProcess(proc.Id, "trk1", currentCreationTicks: 0));
                Assert.Contains(proc.Id, RobloxWatcher.Seen);
                Assert.True(RobloxWatcher.HasSeenCreationTicksForTest(proc.Id));

                RobloxWatcher.ClearAndDisposeTrackedProcesses();
                RobloxWatcher.SeedSeenWithoutMetadataForTest(999901, 12345L);
                RobloxWatcher.SeedSeenWithoutMetadataForTest(999902, null);
                RobloxWatcher.SeedProcessCacheForTest(999903, "live", isGameClient: true, creationTicks: 54321L);

                lock (RobloxWatcher.InstanceRegistryLock)
                {
                    RobloxWatcher.PruneExitedMetadataUnderLock(new[] { 999903 });
                }

                Assert.DoesNotContain(999901, RobloxWatcher.Seen);
                Assert.DoesNotContain(999902, RobloxWatcher.Seen);
                Assert.False(RobloxWatcher.HasSeenCreationTicksForTest(999901));
                Assert.Contains(999903, RobloxWatcher.Seen);
                Assert.True(RobloxWatcher.HasSeenCreationTicksForTest(999903));
            }
            finally
            {
                RobloxWatcher.ClearAndDisposeTrackedProcesses();
                RobloxJobObjectService.Close();
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            }
        }

        [Fact]
        public void RobloxWindowArrangerService_TryEvictStaleTrackerEntry_PreservesConcurrentUpdate()
        {
            RobloxWindowArrangerService.ClearTrackerCacheForTest();
            try
            {
                int cliCalls = 0;
                Assert.Equal("111", RobloxWindowArrangerService.ResolveTrackerForPidWithCacheForTest(
                    88702, _ => "\"RobloxPlayerBeta.exe\" -b 111", _ => 1000L));
                Assert.Equal("222", RobloxWindowArrangerService.ResolveTrackerForPidWithCacheForTest(
                    88702, _ => "\"RobloxPlayerBeta.exe\" -b 222", _ => 2000L));

                Assert.False(RobloxWindowArrangerService.TryEvictStaleTrackerEntry(88702, ("111", 1000L)));
                string hit = RobloxWindowArrangerService.ResolveTrackerForPidWithCacheForTest(
                    88702, _ => { cliCalls++; return "\"RobloxPlayerBeta.exe\" -b 999"; }, _ => 2000L);
                Assert.Equal("222", hit);
                Assert.Equal(0, cliCalls);
                Assert.True(RobloxWindowArrangerService.TryEvictStaleTrackerEntry(88702, ("222", 2000L)));
            }
            finally
            {
                RobloxWindowArrangerService.ClearTrackerCacheForTest();
            }
        }
    }
}
