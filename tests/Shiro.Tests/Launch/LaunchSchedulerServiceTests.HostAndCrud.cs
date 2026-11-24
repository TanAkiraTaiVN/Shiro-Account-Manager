#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
    }
}
