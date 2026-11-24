#nullable enable
using System;
using System.Collections.Generic;
using Avalonia.Media;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public sealed class SchedulerTimelineBlockViewModel : ObservableObject
    {
        private readonly LaunchSchedule? _schedule;
        private readonly Func<IReadOnlyList<Account>>? _getAccounts;
        private readonly ISettingsService? _settings;

        public SchedulerTimelineBlockViewModel(
            ScheduleTimelineSegment segment,
            LaunchSchedule? schedule = null,
            Func<IReadOnlyList<Account>>? getAccounts = null,
            ISettingsService? settings = null)
        {
            _schedule = schedule;
            _getAccounts = getAccounts;
            _settings = settings;

            ScheduleId = segment.ScheduleId;
            ScheduleName = segment.ScheduleName;
            ColorHex = SchedulerBrushHelper.NormalizeColorHex(segment.ColorHex);
            AccentBrush = SchedulerBrushHelper.GetAccentBrush(ColorHex);
            FillBrush = SchedulerBrushHelper.GetBlockFillBrush(ColorHex, segment.HasConflict);
            BorderBrush = SchedulerBrushHelper.GetBlockBorderBrush(ColorHex, segment.HasConflict);
            StartMinute = segment.StartMinute;
            EndMinute = segment.EndMinute;
            Lane = segment.Lane;
            IsCarryoverFromPreviousDay = segment.IsCarryoverFromPreviousDay;
            IsOpenOnly = segment.IsOpenOnly;
            HasConflict = segment.HasConflict;

            const double minWidth = 0.015;
            double left = Math.Clamp(StartMinute / 1440.0, 0.0, 1.0 - minWidth);
            double maxWidth = Math.Max(0.0, 1.0 - left);
            double rawWidth = Math.Max(0.0, EndMinute - StartMinute) / 1440.0;
            LeftFraction = left;
            WidthFraction = Math.Clamp(rawWidth, Math.Min(minWidth, maxWidth), maxWidth);
            TimeRangeLabel = BuildTimeRangeLabel();
            TooltipText = BuildTooltipText();
        }

        public string ScheduleId { get; }
        public string ScheduleName { get; }
        public string ColorHex { get; }
        public IBrush AccentBrush { get; }
        public IBrush FillBrush { get; }
        public IBrush BorderBrush { get; }
        public int StartMinute { get; }
        public int EndMinute { get; }
        public int Lane { get; }
        public double LeftFraction { get; }
        public double WidthFraction { get; }
        public bool IsCarryoverFromPreviousDay { get; }
        public bool IsOpenOnly { get; }
        public bool HasConflict { get; }
        public string TimeRangeLabel { get; private set; }
        public string TooltipText { get; private set; }

        public void NotifyStreamerModeChanged()
        {
            TooltipText = BuildTooltipText();
            OnPropertyChanged(nameof(TooltipText));
        }

        public void NotifyLanguageChanged()
        {
            TimeRangeLabel = BuildTimeRangeLabel();
            TooltipText = BuildTooltipText();
            OnPropertyChanged(nameof(TimeRangeLabel));
            OnPropertyChanged(nameof(TooltipText));
        }

        private string BuildTimeRangeLabel()
        {
            string start = SchedulerBrushHelper.FormatMinutes(StartMinute);
            if (IsOpenOnly)
                return LocalizationService.Format("Sched_Window_OpenOnlyFormat", start);

            string end = SchedulerBrushHelper.FormatMinutes(EndMinute);
            return LocalizationService.Format("Sched_Window_RangeFormat", start, end);
        }

        private string BuildTooltipText()
        {
            var lines = new List<string>(4)
            {
                $"{ScheduleName} ({TimeRangeLabel})"
            };

            if (_schedule != null)
            {
                IReadOnlyList<Account> accounts = _getAccounts?.Invoke() ?? Array.Empty<Account>();
                lines.Add(SchedulerBrushHelper.FormatTargetSummary(_schedule, accounts, _settings));
                lines.Add(SchedulerBrushHelper.FormatDestinationSummary(_schedule));
            }

            if (IsCarryoverFromPreviousDay)
                lines.Add(LocalizationService.Get("Sched_Timeline_Carryover"));
            if (IsOpenOnly)
                lines.Add(LocalizationService.Get("Sched_Timeline_OpenOnlySuffix"));
            if (HasConflict)
                lines.Add(LocalizationService.Get("Sched_Timeline_ConflictTag"));

            return string.Join("\n", lines);
        }
    }
}
