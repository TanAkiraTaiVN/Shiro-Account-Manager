#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed partial class LaunchSchedulerServiceTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _storePath;

        public LaunchSchedulerServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "Shiro_SchedSvcTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _storePath = Path.Combine(_tempDir, "LaunchSchedules.json");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup
            }
        }

        private sealed class FakeExecutor : ISchedulerActionExecutor
        {
            private readonly HashSet<Account> _live = new(ReferenceEqualityComparer.Instance);
            public List<string> Events { get; } = new();
            public List<IReadOnlyList<Account>> ClosedBatches { get; } = new();
            public List<(IReadOnlyList<Account> Accounts, long PlaceId, string JobId, bool IsVip)> ServerLaunches { get; } = new();
            public List<IReadOnlyList<Account>> HomeLaunches { get; } = new();
            public List<ScheduleArrangeMode> ArrangedModes { get; } = new();
            public List<(LaunchSchedule Schedule, ScheduleRunEntry Entry)> WebhookAlerts { get; } = new();
            public int LiveStatusNotifyCount { get; private set; }
            public int MultiRobloxEnsureCount { get; private set; }
            public TimeSpan? LastQueueWaitTimeout { get; private set; }
            public bool QueueIdleResult { get; set; } = true;
            public Func<IReadOnlyList<Account>, (int Succeeded, int Attempted, bool Cancelled, string Detail)>? OnLaunchServer { get; set; }
            public Func<CancellationToken, Task>? BeforeLaunchServerAsync { get; set; }
            public Func<IReadOnlyList<Account>, (int Succeeded, int Attempted, bool Cancelled, string Detail)>? OnLaunchHome { get; set; }
            public TaskCompletionSource<bool>? LaunchSignal { get; set; }

            public void SetLive(params Account[] accounts)
            {
                _live.Clear();
                foreach (Account acc in accounts)
                    _live.Add(acc);
            }

            public IReadOnlySet<Account> GetLiveRunningAccounts(IReadOnlyList<Account> candidates)
            {
                var result = new HashSet<Account>(ReferenceEqualityComparer.Instance);
                foreach (Account acc in candidates)
                {
                    if (_live.Contains(acc))
                        result.Add(acc);
                }
                return result;
            }

            public Task<int> CloseAccountsAsync(IReadOnlyList<Account> accounts, CancellationToken ct)
            {
                ClosedBatches.Add(accounts.ToList());
                foreach (Account acc in accounts)
                {
                    _live.Remove(acc);
                    Events.Add($"Close:{acc.Username}");
                }
                return Task.FromResult(accounts.Count);
            }

            public Task<bool> WaitForLaunchQueueIdleAsync(TimeSpan timeout, CancellationToken ct)
            {
                LastQueueWaitTimeout = timeout;
                Events.Add($"WaitIdle:{(int)timeout.TotalSeconds}");
                return Task.FromResult(QueueIdleResult);
            }

            public bool TryEnsureMultiRoblox(int targetCount)
            {
                MultiRobloxEnsureCount++;
                Events.Add($"EnsureMulti:{targetCount}");
                return true;
            }

            public async Task<(int Succeeded, int Attempted, bool Cancelled, string Detail)> LaunchServerAsync(
                IReadOnlyList<Account> accounts, long placeId, string jobId, bool isVip, CancellationToken ct)
            {
                if (BeforeLaunchServerAsync != null)
                    await BeforeLaunchServerAsync(ct).ConfigureAwait(false);

                ServerLaunches.Add((accounts.ToList(), placeId, jobId, isVip));
                Events.Add($"LaunchServer:{string.Join(",", accounts.Select(a => a.Username))}");
                var result = OnLaunchServer != null
                    ? OnLaunchServer(accounts)
                    : (accounts.Count, accounts.Count, false, string.Empty);
                LaunchSignal?.TrySetResult(true);
                return result;
            }

            public Task<(int Succeeded, int Attempted, bool Cancelled, string Detail)> LaunchHomeAsync(
                IReadOnlyList<Account> accounts, CancellationToken ct)
            {
                HomeLaunches.Add(accounts.ToList());
                Events.Add($"LaunchHome:{string.Join(",", accounts.Select(a => a.Username))}");
                var result = OnLaunchHome != null
                    ? OnLaunchHome(accounts)
                    : (accounts.Count, accounts.Count, false, string.Empty);
                LaunchSignal?.TrySetResult(true);
                return Task.FromResult(result);
            }

            public void ArrangeWindows(ScheduleArrangeMode mode)
            {
                ArrangedModes.Add(mode);
                Events.Add($"Arrange:{mode}");
            }

            public void NotifyWebhook(LaunchSchedule schedule, ScheduleRunEntry entry)
            {
                WebhookAlerts.Add((schedule.Clone(), entry));
                Events.Add($"Webhook:{schedule.Id}");
            }

            public void NotifyLiveProcessStatusChanged() => LiveStatusNotifyCount++;
        }

        private static Account MakeAccount(string username, long userId, string group = "Default", bool valid = true, string issue = "")
        {
            using var _ = Account.BeginSuppressDeferredSaveScope();
            var acc = new Account
            {
                Username = username,
                UserID = userId,
                SecurityToken = valid ? "_|WARNING:-DO-NOT-SHARE-THIS.--" + username : string.Empty,
                Valid = valid,
                Group = group
            };
            if (!string.IsNullOrEmpty(issue))
                acc.AccountStatusIssue = issue;
            return acc;
        }

        private static LaunchSchedule MakeSchedule(
            string id,
            string name,
            string start,
            string? end,
            params Account[] accounts) => new()
        {
            Id = id,
            Name = name,
            IsEnabled = true,
            Recurrence = ScheduleRecurrence.Daily,
            DaysOfWeek = ScheduleDaysOfWeek.All,
            StartTime = start,
            HasEndTime = end != null,
            EndTime = end,
            TargetMode = ScheduleTargetMode.SpecificAccounts,
            TargetAccounts = accounts.Select(a => new ScheduleAccountRef { UserId = a.UserID, Username = a.Username }).ToList(),
            LaunchMode = ScheduleLaunchMode.JoinServer,
            PlaceId = 2753915549,
            HandoffDelaySeconds = 2
        };

        private (LaunchSchedulerService Service, LaunchScheduleStore Store, IAccountRepository Repo, IToastNotifier Toast, FakeExecutor Executor, TestTimeProvider Time) CreateHarness(
            DateTimeOffset utcNow,
            bool vaultLocked = false,
            params Account[] accounts)
        {
            var store = new LaunchScheduleStore(() => _storePath);
            var repo = new AccountRepository();
            if (accounts.Length > 0)
                repo.ReplaceAll(accounts);

            var settings = new IniFile()["General"];
            var toast = Substitute.For<IToastNotifier>();
            var uiBridge = Substitute.For<IHostUiBridgeService>();
            var time = new TestTimeProvider(utcNow, TimeZoneInfo.Utc);
            var logger = Substitute.For<IAppLogger>();
            var executor = new FakeExecutor();
            var svc = new LaunchSchedulerService(
                store,
                repo,
                settings,
                toast,
                uiBridge,
                isVaultLocked: () => vaultLocked,
                timeProvider: time,
                logger: logger);
            svc.AttachExecutor(executor);
            return (svc, store, repo, toast, executor, time);
        }

        [Fact]
        public async Task EvaluateTick_ExecutesClosesBeforeOpens_WaitsHandoffCooldown_ThenLaunches()
        {
            Account a1 = MakeAccount("Alpha", 101);
            Account a2 = MakeAccount("Beta", 102);
            var (svc, _, _, _, exec, time) = CreateHarness(new DateTimeOffset(2026, 11, 24, 12, 0, 5, TimeSpan.Zero), false, a1, a2);
            using (svc)
            {
                exec.SetLive(a1);
                LaunchSchedule morning = MakeSchedule("s-am", "Morning", "08:00", "12:00", a1);
                LaunchSchedule afternoon = MakeSchedule("s-pm", "Afternoon", "12:00", "18:00", a2);
                afternoon.HandoffDelaySeconds = 4;

                Assert.True(svc.UpsertSchedule(morning).IsValid);
                Assert.True(svc.UpsertSchedule(afternoon).IsValid);

                Task evalTask = svc.EvaluateTickOnceAsync();
                await Task.Delay(50);
                Assert.Single(exec.ClosedBatches);
                Assert.Empty(exec.ServerLaunches);
                Assert.False(evalTask.IsCompleted);

                time.Advance(TimeSpan.FromSeconds(3));
                await Task.Delay(50);
                Assert.Empty(exec.ServerLaunches);
                Assert.False(evalTask.IsCompleted);

                time.Advance(TimeSpan.FromSeconds(1));
                await evalTask;

                Assert.Single(exec.ServerLaunches);
                Assert.Equal(a2.Username, Assert.Single(exec.ServerLaunches[0].Accounts).Username);
                Assert.True(exec.Events.IndexOf("Close:Alpha") < exec.Events.IndexOf("LaunchServer:Beta"));
                IReadOnlyList<ScheduleRunEntry> history = svc.GetHistory();
                Assert.Equal(2, history.Count);
                Assert.Contains(history, h => h.ScheduleId == "s-am" && h.Phase == ScheduleRunPhase.Close && h.Outcome == ScheduleRunOutcome.Success);
                Assert.Contains(history, h => h.ScheduleId == "s-pm" && h.Phase == ScheduleRunPhase.Open && h.Outcome == ScheduleRunOutcome.Success);
            }
        }

        [Fact]
        public async Task EvaluateTick_Error273Prevention_ClosesLiveTargetAccountBeforeScheduledOpen()
        {
            Account a1 = MakeAccount("Alpha273", 201);
            var (svc, _, _, _, exec, time) = CreateHarness(new DateTimeOffset(2026, 11, 24, 8, 0, 5, TimeSpan.Zero), false, a1);
            using (svc)
            {
                exec.SetLive(a1);
                LaunchSchedule sched = MakeSchedule("s-273", "Rotate", "08:00", "12:00", a1);
                sched.HandoffDelaySeconds = 2;
                Assert.True(svc.UpsertSchedule(sched).IsValid);

                Task evalTask = svc.EvaluateTickOnceAsync();
                await Task.Delay(40);
                Assert.Single(exec.ClosedBatches);
                Assert.Empty(exec.ServerLaunches);

                time.Advance(TimeSpan.FromSeconds(2));
                await evalTask;

                Assert.Single(exec.ServerLaunches);
                Assert.True(exec.Events.IndexOf("Close:Alpha273") < exec.Events.IndexOf("LaunchServer:Alpha273"));
            }
        }

        [Fact]
        public async Task StartupCatchUp_SkipsAlreadyRunningAccounts_AndLaunchesOnlyMissingTargets()
        {
            Account running = MakeAccount("RunningAcc", 301);
            Account missing = MakeAccount("MissingAcc", 302);
            var (svc, _, _, _, exec, _) = CreateHarness(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero), false, running, missing);
            using (svc)
            {
                exec.SetLive(running);
                LaunchSchedule sched = MakeSchedule("s-catch", "CatchUp", "08:00", "12:00", running, missing);
                sched.CatchUpIfActiveOnStartup = true;
                Assert.True(svc.UpsertSchedule(sched).IsValid);

                await svc.EvaluateTickOnceAsync();

                Assert.Empty(exec.ClosedBatches);
                var launch = Assert.Single(exec.ServerLaunches);
                Assert.Equal("MissingAcc", Assert.Single(launch.Accounts).Username);
                ScheduleRunEntry entry = Assert.Single(svc.GetHistory());
                Assert.Equal(ScheduleRunPhase.CatchUpOpen, entry.Phase);
                Assert.Equal(ScheduleRunOutcome.Success, entry.Outcome);
                Assert.Equal(1, entry.SucceededCount);
            }
        }

        [Fact]
        public async Task VaultLockedOnStartup_PausesTicksAndDefersCatchUpUntilUnlocked()
        {
            Account a1 = MakeAccount("VaultUser", 401);
            var store = new LaunchScheduleStore(() => _storePath);
            var repo = new AccountRepository();
            var settings = new IniFile()["General"];
            var toast = Substitute.For<IToastNotifier>();
            var uiBridge = Substitute.For<IHostUiBridgeService>();
            var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 24, 8, 0, 15, TimeSpan.Zero), TimeZoneInfo.Utc);
            bool isLocked = true;
            var exec = new FakeExecutor { LaunchSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };

            using var svc = new LaunchSchedulerService(store, repo, settings, toast, uiBridge, () => isLocked, time, Substitute.For<IAppLogger>());
            LaunchSchedule sched = MakeSchedule("s-vault", "VaultShift", "08:00", "12:00", a1);
            sched.CatchUpIfActiveOnStartup = true;
            Assert.True(svc.UpsertSchedule(sched).IsValid);
            svc.AttachExecutor(exec);
            svc.Start();

            await svc.EvaluateTickOnceAsync();
            time.Advance(TimeSpan.FromHours(2));
            await svc.EvaluateTickOnceAsync();

            Assert.Empty(exec.ServerLaunches);
            Assert.Null(Assert.Single(svc.GetSchedules()).LastTriggeredStartKey);

            isLocked = false;
            repo.ReplaceAll(new[] { a1 });
            await exec.LaunchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(30);

            var launch = Assert.Single(exec.ServerLaunches);
            Assert.Equal("VaultUser", Assert.Single(launch.Accounts).Username);
            Assert.Equal("2026-11-24", Assert.Single(svc.GetSchedules()).LastTriggeredStartKey);
            ScheduleRunEntry entry = Assert.Single(svc.GetHistory());
            Assert.Equal(ScheduleRunPhase.CatchUpOpen, entry.Phase);

            await svc.EvaluateTickOnceAsync();
            Assert.Single(exec.ServerLaunches);
        }

        [Fact]
        public async Task ManualRunNow_OutsideGraceWindow_DoesNotSuppressScheduledOpenOrDisableOneTime()
        {
            Account a1 = MakeAccount("EarlyUser", 501);
            var (svc, _, _, _, exec, time) = CreateHarness(new DateTimeOffset(2026, 11, 24, 7, 50, 0, TimeSpan.Zero), false, a1);
            using (svc)
            {
                LaunchSchedule oneTime = MakeSchedule("s-ot-early", "OneTimeEarly", "08:00", "12:00", a1);
                oneTime.Recurrence = ScheduleRecurrence.OneTime;
                oneTime.OneTimeDate = "2026-11-24";
                Assert.True(svc.UpsertSchedule(oneTime).IsValid);

                ScheduleRunEntry? manual = await svc.RunScheduleNowAsync("s-ot-early");
                Assert.NotNull(manual);
                Assert.Equal(ScheduleRunPhase.ManualOpen, manual!.Phase);
                Assert.True(Assert.Single(svc.GetSchedules()).IsEnabled);
                Assert.Null(Assert.Single(svc.GetSchedules()).LastTriggeredStartKey);
                Assert.Single(exec.ServerLaunches);

                time.Advance(TimeSpan.FromMinutes(10));
                await svc.EvaluateTickOnceAsync();

                Assert.Equal(2, exec.ServerLaunches.Count);
                Assert.Equal("2026-11-24", Assert.Single(svc.GetSchedules()).LastTriggeredStartKey);
                Assert.True(Assert.Single(svc.GetSchedules()).IsEnabled);
            }
        }

        [Fact]
        public async Task ManualRunNow_Inside90sGraceWindow_MarksOccurrenceKey_PreventingImmediateDuplicateRelaunch()
        {
            Account a1 = MakeAccount("GraceUser", 601);
            var (svc, _, _, _, exec, time) = CreateHarness(new DateTimeOffset(2026, 11, 24, 8, 0, 20, TimeSpan.Zero), false, a1);
            using (svc)
            {
                LaunchSchedule sched = MakeSchedule("s-grace", "GraceShift", "08:00", "12:00", a1);
                Assert.True(svc.UpsertSchedule(sched).IsValid);

                ScheduleRunEntry? manualOpen = await svc.RunScheduleNowAsync("s-grace");
                Assert.NotNull(manualOpen);
                Assert.Equal("2026-11-24", Assert.Single(svc.GetSchedules()).LastTriggeredStartKey);

                time.Advance(TimeSpan.FromSeconds(1));
                await svc.EvaluateTickOnceAsync();
                Assert.Single(exec.ServerLaunches);

                exec.SetLive(a1);
                time.SetUtcNow(new DateTimeOffset(2026, 11, 24, 12, 0, 30, TimeSpan.Zero));
                ScheduleRunEntry? manualClose = await svc.CloseScheduleTabsNowAsync("s-grace");
                Assert.NotNull(manualClose);
                Assert.Equal(ScheduleRunPhase.ManualClose, manualClose!.Phase);
                Assert.Equal("2026-11-24", Assert.Single(svc.GetSchedules()).LastTriggeredEndKey);

                time.Advance(TimeSpan.FromSeconds(1));
                await svc.EvaluateTickOnceAsync();
                Assert.Single(exec.ClosedBatches);
            }
        }
    }
}
