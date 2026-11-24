#nullable enable
using System;
using System.Collections.Generic;

namespace Shiro.Services
{
    public static partial class LaunchScheduleEvaluatorHelper
    {
        private const int MinutesPerDay = 1440;
        private const int OpenOnlyTimelineVisualMinutes = 30;

        private readonly record struct RawDaySlice(
            int ScheduleIndex,
            LaunchSchedule Schedule,
            int ClippedStartMinute,
            int ClippedEndMinute,
            int WindowStartRelative,
            int WindowEndRelative,
            bool HasStartOnDate,
            int StartOnDateMinute,
            bool HasActiveWindow,
            bool IsCarryoverFromPreviousDay,
            bool IsOpenOnly);

        public static IReadOnlyList<ScheduleTimelineSegment> ProjectDayTimeline(IReadOnlyList<LaunchSchedule> schedules, DateOnly date) =>
            ProjectDayTimeline(schedules, date, conflictingScheduleIds: null);

        public static IReadOnlyList<ScheduleTimelineSegment> ProjectDayTimeline(
            IReadOnlyList<LaunchSchedule> schedules, DateOnly date, IReadOnlySet<string>? conflictingScheduleIds)
        {
            if (schedules is null || schedules.Count == 0)
                return Array.Empty<ScheduleTimelineSegment>();

            List<RawDaySlice> slices = BuildDaySlices(schedules, date, OpenOnlyTimelineVisualMinutes);
            if (slices.Count == 0)
                return Array.Empty<ScheduleTimelineSegment>();

            slices.Sort(CompareTimelineSlices);

            var laneEndMinutes = new List<int>();
            var segments = new List<ScheduleTimelineSegment>(slices.Count);

            foreach (RawDaySlice slice in slices)
            {
                int assignedLane = PackLane(laneEndMinutes, slice.ClippedStartMinute, slice.ClippedEndMinute);
                string scheduleId = slice.Schedule.Id ?? string.Empty;

                segments.Add(new ScheduleTimelineSegment(
                    ScheduleId: scheduleId,
                    ScheduleName: slice.Schedule.Name ?? string.Empty,
                    ColorHex: string.IsNullOrWhiteSpace(slice.Schedule.ColorHex) ? "#38BDF8" : slice.Schedule.ColorHex,
                    StartMinute: slice.ClippedStartMinute,
                    EndMinute: slice.ClippedEndMinute,
                    Lane: assignedLane,
                    IsCarryoverFromPreviousDay: slice.IsCarryoverFromPreviousDay,
                    IsOpenOnly: slice.IsOpenOnly,
                    HasConflict: conflictingScheduleIds != null && conflictingScheduleIds.Contains(scheduleId)));
            }

            return segments;
        }

        public static IReadOnlyList<ScheduleConflictInfo> DetectConflicts(
            IReadOnlyList<LaunchSchedule> schedules,
            DateOnly startDate,
            Func<LaunchSchedule, IReadOnlyCollection<string>> resolveAccountKeys)
        {
            ArgumentNullException.ThrowIfNull(resolveAccountKeys);
            if (schedules is null || schedules.Count == 0)
                return Array.Empty<ScheduleConflictInfo>();

            SortedSet<DateOnly> datesToScan = CollectConflictScanDates(schedules, startDate);
            var accountCache = new Dictionary<int, HashSet<string>>();
            var dedup = new HashSet<(string AId, string BId, ScheduleConflictKind Kind, DateOnly Date)>();
            var conflicts = new List<ScheduleConflictInfo>();

            foreach (DateOnly date in datesToScan)
            {
                List<RawDaySlice> slices = BuildDaySlices(schedules, date, OpenOnlyConflictFootprintMinutes);
                if (slices.Count < 2)
                    continue;

                for (int i = 0; i < slices.Count; i++)
                {
                    RawDaySlice first = slices[i];
                    for (int j = i + 1; j < slices.Count; j++)
                    {
                        RawDaySlice second = slices[j];
                        if (ReferenceEquals(first.Schedule, second.Schedule) ||
                            string.Equals(first.Schedule.Id, second.Schedule.Id, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (first.ClippedStartMinute < second.ClippedEndMinute && second.ClippedStartMinute < first.ClippedEndMinute)
                        {
                            int sharedCount = CountSharedKeys(
                                GetOrResolveKeys(first, accountCache, resolveAccountKeys),
                                GetOrResolveKeys(second, accountCache, resolveAccountKeys));
                            if (sharedCount > 0)
                            {
                                (RawDaySlice left, RawDaySlice right) = OrderOverlapPair(first, second);
                                TryAddConflict(conflicts, dedup, left.Schedule, right.Schedule, ScheduleConflictKind.AccountOverlap, sharedCount, date);
                            }
                        }

                        TryAddExclusiveCollision(conflicts, dedup, accountCache, resolveAccountKeys, first, second, date);
                        TryAddExclusiveCollision(conflicts, dedup, accountCache, resolveAccountKeys, second, first, date);
                    }
                }
            }

            conflicts.Sort(CompareConflicts);
            return conflicts;
        }

        private static SortedSet<DateOnly> CollectConflictScanDates(IReadOnlyList<LaunchSchedule> schedules, DateOnly startDate)
        {
            var dates = new SortedSet<DateOnly>();
            DateOnly maxSevenDayLimit = DateOnly.MaxValue.AddDays(-6);
            for (int d = 0; d < 7; d++)
            {
                if (startDate > maxSevenDayLimit && d > 0)
                    break;
                DateOnly candidate = startDate.AddDays(d);
                dates.Add(candidate);
                if (candidate == DateOnly.MaxValue)
                    break;
            }

            foreach (LaunchSchedule schedule in schedules)
            {
                if (schedule is null || !schedule.IsEnabled || schedule.Recurrence != ScheduleRecurrence.OneTime)
                    continue;

                if (!TryParseDate(schedule.OneTimeDate, out DateOnly oneTimeDate) || oneTimeDate < startDate)
                    continue;

                dates.Add(oneTimeDate);
                if (TryParseTime(schedule.StartTime, out TimeOnly startTime) && oneTimeDate < DateOnly.MaxValue)
                {
                    bool carriesToNextDay = IsOvernight(schedule) ||
                        (!schedule.HasEndTime && ToMinutes(startTime) + OpenOnlyConflictFootprintMinutes > MinutesPerDay);
                    if (carriesToNextDay)
                        dates.Add(oneTimeDate.AddDays(1));
                }
            }

            return dates;
        }

        private static int PackLane(List<int> laneEndMinutes, int startMinute, int endMinute)
        {
            for (int lane = 0; lane < laneEndMinutes.Count; lane++)
            {
                if (laneEndMinutes[lane] <= startMinute)
                {
                    laneEndMinutes[lane] = endMinute;
                    return lane;
                }
            }

            laneEndMinutes.Add(endMinute);
            return laneEndMinutes.Count - 1;
        }

        private static List<RawDaySlice> BuildDaySlices(IReadOnlyList<LaunchSchedule> schedules, DateOnly date, int openOnlyMinutes)
        {
            var slices = new List<RawDaySlice>();
            DateOnly? yesterday = date > DateOnly.MinValue ? date.AddDays(-1) : null;

            for (int i = 0; i < schedules.Count; i++)
            {
                LaunchSchedule schedule = schedules[i];
                if (schedule is null || !schedule.IsEnabled || !TryParseTime(schedule.StartTime, out TimeOnly startTime))
                    continue;

                TimeOnly endTime = default;
                if (schedule.HasEndTime && (!TryParseTime(schedule.EndTime, out endTime) || endTime == startTime))
                    continue;

                int startMin = ToMinutes(startTime);

                if (yesterday.HasValue && MatchesOccurrenceDate(schedule, yesterday.Value))
                    AddCarryoverSlice(slices, i, schedule, startTime, endTime, startMin, openOnlyMinutes);

                if (MatchesOccurrenceDate(schedule, date))
                    AddSameDaySlice(slices, i, schedule, startTime, endTime, startMin, openOnlyMinutes);
            }

            return slices;
        }

        private static void AddCarryoverSlice(
            List<RawDaySlice> slices, int index, LaunchSchedule schedule, TimeOnly startTime, TimeOnly endTime, int startMin, int openOnlyMinutes)
        {
            if (schedule.HasEndTime && endTime < startTime)
            {
                int endMin = ToMinutes(endTime);
                if (endMin > 0)
                    slices.Add(new RawDaySlice(index, schedule, 0, endMin, startMin - MinutesPerDay, endMin, false, 0, true, true, false));
            }
            else if (!schedule.HasEndTime && startMin + openOnlyMinutes > MinutesPerDay)
            {
                int carryEnd = startMin + openOnlyMinutes - MinutesPerDay;
                slices.Add(new RawDaySlice(index, schedule, 0, carryEnd, startMin - MinutesPerDay, carryEnd, false, 0, false, true, true));
            }
        }

        private static void AddSameDaySlice(
            List<RawDaySlice> slices, int index, LaunchSchedule schedule, TimeOnly startTime, TimeOnly endTime, int startMin, int openOnlyMinutes)
        {
            if (schedule.HasEndTime)
            {
                int endMin = ToMinutes(endTime);
                int relativeEnd = endTime < startTime ? MinutesPerDay + endMin : endMin;
                int clippedEnd = endTime < startTime ? MinutesPerDay : endMin;
                slices.Add(new RawDaySlice(index, schedule, startMin, clippedEnd, startMin, relativeEnd, true, startMin, true, false, false));
                return;
            }

            int openClippedEnd = Math.Min(MinutesPerDay, startMin + openOnlyMinutes);
            slices.Add(new RawDaySlice(index, schedule, startMin, openClippedEnd, startMin, startMin + openOnlyMinutes, true, startMin, false, false, true));
        }

        private static void TryAddExclusiveCollision(
            List<ScheduleConflictInfo> conflicts,
            HashSet<(string AId, string BId, ScheduleConflictKind Kind, DateOnly Date)> dedup,
            Dictionary<int, HashSet<string>> accountCache,
            Func<LaunchSchedule, IReadOnlyCollection<string>> resolveAccountKeys,
            RawDaySlice activeSlice,
            RawDaySlice launchingSlice,
            DateOnly date)
        {
            if (!activeSlice.HasActiveWindow || !launchingSlice.HasStartOnDate || !launchingSlice.Schedule.CloseOtherTabsBeforeLaunch)
                return;

            int launchStart = launchingSlice.StartOnDateMinute;
            if (activeSlice.WindowStartRelative < launchStart && launchStart < activeSlice.WindowEndRelative)
            {
                int sharedCount = CountSharedKeys(
                    GetOrResolveKeys(activeSlice, accountCache, resolveAccountKeys),
                    GetOrResolveKeys(launchingSlice, accountCache, resolveAccountKeys));
                TryAddConflict(conflicts, dedup, activeSlice.Schedule, launchingSlice.Schedule, ScheduleConflictKind.ExclusiveCloseCollision, sharedCount, date);
            }
        }

        private static void TryAddConflict(
            List<ScheduleConflictInfo> conflicts,
            HashSet<(string AId, string BId, ScheduleConflictKind Kind, DateOnly Date)> dedup,
            LaunchSchedule scheduleA, LaunchSchedule scheduleB, ScheduleConflictKind kind, int sharedAccountCount, DateOnly date)
        {
            string aId = scheduleA.Id ?? string.Empty;
            string bId = scheduleB.Id ?? string.Empty;
            if (!dedup.Add((aId, bId, kind, date)))
                return;

            conflicts.Add(new ScheduleConflictInfo(aId, scheduleA.Name ?? string.Empty, bId, scheduleB.Name ?? string.Empty, kind, sharedAccountCount, date));
        }

        private static HashSet<string> GetOrResolveKeys(
            RawDaySlice slice, Dictionary<int, HashSet<string>> cache, Func<LaunchSchedule, IReadOnlyCollection<string>> resolveAccountKeys)
        {
            if (cache.TryGetValue(slice.ScheduleIndex, out HashSet<string>? existing))
                return existing;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyCollection<string>? raw = resolveAccountKeys(slice.Schedule);
            if (raw != null)
            {
                foreach (string key in raw)
                {
                    if (!string.IsNullOrWhiteSpace(key))
                        set.Add(key);
                }
            }

            cache[slice.ScheduleIndex] = set;
            return set;
        }

        private static int CountSharedKeys(HashSet<string> left, HashSet<string> right)
        {
            if (left.Count == 0 || right.Count == 0)
                return 0;

            (HashSet<string> smaller, HashSet<string> larger) = left.Count <= right.Count ? (left, right) : (right, left);
            int shared = 0;
            foreach (string key in smaller)
            {
                if (larger.Contains(key))
                    shared++;
            }

            return shared;
        }

        private static (RawDaySlice Left, RawDaySlice Right) OrderOverlapPair(RawDaySlice first, RawDaySlice second)
        {
            int cmp = first.ClippedStartMinute.CompareTo(second.ClippedStartMinute);
            if (cmp == 0)
                cmp = StringComparer.Ordinal.Compare(first.Schedule.Id, second.Schedule.Id);

            return cmp <= 0 ? (first, second) : (second, first);
        }

        private static int ToMinutes(TimeOnly time) => (time.Hour * 60) + time.Minute;

        private static int CompareTimelineSlices(RawDaySlice a, RawDaySlice b)
        {
            int cmp = a.ClippedStartMinute.CompareTo(b.ClippedStartMinute);
            if (cmp != 0)
                return cmp;

            cmp = a.ClippedEndMinute.CompareTo(b.ClippedEndMinute);
            if (cmp != 0)
                return cmp;

            cmp = (a.IsCarryoverFromPreviousDay ? 0 : 1).CompareTo(b.IsCarryoverFromPreviousDay ? 0 : 1);
            if (cmp != 0)
                return cmp;

            cmp = StringComparer.OrdinalIgnoreCase.Compare(a.Schedule.Name, b.Schedule.Name);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Schedule.Id, b.Schedule.Id);
        }

        private static int CompareConflicts(ScheduleConflictInfo a, ScheduleConflictInfo b)
        {
            int cmp = a.Date.CompareTo(b.Date);
            if (cmp != 0)
                return cmp;

            cmp = a.Kind.CompareTo(b.Kind);
            if (cmp != 0)
                return cmp;

            cmp = StringComparer.OrdinalIgnoreCase.Compare(a.ScheduleAName, b.ScheduleAName);
            if (cmp != 0)
                return cmp;

            cmp = StringComparer.OrdinalIgnoreCase.Compare(a.ScheduleBName, b.ScheduleBName);
            if (cmp != 0)
                return cmp;

            cmp = StringComparer.Ordinal.Compare(a.ScheduleAId, b.ScheduleAId);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.ScheduleBId, b.ScheduleBId);
        }
    }
}
