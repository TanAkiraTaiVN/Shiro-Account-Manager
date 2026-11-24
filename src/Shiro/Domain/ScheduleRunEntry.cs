#nullable enable
using System;
using System.Collections.Generic;

namespace Shiro
{
    public enum ScheduleRunPhase
    {
        Open = 0,
        Close = 1,
        ManualOpen = 2,
        ManualClose = 3,
        CatchUpOpen = 4
    }

    public enum ScheduleRunOutcome
    {
        Success = 0,
        Partial = 1,
        Skipped = 2,
        Failed = 3
    }

    public sealed class ScheduleRunEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ScheduleId { get; set; } = string.Empty;
        public string ScheduleName { get; set; } = string.Empty;
        public DateTimeOffset TimestampUtc { get; set; }
        public ScheduleRunPhase Phase { get; set; }
        public ScheduleRunOutcome Outcome { get; set; }
        public int TargetCount { get; set; }
        public int SucceededCount { get; set; }
        public int SkippedCount { get; set; }
        public string Summary { get; set; } = string.Empty;
    }

    public sealed class LaunchScheduleDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<LaunchSchedule> Schedules { get; set; } = new();
        public List<ScheduleRunEntry> History { get; set; } = new();
    }
}
