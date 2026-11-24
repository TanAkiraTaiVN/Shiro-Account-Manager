#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public sealed class ScheduleItemViewModel : ObservableObject
    {
        private readonly Action<string, bool> _onToggleEnabled;
        private readonly ISettingsService? _settings;
        private LaunchSchedule _model;
        private bool _isEnabled;
        private bool _isActiveNow;
        private bool _hasConflict;
        private string _windowBadgeText = string.Empty;
        private string _recurrenceText = string.Empty;
        private string _statusBadgeText = string.Empty;
        private string _targetSummaryText = string.Empty;
        private string _destinationSummaryText = string.Empty;
        private string _optionsSummaryText = string.Empty;
        private string _conflictBadgeText = string.Empty;
        private IBrush _accentBrush;
        private IBrush _statusBadgeBackground = SchedulerBrushHelper.DefaultBg;
        private IBrush _statusBadgeBorder = SchedulerBrushHelper.DefaultBorder;
        private IBrush _statusBadgeForeground = SchedulerBrushHelper.DefaultFg;

        public ScheduleItemViewModel(
            LaunchSchedule model,
            ISettingsService? settings,
            Action<string, bool> onToggleEnabled,
            Func<ScheduleItemViewModel, Task> onRunNow,
            Func<ScheduleItemViewModel, Task> onCloseTabs,
            Action<ScheduleItemViewModel> onAddNextShift,
            Action<ScheduleItemViewModel> onEdit,
            Action<ScheduleItemViewModel> onDuplicate,
            Action<ScheduleItemViewModel> onDelete)
        {
            _model = model.Clone();
            _settings = settings;
            _onToggleEnabled = onToggleEnabled;
            _isEnabled = _model.IsEnabled;
            _accentBrush = SchedulerBrushHelper.GetAccentBrush(_model.ColorHex);

            RunNowCommand = new AsyncRelayCommand(() => onRunNow(this));
            CloseTabsCommand = new AsyncRelayCommand(() => onCloseTabs(this));
            AddNextShiftCommand = new RelayCommand(() => onAddNextShift(this));
            EditCommand = new RelayCommand(() => onEdit(this));
            DuplicateCommand = new RelayCommand(() => onDuplicate(this));
            DeleteCommand = new RelayCommand(() => onDelete(this));
        }

        internal LaunchSchedule ModelSnapshot => _model.Clone();
        public string Id => _model.Id;
        public string Name => _model.Name;
        public bool HasEndTime => _model.HasEndTime;
        public string ColorHex => SchedulerBrushHelper.NormalizeColorHex(_model.ColorHex);
        public string WindowBadgeText => _windowBadgeText;
        public string RecurrenceText => _recurrenceText;
        public string StatusBadgeText => _statusBadgeText;
        public string TargetSummaryText => _targetSummaryText;
        public string DestinationSummaryText => _destinationSummaryText;
        public string OptionsSummaryText => _optionsSummaryText;
        public string ConflictBadgeText => _conflictBadgeText;
        public bool HasOptionsSummary => !string.IsNullOrEmpty(_optionsSummaryText);

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (SetProperty(ref _isEnabled, value))
                    _onToggleEnabled(Id, value);
            }
        }

        public bool IsActiveNow => _isActiveNow;
        public bool HasConflict => _hasConflict;
        public IBrush AccentBrush => _accentBrush;
        public IBrush StatusBadgeBackground => _statusBadgeBackground;
        public IBrush StatusBadgeBorder => _statusBadgeBorder;
        public IBrush StatusBadgeForeground => _statusBadgeForeground;

        public ICommand RunNowCommand { get; }
        public ICommand CloseTabsCommand { get; }
        public ICommand AddNextShiftCommand { get; }
        public ICommand EditCommand { get; }
        public ICommand DuplicateCommand { get; }
        public ICommand DeleteCommand { get; }

        public void SyncFromModel(
            LaunchSchedule model,
            DateTimeOffset localNow,
            TimeZoneInfo tz,
            IReadOnlyList<Account> allAccounts,
            bool hasConflict)
        {
            _model = model.Clone();
            if (_isEnabled != _model.IsEnabled)
            {
                _isEnabled = _model.IsEnabled;
                OnPropertyChanged(nameof(IsEnabled));
            }

            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(HasEndTime));
            OnPropertyChanged(nameof(ColorHex));
            SetProperty(ref _accentBrush, SchedulerBrushHelper.GetAccentBrush(_model.ColorHex), nameof(AccentBrush));
            RefreshDisplayAndTiming(localNow, tz, allAccounts, hasConflict);
        }

        public void RefreshDisplayAndTiming(
            DateTimeOffset localNow,
            TimeZoneInfo tz,
            IReadOnlyList<Account> allAccounts,
            bool hasConflict)
        {
            bool active = _model.IsEnabled && LaunchScheduleEvaluatorHelper.IsCurrentlyActive(_model, localNow, tz);
            SetProperty(ref _isActiveNow, active, nameof(IsActiveNow));
            SetProperty(ref _hasConflict, _model.IsEnabled && hasConflict, nameof(HasConflict));

            SetProperty(ref _windowBadgeText, SchedulerBrushHelper.FormatWindowBadge(_model), nameof(WindowBadgeText));
            SetProperty(ref _recurrenceText, SchedulerBrushHelper.FormatRecurrence(_model), nameof(RecurrenceText));
            SetProperty(ref _targetSummaryText, SchedulerBrushHelper.FormatTargetSummary(_model, allAccounts, _settings), nameof(TargetSummaryText));
            SetProperty(ref _destinationSummaryText, SchedulerBrushHelper.FormatDestinationSummary(_model), nameof(DestinationSummaryText));

            string opts = SchedulerBrushHelper.FormatOptionsSummary(_model);
            if (SetProperty(ref _optionsSummaryText, opts, nameof(OptionsSummaryText)))
                OnPropertyChanged(nameof(HasOptionsSummary));

            SetProperty(
                ref _conflictBadgeText,
                _hasConflict ? LocalizationService.Get("Sched_Card_ConflictBadge") : string.Empty,
                nameof(ConflictBadgeText));

            UpdateStatusBadge(localNow, tz, active);
        }

        public void RefreshStreamerMode(IReadOnlyList<Account> allAccounts)
        {
            SetProperty(ref _targetSummaryText, SchedulerBrushHelper.FormatTargetSummary(_model, allAccounts, _settings), nameof(TargetSummaryText));
            SetProperty(ref _destinationSummaryText, SchedulerBrushHelper.FormatDestinationSummary(_model), nameof(DestinationSummaryText));
        }

        private void UpdateStatusBadge(DateTimeOffset localNow, TimeZoneInfo tz, bool active)
        {
            string badgeText;
            IBrush bg;
            IBrush border;
            IBrush fg;

            if (!_model.IsEnabled)
            {
                bool completedOneTime = _model.Recurrence == ScheduleRecurrence.OneTime &&
                    (!string.IsNullOrEmpty(_model.LastTriggeredEndKey) || !string.IsNullOrEmpty(_model.LastTriggeredStartKey));
                badgeText = completedOneTime
                    ? LocalizationService.Get("Sched_Status_Completed")
                    : LocalizationService.Get("Sched_Status_Paused");
                bg = SchedulerBrushHelper.DefaultBg;
                border = SchedulerBrushHelper.DefaultBorder;
                fg = SchedulerBrushHelper.DefaultFg;
            }
            else if (active)
            {
                badgeText = LocalizationService.Get("Sched_Status_ActiveNow");
                bg = SchedulerBrushHelper.HealthyBg;
                border = SchedulerBrushHelper.HealthyBorder;
                fg = SchedulerBrushHelper.HealthyFg;
            }
            else
            {
                ScheduleTransitionInfo? next = LaunchScheduleEvaluatorHelper.GetNextTransition(_model, localNow, tz);
                if (next.HasValue)
                {
                    TimeSpan remaining = next.Value.OccurringAtLocal - localNow;
                    badgeText = LocalizationService.Format("Sched_Status_NextIn", FormatCountdown(remaining));
                }
                else
                {
                    badgeText = LocalizationService.Get("Sched_Status_Scheduled");
                }

                if (_hasConflict)
                {
                    bg = SchedulerBrushHelper.WarnBg;
                    border = SchedulerBrushHelper.WarnBorder;
                    fg = SchedulerBrushHelper.WarnFg;
                }
                else
                {
                    bg = SchedulerBrushHelper.InfoBg;
                    border = SchedulerBrushHelper.InfoBorder;
                    fg = SchedulerBrushHelper.InfoFg;
                }
            }

            SetProperty(ref _statusBadgeText, badgeText, nameof(StatusBadgeText));
            SetProperty(ref _statusBadgeBackground, bg, nameof(StatusBadgeBackground));
            SetProperty(ref _statusBadgeBorder, border, nameof(StatusBadgeBorder));
            SetProperty(ref _statusBadgeForeground, fg, nameof(StatusBadgeForeground));
        }

        internal static string FormatCountdown(TimeSpan remaining)
        {
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            int totalHours = (int)remaining.TotalHours;
            return remaining.TotalDays >= 1.0
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)remaining.TotalDays}d {remaining.Hours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}")
                : string.Create(CultureInfo.InvariantCulture, $"{totalHours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}");
        }
    }
}
