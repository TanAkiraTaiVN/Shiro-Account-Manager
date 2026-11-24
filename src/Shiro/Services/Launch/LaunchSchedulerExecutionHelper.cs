#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Shiro.Services
{
    internal static class LaunchSchedulerExecutionHelper
    {
        internal sealed record ResolvedOpenPlan(
            LaunchSchedule Schedule,
            ScheduleRunPhase Phase,
            int TotalMatchedCount,
            int SkippedInvalidCount,
            int AlreadyRunningCount,
            List<Account> ToLaunch,
            List<Account> AccountsToCloseFirst);

        public static ResolvedOpenPlan PrepareOpenPlan(
            ISchedulerActionExecutor executor,
            LaunchSchedule schedule,
            ScheduleRunPhase phase,
            IReadOnlyList<Account> allAccounts)
        {
            List<Account> matched = ResolveMatchedAccounts(schedule, allAccounts);
            int missingRefs = schedule.TargetMode == ScheduleTargetMode.SpecificAccounts
                ? Math.Max(0, (schedule.TargetAccounts?.Count ?? 0) - matched.Count)
                : 0;

            var eligible = new List<Account>(matched.Count);
            int ineligibleCount = 0;
            foreach (Account acc in matched)
            {
                if (acc.Valid && !acc.IsBannedOrLocked && string.IsNullOrWhiteSpace(acc.AccountStatusIssue))
                    eligible.Add(acc);
                else
                    ineligibleCount++;
            }

            int skippedInvalid = ineligibleCount + missingRefs;
            int totalMatched = matched.Count + missingRefs;
            var toCloseFirst = new List<Account>();

            if (schedule.CloseOtherTabsBeforeLaunch && allAccounts.Count > 0)
            {
                var targetSet = new HashSet<Account>(matched, ReferenceEqualityComparer.Instance);
                List<Account> nonTargets = allAccounts.Where(a => a != null && !targetSet.Contains(a)).ToList();
                if (nonTargets.Count > 0)
                {
                    IReadOnlySet<Account> liveNonTargets = executor.GetLiveRunningAccounts(nonTargets);
                    toCloseFirst.AddRange(nonTargets.Where(liveNonTargets.Contains));
                }
            }

            int alreadyRunningCount = 0;
            List<Account> toLaunch;
            if (eligible.Count == 0)
            {
                toLaunch = new List<Account>();
            }
            else
            {
                IReadOnlySet<Account> liveEligible = executor.GetLiveRunningAccounts(eligible);
                if (phase == ScheduleRunPhase.CatchUpOpen)
                {
                    toLaunch = new List<Account>(eligible.Count);
                    foreach (Account acc in eligible)
                    {
                        if (liveEligible.Contains(acc))
                            alreadyRunningCount++;
                        else
                            toLaunch.Add(acc);
                    }
                }
                else
                {
                    foreach (Account acc in eligible)
                    {
                        if (liveEligible.Contains(acc))
                            toCloseFirst.Add(acc);
                    }
                    toLaunch = eligible;
                }
            }

            return new ResolvedOpenPlan(schedule, phase, totalMatched, skippedInvalid, alreadyRunningCount, toLaunch, toCloseFirst);
        }

        public static List<Account> ResolveMatchedAccounts(LaunchSchedule schedule, IReadOnlyList<Account> allAccounts)
        {
            var matched = new List<Account>();
            if (allAccounts == null || allAccounts.Count == 0)
                return matched;

            var seen = new HashSet<Account>(ReferenceEqualityComparer.Instance);
            if (schedule.TargetMode == ScheduleTargetMode.Group)
            {
                string targetGroup = schedule.TargetGroup ?? string.Empty;
                foreach (Account acc in allAccounts)
                {
                    if (acc != null &&
                        string.Equals(acc.Group, targetGroup, StringComparison.OrdinalIgnoreCase) &&
                        seen.Add(acc))
                    {
                        matched.Add(acc);
                    }
                }

                return matched;
            }

            if (schedule.TargetAccounts == null || schedule.TargetAccounts.Count == 0)
                return matched;

            foreach (ScheduleAccountRef target in schedule.TargetAccounts)
            {
                if (target == null)
                    continue;

                Account? found = null;
                if (target.UserId > 0)
                    found = allAccounts.FirstOrDefault(a => a != null && a.UserID == target.UserId);

                if (found == null && !string.IsNullOrWhiteSpace(target.Username))
                {
                    found = allAccounts.FirstOrDefault(a => a != null &&
                        string.Equals(a.Username, target.Username, StringComparison.OrdinalIgnoreCase));
                }

                if (found != null && seen.Add(found))
                    matched.Add(found);
            }

            return matched;
        }

        public static ScheduleRunOutcome DeriveOpenOutcome(int succeeded, int attempted, bool cancelled, int skippedInvalid)
        {
            if (cancelled)
                return succeeded > 0 ? ScheduleRunOutcome.Partial : ScheduleRunOutcome.Failed;
            if (attempted == 0)
                return ScheduleRunOutcome.Skipped;
            if (succeeded == 0)
                return ScheduleRunOutcome.Failed;
            if (succeeded < attempted || skippedInvalid > 0)
                return ScheduleRunOutcome.Partial;
            return ScheduleRunOutcome.Success;
        }

        public static string BuildOpenSummary(ResolvedOpenPlan plan, int succeeded, int attempted, bool cancelled, string detail)
        {
            string baseSummary;
            if (cancelled)
            {
                baseSummary = LocalizationService.Format("Sched_Run_Cancelled", succeeded, attempted);
            }
            else if (plan.Phase == ScheduleRunPhase.CatchUpOpen && plan.AlreadyRunningCount > 0)
            {
                int failedOrInvalid = plan.SkippedInvalidCount + Math.Max(0, attempted - succeeded);
                baseSummary = failedOrInvalid > 0
                    ? LocalizationService.Format("Sched_Run_OpenedPartialCatchUp", succeeded, attempted, failedOrInvalid, plan.AlreadyRunningCount)
                    : LocalizationService.Format("Sched_Run_OpenedCatchUp", succeeded, attempted, plan.AlreadyRunningCount);
            }
            else
            {
                int failedOrInvalid = plan.SkippedInvalidCount + Math.Max(0, attempted - succeeded);
                baseSummary = failedOrInvalid > 0
                    ? LocalizationService.Format("Sched_Run_OpenedPartialSummary", succeeded, plan.TotalMatchedCount, failedOrInvalid)
                    : LocalizationService.Format("Sched_Run_OpenedSummary", succeeded, plan.TotalMatchedCount);
            }

            return string.IsNullOrWhiteSpace(detail) || cancelled
                ? baseSummary
                : $"{baseSummary} ({detail})";
        }

        public static string ResolveCatchUpOccurrenceKey(LaunchSchedule schedule, DateTimeOffset localNow, TimeZoneInfo localTimeZone)
        {
            ScheduleTransitionInfo? next = LaunchScheduleEvaluatorHelper.GetNextTransition(schedule, localNow, localTimeZone);
            if (next.HasValue && next.Value.Phase == ScheduleRunPhase.Close && !string.IsNullOrEmpty(next.Value.OccurrenceKey))
                return next.Value.OccurrenceKey;

            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(localNow, localTimeZone).DateTime)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }
}
