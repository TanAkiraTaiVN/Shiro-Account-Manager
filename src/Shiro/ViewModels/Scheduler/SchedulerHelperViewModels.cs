#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public sealed class ScheduleAccountChecklistItemViewModel : ObservableObject
    {
        private readonly ISettingsService? _settings;
        private readonly Action<ScheduleAccountChecklistItemViewModel> _onCheckedChanged;
        private readonly Account _account;
        private bool _isChecked;

        public ScheduleAccountChecklistItemViewModel(
            Account account,
            ISettingsService? settings,
            Action<ScheduleAccountChecklistItemViewModel> onCheckedChanged)
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
            _settings = settings;
            _onCheckedChanged = onCheckedChanged;
        }

        public long UserId => _account.UserID;
        public string Username => _account.Username ?? string.Empty;
        public string Group => string.IsNullOrWhiteSpace(_account.Group) ? "Default" : _account.Group;
        public bool IsReady => SchedulerBrushHelper.IsAccountReady(_account);
        public string DisplayName => StreamerModeService.GetDisplayUsername(Username, _settings);
        public string DisplayAlias => StreamerModeService.GetDisplayAlias(_account.Alias, _settings);
        public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayAlias) ? DisplayName : $"{DisplayAlias} (@{DisplayName})";

        public string StatusText => _account.IsBannedOrLocked
            ? LocalizationService.Get("FastLogin.LockedOrBannedBadge")
            : !_account.Valid
                ? LocalizationService.Get("Status_Expired")
                : LocalizationService.Get("Status_Ready");

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (SetProperty(ref _isChecked, value))
                    _onCheckedChanged(this);
            }
        }

        internal void SetCheckedSilent(bool value) => SetProperty(ref _isChecked, value, nameof(IsChecked));

        public void NotifyStreamerModeChanged()
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(DisplayAlias));
            OnPropertyChanged(nameof(DisplayLabel));
        }

        public void NotifyLanguageChanged() => OnPropertyChanged(nameof(StatusText));
    }

    public sealed class ScheduleColorSwatchViewModel : ObservableObject
    {
        private bool _isSelected;

        public ScheduleColorSwatchViewModel(string colorHex, string labelKey)
        {
            ColorHex = colorHex;
            LabelKey = labelKey;
            SwatchBrush = SchedulerBrushHelper.GetAccentBrush(colorHex);
        }

        public string ColorHex { get; }
        public string LabelKey { get; }
        public string Label => LocalizationService.Get(LabelKey);
        public IBrush SwatchBrush { get; }
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

        public void NotifyLanguageChanged() => OnPropertyChanged(nameof(Label));
    }

    public sealed class ScheduleArrangeOptionViewModel : ObservableObject
    {
        public ScheduleArrangeOptionViewModel(ScheduleArrangeMode mode) => Mode = mode;
        public ScheduleArrangeMode Mode { get; }
        public string Label => SchedulerBrushHelper.FormatArrangeLabel(Mode);
        public void NotifyLanguageChanged() => OnPropertyChanged(nameof(Label));
        public override string ToString() => Label;
    }

    public sealed class ScheduleGameQuickPickViewModel
    {
        public long PlaceId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string JobId { get; init; } = string.Empty;
        public bool IsFavorite { get; init; }
        public string Subtitle => $"Place ID: {PlaceId}";
    }

    public sealed class ScheduleHistoryItemViewModel : ObservableObject
    {
        private readonly ScheduleRunEntry _entry;
        private readonly ISettingsService? _settings;
        private IReadOnlyList<string> _knownUsernames;

        public ScheduleHistoryItemViewModel(ScheduleRunEntry entry, IReadOnlyList<string> knownUsernames, ISettingsService? settings)
        {
            _entry = entry;
            _knownUsernames = knownUsernames;
            _settings = settings;
        }

        public string Id => _entry.Id;
        public string ScheduleId => _entry.ScheduleId;
        public string ScheduleName => _entry.ScheduleName;
        public DateTimeOffset TimestampUtc => _entry.TimestampUtc;
        public string TimestampText => _entry.TimestampUtc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        public ScheduleRunPhase Phase => _entry.Phase;
        public ScheduleRunOutcome Outcome => _entry.Outcome;
        public string SummaryText => SchedulerBrushHelper.MaskHistorySummary(_entry.Summary, _knownUsernames, _settings);

        public string PhaseBadgeText => _entry.Phase switch
        {
            ScheduleRunPhase.Close => LocalizationService.Get("Sched_Phase_Close"),
            ScheduleRunPhase.ManualOpen => LocalizationService.Get("Sched_Phase_ManualOpen"),
            ScheduleRunPhase.ManualClose => LocalizationService.Get("Sched_Phase_ManualClose"),
            ScheduleRunPhase.CatchUpOpen => LocalizationService.Get("Sched_Phase_CatchUpOpen"),
            _ => LocalizationService.Get("Sched_Phase_Open")
        };

        public string OutcomeBadgeText => _entry.Outcome switch
        {
            ScheduleRunOutcome.Partial => LocalizationService.Get("Sched_Outcome_Partial"),
            ScheduleRunOutcome.Skipped => LocalizationService.Get("Sched_Outcome_Skipped"),
            ScheduleRunOutcome.Failed => LocalizationService.Get("Sched_Outcome_Failed"),
            _ => LocalizationService.Get("Sched_Outcome_Success")
        };

        public IBrush OutcomeBackground => _entry.Outcome switch
        {
            ScheduleRunOutcome.Partial => SchedulerBrushHelper.WarnBg,
            ScheduleRunOutcome.Skipped => SchedulerBrushHelper.DefaultBg,
            ScheduleRunOutcome.Failed => SchedulerBrushHelper.DangerBg,
            _ => SchedulerBrushHelper.HealthyBg
        };

        public IBrush OutcomeBorder => _entry.Outcome switch
        {
            ScheduleRunOutcome.Partial => SchedulerBrushHelper.WarnBorder,
            ScheduleRunOutcome.Skipped => SchedulerBrushHelper.DefaultBorder,
            ScheduleRunOutcome.Failed => SchedulerBrushHelper.DangerBorder,
            _ => SchedulerBrushHelper.HealthyBorder
        };

        public IBrush OutcomeForeground => _entry.Outcome switch
        {
            ScheduleRunOutcome.Partial => SchedulerBrushHelper.WarnFg,
            ScheduleRunOutcome.Skipped => SchedulerBrushHelper.DefaultFg,
            ScheduleRunOutcome.Failed => SchedulerBrushHelper.DangerFg,
            _ => SchedulerBrushHelper.HealthyFg
        };

        public void UpdateKnownUsernames(IReadOnlyList<string> knownUsernames)
        {
            _knownUsernames = knownUsernames;
            OnPropertyChanged(nameof(SummaryText));
        }

        public void NotifyStreamerModeChanged() => OnPropertyChanged(nameof(SummaryText));

        public void NotifyLanguageChanged()
        {
            OnPropertyChanged(nameof(PhaseBadgeText));
            OnPropertyChanged(nameof(OutcomeBadgeText));
        }
    }

    public sealed class SchedulerDayChipViewModel : ObservableObject
    {
        private bool _isSelected;
        private readonly string _labelKey;

        public SchedulerDayChipViewModel(int offset, string labelKey, ICommand selectCommand)
        {
            Offset = offset;
            _labelKey = labelKey;
            SelectCommand = selectCommand;
        }

        public int Offset { get; }
        public string Label => LocalizationService.Get(_labelKey);
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
        public ICommand SelectCommand { get; }
        public void NotifyLanguageChanged() => OnPropertyChanged(nameof(Label));
    }
}
