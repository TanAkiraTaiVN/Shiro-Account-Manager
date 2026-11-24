#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class LaunchSchedulerService
    {
        internal async Task EvaluateTickOnceAsync(CancellationToken ct = default)
        {
            if (IsDisposedOrPausedOrLocked(out ISchedulerActionExecutor? executor))
                return;

            DateTimeOffset localNow = _timeProvider.GetLocalNow();
            bool maybeWork;
            lock (_stateLock)
            {
                if (_disposed || !_isGlobalEnabled || _executor == null)
                    return;

                bool hasEnabled = _schedules.Any(s => s.IsEnabled);
                if (!_startupCatchUpPending && !hasEnabled)
                    return;

                maybeWork = _startupCatchUpPending ||
                    LaunchScheduleEvaluatorHelper.GetDueEvents(_schedules, localNow, _timeProvider.LocalTimeZone).Count > 0;
            }

            if (!maybeWork)
                return;

            bool acquired = await TryAcquireExecutionGateAsync(ct).ConfigureAwait(false);
            if (!acquired)
                return;

            try
            {
                if (IsDisposedOrPausedOrLocked(out executor) || executor == null)
                    return;

                localNow = _timeProvider.GetLocalNow();
                var closeEvents = new List<ScheduleDueEvent>();
                var openItems = new List<(LaunchSchedule Schedule, ScheduleRunPhase Phase)>();
                bool schedulesMutated = false;

                lock (_stateLock)
                {
                    if (_disposed || !_isGlobalEnabled || _executor == null)
                        return;

                    bool runCatchUp = _startupCatchUpPending;
                    _startupCatchUpPending = false;

                    IReadOnlyList<ScheduleDueEvent> due = LaunchScheduleEvaluatorHelper.GetDueEvents(
                        _schedules, localNow, _timeProvider.LocalTimeZone);
                    IReadOnlyList<LaunchSchedule> catchUp = runCatchUp
                        ? LaunchScheduleEvaluatorHelper.GetStartupCatchUpSchedules(_schedules, localNow, _timeProvider.LocalTimeZone)
                        : Array.Empty<LaunchSchedule>();

                    if (due.Count == 0 && catchUp.Count == 0)
                        return;

                    schedulesMutated = StampDueAndCatchUpLocked(due, catchUp, localNow, closeEvents, openItems);
                    if (schedulesMutated)
                        SaveDocumentLocked();
                }

                if (schedulesMutated)
                    SchedulesChanged?.Invoke();

                await ExecuteBatchPipelineAsync(executor, closeEvents, openItems, ct).ConfigureAwait(false);
            }
            finally
            {
                ReleaseExecutionGate(acquired);
            }
        }

        public async Task<ScheduleRunEntry?> RunScheduleNowAsync(string scheduleId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(scheduleId))
                return null;

            bool acquired = await TryAcquireExecutionGateAsync(ct).ConfigureAwait(false);
            if (!acquired)
                return null;

            try
            {
                if (!TryPrepareManualExecution(scheduleId, ScheduleRunPhase.ManualOpen, out ISchedulerActionExecutor? executor, out LaunchSchedule? snapshot))
                    return null;

                IReadOnlyList<ScheduleRunEntry> entries = await ExecuteBatchPipelineAsync(
                    executor!,
                    Array.Empty<ScheduleDueEvent>(),
                    new[] { (snapshot!, ScheduleRunPhase.ManualOpen) },
                    ct).ConfigureAwait(false);

                return entries.FirstOrDefault();
            }
            finally
            {
                ReleaseExecutionGate(acquired);
            }
        }

        public async Task<ScheduleRunEntry?> CloseScheduleTabsNowAsync(string scheduleId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(scheduleId))
                return null;

            bool acquired = await TryAcquireExecutionGateAsync(ct).ConfigureAwait(false);
            if (!acquired)
                return null;

            try
            {
                if (!TryPrepareManualExecution(scheduleId, ScheduleRunPhase.ManualClose, out ISchedulerActionExecutor? executor, out LaunchSchedule? snapshot))
                    return null;

                ScheduleRunEntry entry = await ExecuteCloseItemAsync(executor!, snapshot!, ScheduleRunPhase.ManualClose, ct).ConfigureAwait(false);
                RecordRunEntryAndNotify(executor!, snapshot!, entry, isErrorToast: entry.Outcome == ScheduleRunOutcome.Failed);
                return entry;
            }
            finally
            {
                ReleaseExecutionGate(acquired);
            }
        }

        private bool StampDueAndCatchUpLocked(
            IReadOnlyList<ScheduleDueEvent> due,
            IReadOnlyList<LaunchSchedule> catchUp,
            DateTimeOffset localNow,
            List<ScheduleDueEvent> closeEvents,
            List<(LaunchSchedule Schedule, ScheduleRunPhase Phase)> openItems)
        {
            bool mutated = false;
            var dueOpenScheduleIds = new HashSet<string>(StringComparer.Ordinal);
            DateTimeOffset nowUtc = _timeProvider.GetUtcNow();

            foreach (ScheduleDueEvent ev in due)
            {
                if (ev.Phase == ScheduleRunPhase.Close)
                {
                    ev.Schedule.LastTriggeredEndKey = ev.OccurrenceKey;
                    if (ev.Schedule.Recurrence == ScheduleRecurrence.OneTime)
                        ev.Schedule.IsEnabled = false;
                    ev.Schedule.UpdatedAtUtc = nowUtc;
                    closeEvents.Add(ev);
                    mutated = true;
                }
                else if (ev.Phase == ScheduleRunPhase.Open)
                {
                    ev.Schedule.LastTriggeredStartKey = ev.OccurrenceKey;
                    if (ev.Schedule.Recurrence == ScheduleRecurrence.OneTime && !ev.Schedule.HasEndTime)
                        ev.Schedule.IsEnabled = false;
                    ev.Schedule.UpdatedAtUtc = nowUtc;
                    dueOpenScheduleIds.Add(ev.Schedule.Id);
                    openItems.Add((ev.Schedule.Clone(), ScheduleRunPhase.Open));
                    mutated = true;
                }
            }

            foreach (LaunchSchedule catchSched in catchUp)
            {
                if (dueOpenScheduleIds.Contains(catchSched.Id))
                    continue;

                catchSched.LastTriggeredStartKey = LaunchSchedulerExecutionHelper.ResolveCatchUpOccurrenceKey(
                    catchSched, localNow, _timeProvider.LocalTimeZone);
                catchSched.UpdatedAtUtc = nowUtc;
                openItems.Add((catchSched.Clone(), ScheduleRunPhase.CatchUpOpen));
                mutated = true;
            }

            return mutated;
        }

        private bool TryPrepareManualExecution(
            string scheduleId,
            ScheduleRunPhase manualPhase,
            out ISchedulerActionExecutor? executor,
            out LaunchSchedule? snapshot)
        {
            bool stamped = false;
            executor = null;
            snapshot = null;

            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                executor = _executor;
                if (executor == null)
                    return false;

                LaunchSchedule? existing = _schedules.FirstOrDefault(s => string.Equals(s.Id, scheduleId, StringComparison.Ordinal));
                if (existing == null)
                    return false;

                DateTimeOffset localNow = _timeProvider.GetLocalNow();
                if (LaunchScheduleEvaluatorHelper.IsWithinTriggerGraceWindow(
                        existing, manualPhase, localNow, out string occKey, _timeProvider.LocalTimeZone))
                {
                    bool isClose = manualPhase == ScheduleRunPhase.ManualClose;
                    string? currentKey = isClose ? existing.LastTriggeredEndKey : existing.LastTriggeredStartKey;
                    if (!string.Equals(currentKey, occKey, StringComparison.Ordinal))
                    {
                        if (isClose)
                            existing.LastTriggeredEndKey = occKey;
                        else
                            existing.LastTriggeredStartKey = occKey;

                        existing.UpdatedAtUtc = _timeProvider.GetUtcNow();
                        SaveDocumentLocked();
                        stamped = true;
                    }
                }

                snapshot = existing.Clone();
            }

            if (stamped)
                SchedulesChanged?.Invoke();

            return true;
        }

        private async Task<List<ScheduleRunEntry>> ExecuteBatchPipelineAsync(
            ISchedulerActionExecutor executor,
            IReadOnlyList<ScheduleDueEvent> closeEvents,
            IReadOnlyList<(LaunchSchedule Schedule, ScheduleRunPhase Phase)> openItems,
            CancellationToken ct)
        {
            var recorded = new List<ScheduleRunEntry>();
            int totalClosedInPhase1 = 0;

            foreach (ScheduleDueEvent closeEv in closeEvents)
            {
                ct.ThrowIfCancellationRequested();
                ScheduleRunEntry closeEntry = await ExecuteCloseItemAsync(executor, closeEv.Schedule, closeEv.Phase, ct).ConfigureAwait(false);
                totalClosedInPhase1 += closeEntry.SucceededCount;
                RecordRunEntryAndNotify(executor, closeEv.Schedule, closeEntry, isErrorToast: closeEntry.Outcome == ScheduleRunOutcome.Failed);
                recorded.Add(closeEntry);
            }

            IReadOnlyList<Account> allAccounts = _accountRepository.GetAll();
            var openPlans = new List<LaunchSchedulerExecutionHelper.ResolvedOpenPlan>(openItems.Count);
            foreach ((LaunchSchedule schedule, ScheduleRunPhase phase) in openItems)
            {
                ct.ThrowIfCancellationRequested();
                LaunchSchedulerExecutionHelper.ResolvedOpenPlan plan = LaunchSchedulerExecutionHelper.PrepareOpenPlan(
                    executor, schedule, phase, allAccounts);
                if (plan.AccountsToCloseFirst.Count > 0)
                {
                    int closed = await executor.CloseAccountsAsync(plan.AccountsToCloseFirst, ct).ConfigureAwait(false);
                    totalClosedInPhase1 += closed;
                }
                openPlans.Add(plan);
            }

            List<LaunchSchedulerExecutionHelper.ResolvedOpenPlan> launchingPlans = openPlans.Where(p => p.ToLaunch.Count > 0).ToList();
            if (totalClosedInPhase1 > 0 && launchingPlans.Count > 0)
            {
                int handoffDelaySeconds = launchingPlans.Max(p => Math.Clamp(p.Schedule.HandoffDelaySeconds, 0, 15));
                if (handoffDelaySeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(handoffDelaySeconds), _timeProvider, ct).ConfigureAwait(false);
            }

            foreach (LaunchSchedulerExecutionHelper.ResolvedOpenPlan plan in openPlans)
            {
                ct.ThrowIfCancellationRequested();
                (ScheduleRunEntry entry, bool isErrorToast) = await ExecuteOpenPlanAsync(executor, plan, ct).ConfigureAwait(false);
                RecordRunEntryAndNotify(executor, plan.Schedule, entry, isErrorToast);
                recorded.Add(entry);
            }

            return recorded;
        }

        private async Task<ScheduleRunEntry> ExecuteCloseItemAsync(
            ISchedulerActionExecutor executor,
            LaunchSchedule schedule,
            ScheduleRunPhase phase,
            CancellationToken ct)
        {
            IReadOnlyList<Account> matched = LaunchSchedulerExecutionHelper.ResolveMatchedAccounts(schedule, _accountRepository.GetAll());
            IReadOnlySet<Account> liveRunning = executor.GetLiveRunningAccounts(matched);
            List<Account> toClose = matched.Where(liveRunning.Contains).ToList();

            int closed = toClose.Count > 0
                ? await executor.CloseAccountsAsync(toClose, ct).ConfigureAwait(false)
                : 0;

            ScheduleRunOutcome outcome = toClose.Count == 0
                ? ScheduleRunOutcome.Skipped
                : closed == toClose.Count
                    ? ScheduleRunOutcome.Success
                    : closed > 0
                        ? ScheduleRunOutcome.Partial
                        : ScheduleRunOutcome.Failed;

            string summary = toClose.Count == 0
                ? LocalizationService.Get("Sched_Run_NoLiveTabsToClose")
                : LocalizationService.Format("Sched_Run_ClosedSummary", closed, toClose.Count);

            return new ScheduleRunEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                ScheduleId = schedule.Id,
                ScheduleName = schedule.Name,
                TimestampUtc = _timeProvider.GetUtcNow(),
                Phase = phase,
                Outcome = outcome,
                TargetCount = toClose.Count,
                SucceededCount = closed,
                SkippedCount = Math.Max(0, toClose.Count - closed),
                Summary = summary
            };
        }

        private async Task<(ScheduleRunEntry Entry, bool IsErrorToast)> ExecuteOpenPlanAsync(
            ISchedulerActionExecutor executor,
            LaunchSchedulerExecutionHelper.ResolvedOpenPlan plan,
            CancellationToken ct)
        {
            if (plan.ToLaunch.Count == 0)
            {
                string skipSummary = plan.TotalMatchedCount == 0
                    ? LocalizationService.Get("Sched_Run_NoMatchingAccounts")
                    : plan.Phase == ScheduleRunPhase.CatchUpOpen && plan.AlreadyRunningCount > 0 && plan.SkippedInvalidCount == 0
                        ? LocalizationService.Format("Sched_Run_OpenedAllRunning", plan.AlreadyRunningCount)
                        : LocalizationService.Format("Sched_Run_NoEligibleAccounts", plan.SkippedInvalidCount + plan.AlreadyRunningCount);

                return (CreateOpenEntry(plan, ScheduleRunOutcome.Skipped, 0, plan.SkippedInvalidCount + plan.AlreadyRunningCount, skipSummary), false);
            }

            bool queueIdle = await executor.WaitForLaunchQueueIdleAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (!queueIdle)
            {
                int totalSkipped = plan.ToLaunch.Count + plan.SkippedInvalidCount + plan.AlreadyRunningCount;
                return (CreateOpenEntry(plan, ScheduleRunOutcome.Skipped, 0, totalSkipped, LocalizationService.Get("Sched_Run_QueueBusySkipped")), true);
            }

            if (plan.ToLaunch.Count > 1)
                executor.TryEnsureMultiRoblox(plan.ToLaunch.Count);

            (int succeeded, int attempted, bool cancelled, string detail) = await LaunchPlanAsync(executor, plan, ct).ConfigureAwait(false);

            if (!cancelled && plan.Schedule.PostLaunchArrange != ScheduleArrangeMode.None && succeeded > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), _timeProvider, ct).ConfigureAwait(false);
                executor.ArrangeWindows(plan.Schedule.PostLaunchArrange);
            }

            ScheduleRunOutcome outcome = LaunchSchedulerExecutionHelper.DeriveOpenOutcome(
                succeeded, attempted, cancelled, plan.SkippedInvalidCount);
            int skippedTotal = plan.SkippedInvalidCount + plan.AlreadyRunningCount + Math.Max(0, attempted - succeeded);
            string summary = LaunchSchedulerExecutionHelper.BuildOpenSummary(plan, succeeded, attempted, cancelled, detail);

            return (CreateOpenEntry(plan, outcome, succeeded, skippedTotal, summary), outcome == ScheduleRunOutcome.Failed);
        }

        private static Task<(int Succeeded, int Attempted, bool Cancelled, string Detail)> LaunchPlanAsync(
            ISchedulerActionExecutor executor,
            LaunchSchedulerExecutionHelper.ResolvedOpenPlan plan,
            CancellationToken ct)
        {
            if (plan.Schedule.LaunchMode == ScheduleLaunchMode.OpenHome)
                return executor.LaunchHomeAsync(plan.ToLaunch, ct);

            string rawJobId = (plan.Schedule.JobId ?? string.Empty).Trim();
            bool isVip = rawJobId.StartsWith("VIP:", StringComparison.OrdinalIgnoreCase);
            string cleanJobId = isVip ? rawJobId[4..].Trim() : rawJobId;
            return executor.LaunchServerAsync(plan.ToLaunch, plan.Schedule.PlaceId, cleanJobId, isVip, ct);
        }
    }
}
