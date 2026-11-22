#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P0LaunchAndReconnectRegressionTests
    {
        [Fact]
        public void IsEnabled_UsesCanonicalAutoReconnectKey_WithLegacyFallbackAndSettingsViewModelSync()
        {
            var ini = new IniFile();
            var general = ini["General"];
            using var settingsScope = AppServices.Override<ISettingsService>(general);
            using var svc = new AutoReconnectService(TimeSpan.FromHours(1));

            // 1. Neither canonical nor legacy key present -> false
            Assert.False(svc.IsEnabled);

            // 2. Legacy EnableAutoReconnect fallback works only when canonical AutoReconnect is absent
            general.Set("EnableAutoReconnect", "false");
            Assert.False(svc.IsEnabled);

            general.Set("EnableAutoReconnect", "true");
            Assert.True(svc.IsEnabled);

            // 3. Canonical AutoReconnect takes precedence over legacy key
            general.Set(AppSettingsKeys.General.AutoReconnect, "false");
            Assert.False(svc.IsEnabled);

            general.Set(AppSettingsKeys.General.AutoReconnect, "true");
            Assert.True(svc.IsEnabled);

            // 4. Setter writes canonical AutoReconnect key (matching SettingsViewModel.Advanced)
            svc.IsEnabled = false;
            Assert.Equal("false", general.Get<string>(AppSettingsKeys.General.AutoReconnect));
            Assert.False(svc.IsEnabled);

            svc.IsEnabled = true;
            Assert.Equal("true", general.Get<string>(AppSettingsKeys.General.AutoReconnect));
            Assert.True(svc.IsEnabled);

            // 5. SettingsViewModel.Load reads the persisted canonical key consistently
            var vm = new SettingsViewModel(
                new AppSettings(),
                general,
                Substitute.For<ISettingsSideEffects>(),
                Substitute.For<IDialogService>(),
                TimeSpan.FromMilliseconds(10),
                Substitute.For<IAppLogger>());
            vm.Load();
            Assert.True(vm.EnableAutoReconnect);

            try
            {
                using var bridgeScope = AppServices.Override<IHostUiBridgeService>(new HostUiBridgeService());
                vm.EnableAutoReconnect = false;
                vm.Save();
                Assert.Equal("false", general.Get<string>(AppSettingsKeys.General.AutoReconnect));
                Assert.False(AutoReconnectService.Instance.IsEnabled);
            }
            finally
            {
                // Restore singleton IsEnabled back to clean true for subsequent tests
                AutoReconnectService.Instance.IsEnabled = true;
            }
        }

        [Fact]
        public void ShouldResetRetryCount_ReturnsTrueOnlyForHealthyRunningSessionsPastWindow()
        {
            DateTime now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
            TimeSpan window = TimeSpan.FromMinutes(10);

            AutoReconnectService.ReconnectSession MakeHealthySession() => new()
            {
                Account = new Account { Username = "HealthyStableUser", UserID = 4001 },
                PlaceId = 1818,
                ProcessId = 12345,
                RetryCount = 2,
                LaunchTimeUtc = now.AddMinutes(-15),
                LastDisconnectUtc = null,
                IsActive = true,
                IsSuppressed = false,
                NeedsRetry = false,
                ObservedProcessExited = false
            };

            Assert.False(AutoReconnectService.ShouldResetRetryCount(null, now, window));
            Assert.True(AutoReconnectService.ShouldResetRetryCount(MakeHealthySession(), now, window));

            var inactive = MakeHealthySession();
            inactive.IsActive = false;
            Assert.False(AutoReconnectService.ShouldResetRetryCount(inactive, now, window));

            var suppressed = MakeHealthySession();
            suppressed.IsSuppressed = true;
            Assert.False(AutoReconnectService.ShouldResetRetryCount(suppressed, now, window));

            var noPid = MakeHealthySession();
            noPid.ProcessId = 0;
            Assert.False(AutoReconnectService.ShouldResetRetryCount(noPid, now, window));

            var needsRetry = MakeHealthySession();
            needsRetry.NeedsRetry = true;
            Assert.False(AutoReconnectService.ShouldResetRetryCount(needsRetry, now, window));

            var exited = MakeHealthySession();
            exited.ObservedProcessExited = true;
            Assert.False(AutoReconnectService.ShouldResetRetryCount(exited, now, window));

            var disconnected = MakeHealthySession();
            disconnected.LastDisconnectUtc = now.AddMinutes(-1);
            Assert.False(AutoReconnectService.ShouldResetRetryCount(disconnected, now, window));

            var recent = MakeHealthySession();
            recent.LaunchTimeUtc = now.AddMinutes(-5);
            Assert.False(AutoReconnectService.ShouldResetRetryCount(recent, now, window));
        }

        [Fact]
        public void CheckSessionsCallback_DoesNotResetRetryCount_ForFailedRetryOrExitedSessions()
        {
            var ini = new IniFile();
            var general = ini["General"];
            general.Set(AppSettingsKeys.General.AutoReconnect, "true");
            using var settingsScope = AppServices.Override<ISettingsService>(general);

            using var svc = new AutoReconnectService(TimeSpan.FromHours(1))
            {
                MaxRetries = 5,
                StableResetWindow = TimeSpan.FromMinutes(10),
                TestReconnectDelayMsOverride = 60_000,
                TestJoinServerOverride = _ => Task.FromResult("Failed")
            };

            DateTime oldLaunch = DateTime.UtcNow.AddMinutes(-20);

            // Case 1: Session waiting to retry after failed reconnect (ProcessId == 0, NeedsRetry == true)
            var retryAcc = new Account { Username = "PendingRetryUser", UserID = 3001 };
            svc.RegisterSession(retryAcc, 1818, processId: 0, nowUtcOverride: oldLaunch);
            Assert.True(svc.TryGetSession("PendingRetryUser", out var retrySession));
            retrySession!.RetryCount = 2;
            retrySession.NeedsRetry = true;
            retrySession.LastDisconnectUtc = null;

            svc.CheckSessionsCallback(null);

            // RetryCount must NOT be reset to 0; HandleProcessExit increments it from 2 to 3.
            Assert.Equal(3, retrySession.RetryCount);

            // Case 2: Session whose process already exited (ObservedProcessExited == true)
            var exitedAcc = new Account { Username = "ExitedAfterWindowUser", UserID = 3002 };
            svc.RegisterSession(exitedAcc, 1818, processId: 777771, nowUtcOverride: oldLaunch);
            Assert.True(svc.TryGetSession("ExitedAfterWindowUser", out var exitedSession));
            exitedSession!.RetryCount = 2;
            exitedSession.ObservedProcessExited = true;
            exitedSession.ObservedExitCode = 1;

            svc.CheckSessionsCallback(null);

            // RetryCount must NOT be reset to 0; HandleProcessExit increments it from 2 to 3.
            Assert.Equal(3, exitedSession.RetryCount);
        }

        [Fact]
        public async Task RecordObservedProcessExit_PopulatesExitCodeBeforePublishingExitedFlag_UnderConcurrency()
        {
            var ini = new IniFile();
            var general = ini["General"];
            general.Set(AppSettingsKeys.General.AutoReconnect, "true");
            using var settingsScope = AppServices.Override<ISettingsService>(general);

            using var svc = new AutoReconnectService(TimeSpan.FromHours(1));
            svc.RecordObservedProcessExit(null, () => 0);

            // 1. Verify resolver executes BEFORE ObservedProcessExited flips to true
            var session = new AutoReconnectService.ReconnectSession
            {
                Account = new Account { Username = "ExitOrderUser", UserID = 5001 },
                PlaceId = 1818,
                ProcessId = 11223,
                LaunchTimeUtc = DateTime.UtcNow.AddMinutes(-2),
                IsActive = true
            };

            bool exitedSeenInsideResolver = true;
            svc.RecordObservedProcessExit(session, () =>
            {
                exitedSeenInsideResolver = session.ObservedProcessExited;
                return 0;
            });

            Assert.False(exitedSeenInsideResolver);
            Assert.Equal(0, session.ObservedExitCode);
            Assert.True(session.ObservedProcessExited);

            // 2. Concurrent reader must never observe ObservedProcessExited == true with ObservedExitCode == null
            int tornReads = 0;
            for (int i = 0; i < 100; i++)
            {
                var raceSession = new AutoReconnectService.ReconnectSession
                {
                    Account = new Account { Username = "RaceUser", UserID = 5002 },
                    PlaceId = 1818,
                    ProcessId = 22334,
                    LaunchTimeUtc = DateTime.UtcNow.AddMinutes(-2),
                    IsActive = true
                };

                using var startGate = new ManualResetEventSlim(false);
                var readerTask = Task.Run(() =>
                {
                    startGate.Wait();
                    for (int spin = 0; spin < 2000; spin++)
                    {
                        var (exited, code) = svc.ReadObservedProcessExitState(raceSession);
                        if (exited && code == null)
                        {
                            Interlocked.Increment(ref tornReads);
                            break;
                        }
                        if (exited)
                            break;
                    }
                });

                startGate.Set();
                svc.RecordObservedProcessExit(raceSession, () => 0);
                await readerTask;
            }

            Assert.Equal(0, tornReads);
        }

        [Fact]
        public void CompleteObservedProcessExit_And_OnJobProcessExited_ClassifyCleanAndAbnormalExitsAccurately()
        {
            var ini = new IniFile();
            var general = ini["General"];
            general.Set(AppSettingsKeys.General.AutoReconnect, "true");
            using var settingsScope = AppServices.Override<ISettingsService>(general);

            using var svc = new AutoReconnectService(TimeSpan.FromHours(1))
            {
                MaxRetries = 5,
                LaunchGracePeriod = TimeSpan.FromSeconds(15),
                TestReconnectDelayMsOverride = 60_000,
                TestJoinServerOverride = _ => Task.FromResult("Success")
            };

            DateTime pastGrace = DateTime.UtcNow.AddMinutes(-2);

            // 1. Clean exit (0) closes cleanly instead of triggering reconnect
            var cleanAcc = new Account { Username = "CleanExitUser", UserID = 6001 };
            svc.RegisterSession(cleanAcc, 1818, processId: 0, nowUtcOverride: pastGrace);
            Assert.True(svc.TryGetSession("CleanExitUser", out var cleanSession));

            svc.CompleteObservedProcessExit(null, () => 0);
            svc.CompleteObservedProcessExit(cleanSession, () => 0);
            Assert.True(cleanSession!.IsSuppressed);
            Assert.False(cleanSession.IsActive);
            Assert.False(svc.TryGetSession("CleanExitUser", out _));

            // 2. Non-zero exit (1) schedules reconnect
            var crashAcc = new Account { Username = "CrashExitUser", UserID = 6002 };
            svc.RegisterSession(crashAcc, 1818, processId: 0, nowUtcOverride: pastGrace);
            Assert.True(svc.TryGetSession("CrashExitUser", out var crashSession));

            svc.CompleteObservedProcessExit(crashSession, () => 1);
            Assert.Equal(1, crashSession!.RetryCount);
            Assert.False(crashSession.IsActive);

            // 3. OnJobProcessExited sets exit code (-1 abnormal, 0 clean fallback) before marking exited.
            // Register within LaunchGracePeriod so any background CheckSessionsCallback queued by
            // OnJobProcessExited evaluates ReconnectDecision.None instead of racing with HandleProcessExit.
            svc.LaunchGracePeriod = TimeSpan.FromMinutes(2);
            svc.OnJobProcessExited(0, abnormalExit: true);

            DateTime withinGrace = DateTime.UtcNow;
            var jobCrashAcc = new Account { Username = "JobAbnormalUser", UserID = 6003 };
            svc.RegisterSession(jobCrashAcc, 1818, processId: 888881, nowUtcOverride: withinGrace);
            Assert.True(svc.TryGetSession("JobAbnormalUser", out var jobCrashSession));
            svc.OnJobProcessExited(888881, abnormalExit: true);
            Assert.Equal(-1, jobCrashSession!.ObservedExitCode);
            Assert.True(jobCrashSession.ObservedProcessExited);

            var jobCleanAcc = new Account { Username = "JobCleanUser", UserID = 6004 };
            svc.RegisterSession(jobCleanAcc, 1818, processId: 888882, nowUtcOverride: withinGrace);
            Assert.True(svc.TryGetSession("JobCleanUser", out var jobCleanSession));
            svc.OnJobProcessExited(888882, abnormalExit: false);
            Assert.Equal(0, jobCleanSession!.ObservedExitCode);
            Assert.True(jobCleanSession.ObservedProcessExited);
        }

        [Fact]
        public async Task ObserveProcessExitImmediateAsync_WithRealExitedProcess_RecordsExitCodeZeroAndClosesCleanly()
        {
            var ini = new IniFile();
            var general = ini["General"];
            general.Set(AppSettingsKeys.General.AutoReconnect, "true");
            using var settingsScope = AppServices.Override<ISettingsService>(general);

            using var svc = new AutoReconnectService(TimeSpan.FromHours(1))
            {
                LaunchGracePeriod = TimeSpan.Zero
            };

            string cmdPath = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = cmdPath,
                Arguments = "/c \"more >nul & exit /b 0\"",
                RedirectStandardInput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            Assert.NotNull(proc);

            try
            {
                Assert.False(proc!.HasExited);

                var acc = new Account { Username = "RealProcCleanUser", UserID = 6005 };
                svc.RegisterSession(acc, 1818, processId: proc.Id, nowUtcOverride: DateTime.UtcNow.AddMinutes(-1));

                // Hold stdin open until RegisterSession has synchronously attached ObserveProcessExitImmediateAsync.
                proc.StandardInput.Close();
                await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, proc.ExitCode);

                bool closedCleanly = false;
                for (int i = 0; i < 80; i++)
                {
                    if (!svc.TryGetSession("RealProcCleanUser", out _))
                    {
                        closedCleanly = true;
                        break;
                    }
                    await Task.Delay(25);
                }

                Assert.True(closedCleanly);
                Assert.Equal(0, svc.TotalReconnections);
            }
            finally
            {
                try
                {
                    if (!proc!.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process already exited before cleanup.
                }
            }
        }

        [Fact]
        public void CloseAccountRobloxProcessCore_InvokesBeforeTerminateStrictlyBeforeKill()
        {
            var events = new List<string>();
            var pidAcc = new Account { Username = "PidPathUser", LastLaunchedProcessId = 1001, BrowserTrackerID = "111" };
            bool pidClosed = RobloxProcessService.CloseAccountRobloxProcessCore(
                pidAcc,
                pid => pid == 1001,
                _ => "RobloxPlayerBeta.exe -b 111",
                () => new List<int>(),
                pid => events.Add($"kill:{pid}"),
                acc => events.Add($"before:{acc.Username}"));
            Assert.True(pidClosed);
            Assert.Equal(0, pidAcc.LastLaunchedProcessId);
            Assert.Equal(new[] { "before:PidPathUser", "kill:1001" }, events);

            events.Clear();
            var trackerAcc = new Account { Username = "TrackerPathUser", LastLaunchedProcessId = 0, BrowserTrackerID = "222" };
            bool trackerClosed = RobloxProcessService.CloseAccountRobloxProcessCore(
                trackerAcc,
                _ => false,
                pid => pid == 2002 ? "RobloxPlayerBeta.exe -b 222" : null,
                () => new List<int> { 2002 },
                pid => events.Add($"kill:{pid}"),
                acc => events.Add($"before:{acc.Username}"));
            Assert.True(trackerClosed);
            Assert.Equal(new[] { "before:TrackerPathUser", "kill:2002" }, events);

            events.Clear();
            var missAcc = new Account { Username = "MissUser", LastLaunchedProcessId = 9999, BrowserTrackerID = "333" };
            bool missClosed = RobloxProcessService.CloseAccountRobloxProcessCore(
                missAcc,
                _ => false,
                _ => null,
                () => new List<int> { 3003 },
                pid => events.Add($"kill:{pid}"),
                acc => events.Add($"before:{acc.Username}"));
            Assert.False(missClosed);
            Assert.Empty(events);
        }

        [Fact]
        public async Task CloseAccountRobloxProcesses_SuppressesAutoReconnectBeforeKillingProcesses()
        {
            var ini = new IniFile();
            var general = ini["General"];
            general.Set(AppSettingsKeys.General.AutoReconnect, "true");
            using var settingsScope = AppServices.Override<ISettingsService>(general);

            var svc = AutoReconnectService.Instance;
            bool prevEnabled = svc.IsEnabled;
            var prevLaunchGrace = svc.LaunchGracePeriod;
            var prevDelayOverride = svc.TestReconnectDelayMsOverride;
            var prevJoinOverride = svc.TestJoinServerOverride;
            var prevPidsOverride = RobloxProcessService.TestGetRunningRobloxPidsOverride;
            var prevCmdOverride = ProcessExtensions.TestCommandLineOverride;

            int reconnectAttempts = 0;
            Action<AutoReconnectService.ReconnectSession, int> onAttempt = (_, _) => Interlocked.Increment(ref reconnectAttempts);

            string cmdPath = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            static Process StartHoldProcess(string exe) => Process.Start(new ProcessStartInfo
            { FileName = exe, Arguments = "/c \"more >nul\"", RedirectStandardInput = true, CreateNoWindow = true, UseShellExecute = false })!;

            using var proc1 = StartHoldProcess(cmdPath);
            using var proc2 = StartHoldProcess(cmdPath);

            try
            {
                svc.Clear();
                svc.IsEnabled = true;
                svc.LaunchGracePeriod = TimeSpan.Zero;
                svc.TestReconnectDelayMsOverride = 10;
                svc.TestJoinServerOverride = _ => Task.FromResult("Success");
                svc.ReconnectAttempted += onAttempt;

                var acc1 = new Account { Username = "BatchSuppress1", UserID = 7101, BrowserTrackerID = "777001" };
                var acc2 = new Account { Username = "BatchSuppress2", UserID = 7102, BrowserTrackerID = "777002" };
                DateTime pastGrace = DateTime.UtcNow.AddMinutes(-2);
                svc.RegisterSession(acc1, 1818, processId: proc1.Id, nowUtcOverride: pastGrace);
                svc.RegisterSession(acc2, 1818, processId: proc2.Id, nowUtcOverride: pastGrace);

                RobloxProcessService.TestGetRunningRobloxPidsOverride = () => new[] { proc1.Id, proc2.Id };
                ProcessExtensions.TestCommandLineOverride = pid =>
                    pid == proc1.Id ? "RobloxPlayerBeta.exe -b 777001" :
                    pid == proc2.Id ? "RobloxPlayerBeta.exe -b 777002" : null;

                int closed = RobloxProcessService.CloseAccountRobloxProcesses(new[] { acc1, acc2 });
                await Task.Delay(120);

                Assert.Equal(2, closed);
                Assert.True(svc.TryGetAccountSession(acc1, out var s1) && s1!.IsSuppressed && !s1.IsActive);
                Assert.True(svc.TryGetAccountSession(acc2, out var s2) && s2!.IsSuppressed && !s2.IsActive);
                Assert.Equal(0, reconnectAttempts);
            }
            finally
            {
                svc.ReconnectAttempted -= onAttempt;
                svc.Clear();
                svc.LaunchGracePeriod = prevLaunchGrace;
                svc.TestReconnectDelayMsOverride = prevDelayOverride;
                svc.TestJoinServerOverride = prevJoinOverride;
                svc.IsEnabled = prevEnabled;
                RobloxProcessService.TestGetRunningRobloxPidsOverride = prevPidsOverride;
                ProcessExtensions.TestCommandLineOverride = prevCmdOverride;
                try { if (!proc1.HasExited) proc1.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* Process already exited before cleanup. */ }
                try { if (!proc2.HasExited) proc2.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* Process already exited before cleanup. */ }
            }
        }

        [Fact]
        public void SettingsViewModel_Load_FallsBackToLegacyEnableAutoReconnect_AndPrefersCanonicalAutoReconnect()
        {
            var ini = new IniFile();
            var general = ini["General"];
            var vm = new SettingsViewModel(
                new AppSettings(),
                general,
                Substitute.For<ISettingsSideEffects>(),
                Substitute.For<IDialogService>(),
                TimeSpan.FromMilliseconds(10),
                Substitute.For<IAppLogger>());

            vm.Load();
            Assert.False(vm.EnableAutoReconnect);

            general.Set("EnableAutoReconnect", "true");
            vm.Load();
            Assert.True(vm.EnableAutoReconnect);

            general.Set(AppSettingsKeys.General.AutoReconnect, "false");
            vm.Load();
            Assert.False(vm.EnableAutoReconnect);

            general.Set(AppSettingsKeys.General.AutoReconnect, "true");
            general.Set("EnableAutoReconnect", "false");
            vm.Load();
            Assert.True(vm.EnableAutoReconnect);
        }

        [Fact]
        public void AutoReconnectService_HandleProcessExit_WhenJoinFailsOrThrows_SetsNeedsRetryAndIsActive()
        {
            var ini = new IniFile();
            var general = ini["General"];
            general.Set(AppSettingsKeys.General.AutoReconnect, "true");
            using var settingsScope = AppServices.Override<ISettingsService>(general);

            int callCount = 0;
            using var svc = new AutoReconnectService(TimeSpan.FromHours(1))
            {
                MaxRetries = 5,
                TestReconnectDelayMsOverride = 10,
                TestJoinServerOverride = _ => Interlocked.Increment(ref callCount) == 1
                    ? Task.FromResult("Failed")
                    : Task.FromException<string>(new InvalidOperationException("sim join failure"))
            };

            var accFail = new Account { Username = "RetryFailUser", UserID = 7201 };
            svc.RegisterSession(accFail, 1818, processId: 0, nowUtcOverride: DateTime.UtcNow.AddMinutes(-1));
            Assert.True(svc.TryGetSession("RetryFailUser", out var sFail));
            svc.HandleProcessExit(sFail!);
            Assert.True(SpinWait.SpinUntil(() => sFail!.NeedsRetry && sFail.IsActive, TimeSpan.FromSeconds(3)));

            var accThrow = new Account { Username = "RetryThrowUser", UserID = 7202 };
            svc.RegisterSession(accThrow, 1818, processId: 0, nowUtcOverride: DateTime.UtcNow.AddMinutes(-1));
            Assert.True(svc.TryGetSession("RetryThrowUser", out var sThrow));
            svc.HandleProcessExit(sThrow!);
            Assert.True(SpinWait.SpinUntil(() => sThrow!.NeedsRetry && sThrow.IsActive, TimeSpan.FromSeconds(3)));
            Assert.Equal(2, Volatile.Read(ref callCount));
        }
    }
}
