#nullable enable
using System;

namespace Shiro
{
    public enum ScheduleConflictKind
    {
        AccountOverlap = 0,
        ExclusiveCloseCollision = 1
    }

    public readonly record struct ScheduleDueEvent(
        LaunchSchedule Schedule,
        ScheduleRunPhase Phase,
        string OccurrenceKey,
        DateTimeOffset ScheduledAtLocal);

    public readonly record struct ScheduleTransitionInfo(
        LaunchSchedule Schedule,
        ScheduleRunPhase Phase,
        DateTimeOffset OccurringAtLocal,
        string OccurrenceKey);

    public readonly record struct ScheduleConflictInfo(
        string ScheduleAId,
        string ScheduleAName,
        string ScheduleBId,
        string ScheduleBName,
        ScheduleConflictKind Kind,
        int SharedAccountCount,
        DateOnly Date);

    public readonly record struct ScheduleTimelineSegment(
        string ScheduleId,
        string ScheduleName,
        string ColorHex,
        int StartMinute,
        int EndMinute,
        int Lane,
        bool IsCarryoverFromPreviousDay,
        bool IsOpenOnly,
        bool HasConflict);

    public readonly record struct ScheduleValidationResult(
        bool IsValid,
        string? ErrorKey);
}
