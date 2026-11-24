#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Shiro.Services
{
    /// <summary>
    /// Pure, stateless evaluation, timeline projection, and conflict detection helper for <see cref="LaunchSchedule"/>.
    /// </summary>
    public static partial class LaunchScheduleEvaluatorHelper
    {
        public const int DefaultGraceSeconds = 90;
        public const int OpenOnlyConflictFootprintMinutes = 15;

        private static readonly string[] TimeFormats = ["HH:mm", "H:mm"];

        public static bool TryParseTime(string? text, out TimeOnly time)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                time = default;
                return false;
            }

            return TimeOnly.TryParseExact(text.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
        }

        public static bool TryParseDate(string? text, out DateOnly date)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                date = default;
                return false;
            }

            return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        public static bool IsOvernight(LaunchSchedule schedule)
        {
            if (schedule is null || !schedule.HasEndTime)
                return false;

            return TryParseTime(schedule.StartTime, out TimeOnly start)
                && TryParseTime(schedule.EndTime, out TimeOnly end)
                && end < start;
        }

        public static ScheduleValidationResult Validate(LaunchSchedule schedule)
        {
            ArgumentNullException.ThrowIfNull(schedule);

            if (string.IsNullOrWhiteSpace(schedule.Name))
                return new ScheduleValidationResult(false, "Sched_Err_NameRequired");

            if (!TryParseTime(schedule.StartTime, out TimeOnly start))
                return new ScheduleValidationResult(false, "Sched_Err_InvalidStartTime");

            if (schedule.HasEndTime)
            {
                if (!TryParseTime(schedule.EndTime, out TimeOnly end))
                    return new ScheduleValidationResult(false, "Sched_Err_InvalidEndTime");

                if (end == start)
                    return new ScheduleValidationResult(false, "Sched_Err_SameStartEndTime");
            }

            if (schedule.Recurrence == ScheduleRecurrence.OneTime && !TryParseDate(schedule.OneTimeDate, out _))
                return new ScheduleValidationResult(false, "Sched_Err_InvalidDate");

            if (schedule.Recurrence == ScheduleRecurrence.Weekly &&
                (schedule.DaysOfWeek & ScheduleDaysOfWeek.All) == ScheduleDaysOfWeek.None)
            {
                return new ScheduleValidationResult(false, "Sched_Err_NoDaysSelected");
            }

            if (schedule.TargetMode == ScheduleTargetMode.Group && string.IsNullOrWhiteSpace(schedule.TargetGroup))
                return new ScheduleValidationResult(false, "Sched_Err_GroupRequired");

            if (schedule.TargetMode == ScheduleTargetMode.SpecificAccounts &&
                (schedule.TargetAccounts == null || schedule.TargetAccounts.Count == 0))
            {
                return new ScheduleValidationResult(false, "Sched_Err_AccountsRequired");
            }

            if (schedule.LaunchMode == ScheduleLaunchMode.JoinServer && schedule.PlaceId <= 0)
                return new ScheduleValidationResult(false, "Sched_Err_PlaceIdRequired");

            return new ScheduleValidationResult(true, null);
        }

        public static IReadOnlyList<ScheduleDueEvent> GetDueEvents(
            IReadOnlyList<LaunchSchedule> schedules, DateTimeOffset localNow, TimeZoneInfo? tz = null)
        {
            if (schedules is null || schedules.Count == 0)
                return Array.Empty<ScheduleDueEvent>();

            (TimeZoneInfo resolvedTz, DateTimeOffset zonedNow, DateOnly firstDate, DateOnly? secondDate) = ResolveNow(localNow, tz);
            TimeSpan grace = TimeSpan.FromSeconds(DefaultGraceSeconds);
            List<ScheduleDueEvent>? events = null;

            foreach (LaunchSchedule schedule in schedules)
            {
                if (schedule is null || !schedule.IsEnabled)
                    continue;

                EvaluateDueDate(schedule, firstDate, resolvedTz, zonedNow, grace, ref events);
                if (secondDate.HasValue)
                    EvaluateDueDate(schedule, secondDate.Value, resolvedTz, zonedNow, grace, ref events);
            }

            if (events is null)
                return Array.Empty<ScheduleDueEvent>();

            if (events.Count > 1)
                events.Sort(CompareDueEvents);

            return events;
        }

        public static IReadOnlyList<LaunchSchedule> GetStartupCatchUpSchedules(
            IReadOnlyList<LaunchSchedule> schedules, DateTimeOffset localNow, TimeZoneInfo? tz = null)
        {
            if (schedules is null || schedules.Count == 0)
                return Array.Empty<LaunchSchedule>();

            (TimeZoneInfo resolvedTz, DateTimeOffset zonedNow, DateOnly firstDate, DateOnly? secondDate) = ResolveNow(localNow, tz);
            List<LaunchSchedule>? result = null;

            foreach (LaunchSchedule schedule in schedules)
            {
                if (schedule is null || !schedule.IsEnabled || !schedule.CatchUpIfActiveOnStartup || !schedule.HasEndTime)
                    continue;

                if (IsCatchUpCandidateForDate(schedule, firstDate, resolvedTz, zonedNow) ||
                    (secondDate.HasValue && IsCatchUpCandidateForDate(schedule, secondDate.Value, resolvedTz, zonedNow)))
                {
                    (result ??= new List<LaunchSchedule>()).Add(schedule);
                }
            }

            return result ?? (IReadOnlyList<LaunchSchedule>)Array.Empty<LaunchSchedule>();
        }

        public static bool IsWithinTriggerGraceWindow(
            LaunchSchedule schedule, ScheduleRunPhase phase, DateTimeOffset localNow, out string occurrenceKey, TimeZoneInfo? tz = null)
        {
            occurrenceKey = string.Empty;
            if (schedule is null)
                return false;

            bool isEndPhase = phase is ScheduleRunPhase.Close or ScheduleRunPhase.ManualClose;
            bool isStartPhase = phase is ScheduleRunPhase.Open or ScheduleRunPhase.ManualOpen or ScheduleRunPhase.CatchUpOpen;
            if ((!isEndPhase && !isStartPhase) || (isEndPhase && !schedule.HasEndTime))
                return false;

            (TimeZoneInfo resolvedTz, DateTimeOffset zonedNow, DateOnly firstDate, DateOnly? secondDate) = ResolveNow(localNow, tz);
            TimeSpan grace = TimeSpan.FromSeconds(DefaultGraceSeconds);

            return TryMatchGraceDate(schedule, firstDate, isEndPhase, resolvedTz, zonedNow, grace, out occurrenceKey) ||
                   (secondDate.HasValue && TryMatchGraceDate(schedule, secondDate.Value, isEndPhase, resolvedTz, zonedNow, grace, out occurrenceKey));
        }

        public static bool IsCurrentlyActive(LaunchSchedule schedule, DateTimeOffset localNow, TimeZoneInfo? tz = null)
        {
            if (schedule is null || !schedule.IsEnabled || !schedule.HasEndTime)
                return false;

            (TimeZoneInfo resolvedTz, DateTimeOffset zonedNow, DateOnly firstDate, DateOnly? secondDate) = ResolveNow(localNow, tz);
            return IsActiveForDate(schedule, firstDate, resolvedTz, zonedNow) ||
                   (secondDate.HasValue && IsActiveForDate(schedule, secondDate.Value, resolvedTz, zonedNow));
        }

        public static ScheduleTransitionInfo? GetNextTransition(LaunchSchedule schedule, DateTimeOffset localNow, TimeZoneInfo? tz = null)
        {
            if (schedule is null || !schedule.IsEnabled)
                return null;

            (TimeZoneInfo resolvedTz, DateTimeOffset zonedNow, DateOnly firstDate, DateOnly? secondDate) = ResolveNow(localNow, tz);
            DateOnly today = secondDate ?? firstDate;
            ScheduleTransitionInfo? best = null;

            if (schedule.Recurrence == ScheduleRecurrence.OneTime)
            {
                if (TryParseDate(schedule.OneTimeDate, out DateOnly targetDate))
                    ConsiderTransitionForDate(schedule, targetDate, resolvedTz, zonedNow, ref best);
                return best;
            }

            DateOnly maxDate = DateOnly.MaxValue.AddDays(-8);
            int startOffset = secondDate.HasValue ? -1 : 0;
            for (int offset = startOffset; offset <= 8; offset++)
            {
                if (offset > 0 && today > maxDate)
                    break;

                ConsiderTransitionForDate(schedule, today.AddDays(offset), resolvedTz, zonedNow, ref best);
            }

            return best;
        }

        public static ScheduleTransitionInfo? GetSoonestTransition(
            IReadOnlyList<LaunchSchedule> schedules, DateTimeOffset localNow, TimeZoneInfo? tz = null)
        {
            if (schedules is null || schedules.Count == 0)
                return null;

            ScheduleTransitionInfo? best = null;
            foreach (LaunchSchedule schedule in schedules)
            {
                ScheduleTransitionInfo? candidate = GetNextTransition(schedule, localNow, tz);
                if (candidate.HasValue && (!best.HasValue || CompareTransitions(candidate.Value, best.Value) < 0))
                    best = candidate;
            }

            return best;
        }

        private static void EvaluateDueDate(
            LaunchSchedule schedule, DateOnly occDate, TimeZoneInfo tz, DateTimeOffset zonedNow, TimeSpan grace, ref List<ScheduleDueEvent>? events)
        {
            if (!TryGetMatchedOccurrenceWindow(schedule, occDate, tz, out DateTimeOffset startLocal, out DateTimeOffset? endLocal, out string occKey))
                return;

            if (zonedNow >= startLocal && zonedNow < startLocal + grace &&
                !string.Equals(schedule.LastTriggeredStartKey, occKey, StringComparison.Ordinal))
            {
                (events ??= new List<ScheduleDueEvent>()).Add(new ScheduleDueEvent(schedule, ScheduleRunPhase.Open, occKey, startLocal));
            }

            if (endLocal.HasValue && zonedNow >= endLocal.Value && zonedNow < endLocal.Value + grace &&
                !string.Equals(schedule.LastTriggeredEndKey, occKey, StringComparison.Ordinal))
            {
                (events ??= new List<ScheduleDueEvent>()).Add(new ScheduleDueEvent(schedule, ScheduleRunPhase.Close, occKey, endLocal.Value));
            }
        }

        private static bool IsCatchUpCandidateForDate(LaunchSchedule schedule, DateOnly occDate, TimeZoneInfo tz, DateTimeOffset zonedNow)
        {
            if (!TryGetMatchedOccurrenceWindow(schedule, occDate, tz, out DateTimeOffset startLocal, out DateTimeOffset? endLocal, out string occKey))
                return false;

            return endLocal.HasValue && zonedNow >= startLocal && zonedNow < endLocal.Value &&
                   !string.Equals(schedule.LastTriggeredStartKey, occKey, StringComparison.Ordinal);
        }

        private static bool TryMatchGraceDate(
            LaunchSchedule schedule, DateOnly occDate, bool isEndPhase, TimeZoneInfo tz, DateTimeOffset zonedNow, TimeSpan grace, out string occurrenceKey)
        {
            occurrenceKey = string.Empty;
            if (!TryGetMatchedOccurrenceWindow(schedule, occDate, tz, out DateTimeOffset startLocal, out DateTimeOffset? endLocal, out string occKey))
                return false;

            DateTimeOffset? targetAt = isEndPhase ? endLocal : startLocal;
            if (!targetAt.HasValue || zonedNow < targetAt.Value || zonedNow >= targetAt.Value + grace)
                return false;

            occurrenceKey = occKey;
            return true;
        }

        private static bool IsActiveForDate(LaunchSchedule schedule, DateOnly occDate, TimeZoneInfo tz, DateTimeOffset zonedNow)
        {
            return TryGetMatchedOccurrenceWindow(schedule, occDate, tz, out DateTimeOffset startLocal, out DateTimeOffset? endLocal, out _) &&
                   endLocal.HasValue && zonedNow >= startLocal && zonedNow < endLocal.Value;
        }

        private static void ConsiderTransitionForDate(
            LaunchSchedule schedule, DateOnly occDate, TimeZoneInfo tz, DateTimeOffset zonedNow, ref ScheduleTransitionInfo? best)
        {
            if (!TryGetMatchedOccurrenceWindow(schedule, occDate, tz, out DateTimeOffset startLocal, out DateTimeOffset? endLocal, out string occKey))
                return;

            if (startLocal > zonedNow)
            {
                var startCandidate = new ScheduleTransitionInfo(schedule, ScheduleRunPhase.Open, startLocal, occKey);
                if (!best.HasValue || CompareTransitions(startCandidate, best.Value) < 0)
                    best = startCandidate;
            }

            if (endLocal.HasValue && endLocal.Value > zonedNow)
            {
                var endCandidate = new ScheduleTransitionInfo(schedule, ScheduleRunPhase.Close, endLocal.Value, occKey);
                if (!best.HasValue || CompareTransitions(endCandidate, best.Value) < 0)
                    best = endCandidate;
            }
        }

        private static (TimeZoneInfo ResolvedTz, DateTimeOffset ZonedNow, DateOnly FirstDate, DateOnly? SecondDate) ResolveNow(
            DateTimeOffset localNow, TimeZoneInfo? tz)
        {
            TimeZoneInfo resolvedTz = tz ?? TimeZoneInfo.Local;
            DateTimeOffset zonedNow = TimeZoneInfo.ConvertTime(localNow, resolvedTz);
            DateOnly today = DateOnly.FromDateTime(zonedNow.DateTime);
            return today > DateOnly.MinValue
                ? (resolvedTz, zonedNow, today.AddDays(-1), today)
                : (resolvedTz, zonedNow, today, null);
        }

        private static bool MatchesOccurrenceDate(LaunchSchedule schedule, DateOnly date) =>
            schedule.Recurrence switch
            {
                ScheduleRecurrence.Daily => true,
                ScheduleRecurrence.OneTime => TryParseDate(schedule.OneTimeDate, out DateOnly targetDate) && targetDate == date,
                ScheduleRecurrence.Weekly => (schedule.DaysOfWeek & ToScheduleDayFlag(date.DayOfWeek)) != ScheduleDaysOfWeek.None,
                _ => false
            };

        private static ScheduleDaysOfWeek ToScheduleDayFlag(DayOfWeek dayOfWeek) =>
            dayOfWeek switch
            {
                DayOfWeek.Monday => ScheduleDaysOfWeek.Monday,
                DayOfWeek.Tuesday => ScheduleDaysOfWeek.Tuesday,
                DayOfWeek.Wednesday => ScheduleDaysOfWeek.Wednesday,
                DayOfWeek.Thursday => ScheduleDaysOfWeek.Thursday,
                DayOfWeek.Friday => ScheduleDaysOfWeek.Friday,
                DayOfWeek.Saturday => ScheduleDaysOfWeek.Saturday,
                DayOfWeek.Sunday => ScheduleDaysOfWeek.Sunday,
                _ => ScheduleDaysOfWeek.None
            };

        private static bool TryGetMatchedOccurrenceWindow(
            LaunchSchedule schedule,
            DateOnly occurrenceDate,
            TimeZoneInfo tz,
            out DateTimeOffset startLocal,
            out DateTimeOffset? endLocal,
            out string occurrenceKey)
        {
            startLocal = default;
            endLocal = null;
            occurrenceKey = string.Empty;

            if (!MatchesOccurrenceDate(schedule, occurrenceDate) || !TryParseTime(schedule.StartTime, out TimeOnly startTime))
                return false;

            if (schedule.HasEndTime)
            {
                if (!TryParseTime(schedule.EndTime, out TimeOnly endTime) || endTime == startTime)
                    return false;

                DateOnly endDate = endTime < startTime && occurrenceDate < DateOnly.MaxValue
                    ? occurrenceDate.AddDays(1)
                    : occurrenceDate;
                endLocal = ResolveLocalTime(endDate, endTime, tz);
            }

            startLocal = ResolveLocalTime(occurrenceDate, startTime, tz);
            occurrenceKey = occurrenceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        private static DateTimeOffset ResolveLocalTime(DateOnly date, TimeOnly time, TimeZoneInfo tz)
        {
            var localDt = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
            if (tz.IsInvalidTime(localDt))
                localDt = localDt.AddHours(1);

            TimeSpan offset = tz.IsAmbiguousTime(localDt)
                ? tz.GetAmbiguousTimeOffsets(localDt)[0]
                : tz.GetUtcOffset(localDt);

            return new DateTimeOffset(localDt, offset);
        }

        private static int CompareDueEvents(ScheduleDueEvent a, ScheduleDueEvent b)
        {
            int cmp = (a.Phase == ScheduleRunPhase.Close ? 0 : 1).CompareTo(b.Phase == ScheduleRunPhase.Close ? 0 : 1);
            if (cmp != 0)
                return cmp;

            cmp = a.ScheduledAtLocal.CompareTo(b.ScheduledAtLocal);
            if (cmp != 0)
                return cmp;

            cmp = StringComparer.OrdinalIgnoreCase.Compare(a.Schedule.Name, b.Schedule.Name);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Schedule.Id, b.Schedule.Id);
        }

        private static int CompareTransitions(ScheduleTransitionInfo a, ScheduleTransitionInfo b)
        {
            int cmp = a.OccurringAtLocal.CompareTo(b.OccurringAtLocal);
            if (cmp != 0)
                return cmp;

            cmp = (a.Phase == ScheduleRunPhase.Close ? 0 : 1).CompareTo(b.Phase == ScheduleRunPhase.Close ? 0 : 1);
            if (cmp != 0)
                return cmp;

            cmp = StringComparer.OrdinalIgnoreCase.Compare(a.Schedule.Name, b.Schedule.Name);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Schedule.Id, b.Schedule.Id);
        }
    }
}
