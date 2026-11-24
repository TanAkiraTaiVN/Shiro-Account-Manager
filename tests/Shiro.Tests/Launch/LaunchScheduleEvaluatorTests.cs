#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public sealed class LaunchScheduleEvaluatorTests
    {
        private static LaunchSchedule MakeValidSchedule(
            string id = "sched-1",
            string name = "Shift 1",
            string start = "08:00",
            string? end = "12:00",
            bool hasEnd = true,
            ScheduleRecurrence recurrence = ScheduleRecurrence.Daily,
            ScheduleDaysOfWeek days = ScheduleDaysOfWeek.All) => new()
        {
            Id = id,
            Name = name,
            IsEnabled = true,
            Recurrence = recurrence,
            DaysOfWeek = days,
            OneTimeDate = recurrence == ScheduleRecurrence.OneTime ? "2026-11-24" : null,
            StartTime = start,
            HasEndTime = hasEnd,
            EndTime = end,
            TargetMode = ScheduleTargetMode.SpecificAccounts,
            TargetAccounts = new List<ScheduleAccountRef>
            {
                new() { UserId = 1001, Username = "Alpha" }
            },
            LaunchMode = ScheduleLaunchMode.JoinServer,
            PlaceId = 2753915549
        };

        [Fact]
        public void Validate_RejectsEqualStartAndEndTime_InvalidTimeFormat_EmptyTarget_OrZeroPlaceIdWhenJoinServer()
        {
            Assert.Equal(90, LaunchScheduleEvaluatorHelper.DefaultGraceSeconds);
            Assert.Equal(15, LaunchScheduleEvaluatorHelper.OpenOnlyConflictFootprintMinutes);

            Assert.True(LaunchScheduleEvaluatorHelper.TryParseTime("8:05", out var t1));
            Assert.Equal(new TimeOnly(8, 5), t1);
            Assert.False(LaunchScheduleEvaluatorHelper.TryParseTime("24:00", out _));
            Assert.False(LaunchScheduleEvaluatorHelper.TryParseTime("08:00:00", out _));
            Assert.True(LaunchScheduleEvaluatorHelper.TryParseDate("2026-11-24", out var d1));
            Assert.Equal(new DateOnly(2026, 11, 24), d1);
            Assert.False(LaunchScheduleEvaluatorHelper.TryParseDate("2026/11/24", out _));

            LaunchSchedule missingName = MakeValidSchedule(name: "   ");
            Assert.Equal("Sched_Err_NameRequired", LaunchScheduleEvaluatorHelper.Validate(missingName).ErrorKey);

            LaunchSchedule badStart = MakeValidSchedule(start: "25:00");
            Assert.Equal("Sched_Err_InvalidStartTime", LaunchScheduleEvaluatorHelper.Validate(badStart).ErrorKey);

            LaunchSchedule badEnd = MakeValidSchedule(end: "99:99");
            Assert.Equal("Sched_Err_InvalidEndTime", LaunchScheduleEvaluatorHelper.Validate(badEnd).ErrorKey);

            LaunchSchedule equalTimes = MakeValidSchedule(start: "10:00", end: "10:00");
            ScheduleValidationResult eqRes = LaunchScheduleEvaluatorHelper.Validate(equalTimes);
            Assert.False(eqRes.IsValid);
            Assert.Equal("Sched_Err_SameStartEndTime", eqRes.ErrorKey);

            LaunchSchedule badOneTime = MakeValidSchedule(recurrence: ScheduleRecurrence.OneTime);
            badOneTime.OneTimeDate = "bad-date";
            Assert.Equal("Sched_Err_InvalidDate", LaunchScheduleEvaluatorHelper.Validate(badOneTime).ErrorKey);

            LaunchSchedule noDays = MakeValidSchedule(recurrence: ScheduleRecurrence.Weekly, days: ScheduleDaysOfWeek.None);
            Assert.Equal("Sched_Err_NoDaysSelected", LaunchScheduleEvaluatorHelper.Validate(noDays).ErrorKey);

            LaunchSchedule emptyGroup = MakeValidSchedule();
            emptyGroup.TargetMode = ScheduleTargetMode.Group;
            emptyGroup.TargetGroup = " ";
            Assert.Equal("Sched_Err_GroupRequired", LaunchScheduleEvaluatorHelper.Validate(emptyGroup).ErrorKey);

            LaunchSchedule emptyAccounts = MakeValidSchedule();
            emptyAccounts.TargetAccounts.Clear();
            Assert.Equal("Sched_Err_AccountsRequired", LaunchScheduleEvaluatorHelper.Validate(emptyAccounts).ErrorKey);

            LaunchSchedule zeroPlace = MakeValidSchedule();
            zeroPlace.PlaceId = 0;
            Assert.Equal("Sched_Err_PlaceIdRequired", LaunchScheduleEvaluatorHelper.Validate(zeroPlace).ErrorKey);

            LaunchSchedule validHome = MakeValidSchedule(start: "09:00", end: null, hasEnd: false);
            validHome.LaunchMode = ScheduleLaunchMode.OpenHome;
            validHome.PlaceId = 0;
            ScheduleValidationResult ok = LaunchScheduleEvaluatorHelper.Validate(validHome);
            Assert.True(ok.IsValid);
            Assert.Null(ok.ErrorKey);
        }

        [Fact]
        public void GetDueEvents_FiresStartAndEndWithin90sGraceWindow_AndRespectsOccurrenceDedupKeys()
        {
            var tz = TimeZoneInfo.Utc;
            LaunchSchedule sched = MakeValidSchedule(start: "08:00", end: "12:00");
            var list = new[] { sched };

            Assert.Empty(LaunchScheduleEvaluatorHelper.GetDueEvents(list, new DateTimeOffset(2026, 11, 24, 7, 59, 59, TimeSpan.Zero), tz));

            IReadOnlyList<ScheduleDueEvent> openAtStart = LaunchScheduleEvaluatorHelper.GetDueEvents(
                list, new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero), tz);
            ScheduleDueEvent openEv = Assert.Single(openAtStart);
            Assert.Equal(ScheduleRunPhase.Open, openEv.Phase);
            Assert.Equal("2026-11-24", openEv.OccurrenceKey);

            Assert.Single(LaunchScheduleEvaluatorHelper.GetDueEvents(
                list, new DateTimeOffset(2026, 11, 24, 8, 1, 29, TimeSpan.Zero), tz));
            Assert.Empty(LaunchScheduleEvaluatorHelper.GetDueEvents(
                list, new DateTimeOffset(2026, 11, 24, 8, 1, 30, TimeSpan.Zero), tz));

            sched.LastTriggeredStartKey = "2026-11-24";
            Assert.Empty(LaunchScheduleEvaluatorHelper.GetDueEvents(
                list, new DateTimeOffset(2026, 11, 24, 8, 0, 15, TimeSpan.Zero), tz));

            IReadOnlyList<ScheduleDueEvent> closeEvents = LaunchScheduleEvaluatorHelper.GetDueEvents(
                list, new DateTimeOffset(2026, 11, 24, 12, 0, 45, TimeSpan.Zero), tz);
            ScheduleDueEvent closeEv = Assert.Single(closeEvents);
            Assert.Equal(ScheduleRunPhase.Close, closeEv.Phase);
            Assert.Equal("2026-11-24", closeEv.OccurrenceKey);

            sched.LastTriggeredEndKey = "2026-11-24";
            Assert.Empty(LaunchScheduleEvaluatorHelper.GetDueEvents(
                list, new DateTimeOffset(2026, 11, 24, 12, 0, 50, TimeSpan.Zero), tz));

            // Next day occurrence still fires despite prior day's keys
            IReadOnlyList<ScheduleDueEvent> nextDay = LaunchScheduleEvaluatorHelper.GetDueEvents(
                list, new DateTimeOffset(2026, 11, 25, 8, 0, 10, TimeSpan.Zero), tz);
            Assert.Equal("2026-11-25", Assert.Single(nextDay).OccurrenceKey);
        }

        [Fact]
        public void GetDueEvents_OvernightWeeklyShift_FiresCloseOnNextMorningEvenWhenNextWeekdayUnchecked()
        {
            var tz = TimeZoneInfo.Utc;
            // 2026-11-27 is Friday; Saturday is intentionally unchecked
            LaunchSchedule night = MakeValidSchedule(
                id: "friday-night",
                name: "Friday Night",
                start: "22:00",
                end: "06:00",
                recurrence: ScheduleRecurrence.Weekly,
                days: ScheduleDaysOfWeek.Friday);

            Assert.True(LaunchScheduleEvaluatorHelper.IsOvernight(night));

            IReadOnlyList<ScheduleDueEvent> openEvents = LaunchScheduleEvaluatorHelper.GetDueEvents(
                new[] { night }, new DateTimeOffset(2026, 11, 27, 22, 0, 10, TimeSpan.Zero), tz);
            ScheduleDueEvent openEv = Assert.Single(openEvents);
            Assert.Equal(ScheduleRunPhase.Open, openEv.Phase);
            Assert.Equal("2026-11-27", openEv.OccurrenceKey);

            Assert.True(LaunchScheduleEvaluatorHelper.IsCurrentlyActive(
                night, new DateTimeOffset(2026, 11, 28, 3, 30, 0, TimeSpan.Zero), tz));
            Assert.False(LaunchScheduleEvaluatorHelper.IsCurrentlyActive(
                night, new DateTimeOffset(2026, 11, 28, 6, 0, 0, TimeSpan.Zero), tz));

            IReadOnlyList<ScheduleDueEvent> closeEvents = LaunchScheduleEvaluatorHelper.GetDueEvents(
                new[] { night }, new DateTimeOffset(2026, 11, 28, 6, 0, 20, TimeSpan.Zero), tz);
            ScheduleDueEvent closeEv = Assert.Single(closeEvents);
            Assert.Equal(ScheduleRunPhase.Close, closeEv.Phase);
            Assert.Equal("2026-11-27", closeEv.OccurrenceKey);
        }

        [Fact]
        public void GetDueEvents_ReturnsClosesOrderedBeforeOpens_AtExactHandoffBoundary()
        {
            var tz = TimeZoneInfo.Utc;
            LaunchSchedule afternoon = MakeValidSchedule(id: "b", name: "Afternoon Shift", start: "12:00", end: "18:00");
            LaunchSchedule morning = MakeValidSchedule(id: "a", name: "Morning Shift", start: "08:00", end: "12:00");
            LaunchSchedule overnight = MakeValidSchedule(id: "c", name: "Overnight Shift", start: "22:00", end: "12:00");

            IReadOnlyList<ScheduleDueEvent> events = LaunchScheduleEvaluatorHelper.GetDueEvents(
                new[] { afternoon, morning, overnight },
                new DateTimeOffset(2026, 11, 24, 12, 0, 5, TimeSpan.Zero),
                tz);

            Assert.Equal(3, events.Count);
            Assert.Equal(ScheduleRunPhase.Close, events[0].Phase);
            Assert.Equal(ScheduleRunPhase.Close, events[1].Phase);
            Assert.Equal(ScheduleRunPhase.Open, events[2].Phase);
            Assert.Equal("b", events[2].Schedule.Id);
        }

        [Fact]
        public void GetStartupCatchUpSchedules_SelectsOnlyOptInActiveSchedulesNotAlreadyTriggered()
        {
            var tz = TimeZoneInfo.Utc;
            var now = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

            LaunchSchedule eligibleSameDay = MakeValidSchedule(id: "c1", start: "08:00", end: "12:00");
            eligibleSameDay.CatchUpIfActiveOnStartup = true;

            LaunchSchedule eligibleOvernight = MakeValidSchedule(id: "c2", start: "22:00", end: "11:00");
            eligibleOvernight.CatchUpIfActiveOnStartup = true;

            LaunchSchedule optOut = MakeValidSchedule(id: "c3", start: "08:00", end: "12:00");
            optOut.CatchUpIfActiveOnStartup = false;

            LaunchSchedule openOnly = MakeValidSchedule(id: "c4", start: "08:00", end: null, hasEnd: false);
            openOnly.CatchUpIfActiveOnStartup = true;

            LaunchSchedule alreadyTriggered = MakeValidSchedule(id: "c5", start: "08:00", end: "12:00");
            alreadyTriggered.CatchUpIfActiveOnStartup = true;
            alreadyTriggered.LastTriggeredStartKey = "2026-11-24";

            LaunchSchedule disabled = MakeValidSchedule(id: "c6", start: "08:00", end: "12:00");
            disabled.CatchUpIfActiveOnStartup = true;
            disabled.IsEnabled = false;

            IReadOnlyList<LaunchSchedule> due = LaunchScheduleEvaluatorHelper.GetStartupCatchUpSchedules(
                new[] { eligibleSameDay, eligibleOvernight, optOut, openOnly, alreadyTriggered, disabled },
                now,
                tz);

            Assert.Equal(new[] { "c1", "c2" }, due.Select(s => s.Id).ToArray());
        }

        [Fact]
        public void IsWithinTriggerGraceWindow_DistinguishesEarlyManualRunFromInWindowRun()
        {
            var tz = TimeZoneInfo.Utc;
            LaunchSchedule sched = MakeValidSchedule(start: "23:59", end: "06:00");

            bool early = LaunchScheduleEvaluatorHelper.IsWithinTriggerGraceWindow(
                sched, ScheduleRunPhase.ManualOpen, new DateTimeOffset(2026, 11, 24, 23, 49, 0, TimeSpan.Zero), out string earlyKey, tz);
            Assert.False(early);
            Assert.Equal(string.Empty, earlyKey);

            // Crossing midnight within 90s of 23:59 start still belongs to 2026-11-24
            bool inStartWindow = LaunchScheduleEvaluatorHelper.IsWithinTriggerGraceWindow(
                sched, ScheduleRunPhase.ManualOpen, new DateTimeOffset(2026, 11, 25, 0, 0, 20, TimeSpan.Zero), out string startKey, tz);
            Assert.True(inStartWindow);
            Assert.Equal("2026-11-24", startKey);

            bool inCloseWindow = LaunchScheduleEvaluatorHelper.IsWithinTriggerGraceWindow(
                sched, ScheduleRunPhase.ManualClose, new DateTimeOffset(2026, 11, 25, 6, 0, 40, TimeSpan.Zero), out string closeKey, tz);
            Assert.True(inCloseWindow);
            Assert.Equal("2026-11-24", closeKey);

            bool expired = LaunchScheduleEvaluatorHelper.IsWithinTriggerGraceWindow(
                sched, ScheduleRunPhase.ManualClose, new DateTimeOffset(2026, 11, 25, 6, 1, 30, TimeSpan.Zero), out _, tz);
            Assert.False(expired);
        }

        [Fact]
        public void GetNextTransition_FindsNextStartOrEndAcrossWeekdaysAndOneTimeDates()
        {
            var tz = TimeZoneInfo.Utc;
            // 2026-11-24 is Tuesday
            LaunchSchedule monFri = MakeValidSchedule(
                id: "wk",
                name: "MonFri",
                start: "22:00",
                end: "06:00",
                recurrence: ScheduleRecurrence.Weekly,
                days: ScheduleDaysOfWeek.Monday | ScheduleDaysOfWeek.Friday);

            // Tuesday 03:00 inside Monday's overnight shift -> next transition is Close at Tuesday 06:00 (Monday key)
            ScheduleTransitionInfo? activeClose = LaunchScheduleEvaluatorHelper.GetNextTransition(
                monFri, new DateTimeOffset(2026, 11, 24, 3, 0, 0, TimeSpan.Zero), tz);
            Assert.NotNull(activeClose);
            Assert.Equal(ScheduleRunPhase.Close, activeClose!.Value.Phase);
            Assert.Equal(new DateTimeOffset(2026, 11, 24, 6, 0, 0, TimeSpan.Zero), activeClose.Value.OccurringAtLocal);
            Assert.Equal("2026-11-23", activeClose.Value.OccurrenceKey);

            // Tuesday 07:00 -> jumps across Wed/Thu to Friday 22:00 Open
            ScheduleTransitionInfo? nextFri = LaunchScheduleEvaluatorHelper.GetNextTransition(
                monFri, new DateTimeOffset(2026, 11, 24, 7, 0, 0, TimeSpan.Zero), tz);
            Assert.NotNull(nextFri);
            Assert.Equal(ScheduleRunPhase.Open, nextFri!.Value.Phase);
            Assert.Equal(new DateTimeOffset(2026, 11, 27, 22, 0, 0, TimeSpan.Zero), nextFri.Value.OccurringAtLocal);
            Assert.Equal("2026-11-27", nextFri.Value.OccurrenceKey);

            // Future OneTime schedule beyond 8 days is still discovered
            LaunchSchedule futureOneTime = MakeValidSchedule(
                id: "ot", name: "Event", start: "15:00", end: "17:00", recurrence: ScheduleRecurrence.OneTime);
            futureOneTime.OneTimeDate = "2026-12-10";
            ScheduleTransitionInfo? soonest = LaunchScheduleEvaluatorHelper.GetSoonestTransition(
                new[] { monFri, futureOneTime }, new DateTimeOffset(2026, 11, 24, 7, 0, 0, TimeSpan.Zero), tz);
            Assert.NotNull(soonest);
            Assert.Equal("wk", soonest!.Value.Schedule.Id);

            ScheduleTransitionInfo? onlyFuture = LaunchScheduleEvaluatorHelper.GetNextTransition(
                futureOneTime, new DateTimeOffset(2026, 11, 24, 7, 0, 0, TimeSpan.Zero), tz);
            Assert.NotNull(onlyFuture);
            Assert.Equal("2026-12-10", onlyFuture!.Value.OccurrenceKey);

            // Past OneTime returns null
            Assert.Null(LaunchScheduleEvaluatorHelper.GetNextTransition(
                futureOneTime, new DateTimeOffset(2026, 12, 10, 17, 0, 0, TimeSpan.Zero), tz));
        }

        [Fact]
        public void ProjectDayTimeline_SplitsOvernightCarryoverOntoNextDay_AndPacksConcurrentLanes()
        {
            // Friday 2026-11-27 overnight 22:00 -> 06:00 (Saturday unchecked)
            LaunchSchedule friNight = MakeValidSchedule(
                id: "fri",
                name: "Friday Night",
                start: "22:00",
                end: "06:00",
                recurrence: ScheduleRecurrence.Weekly,
                days: ScheduleDaysOfWeek.Friday);

            LaunchSchedule satMorning = MakeValidSchedule(
                id: "sat-am",
                name: "Sat Morning",
                start: "04:00",
                end: "08:00",
                recurrence: ScheduleRecurrence.Weekly,
                days: ScheduleDaysOfWeek.Saturday);

            LaunchSchedule satHandoff = MakeValidSchedule(
                id: "sat-mid",
                name: "Sat Midday",
                start: "06:00",
                end: "12:00",
                recurrence: ScheduleRecurrence.Weekly,
                days: ScheduleDaysOfWeek.Saturday);

            LaunchSchedule satOpenOnly = MakeValidSchedule(
                id: "sat-open",
                name: "Sat Pulse",
                start: "09:00",
                end: null,
                hasEnd: false,
                recurrence: ScheduleRecurrence.Weekly,
                days: ScheduleDaysOfWeek.Saturday);

            var friday = new DateOnly(2026, 11, 27);
            IReadOnlyList<ScheduleTimelineSegment> friSegs = LaunchScheduleEvaluatorHelper.ProjectDayTimeline(
                new[] { friNight, satMorning, satHandoff, satOpenOnly }, friday);
            ScheduleTimelineSegment friSeg = Assert.Single(friSegs);
            Assert.Equal(22 * 60, friSeg.StartMinute);
            Assert.Equal(1440, friSeg.EndMinute);
            Assert.False(friSeg.IsCarryoverFromPreviousDay);

            var saturday = new DateOnly(2026, 11, 28);
            IReadOnlyList<ScheduleTimelineSegment> satSegs = LaunchScheduleEvaluatorHelper.ProjectDayTimeline(
                new[] { friNight, satMorning, satHandoff, satOpenOnly },
                saturday,
                new HashSet<string>(StringComparer.Ordinal) { "fri", "sat-am" });

            Assert.Equal(4, satSegs.Count);
            ScheduleTimelineSegment carryover = satSegs.Single(s => s.ScheduleId == "fri");
            Assert.Equal(0, carryover.StartMinute);
            Assert.Equal(360, carryover.EndMinute);
            Assert.True(carryover.IsCarryoverFromPreviousDay);
            Assert.Equal(0, carryover.Lane);
            Assert.True(carryover.HasConflict);

            ScheduleTimelineSegment overlap = satSegs.Single(s => s.ScheduleId == "sat-am");
            Assert.Equal(1, overlap.Lane);
            Assert.True(overlap.HasConflict);

            // Exact handoff 06:00 reuses Lane 0
            ScheduleTimelineSegment handoff = satSegs.Single(s => s.ScheduleId == "sat-mid");
            Assert.Equal(0, handoff.Lane);
            Assert.False(handoff.HasConflict);

            ScheduleTimelineSegment openSeg = satSegs.Single(s => s.ScheduleId == "sat-open");
            Assert.True(openSeg.IsOpenOnly);
            Assert.Equal(9 * 60, openSeg.StartMinute);
            Assert.Equal(9 * 60 + 30, openSeg.EndMinute);
            Assert.Equal(1, openSeg.Lane);

            // Late open-only schedule (23:45) clips at 1440 on start day and never creates next-day carryover
            LaunchSchedule lateOpenOnly = MakeValidSchedule(
                id: "fri-open", name: "Fri Late", start: "23:45", end: null, hasEnd: false,
                recurrence: ScheduleRecurrence.Weekly, days: ScheduleDaysOfWeek.Friday);
            ScheduleTimelineSegment lateSeg = Assert.Single(LaunchScheduleEvaluatorHelper.ProjectDayTimeline(new[] { lateOpenOnly }, friday));
            Assert.Equal(23 * 60 + 45, lateSeg.StartMinute);
            Assert.Equal(1440, lateSeg.EndMinute);
            Assert.True(lateSeg.IsOpenOnly);
            Assert.Empty(LaunchScheduleEvaluatorHelper.ProjectDayTimeline(new[] { lateOpenOnly }, saturday));
        }

        [Fact]
        public void DetectConflicts_DetectsAccountOverlap_OpenOnlyFootprint_AndExclusiveCloseCollision()
        {
            var startDate = new DateOnly(2026, 11, 24); // Tuesday

            LaunchSchedule baseShift = MakeValidSchedule(id: "a", name: "Base", start: "08:00", end: "12:00");
            LaunchSchedule sharedOverlap = MakeValidSchedule(id: "b", name: "Overlap", start: "11:00", end: "14:00");
            LaunchSchedule disjointExclusive = MakeValidSchedule(id: "c", name: "Exclusive", start: "10:00", end: "13:00");
            disjointExclusive.CloseOtherTabsBeforeLaunch = true;

            LaunchSchedule openOnlyWithin15m = MakeValidSchedule(id: "d", name: "OpenPulse", start: "07:50", end: null, hasEnd: false);
            LaunchSchedule openOnlyBoundary = MakeValidSchedule(id: "e", name: "EarlyPulse", start: "07:45", end: null, hasEnd: false);
            LaunchSchedule exactHandoff = MakeValidSchedule(id: "f", name: "Handoff", start: "12:00", end: "15:00");
            exactHandoff.CloseOtherTabsBeforeLaunch = true;

            IReadOnlyCollection<string> ResolveAccounts(LaunchSchedule s) => s.Id switch
            {
                "a" => new[] { "acc-1", "acc-2" },
                "b" => new[] { "acc-2", "acc-3" },
                "c" => new[] { "acc-9" }, // disjoint, still collides via ExclusiveCloseCollision
                "d" => new[] { "acc-1" },
                "e" => new[] { "acc-1" }, // [07:45, 08:00) touches 08:00 without overlapping
                "f" => new[] { "acc-1" },
                _ => Array.Empty<string>()
            };

            IReadOnlyList<ScheduleConflictInfo> conflicts = LaunchScheduleEvaluatorHelper.DetectConflicts(
                new[] { baseShift, sharedOverlap, disjointExclusive, openOnlyWithin15m, openOnlyBoundary, exactHandoff },
                startDate,
                ResolveAccounts);

            // Expect conflicts on each of the 7 days, deduplicated per (A, B, Kind, Date)
            var day0 = conflicts.Where(c => c.Date == startDate).ToList();
            Assert.Contains(day0, c => c.Kind == ScheduleConflictKind.AccountOverlap &&
                ((c.ScheduleAId == "a" && c.ScheduleBId == "b") || (c.ScheduleAId == "b" && c.ScheduleBId == "a")) &&
                c.SharedAccountCount == 1);
            Assert.Contains(day0, c => c.Kind == ScheduleConflictKind.AccountOverlap &&
                ((c.ScheduleAId == "d" && c.ScheduleBId == "a") || (c.ScheduleAId == "a" && c.ScheduleBId == "d")) &&
                c.SharedAccountCount == 1);
            Assert.Contains(day0, c => c.Kind == ScheduleConflictKind.ExclusiveCloseCollision &&
                c.ScheduleAId == "a" && c.ScheduleBId == "c" && c.SharedAccountCount == 0);
            Assert.Contains(day0, c => c.Kind == ScheduleConflictKind.ExclusiveCloseCollision &&
                c.ScheduleAId == "b" && c.ScheduleBId == "f" && c.SharedAccountCount == 0);

            // EarlyPulse [07:45, 08:00) does not overlap Base [08:00, 12:00), only overlaps OpenPulse [07:50, 08:05)
            Assert.DoesNotContain(day0, c =>
                (c.ScheduleAId == "e" && c.ScheduleBId == "a") || (c.ScheduleAId == "a" && c.ScheduleBId == "e"));
            // Handoff 12:00 does NOT collide with Base 08:00..12:00
            Assert.DoesNotContain(day0, c =>
                c.ScheduleAId == "a" && c.ScheduleBId == "f");
            Assert.Equal(day0.Count * 7, conflicts.Count);

            // Future OneTime overnight shift (beyond 7d window) + midnight exclusive collision + 23:55 open-only carryover
            LaunchSchedule futureOvernight = MakeValidSchedule(id: "ot-night", name: "OT Night", start: "22:00", end: "06:00", recurrence: ScheduleRecurrence.OneTime);
            futureOvernight.OneTimeDate = "2026-12-20";
            LaunchSchedule midnightExclusive = MakeValidSchedule(id: "ot-mid", name: "OT Midnight", start: "00:00", end: "02:00", recurrence: ScheduleRecurrence.OneTime);
            midnightExclusive.OneTimeDate = "2026-12-21";
            midnightExclusive.CloseOtherTabsBeforeLaunch = true;
            LaunchSchedule lateOpenOnly = MakeValidSchedule(id: "ot-late", name: "OT Late", start: "23:55", end: null, hasEnd: false, recurrence: ScheduleRecurrence.OneTime);
            lateOpenOnly.OneTimeDate = "2026-12-20";

            IReadOnlyList<ScheduleConflictInfo> futureConflicts = LaunchScheduleEvaluatorHelper.DetectConflicts(
                new[] { futureOvernight, midnightExclusive, lateOpenOnly },
                startDate,
                _ => new[] { "acc-1" });
            var dec21 = new DateOnly(2026, 12, 21);
            Assert.Contains(futureConflicts, c => c.Date == dec21 && c.Kind == ScheduleConflictKind.ExclusiveCloseCollision &&
                c.ScheduleAId == "ot-night" && c.ScheduleBId == "ot-mid");
            Assert.Contains(futureConflicts, c => c.Date == dec21 && c.Kind == ScheduleConflictKind.AccountOverlap &&
                ((c.ScheduleAId == "ot-late" && c.ScheduleBId == "ot-mid") || (c.ScheduleAId == "ot-mid" && c.ScheduleBId == "ot-late")));

            // Overnight (22:00->06:00) overlapping long daytime (02:00->23:00) twice on the same day emits a single deduplicated AccountOverlap
            LaunchSchedule nightShift = MakeValidSchedule(id: "night", name: "Night", start: "22:00", end: "06:00");
            LaunchSchedule longDayShift = MakeValidSchedule(id: "day", name: "Long Day", start: "02:00", end: "23:00");
            IReadOnlyList<ScheduleConflictInfo> doubleOverlap = LaunchScheduleEvaluatorHelper.DetectConflicts(
                new[] { nightShift, longDayShift }, startDate, _ => new[] { "acc-1" });
            ScheduleConflictInfo singleDayOverlap = Assert.Single(doubleOverlap, c => c.Date == startDate && c.Kind == ScheduleConflictKind.AccountOverlap);
            Assert.Equal("day", singleDayOverlap.ScheduleAId);
            Assert.Equal("night", singleDayOverlap.ScheduleBId);
        }

        [Fact]
        public void NullOrDisabledOrEmptyInputs_AreHandledSafelyWithoutThrowing()
        {
            var tz = TimeZoneInfo.Utc;
            var now = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
            LaunchSchedule disabled = MakeValidSchedule(id: "dis", start: "08:00", end: "12:00");
            disabled.IsEnabled = false;
            LaunchSchedule openOnly = MakeValidSchedule(id: "oo", start: "08:00", end: null, hasEnd: false);

            Assert.False(LaunchScheduleEvaluatorHelper.TryParseTime(null, out _));
            Assert.False(LaunchScheduleEvaluatorHelper.TryParseDate(" ", out _));
            Assert.False(LaunchScheduleEvaluatorHelper.IsOvernight(openOnly));
            Assert.False(LaunchScheduleEvaluatorHelper.IsCurrentlyActive(disabled, now, tz));
            Assert.False(LaunchScheduleEvaluatorHelper.IsCurrentlyActive(openOnly, now, tz));
            Assert.False(LaunchScheduleEvaluatorHelper.IsWithinTriggerGraceWindow(openOnly, ScheduleRunPhase.ManualClose, now, out _, tz));
            Assert.Null(LaunchScheduleEvaluatorHelper.GetNextTransition(disabled, now, tz));
            Assert.Null(LaunchScheduleEvaluatorHelper.GetSoonestTransition(new[] { disabled }, now, tz));
            Assert.Empty(LaunchScheduleEvaluatorHelper.GetDueEvents(Array.Empty<LaunchSchedule>(), now, tz));
            Assert.Empty(LaunchScheduleEvaluatorHelper.GetStartupCatchUpSchedules(Array.Empty<LaunchSchedule>(), now, tz));
            Assert.Empty(LaunchScheduleEvaluatorHelper.ProjectDayTimeline(new[] { disabled }, new DateOnly(2026, 11, 24)));
            Assert.Empty(LaunchScheduleEvaluatorHelper.DetectConflicts(Array.Empty<LaunchSchedule>(), new DateOnly(2026, 11, 24), _ => Array.Empty<string>()));
        }

        [Fact]
        public void DstConversion_HandlesInvalidSpringForwardAndAmbiguousFallBackTimesWithoutThrowing()
        {
            TimeZoneInfo eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

            // Spring forward: 2026-03-08 02:30 does not exist in Eastern; shifts +1h to 03:30 -04:00
            LaunchSchedule springSched = MakeValidSchedule(
                id: "dst-spring",
                name: "Spring",
                start: "02:30",
                end: "05:00",
                recurrence: ScheduleRecurrence.OneTime);
            springSched.OneTimeDate = "2026-03-08";

            IReadOnlyList<ScheduleDueEvent> springDue = LaunchScheduleEvaluatorHelper.GetDueEvents(
                new[] { springSched },
                new DateTimeOffset(2026, 3, 8, 3, 30, 15, TimeSpan.FromHours(-4)),
                eastern);
            ScheduleDueEvent springEv = Assert.Single(springDue);
            Assert.Equal(ScheduleRunPhase.Open, springEv.Phase);
            Assert.Equal(new DateTimeOffset(2026, 3, 8, 3, 30, 0, TimeSpan.FromHours(-4)), springEv.ScheduledAtLocal);

            // Fall back: 2026-11-01 01:30 occurs twice; resolves deterministically via GetAmbiguousTimeOffsets[0]
            LaunchSchedule fallSched = MakeValidSchedule(
                id: "dst-fall",
                name: "Fall",
                start: "01:30",
                end: "04:00",
                recurrence: ScheduleRecurrence.OneTime);
            fallSched.OneTimeDate = "2026-11-01";

            TimeSpan firstOffset = eastern.GetAmbiguousTimeOffsets(new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified))[0];
            var fallNow = new DateTimeOffset(2026, 11, 1, 1, 30, 20, firstOffset);
            IReadOnlyList<ScheduleDueEvent> fallDue = LaunchScheduleEvaluatorHelper.GetDueEvents(
                new[] { fallSched }, fallNow, eastern);
            ScheduleDueEvent fallEv = Assert.Single(fallDue);
            Assert.Equal(ScheduleRunPhase.Open, fallEv.Phase);
            Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 30, 0, firstOffset), fallEv.ScheduledAtLocal);
        }
    }
}
