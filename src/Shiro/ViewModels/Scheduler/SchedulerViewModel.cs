#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public enum SchedulerFilterMode
    {
        All = 0,
        Enabled = 1,
        ActiveNow = 2,
        Paused = 3
    }

    public sealed partial class SchedulerViewModel : ObservableObject, IDisposable
    {
        private readonly ILaunchSchedulerService _schedulerService;
        private readonly IAccountRepository _accountRepository;
        private readonly ISettingsService? _settings;
        private readonly TimeProvider _timeProvider;

        private readonly List<ScheduleItemViewModel> _allScheduleCards = new();
        private readonly ObservableCollection<SchedulerTimelineBlockViewModel> _timelineSegments = new();
        private readonly ObservableCollection<ScheduleConflictInfo> _conflicts = new();
        private readonly ObservableCollection<ScheduleItemViewModel> _filteredSchedules = new();
        private readonly ObservableCollection<ScheduleHistoryItemViewModel> _historyEntries = new();
        private readonly ObservableCollection<SchedulerDayChipViewModel> _dayChips = new();
        private readonly Lock _refreshLock = new();
        private long _highestObservedRepoVersion;

        private Action? _schedulesChangedHandler;
        private Action? _historyChangedHandler;
        private Action? _stateChangedHandler;
        private EventHandler<AccountsChangedEventArgs>? _accountsChangedHandler;
        private Action<bool>? _streamerModeChangedHandler;

        private ITimer? _countdownTimer;
        private bool _isPageVisible;
        private int _selectedDayOffset;
        private double _nowFraction;
        private SchedulerFilterMode _filterMode = SchedulerFilterMode.All;
        private string _searchText = string.Empty;
        private bool _isHistoryOpen;
        private int _enabledCount;
        private int _allCount;
        private int _activeNowCount;
        private int _pausedCount;
        private string _engineStatusText = string.Empty;
        private string _nextEventSummaryText = string.Empty;
        private string _nextEventCountdownText = "--:--:--";
        private bool _hasNextEvent;
        private bool _isNextEventClose;
        private string _conflictBannerText = string.Empty;
        private double _timelineCanvasHeight = 40.0;
        private int _disposed;

        public SchedulerViewModel(
            ILaunchSchedulerService? schedulerService = null,
            IAccountRepository? accountRepository = null,
            ISettingsService? settings = null,
            TimeProvider? timeProvider = null,
            Func<IReadOnlyList<Account>>? getSelectedDashboardAccounts = null,
            Func<(string PlaceId, string JobId)>? getDashboardDestination = null,
            Func<IReadOnlyList<RecentGameItemViewModel>?>? getRecentGames = null,
            Func<IReadOnlyList<FavoriteGameItemViewModel>?>? getFavorites = null)
        {
            _schedulerService = schedulerService ?? AppServices.LaunchScheduler;
            _accountRepository = accountRepository ?? AppServices.AccountRepository;
            _settings = settings ?? AppServices.GeneralSettings;
            _timeProvider = timeProvider ?? TimeProvider.System;

            Editor = new ScheduleEditorViewModel(
                _accountRepository,
                _settings,
                _timeProvider,
                () => _schedulerService.GetSchedules(),
                getSelectedDashboardAccounts ?? Array.Empty<Account>,
                getDashboardDestination ?? (() => (string.Empty, string.Empty)),
                getRecentGames ?? (() => null),
                getFavorites ?? (() => null),
                SaveFromEditor);

            NewScheduleCommand = new RelayCommand(() => Editor.OpenNew(_accountRepository.GetAll()));
            ToggleHistoryCommand = new RelayCommand(() => IsHistoryOpen = !IsHistoryOpen);
            ClearHistoryCommand = new RelayCommand(() => _schedulerService.ClearHistory());
            SelectDayCommand = new RelayCommand(ExecuteSelectDay);
            SetFilterCommand = new RelayCommand(ExecuteSetFilter);
            SelectTimelineBlockCommand = new RelayCommand(ExecuteSelectTimelineBlock);

            InitializeDayChips();
            SubscribeWeakEvents();
            RefreshAll();
        }

        public ScheduleEditorViewModel Editor { get; }
        public ObservableCollection<SchedulerTimelineBlockViewModel> TimelineSegments => _timelineSegments;
        public ObservableCollection<ScheduleConflictInfo> Conflicts => _conflicts;
        public ObservableCollection<ScheduleItemViewModel> FilteredSchedules => _filteredSchedules;
        public ObservableCollection<ScheduleHistoryItemViewModel> HistoryEntries => _historyEntries;
        public ObservableCollection<SchedulerDayChipViewModel> DayChips => _dayChips;

        public bool IsEngineEnabled
        {
            get => _schedulerService.IsGlobalEnabled;
            set
            {
                if (_schedulerService.IsGlobalEnabled != value)
                {
                    _schedulerService.IsGlobalEnabled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasActiveBadge));
                    RefreshDynamicTiming();
                }
            }
        }

        public int EnabledCount => _enabledCount;
        public bool HasActiveBadge => IsEngineEnabled && _enabledCount > 0;
        public string EngineStatusText => _engineStatusText;
        public string NextEventSummaryText => _nextEventSummaryText;
        public string NextEventCountdownText => _nextEventCountdownText;
        public bool HasNextEvent => _hasNextEvent;
        public bool IsNextEventClose => _isNextEventClose;

        public int SelectedDayOffset
        {
            get => _selectedDayOffset;
            set
            {
                int normalized = Math.Clamp(value, 0, 7);
                if (SetProperty(ref _selectedDayOffset, normalized))
                {
                    OnPropertyChanged(nameof(ShowNowNeedle));
                    foreach (SchedulerDayChipViewModel chip in _dayChips)
                        chip.IsSelected = chip.Offset == normalized;
                    RefreshTimeline();
                }
            }
        }

        public double NowFraction => _nowFraction;
        public bool ShowNowNeedle => _selectedDayOffset == 0;
        public double TimelineCanvasHeight => _timelineCanvasHeight;
        public bool HasConflicts => _conflicts.Count > 0;
        public string ConflictBannerText => _conflictBannerText;
        public bool HasNoSchedules => _filteredSchedules.Count == 0;
        public bool HasAnySavedSchedules => _allScheduleCards.Count > 0;
        public bool HasHistoryEntries => _historyEntries.Count > 0;

        public bool IsHistoryOpen
        {
            get => _isHistoryOpen;
            set => SetProperty(ref _isHistoryOpen, value);
        }

        public SchedulerFilterMode FilterMode
        {
            get => _filterMode;
            set
            {
                if (SetProperty(ref _filterMode, value))
                {
                    OnPropertyChanged(nameof(IsFilterAll));
                    OnPropertyChanged(nameof(IsFilterEnabled));
                    OnPropertyChanged(nameof(IsFilterActiveNow));
                    OnPropertyChanged(nameof(IsFilterPaused));
                    ApplyFilter();
                }
            }
        }

        public bool IsFilterAll => _filterMode == SchedulerFilterMode.All;
        public bool IsFilterEnabled => _filterMode == SchedulerFilterMode.Enabled;
        public bool IsFilterActiveNow => _filterMode == SchedulerFilterMode.ActiveNow;
        public bool IsFilterPaused => _filterMode == SchedulerFilterMode.Paused;

        public string FilterAllText => LocalizationService.Format("Sched_Filter_All", _allCount);
        public string FilterEnabledText => LocalizationService.Format("Sched_Filter_Enabled", _enabledCount);
        public string FilterActiveNowText => LocalizationService.Format("Sched_Filter_ActiveNow", _activeNowCount);
        public string FilterPausedText => LocalizationService.Format("Sched_Filter_Paused", _pausedCount);

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value ?? string.Empty))
                    ApplyFilter();
            }
        }

        public ICommand NewScheduleCommand { get; }
        public ICommand ToggleHistoryCommand { get; }
        public ICommand ClearHistoryCommand { get; }
        public ICommand SelectDayCommand { get; }
        public ICommand SetFilterCommand { get; }
        public ICommand SelectTimelineBlockCommand { get; }

        public void SetPageVisible(bool visible)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _isPageVisible = visible;
            if (visible)
            {
                RefreshDynamicTiming();
                _countdownTimer ??= _timeProvider.CreateTimer(
                    static state =>
                    {
                        if (state is SchedulerViewModel vm && Volatile.Read(ref vm._disposed) == 0 && vm._isPageVisible)
                            AppDispatcher.Post(() => { if (Volatile.Read(ref vm._disposed) == 0 && vm._isPageVisible) vm.RefreshDynamicTiming(); });
                    },
                    this,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1));
            }
            else
            {
                StopCountdownTimer();
            }
        }

        public void OpenNewForAccounts(IReadOnlyList<Account> accounts, string? placeId, string? jobId)
            => Editor.OpenNewForAccounts(accounts, placeId, jobId, _accountRepository.GetAll());

        public void NotifyLanguageChanged()
        {
            lock (_refreshLock)
            {
                foreach (SchedulerDayChipViewModel chip in _dayChips)
                    chip.NotifyLanguageChanged();
                foreach (ScheduleHistoryItemViewModel hist in _historyEntries)
                    hist.NotifyLanguageChanged();
                foreach (SchedulerTimelineBlockViewModel block in _timelineSegments)
                    block.NotifyLanguageChanged();
                Editor.NotifyLanguageChanged();
                IReadOnlyList<Account> snapshot = _accountRepository.GetAll();
                RefreshSchedulesAndTimelineLocked(snapshot);
                RefreshHistoryLocked(snapshot);
            }
        }

        public void NotifyStreamerModeChanged()
        {
            lock (_refreshLock)
            {
                IReadOnlyList<Account> accounts = _accountRepository.GetAll();
                foreach (ScheduleItemViewModel card in _allScheduleCards)
                    card.RefreshStreamerMode(accounts);
                foreach (SchedulerTimelineBlockViewModel block in _timelineSegments)
                    block.NotifyStreamerModeChanged();
                foreach (ScheduleHistoryItemViewModel hist in _historyEntries)
                    hist.NotifyStreamerModeChanged();
                Editor.NotifyStreamerModeChanged();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            StopCountdownTimer();
            if (_schedulesChangedHandler != null) { _schedulerService.SchedulesChanged -= _schedulesChangedHandler; _schedulesChangedHandler = null; }
            if (_historyChangedHandler != null) { _schedulerService.HistoryChanged -= _historyChangedHandler; _historyChangedHandler = null; }
            if (_stateChangedHandler != null) { _schedulerService.StateChanged -= _stateChangedHandler; _stateChangedHandler = null; }
            if (_accountsChangedHandler != null) { _accountRepository.AccountsChanged -= _accountsChangedHandler; _accountsChangedHandler = null; }
            if (_streamerModeChangedHandler != null) { StreamerModeService.StateChanged -= _streamerModeChangedHandler; _streamerModeChangedHandler = null; }
            GC.SuppressFinalize(this);
        }

        private void StopCountdownTimer()
        {
            ITimer? old = Interlocked.Exchange(ref _countdownTimer, null);
            old?.Dispose();
        }

        private void SubscribeWeakEvents()
        {
            ILaunchSchedulerService svc = _schedulerService;
            IAccountRepository repo = _accountRepository;

            _schedulesChangedHandler = WeakEventSubscription.CreateAction<SchedulerViewModel>(
                this,
                static vm => AppDispatcher.Post(() => { if (Volatile.Read(ref vm._disposed) == 0) vm.OnSchedulesChanged(); }),
                h => svc.SchedulesChanged -= h);
            svc.SchedulesChanged += _schedulesChangedHandler;

            _historyChangedHandler = WeakEventSubscription.CreateAction<SchedulerViewModel>(
                this,
                static vm => AppDispatcher.Post(() => { if (Volatile.Read(ref vm._disposed) == 0) vm.RefreshHistory(); }),
                h => svc.HistoryChanged -= h);
            svc.HistoryChanged += _historyChangedHandler;

            _stateChangedHandler = WeakEventSubscription.CreateAction<SchedulerViewModel>(
                this,
                static vm => AppDispatcher.Post(() => { if (Volatile.Read(ref vm._disposed) == 0) vm.OnEngineStateChanged(); }),
                h => svc.StateChanged -= h);
            svc.StateChanged += _stateChangedHandler;

            _accountsChangedHandler = WeakEventSubscription.Create<SchedulerViewModel, AccountsChangedEventArgs>(
                this,
                static (vm, _, e) => AppDispatcher.Post(() => { if (Volatile.Read(ref vm._disposed) == 0) vm.OnAccountsChanged(e); }),
                h => repo.AccountsChanged -= h);
            repo.AccountsChanged += _accountsChangedHandler;

            _streamerModeChangedHandler = WeakEventSubscription.CreateAction<SchedulerViewModel, bool>(
                this,
                static (vm, _) => AppDispatcher.Post(() => { if (Volatile.Read(ref vm._disposed) == 0) vm.NotifyStreamerModeChanged(); }),
                static h => StreamerModeService.StateChanged -= h);
            StreamerModeService.StateChanged += _streamerModeChangedHandler;
        }
    }
}
