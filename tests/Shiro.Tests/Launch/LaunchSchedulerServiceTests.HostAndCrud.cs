#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public sealed partial class LaunchSchedulerServiceTests
    {
        [Fact]
        public void CrudOperations_AndGlobalToggle_PersistStateAndRaiseEvents()
        {
            Account a1 = MakeAccount("CrudUser", 1301);
            var (svc, store, _, _, exec, _) = CreateHarness(new DateTimeOffset(2026, 11, 24, 7, 0, 0, TimeSpan.Zero), false, a1);
            using (svc)
            {
                int schedulesChanged = 0;
                int historyChanged = 0;
                int stateChanged = 0;
                svc.SchedulesChanged += () => schedulesChanged++;
                svc.HistoryChanged += () => historyChanged++;
                svc.StateChanged += () => stateChanged++;

                LaunchSchedule invalid = new() { Name = "", StartTime = "bad" };
                ScheduleValidationResult invalidRes = svc.UpsertSchedule(invalid);
                Assert.False(invalidRes.IsValid);
                Assert.Equal(0, schedulesChanged);

                LaunchSchedule valid = MakeSchedule("crud-1", "Morning", "08:00", "12:00", a1);
                Assert.True(svc.UpsertSchedule(valid).IsValid);
                Assert.Equal(1, schedulesChanged);
                Assert.Single(store.Load().Schedules);

                Assert.True(svc.SetScheduleEnabled("crud-1", false));
                Assert.False(Assert.Single(svc.GetSchedules()).IsEnabled);

                svc.IsGlobalEnabled = false;
                Assert.False(svc.IsGlobalEnabled);
                Assert.Equal(1, stateChanged);

                Assert.True(svc.DeleteSchedule("crud-1", out LaunchSchedule? removed));
                Assert.NotNull(removed);
                Assert.Empty(svc.GetSchedules());
                Assert.True(svc.RestoreSchedule(removed!));
                Assert.Single(svc.GetSchedules());

                svc.Start();
                Assert.True(svc.IsRunning);
                svc.StopForShutdown();
                Assert.False(svc.IsRunning);
                Assert.Equal(0, historyChanged);
                Assert.Empty(exec.ServerLaunches);
            }
        }

        [Fact]
        public async Task ManualRunNow_ExecutesEvenWhenPaused_AndClearHistoryPersists()
        {
            Account a1 = MakeAccount("PausedUser", 1401);
            var (svc, store, _, _, exec, _) = CreateHarness(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), false, a1);
            using (svc)
            {
                svc.IsGlobalEnabled = false;
                LaunchSchedule pausedSched = MakeSchedule("p-1", "Paused", "08:00", "12:00", a1);
                pausedSched.IsEnabled = false;
                pausedSched.JobId = "VIP:vip-secret-code";
                Assert.True(svc.UpsertSchedule(pausedSched).IsValid);

                ScheduleRunEntry? entry = await svc.RunScheduleNowAsync("p-1");
                Assert.NotNull(entry);
                Assert.Equal(ScheduleRunOutcome.Success, entry!.Outcome);
                var launch = Assert.Single(exec.ServerLaunches);
                Assert.True(launch.IsVip);
                Assert.Equal("vip-secret-code", launch.JobId);

                Assert.Single(svc.GetHistory());
                svc.ClearHistory();
                Assert.Empty(svc.GetHistory());
                Assert.Empty(store.Load().History);
            }
        }

        [Fact]
        public async Task HostActionExecutor_LaunchesWithoutGenericToast_AndWebhookRespectsStreamerMode()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            var prevStreamer = StreamerModeService.IsEnabled;
            AppDispatcher.CustomDispatcher = a => a();
            ToastService.ClearAll();

            var settings = new IniFile()["General"];
            using var settingsScope = AppServices.Override<ISettingsService>(settings);
            var alerts = new List<WebhookAlert>();
            void OnAlert(WebhookAlert alert) => alerts.Add(alert);
            WebhookNotificationService.AlertDispatched += OnAlert;

            try
            {
                StreamerModeService.IsEnabled = true;

                Account a1 = MakeAccount("SecretPlayerOne", 1501);
                Account a2 = MakeAccount("SecretPlayerTwo", 1502);
                var coord = new FakeLaunchCoordinator();
                var bridge = Substitute.For<IHostUiBridgeService>();
                int multiCalls = 0;

                var hostExec = new SchedulerHostActionExecutorService(
                    getCoordinator: () => coord,
                    getJoinDelaySeconds: () => 4,
                    getAsyncJoin: () => false,
                    ensureMultiRoblox: () => { multiCalls++; return true; },
                    getLifetimeToken: () => CancellationToken.None,
                    hostUiBridge: bridge,
                    settings: settings,
                    logger: Substitute.For<IAppLogger>());

                Assert.True(hostExec.TryEnsureMultiRoblox(1));
                Assert.Equal(0, multiCalls);
                Assert.True(hostExec.TryEnsureMultiRoblox(2));
                Assert.Equal(1, multiCalls);

                var homeRes = await hostExec.LaunchHomeAsync(new[] { a1, a2 }, CancellationToken.None);
                Assert.Equal(2, homeRes.Succeeded);
                Assert.Equal(2, homeRes.Attempted);
                Assert.False(homeRes.Cancelled);
                Assert.True(coord.CapturedBatches[0].All(r => r.OpenHome));
                Assert.Empty(ToastService.ActiveToasts);

                LaunchSchedule sched = MakeSchedule("wh-1", "SecretShift", "08:00", "12:00", a1);
                sched.JobId = "private-job-id-leak-check";
                sched.SendWebhookNotification = true;

                var runEntry = new ScheduleRunEntry
                {
                    ScheduleId = sched.Id,
                    ScheduleName = sched.Name,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Phase = ScheduleRunPhase.Open,
                    Outcome = ScheduleRunOutcome.Success,
                    TargetCount = 1,
                    SucceededCount = 1,
                    SkippedCount = 0,
                    Summary = "Opened 1/1 account(s)"
                };

                hostExec.NotifyWebhook(sched, runEntry);
                WebhookAlert dispatched = Assert.Single(alerts);
                Assert.Equal(WebhookEventType.Info, dispatched.EventType);
                Assert.DoesNotContain("SecretPlayerOne", dispatched.AccountUsername ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("private-job-id-leak-check", dispatched.Description ?? string.Empty, StringComparison.Ordinal);

                hostExec.NotifyLiveProcessStatusChanged();
                bridge.Received(1).NotifyLiveProcessStatusChanged();
            }
            finally
            {
                WebhookNotificationService.AlertDispatched -= OnAlert;
                StreamerModeService.IsEnabled = prevStreamer;
                ToastService.ClearAll();
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public async Task TryAcquireExecutionGate_WhenCancelledOrDisposedWhileWaiting_ReleasesRefCountAndDisposesGate()
        {
            Account a1 = MakeAccount("GateUser", 1601);
            var (svc, _, _, _, exec, _) = CreateHarness(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), false, a1);
            Assert.True(svc.UpsertSchedule(MakeSchedule("gate-1", "GateTest", "08:00", "12:00", a1)).IsValid);

            var enteredLaunch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLaunch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            exec.BeforeLaunchServerAsync = async _ =>
            {
                enteredLaunch.TrySetResult(true);
                await releaseLaunch.Task.ConfigureAwait(false);
            };

            FieldInfo activeUsersField = typeof(LaunchSchedulerService).GetField("_activeGateUsers", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo gateDisposedField = typeof(LaunchSchedulerService).GetField("_gateDisposed", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo gateField = typeof(LaunchSchedulerService).GetField("_executionGate", BindingFlags.Instance | BindingFlags.NonPublic)!;

            Task<ScheduleRunEntry?> run1 = svc.RunScheduleNowAsync("gate-1");
            await enteredLaunch.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var cts2 = new CancellationTokenSource();
            Task<ScheduleRunEntry?> run2 = svc.RunScheduleNowAsync("gate-1", cts2.Token);
            Task<ScheduleRunEntry?> run3 = svc.RunScheduleNowAsync("gate-1");

            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while ((int)activeUsersField.GetValue(svc)! < 3 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.Equal(3, (int)activeUsersField.GetValue(svc)!);

            cts2.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run2);
            Assert.Equal(2, (int)activeUsersField.GetValue(svc)!);

            // Dispose while run1 holds the gate and run3 is queued on WaitAsync
            svc.Dispose();
            Assert.Equal(0, (int)gateDisposedField.GetValue(svc)!);

            releaseLaunch.TrySetResult(true);
            await run1;
            ScheduleRunEntry? run3Result = await run3;
            Assert.Null(run3Result);

            // Calling after disposal must enter if (IsDisposed) and still release ref count cleanly
            ScheduleRunEntry? postDispose = await svc.RunScheduleNowAsync("gate-1");
            Assert.Null(postDispose);

            Assert.Equal(0, (int)activeUsersField.GetValue(svc)!);
            Assert.Equal(1, (int)gateDisposedField.GetValue(svc)!);
            var gate = (SemaphoreSlim)gateField.GetValue(svc)!;
            Assert.Throws<ObjectDisposedException>(() => gate.Wait(0));
        }

        [Fact]
        public void HostActionExecutor_ArrangeWindows_DispatchesThroughAppDispatcher_AndSkipsNone()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            var postedActions = new List<Action>();
            AppDispatcher.CustomDispatcher = action => postedActions.Add(action);

            try
            {
                var hostExec = new SchedulerHostActionExecutorService(
                    getCoordinator: () => null,
                    getJoinDelaySeconds: () => 4,
                    getAsyncJoin: () => false,
                    ensureMultiRoblox: () => true,
                    getLifetimeToken: () => CancellationToken.None,
                    hostUiBridge: Substitute.For<IHostUiBridgeService>(),
                    settings: new IniFile()["General"],
                    logger: Substitute.For<IAppLogger>());

                hostExec.ArrangeWindows(ScheduleArrangeMode.None);
                Assert.Empty(postedActions);

                hostExec.ArrangeWindows(ScheduleArrangeMode.Grid3x2);
                hostExec.ArrangeWindows(ScheduleArrangeMode.Auto);
                Assert.Equal(2, postedActions.Count);
            }
            finally
            {
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public async Task UpsertSchedule_DailyAndWeekly_PreservesStartKeyOnNonStartEdits_AndResetsOnlyAffectedKeys()
        {
            Account a1 = MakeAccount("EditKeyUser", 1701);
            var (svc, _, _, _, exec, time) = CreateHarness(new DateTimeOffset(2026, 11, 24, 8, 0, 10, TimeSpan.Zero), false, a1);
            using (svc)
            {
                LaunchSchedule daily = MakeSchedule("d-edit", "DailyOriginal", "08:00", "12:00", a1);
                daily.OneTimeDate = "2026-11-24";
                Assert.True(svc.UpsertSchedule(daily).IsValid);
                Assert.Null(Assert.Single(svc.GetSchedules()).OneTimeDate);

                await svc.EvaluateTickOnceAsync();
                Assert.Single(exec.ServerLaunches);
                Assert.Equal("2026-11-24", Assert.Single(svc.GetSchedules()).LastTriggeredStartKey);

                exec.SetLive(a1);
                time.Advance(TimeSpan.FromSeconds(10));

                LaunchSchedule editedNonStart = MakeSchedule("d-edit", "DailyRenamed", "8:00", "14:00", a1);
                editedNonStart.OneTimeDate = "2099-01-01";
                Assert.True(svc.UpsertSchedule(editedNonStart).IsValid);

                LaunchSchedule saved = Assert.Single(svc.GetSchedules());
                Assert.Equal("08:00", saved.StartTime);
                Assert.Equal("14:00", saved.EndTime);
                Assert.Null(saved.OneTimeDate);
                Assert.Equal("2026-11-24", saved.LastTriggeredStartKey);
                Assert.Null(saved.LastTriggeredEndKey);

                await svc.EvaluateTickOnceAsync();
                Assert.Empty(exec.ClosedBatches);
                Assert.Single(exec.ServerLaunches);

                time.SetUtcNow(new DateTimeOffset(2026, 11, 24, 14, 0, 10, TimeSpan.Zero));
                await svc.EvaluateTickOnceAsync();
                Assert.Single(exec.ClosedBatches);
                Assert.Equal("2026-11-24", Assert.Single(svc.GetSchedules()).LastTriggeredEndKey);

                LaunchSchedule editedEndOnly = MakeSchedule("d-edit", "DailyRenamed", "08:00", "15:00", a1);
                Assert.True(svc.UpsertSchedule(editedEndOnly).IsValid);
                saved = Assert.Single(svc.GetSchedules());
                Assert.Equal("2026-11-24", saved.LastTriggeredStartKey);
                Assert.Null(saved.LastTriggeredEndKey);

                LaunchSchedule editedStart = MakeSchedule("d-edit", "DailyRenamed", "08:05", "15:00", a1);
                Assert.True(svc.UpsertSchedule(editedStart).IsValid);
                saved = Assert.Single(svc.GetSchedules());
                Assert.Null(saved.LastTriggeredStartKey);
                Assert.Null(saved.LastTriggeredEndKey);

                time.SetUtcNow(new DateTimeOffset(2026, 11, 24, 9, 0, 10, TimeSpan.Zero));
                LaunchSchedule weekly = MakeSchedule("w-edit", "WeeklyShift", "09:00", "11:00", a1);
                weekly.Recurrence = ScheduleRecurrence.Weekly;
                weekly.DaysOfWeek = ScheduleDaysOfWeek.Weekdays;
                Assert.True(svc.UpsertSchedule(weekly).IsValid);
                await svc.EvaluateTickOnceAsync();
                Assert.Equal("2026-11-24", svc.GetSchedules().Single(s => s.Id == "w-edit").LastTriggeredStartKey);

                LaunchSchedule weeklyDaysChanged = MakeSchedule("w-edit", "WeeklyShift", "09:00", "11:00", a1);
                weeklyDaysChanged.Recurrence = ScheduleRecurrence.Weekly;
                weeklyDaysChanged.DaysOfWeek = ScheduleDaysOfWeek.All;
                Assert.True(svc.UpsertSchedule(weeklyDaysChanged).IsValid);
                Assert.Null(svc.GetSchedules().Single(s => s.Id == "w-edit").LastTriggeredStartKey);
            }
        }
    }
}
