#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public sealed partial class ScheduleEditorViewModel : ObservableObject
    {
        private readonly IAccountRepository _accountRepository;
        private readonly ISettingsService? _settings;
        private readonly TimeProvider _timeProvider;
        private readonly Func<IReadOnlyList<LaunchSchedule>> _getExistingSchedules;
        private readonly Func<IReadOnlyList<Account>> _getSelectedDashboardAccounts;
        private readonly Func<(string PlaceId, string JobId)> _getDashboardDestination;
        private readonly Func<IReadOnlyList<RecentGameItemViewModel>?> _getRecentGames;
        private readonly Func<IReadOnlyList<FavoriteGameItemViewModel>?> _getFavorites;
        private readonly Func<LaunchSchedule, ScheduleValidationResult> _onSave;

        private readonly List<ScheduleAccountChecklistItemViewModel> _allChecklistItems = new();
        private readonly ObservableCollection<string> _availableGroups = new();
        private readonly ObservableCollection<ScheduleAccountChecklistItemViewModel> _filteredAccountChecklist = new();
        private readonly ObservableCollection<ScheduleColorSwatchViewModel> _availableColorSwatches = new();
        private readonly ObservableCollection<ScheduleArrangeOptionViewModel> _arrangeModeOptions = new();
        private readonly ObservableCollection<ScheduleGameQuickPickViewModel> _recentAndFavoriteGames = new();
        private readonly System.Threading.Lock _editorLock = new();

        private bool _isOpen;
        private bool _isEditingExisting;
        private string _editingScheduleId = string.Empty;
        private DateTimeOffset _editingCreatedAtUtc;
        private bool _editingIsEnabled = true;
        private string _name = string.Empty;
        private string _colorHex = "#38BDF8";
        private ScheduleRecurrence _recurrence = ScheduleRecurrence.Daily;
        private string _oneTimeDateText = string.Empty;
        private bool _dayMon = true, _dayTue = true, _dayWed = true, _dayThu = true, _dayFri = true, _daySat = true, _daySun = true;
        private string _startTimeText = "08:00";
        private bool _hasEndTime = true;
        private string _endTimeText = "12:00";
        private ScheduleTargetMode _targetMode = ScheduleTargetMode.SpecificAccounts;
        private string _selectedGroup = "Default";
        private string _accountSearchText = string.Empty;
        private ScheduleLaunchMode _launchMode = ScheduleLaunchMode.JoinServer;
        private string _placeIdText = string.Empty;
        private string _jobIdText = string.Empty;
        private bool _closeOtherTabsBeforeLaunch;
        private int _handoffDelaySeconds = 2;
        private ScheduleArrangeMode _postLaunchArrange = ScheduleArrangeMode.None;
        private ScheduleArrangeOptionViewModel? _selectedArrangeModeOption;
        private bool _catchUpIfActiveOnStartup;
        private bool _sendWebhookNotification;
        private string? _validationError;
        private string? _liveConflictWarning;
        private bool _suppressDiagnostics;

        public ScheduleEditorViewModel(
            IAccountRepository accountRepository,
            ISettingsService? settings,
            TimeProvider timeProvider,
            Func<IReadOnlyList<LaunchSchedule>> getExistingSchedules,
            Func<IReadOnlyList<Account>> getSelectedDashboardAccounts,
            Func<(string PlaceId, string JobId)> getDashboardDestination,
            Func<IReadOnlyList<RecentGameItemViewModel>?> getRecentGames,
            Func<IReadOnlyList<FavoriteGameItemViewModel>?> getFavorites,
            Func<LaunchSchedule, ScheduleValidationResult> onSave)
        {
            _accountRepository = accountRepository;
            _settings = settings;
            _timeProvider = timeProvider;
            _getExistingSchedules = getExistingSchedules;
            _getSelectedDashboardAccounts = getSelectedDashboardAccounts;
            _getDashboardDestination = getDashboardDestination;
            _getRecentGames = getRecentGames;
            _getFavorites = getFavorites;
            _onSave = onSave;

            InitializeOptionCollections();

            ApplyDurationHoursCommand = new RelayCommand(ApplyDurationHours);
            SaveCommand = new RelayCommand(Save);
            CancelCommand = new RelayCommand(Close);
            CloseCommand = new RelayCommand(Close);
            SelectReadyAccountsCommand = new RelayCommand(SelectReadyAccounts);
            UseDashboardSelectionCommand = new RelayCommand(UseDashboardSelection);
            ClearAccountsCommand = new RelayCommand(ClearAccounts);
            SelectRecentOrFavoriteGameCommand = new RelayCommand(SelectRecentOrFavoriteGame);
            SetColorCommand = new RelayCommand(SetColor);
            SetRecurrenceCommand = new RelayCommand(SetRecurrence);
            SetTargetModeCommand = new RelayCommand(SetTargetMode);
            SetLaunchModeCommand = new RelayCommand(SetLaunchMode);
            SetQuickDateCommand = new RelayCommand(SetQuickDate);
            SetWeekdayPresetCommand = new RelayCommand(SetWeekdayPreset);
        }

        public bool IsOpen
        {
            get => _isOpen;
            private set => SetProperty(ref _isOpen, value);
        }

        public bool IsEditingExisting
        {
            get => _isEditingExisting;
            private set
            {
                if (SetProperty(ref _isEditingExisting, value))
                    OnPropertyChanged(nameof(EditorTitleText));
            }
        }

        public string EditingScheduleId => _editingScheduleId;

        public string EditorTitleText => _isEditingExisting
            ? LocalizationService.Get("Sched_Editor_EditTitle")
            : LocalizationService.Get("Sched_Editor_NewTitle");

        public string Name
        {
            get => _name;
            set { if (SetProperty(ref _name, value ?? string.Empty)) RefreshLiveDiagnostics(); }
        }

        public string ColorHex
        {
            get => _colorHex;
            set
            {
                string normalized = SchedulerBrushHelper.NormalizeColorHex(value);
                if (SetProperty(ref _colorHex, normalized))
                    UpdateSelectedColorSwatch();
            }
        }

        public ScheduleRecurrence Recurrence
        {
            get => _recurrence;
            set
            {
                if (SetProperty(ref _recurrence, value))
                {
                    OnPropertyChanged(nameof(IsOneTimeRecurrence));
                    OnPropertyChanged(nameof(IsDailyRecurrence));
                    OnPropertyChanged(nameof(IsWeeklyRecurrence));
                    RefreshLiveDiagnostics();
                }
            }
        }

        public bool IsOneTimeRecurrence => _recurrence == ScheduleRecurrence.OneTime;
        public bool IsDailyRecurrence => _recurrence == ScheduleRecurrence.Daily;
        public bool IsWeeklyRecurrence => _recurrence == ScheduleRecurrence.Weekly;

        public string OneTimeDateText
        {
            get => _oneTimeDateText;
            set { if (SetProperty(ref _oneTimeDateText, value ?? string.Empty)) RefreshLiveDiagnostics(); }
        }

        public bool DayMon { get => _dayMon; set { if (SetProperty(ref _dayMon, value)) RefreshLiveDiagnostics(); } }
        public bool DayTue { get => _dayTue; set { if (SetProperty(ref _dayTue, value)) RefreshLiveDiagnostics(); } }
        public bool DayWed { get => _dayWed; set { if (SetProperty(ref _dayWed, value)) RefreshLiveDiagnostics(); } }
        public bool DayThu { get => _dayThu; set { if (SetProperty(ref _dayThu, value)) RefreshLiveDiagnostics(); } }
        public bool DayFri { get => _dayFri; set { if (SetProperty(ref _dayFri, value)) RefreshLiveDiagnostics(); } }
        public bool DaySat { get => _daySat; set { if (SetProperty(ref _daySat, value)) RefreshLiveDiagnostics(); } }
        public bool DaySun { get => _daySun; set { if (SetProperty(ref _daySun, value)) RefreshLiveDiagnostics(); } }

        public string StartTimeText
        {
            get => _startTimeText;
            set
            {
                if (SetProperty(ref _startTimeText, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(IsOvernightPreview));
                    RefreshLiveDiagnostics();
                }
            }
        }

        public bool HasEndTime
        {
            get => _hasEndTime;
            set
            {
                if (SetProperty(ref _hasEndTime, value))
                {
                    OnPropertyChanged(nameof(IsOvernightPreview));
                    RefreshLiveDiagnostics();
                }
            }
        }

        public string EndTimeText
        {
            get => _endTimeText;
            set
            {
                if (SetProperty(ref _endTimeText, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(IsOvernightPreview));
                    RefreshLiveDiagnostics();
                }
            }
        }

        public bool IsOvernightPreview =>
            _hasEndTime &&
            LaunchScheduleEvaluatorHelper.TryParseTime(_startTimeText, out TimeOnly start) &&
            LaunchScheduleEvaluatorHelper.TryParseTime(_endTimeText, out TimeOnly end) &&
            end < start;

        public ScheduleTargetMode TargetMode
        {
            get => _targetMode;
            set
            {
                if (SetProperty(ref _targetMode, value))
                {
                    OnPropertyChanged(nameof(IsSpecificAccountsTarget));
                    OnPropertyChanged(nameof(IsGroupTarget));
                    OnPropertyChanged(nameof(SelectedAccountsSummaryText));
                    RefreshLiveDiagnostics();
                }
            }
        }

        public bool IsSpecificAccountsTarget => _targetMode == ScheduleTargetMode.SpecificAccounts;
        public bool IsGroupTarget => _targetMode == ScheduleTargetMode.Group;

        public string SelectedGroup
        {
            get => _selectedGroup;
            set
            {
                if (SetProperty(ref _selectedGroup, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(SelectedAccountsSummaryText));
                    RefreshLiveDiagnostics();
                }
            }
        }

        public ObservableCollection<string> AvailableGroups => _availableGroups;
        public ObservableCollection<ScheduleAccountChecklistItemViewModel> FilteredAccountChecklist => _filteredAccountChecklist;
        public ObservableCollection<ScheduleColorSwatchViewModel> AvailableColorSwatches => _availableColorSwatches;
        public ObservableCollection<ScheduleArrangeOptionViewModel> ArrangeModeOptions => _arrangeModeOptions;
        public ObservableCollection<ScheduleGameQuickPickViewModel> RecentAndFavoriteGames => _recentAndFavoriteGames;
        public bool HasRecentAndFavoriteGames => _recentAndFavoriteGames.Count > 0;

        public string AccountSearchText
        {
            get => _accountSearchText;
            set
            {
                if (SetProperty(ref _accountSearchText, value ?? string.Empty))
                    ApplyChecklistFilter();
            }
        }

        public string SelectedAccountsSummaryText => ComputeTargetSummaryText();

        public ScheduleLaunchMode LaunchMode
        {
            get => _launchMode;
            set
            {
                if (SetProperty(ref _launchMode, value))
                {
                    OnPropertyChanged(nameof(IsJoinServerMode));
                    OnPropertyChanged(nameof(IsOpenHomeMode));
                    RefreshLiveDiagnostics();
                }
            }
        }

        public bool IsJoinServerMode => _launchMode == ScheduleLaunchMode.JoinServer;
        public bool IsOpenHomeMode => _launchMode == ScheduleLaunchMode.OpenHome;

        public string PlaceIdText
        {
            get => _placeIdText;
            set { if (SetProperty(ref _placeIdText, value ?? string.Empty)) RefreshLiveDiagnostics(); }
        }

        public string JobIdText
        {
            get => _jobIdText;
            set => SetProperty(ref _jobIdText, value ?? string.Empty);
        }

        public char JobIdMaskChar => StreamerModeService.IsActive ? '\u2022' : '\0';

        public bool CloseOtherTabsBeforeLaunch
        {
            get => _closeOtherTabsBeforeLaunch;
            set { if (SetProperty(ref _closeOtherTabsBeforeLaunch, value)) RefreshLiveDiagnostics(); }
        }

        public int HandoffDelaySeconds
        {
            get => _handoffDelaySeconds;
            set => SetProperty(ref _handoffDelaySeconds, Math.Clamp(value, 0, 15));
        }

        public ScheduleArrangeMode PostLaunchArrange
        {
            get => _postLaunchArrange;
            set
            {
                if (SetProperty(ref _postLaunchArrange, value))
                {
                    ScheduleArrangeOptionViewModel? match = _arrangeModeOptions.FirstOrDefault(o => o.Mode == value);
                    SetProperty(ref _selectedArrangeModeOption, match, nameof(SelectedArrangeModeOption));
                }
            }
        }

        public ScheduleArrangeOptionViewModel? SelectedArrangeModeOption
        {
            get => _selectedArrangeModeOption;
            set
            {
                if (SetProperty(ref _selectedArrangeModeOption, value) && value != null)
                    PostLaunchArrange = value.Mode;
            }
        }

        public bool CatchUpIfActiveOnStartup
        {
            get => _catchUpIfActiveOnStartup;
            set => SetProperty(ref _catchUpIfActiveOnStartup, value);
        }

        public bool SendWebhookNotification
        {
            get => _sendWebhookNotification;
            set => SetProperty(ref _sendWebhookNotification, value);
        }

        public bool HasWebhookConfigured =>
            !string.IsNullOrWhiteSpace(WebhookNotificationService.DiscordWebhookUrl) ||
            (!string.IsNullOrWhiteSpace(WebhookNotificationService.TelegramBotToken) &&
             !string.IsNullOrWhiteSpace(WebhookNotificationService.TelegramChatId));

        public string? ValidationError
        {
            get => _validationError;
            private set
            {
                if (SetProperty(ref _validationError, value))
                    OnPropertyChanged(nameof(HasValidationError));
            }
        }

        public bool HasValidationError => !string.IsNullOrEmpty(_validationError);

        public string? LiveConflictWarning
        {
            get => _liveConflictWarning;
            private set
            {
                if (SetProperty(ref _liveConflictWarning, value))
                    OnPropertyChanged(nameof(HasLiveConflictWarning));
            }
        }

        public bool HasLiveConflictWarning => !string.IsNullOrEmpty(_liveConflictWarning);

        public ICommand ApplyDurationHoursCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand CloseCommand { get; }
        public ICommand SelectReadyAccountsCommand { get; }
        public ICommand UseDashboardSelectionCommand { get; }
        public ICommand ClearAccountsCommand { get; }
        public ICommand SelectRecentOrFavoriteGameCommand { get; }
        public ICommand SetColorCommand { get; }
        public ICommand SetRecurrenceCommand { get; }
        public ICommand SetTargetModeCommand { get; }
        public ICommand SetLaunchModeCommand { get; }
        public ICommand SetQuickDateCommand { get; }
        public ICommand SetWeekdayPresetCommand { get; }
    }
}
