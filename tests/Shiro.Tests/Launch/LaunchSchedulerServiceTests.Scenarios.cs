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
        private sealed class FakeLaunchCoordinator : ILaunchCoordinator
        {
            private readonly Func<IReadOnlyList<LaunchRequest>, LaunchBatchResult> _onEnqueue;
            public bool IsRunning => false;
            public List<IReadOnlyList<LaunchRequest>> CapturedBatches { get; } = new();

            public FakeLaunchCoordinator(Func<IReadOnlyList<LaunchRequest>, LaunchBatchResult>? onEnqueue = null)
            {
                _onEnqueue = onEnqueue ?? (reqs => new LaunchBatchResult(
                    reqs.Select((r, idx) => new LaunchProgress
                    {
                        Index = idx + 1,
                        Total = reqs.Count,
                        Account = r.Account,
                        Status = LaunchOutcome.Success,
                        Completed = true
                    }).ToList(),
                    cancelled: false));
            }

            public Task<LaunchBatchResult> EnqueueAsync(
                IReadOnlyList<LaunchRequest> requests,
                int delaySeconds,
                bool asyncJoin,
                Action<LaunchProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                CapturedBatches.Add(requests.ToList());
                return Task.FromResult(_onEnqueue(requests));
            }

            public bool TryAppendRequests(IReadOnlyList<LaunchRequest> requests, out int appendedCount)
            {
                appendedCount = 0;
                return false;
            }

            public void SignalNext() { }
            public void Cancel() { }
            public Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
            public void Dispose() { }
        }

        [Fact]
        public async Task PublicServerSchedule_BypassesAccountSavedJobId_AndSuppressesGenericBatchToast()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            AppDispatcher.CustomDispatcher = a => a();
            ToastService.ClearAll();

            try
            {
                Account accWithSavedJob;
                using (Account.BeginSuppressDeferredSaveScope())
                {
                    accWithSavedJob = new Account
                    {
                        Username = "SavedJobUser",
                        UserID = 701,
                        SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--savedjob",
                        Valid = true,
                        Fields = new Dictionary<string, string>
                        {
                            ["SavedPlaceId"] = "111111",
                            ["SavedJobId"] = "stale-saved-job-999"
                        }
                    };
                }

                var fakeCoordinator = new FakeLaunchCoordinator();

                LaunchBatchResult batch = await AccountLaunchBatchService.LaunchAccountsForSchedulerAsync(
                    fakeCoordinator,
                    new[] { accWithSavedJob },
                    placeId: 2753915549,
                    jobId: string.Empty,
                    vipServer: false,
                    accountJoinDelaySeconds: 5,
                    asyncJoin: false,
                    statusCallback: null,
                    CancellationToken.None);

                IReadOnlyList<LaunchRequest> capturedRequests = Assert.Single(fakeCoordinator.CapturedBatches);
                LaunchRequest req = Assert.Single(capturedRequests);
                Assert.Equal(2753915549L, req.PlaceId);
                Assert.Equal(string.Empty, req.JobId);
                Assert.False(req.VipServer);
                Assert.False(req.OpenHome);
                Assert.Single(batch.Results);
                Assert.Empty(ToastService.ActiveToasts);

                var (svc, _, _, _, exec, _) = CreateHarness(new DateTimeOffset(2026, 11, 24, 8, 0, 5, TimeSpan.Zero), false, accWithSavedJob);
                using (svc)
                {
                    LaunchSchedule publicSched = MakeSchedule("s-pub", "Public", "08:00", null, accWithSavedJob);
                    publicSched.JobId = "";
                    Assert.True(svc.UpsertSchedule(publicSched).IsValid);

                    await svc.EvaluateTickOnceAsync();
                    var launch = Assert.Single(exec.ServerLaunches);
                    Assert.Equal(string.Empty, launch.JobId);
                    Assert.False(launch.IsVip);
                }
            }
            finally
            {
                ToastService.ClearAll();
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public async Task QueueContention_WaitsUpTo30s_RecordsSkippedIfStillBusy_WithoutCancellingManualBatch()
        {
            Account a1 = MakeAccount("BusyAcc1", 801);
            Account a2 = MakeAccount("ReadyAcc2", 802);
            var (svc, _, _, toast, exec, _) = CreateHarness(new DateTimeOffset(2026, 11, 24, 8, 0, 10, TimeSpan.Zero), false, a1, a2);
            using (svc)
            {
                exec.QueueIdleResult = false;
                LaunchSchedule busySched = MakeSchedule("s-busy", "BusyShift", "08:00", "12:00", a1);
                Assert.True(svc.UpsertSchedule(busySched).IsValid);

                await svc.EvaluateTickOnceAsync();

                Assert.Equal(TimeSpan.FromSeconds(30), exec.LastQueueWaitTimeout);
                Assert.Empty(exec.ServerLaunches);
                ScheduleRunEntry entry = Assert.Single(svc.GetHistory());
                Assert.Equal(ScheduleRunOutcome.Skipped, entry.Outcome);
                Assert.Contains("30s", entry.Summary, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(LocalizationService.Get("Sched_Run_QueueBusySkipped"), entry.Summary);
                toast.Received(1).ShowError(Arg.Any<string>(), Arg.Is<string>(m => m.Contains("30s", StringComparison.OrdinalIgnoreCase)));
            }
        }

        [Fact]
        public async Task CloseOtherTabsBeforeLaunch_ClosesOnlyRunningNonTargetAccounts()
        {
            Account target = MakeAccount("FarmTarget", 901, group: "Farm-A");
            Account otherRunning = MakeAccount("OtherRunning", 902, group: "Other");
            Account otherStopped = MakeAccount("OtherStopped", 903, group: "Other");
            var (svc, _, _, _, exec, time) = CreateHarness(new DateTimeOffset(2026, 11, 24, 8, 0, 5, TimeSpan.Zero), false, target, otherRunning, otherStopped);
            using (svc)
            {
                exec.SetLive(otherRunning);
                LaunchSchedule sched = new()
                {
                    Id = "s-excl",
                    Name = "ExclusiveGroup",
                    IsEnabled = true,
                    Recurrence = ScheduleRecurrence.Daily,
                    DaysOfWeek = ScheduleDaysOfWeek.All,
                    StartTime = "08:00",
                    HasEndTime = true,
                    EndTime = "12:00",
                    TargetMode = ScheduleTargetMode.Group,
                    TargetGroup = "Farm-A",
                    LaunchMode = ScheduleLaunchMode.JoinServer,
                    PlaceId = 2753915549,
                    CloseOtherTabsBeforeLaunch = true,
                    HandoffDelaySeconds = 1
                };
                Assert.True(svc.UpsertSchedule(sched).IsValid);

                Task eval = svc.EvaluateTickOnceAsync();
                await Task.Delay(40);
                time.Advance(TimeSpan.FromSeconds(1));
                await eval;

                var closed = Assert.Single(exec.ClosedBatches);
                Assert.Equal("OtherRunning", Assert.Single(closed).Username);
                var launched = Assert.Single(exec.ServerLaunches);
                Assert.Equal("FarmTarget", Assert.Single(launched.Accounts).Username);
            }
        }

        [Fact]
        public async Task InvalidAndBannedAccounts_AreSkippedAndReportedInHistory()
        {
            Account validAcc = MakeAccount("GoodUser", 1001, valid: true);
            Account invalidAcc = MakeAccount("ExpiredUser", 1002, valid: false);
            Account bannedAcc = MakeAccount("BannedUser", 1003, valid: false, issue: "Banned");
            var (svc, _, _, _, exec, _) = CreateHarness(new DateTimeOffset(2026, 11, 24, 8, 0, 5, TimeSpan.Zero), false, validAcc, invalidAcc, bannedAcc);
            using (svc)
            {
                LaunchSchedule sched = MakeSchedule("s-skip", "Mixed", "08:00", "12:00", validAcc, invalidAcc, bannedAcc);
                Assert.True(svc.UpsertSchedule(sched).IsValid);

                await svc.EvaluateTickOnceAsync();

                var launched = Assert.Single(exec.ServerLaunches);
                Assert.Equal("GoodUser", Assert.Single(launched.Accounts).Username);
                ScheduleRunEntry entry = Assert.Single(svc.GetHistory());
                Assert.Equal(ScheduleRunOutcome.Partial, entry.Outcome);
                Assert.Equal(3, entry.TargetCount);
                Assert.Equal(1, entry.SucceededCount);
                Assert.Equal(2, entry.SkippedCount);
                Assert.Contains("1/3", entry.Summary, StringComparison.Ordinal);
                Assert.Contains("2", entry.Summary, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task OneTimeSchedule_AutoDisablesAfterTerminalPhase_AndPersists()
        {
            Account a1 = MakeAccount("OneTimeUser", 1101);
            var (svc, store, _, _, exec, time) = CreateHarness(new DateTimeOffset(2026, 11, 24, 8, 0, 5, TimeSpan.Zero), false, a1);
            using (svc)
            {
                LaunchSchedule openOnly = MakeSchedule("ot-open", "OneTimeOpenOnly", "08:00", null, a1);
                openOnly.Recurrence = ScheduleRecurrence.OneTime;
                openOnly.OneTimeDate = "2026-11-24";

                LaunchSchedule windowed = MakeSchedule("ot-win", "OneTimeWindow", "08:00", "12:00", a1);
                windowed.Recurrence = ScheduleRecurrence.OneTime;
                windowed.OneTimeDate = "2026-11-24";

                Assert.True(svc.UpsertSchedule(openOnly).IsValid);
                Assert.True(svc.UpsertSchedule(windowed).IsValid);

                await svc.EvaluateTickOnceAsync();

                IReadOnlyList<LaunchSchedule> afterStart = svc.GetSchedules();
                Assert.False(afterStart.Single(s => s.Id == "ot-open").IsEnabled);
                Assert.True(afterStart.Single(s => s.Id == "ot-win").IsEnabled);
                Assert.False(store.Load().Schedules.Single(s => s.Id == "ot-open").IsEnabled);

                exec.SetLive(a1);
                time.SetUtcNow(new DateTimeOffset(2026, 11, 24, 12, 0, 5, TimeSpan.Zero));
                await svc.EvaluateTickOnceAsync();

                IReadOnlyList<LaunchSchedule> afterEnd = svc.GetSchedules();
                Assert.False(afterEnd.Single(s => s.Id == "ot-win").IsEnabled);
                Assert.False(store.Load().Schedules.Single(s => s.Id == "ot-win").IsEnabled);
            }
        }

        [Fact]
        public async Task PostLaunchArrange_AndWebhookNotification_InvokeExecutorWhenConfigured()
        {
            Account a1 = MakeAccount("HomeUser", 1201);
            var (svc, _, _, _, exec, time) = CreateHarness(new DateTimeOffset(2026, 11, 24, 8, 0, 5, TimeSpan.Zero), false, a1);
            using (svc)
            {
                LaunchSchedule sched = MakeSchedule("s-home", "HomeTile", "08:00", "12:00", a1);
                sched.LaunchMode = ScheduleLaunchMode.OpenHome;
                sched.PostLaunchArrange = ScheduleArrangeMode.Grid3x2;
                sched.SendWebhookNotification = true;
                Assert.True(svc.UpsertSchedule(sched).IsValid);

                Task eval = svc.EvaluateTickOnceAsync();
                await Task.Delay(40);
                Assert.Single(exec.HomeLaunches);
                Assert.Empty(exec.ArrangedModes);
                Assert.False(eval.IsCompleted);

                time.Advance(TimeSpan.FromSeconds(3));
                await eval;

                Assert.Equal(ScheduleArrangeMode.Grid3x2, Assert.Single(exec.ArrangedModes));
                var alert = Assert.Single(exec.WebhookAlerts);
                Assert.Equal("s-home", alert.Schedule.Id);
                Assert.Equal(ScheduleRunOutcome.Success, alert.Entry.Outcome);
                Assert.True(exec.LiveStatusNotifyCount >= 1);
            }
        }
    }
}
