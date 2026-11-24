#nullable enable
using System;
using System.Collections.Generic;

namespace Shiro
{
    public enum ScheduleRecurrence
    {
        OneTime = 0,
        Daily = 1,
        Weekly = 2
    }

    [Flags]
    public enum ScheduleDaysOfWeek
    {
        None = 0,
        Monday = 1,
        Tuesday = 2,
        Wednesday = 4,
        Thursday = 8,
        Friday = 16,
        Saturday = 32,
        Sunday = 64,
        Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
        Weekends = Saturday | Sunday,
        All = Weekdays | Weekends
    }

    public enum ScheduleTargetMode
    {
        SpecificAccounts = 0,
        Group = 1
    }

    public enum ScheduleLaunchMode
    {
        JoinServer = 0,
        OpenHome = 1
    }

    public enum ScheduleArrangeMode
    {
        None = 0,
        Auto = 1,
        Grid2x2 = 2,
        Grid3x2 = 3,
        Grid4x3 = 4,
        Cascade = 5
    }

    public sealed class ScheduleAccountRef
    {
        public long UserId { get; set; }
        public string Username { get; set; } = string.Empty;

        public ScheduleAccountRef Clone() => new()
        {
            UserId = UserId,
            Username = Username
        };
    }

    public sealed class LaunchSchedule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public string ColorHex { get; set; } = "#38BDF8";
        public ScheduleRecurrence Recurrence { get; set; } = ScheduleRecurrence.Daily;
        public ScheduleDaysOfWeek DaysOfWeek { get; set; } = ScheduleDaysOfWeek.All;
        public string? OneTimeDate { get; set; }
        public string StartTime { get; set; } = "08:00";
        public bool HasEndTime { get; set; } = true;
        public string? EndTime { get; set; } = "12:00";
        public ScheduleTargetMode TargetMode { get; set; } = ScheduleTargetMode.SpecificAccounts;
        public string TargetGroup { get; set; } = string.Empty;
        public List<ScheduleAccountRef> TargetAccounts { get; set; } = new();
        public ScheduleLaunchMode LaunchMode { get; set; } = ScheduleLaunchMode.JoinServer;
        public long PlaceId { get; set; }
        public string JobId { get; set; } = string.Empty;
        public bool CloseOtherTabsBeforeLaunch { get; set; }
        public int HandoffDelaySeconds { get; set; } = 2;
        public ScheduleArrangeMode PostLaunchArrange { get; set; } = ScheduleArrangeMode.None;
        public bool CatchUpIfActiveOnStartup { get; set; }
        public bool SendWebhookNotification { get; set; }
        public string? LastTriggeredStartKey { get; set; }
        public string? LastTriggeredEndKey { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }

        public LaunchSchedule Clone()
        {
            var accountsCopy = new List<ScheduleAccountRef>(TargetAccounts?.Count ?? 0);
            if (TargetAccounts != null)
            {
                foreach (ScheduleAccountRef account in TargetAccounts)
                {
                    if (account != null)
                        accountsCopy.Add(account.Clone());
                }
            }

            return new LaunchSchedule
            {
                Id = Id,
                Name = Name,
                IsEnabled = IsEnabled,
                ColorHex = ColorHex,
                Recurrence = Recurrence,
                DaysOfWeek = DaysOfWeek,
                OneTimeDate = OneTimeDate,
                StartTime = StartTime,
                HasEndTime = HasEndTime,
                EndTime = EndTime,
                TargetMode = TargetMode,
                TargetGroup = TargetGroup,
                TargetAccounts = accountsCopy,
                LaunchMode = LaunchMode,
                PlaceId = PlaceId,
                JobId = JobId,
                CloseOtherTabsBeforeLaunch = CloseOtherTabsBeforeLaunch,
                HandoffDelaySeconds = HandoffDelaySeconds,
                PostLaunchArrange = PostLaunchArrange,
                CatchUpIfActiveOnStartup = CatchUpIfActiveOnStartup,
                SendWebhookNotification = SendWebhookNotification,
                LastTriggeredStartKey = LastTriggeredStartKey,
                LastTriggeredEndKey = LastTriggeredEndKey,
                CreatedAtUtc = CreatedAtUtc,
                UpdatedAtUtc = UpdatedAtUtc
            };
        }
    }
}
