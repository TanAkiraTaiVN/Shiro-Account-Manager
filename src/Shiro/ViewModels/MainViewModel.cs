#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public enum NavigationPage
    {
        Accounts,
        ServerList,
        RecentGames,
        Settings,
        About
    }

    public partial class MainViewModel : ObservableObject, IDisposable, IAccountWorkflowHost
    {
        private readonly IAppLogger _logger;
        private readonly ISettingsService _settings = null!;
        private readonly IAccountRepository _accountRepository;
        private readonly BulkObservableCollection<AccountViewModel> _accounts = new BulkObservableCollection<AccountViewModel>();
        private readonly BulkObservableCollection<AccountViewModel> _filteredAccounts = new BulkObservableCollection<AccountViewModel>();
        private readonly BulkObservableCollection<AccountRowViewModel> _filteredAccountRows = new BulkObservableCollection<AccountRowViewModel>();
        private readonly ObservableCollection<string> _availableGroups = new ObservableCollection<string>();
        private readonly Dictionary<Account, AccountViewModel> _vmByModel = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<long, AccountViewModel> _vmByUserId = new();
        private readonly Dictionary<string, AccountViewModel> _vmByUsername = new(StringComparer.OrdinalIgnoreCase);

        private NavigationPage _currentPage = NavigationPage.Accounts;
        private string _statusMessage = string.Empty;
        private string _lastDefaultReadyStatus = string.Empty;
        private string _placeId = string.Empty;
        private string _jobId = string.Empty;
        private bool _isPotatoMode;
        private bool _isAntiAfk;
        private bool _areWindowsHidden;
        private int _lastRobloxProcessCount = -1;
        private int _disposed;
        private long _repoSnapshotSeq;
        private readonly System.Threading.Lock _accountsUiLock = new();
        private EventHandler<AccountsChangedEventArgs>? _accountsChangedHandler;
        private Action<AppLanguage>? _languageChangedHandler;
        private Action? _audioStatusChangedHandler;
        private Action<AccentTheme>? _themeChangedHandler;
        private Action<LaunchProgress>? _launchProgressChangedHandler;
        private Action<bool>? _streamerModeChangedHandler;

        public NavigationPage CurrentPage
        {
            get => _currentPage;
            set
            {
                if (SetProperty(ref _currentPage, value))
                {
                    OnPropertyChanged(nameof(IsAccountsActive));
                    OnPropertyChanged(nameof(IsServerListActive));
                    OnPropertyChanged(nameof(IsRecentGamesActive));
                    OnPropertyChanged(nameof(IsSettingsActive));
                    OnPropertyChanged(nameof(IsAboutActive));
                    OnPageNavigated?.Invoke(value);
                }
            }
        }

        public bool IsAccountsActive => CurrentPage == NavigationPage.Accounts;
        public bool IsServerListActive => CurrentPage == NavigationPage.ServerList;
        public bool IsRecentGamesActive => CurrentPage == NavigationPage.RecentGames;
        public bool IsSettingsActive => CurrentPage == NavigationPage.Settings;
        public bool IsAboutActive => CurrentPage == NavigationPage.About;

        // Callback when navigation page changes
        public Action<NavigationPage>? OnPageNavigated { get; set; }

        public ObservableCollection<AccountViewModel> Accounts => _accounts;
        public ObservableCollection<AccountViewModel> FilteredAccounts => _filteredAccounts;
        public ObservableCollection<AccountRowViewModel> FilteredAccountRows => _filteredAccountRows;
        public ObservableCollection<string> AvailableGroups => _availableGroups;

        public ServerListViewModel ServerListViewModel { get; private set; }

        public MainViewModel() : this(null, null, null) { }

        public MainViewModel(IAppLogger? logger = null, ISettingsService? settings = null, IAccountRepository? accountRepository = null)
        {
            _logger = logger ?? AppServices.Logger;
            _settings = settings ?? AppServices.GeneralSettings!;
            _accountRepository = accountRepository ?? AppServices.AccountRepository;

            string? savedPid = _settings?.Get("SavedPlaceId");
            if (!string.IsNullOrWhiteSpace(savedPid))
            {
                _placeId = savedPid.Trim();
                AppServices.ServerBrowserState.CurrentPlaceId = _placeId;
            }
            else if (!string.IsNullOrWhiteSpace(AppServices.ServerBrowserState.CurrentPlaceId))
            {
                _placeId = AppServices.ServerBrowserState.CurrentPlaceId;
            }
            else
            {
                _placeId = string.Empty;
            }
            _lastDefaultReadyStatus = Loc.Get("Status_ReadyDefault");
            _statusMessage = _lastDefaultReadyStatus;

            ServerListViewModel = new ServerListViewModel(this, _logger, _settings);
            InitializeCommands();
            RefreshStateFromSettings();
            ScheduleCurrentGameLookup(_placeId);
            SubscribeWeakEvents();
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            UnsubscribeWeakEvents();
            OnPageNavigated = null;
            System.Threading.Interlocked.Increment(ref _searchGeneration);
            System.Threading.Interlocked.Increment(ref _gameLookupGeneration);
            AccountListFilterController.DetachPropertyChanged(_accounts, OnAccountViewModelPropertyChanged);

            var searchCts = System.Threading.Interlocked.Exchange(ref _searchCts, null);
            SafeExecuteHelper.Try(() => searchCts?.Cancel(), "MainViewModel.Dispose.SearchCtsCancel");
            SafeExecuteHelper.Try(() => searchCts?.Dispose(), "MainViewModel.Dispose.SearchCtsDispose");

            var gameCts = System.Threading.Interlocked.Exchange(ref _gameLookupCts, null);
            SafeExecuteHelper.Try(() => gameCts?.Cancel(), "MainViewModel.Dispose.GameCtsCancel");
            SafeExecuteHelper.Try(() => gameCts?.Dispose(), "MainViewModel.Dispose.GameCtsDispose");

            ServerListViewModel?.Dispose();
            GC.SuppressFinalize(this);
        }

        public bool IsShuffleJobId
        {
            get => AppServices.ServerBrowserState.ShuffleJobId;
            set
            {
                if (AppServices.ServerBrowserState.ShuffleJobId != value)
                {
                    AppServices.ServerBrowserState.ShuffleJobId = value;
                    OnPropertyChanged();
                    StatusMessage = value
                        ? Shiro.Services.LocalizationService.Get("Main.ShuffleJobIdEnabled")
                        : Shiro.Services.LocalizationService.Get("Main.ShuffleJobIdDisabled");
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public string PlaceId
        {
            get => _placeId;
            set
            {
                if (SetProperty(ref _placeId, value))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        AppServices.ServerBrowserState.CurrentPlaceId = value.Trim();
                        _settings?.Set("SavedPlaceId", value.Trim());
                        AppServices.HostUiBridge.QueueDeferredSaveSettings();
                    }
                    ServerListViewModel?.SyncPlaceId(value);
                    ScheduleCurrentGameLookup(value);
                }
            }
        }

        public string JobId
        {
            get => _jobId;
            set
            {
                if (SetProperty(ref _jobId, value))
                {
                    AppServices.ServerBrowserState.CurrentJobId = value?.Trim() ?? string.Empty;
                }
            }
        }

        private int _runningRobloxProcessCount;
        public int RunningRobloxProcessCount
        {
            get => _runningRobloxProcessCount;
            set
            {
                if (System.Threading.Volatile.Read(ref _disposed) == 0 && SetProperty(ref _runningRobloxProcessCount, value))
                {
                    OnPropertyChanged(nameof(CloseRobloxButtonText));
                    OnPropertyChanged(nameof(HasRunningRobloxProcesses));
                }
            }
        }

        public bool HasRunningRobloxProcesses => _runningRobloxProcessCount > 0;

        private bool _isCompactView;
        public bool IsCompactView
        {
            get => _isCompactView;
            set
            {
                if (SetProperty(ref _isCompactView, value))
                {
                    OnPropertyChanged(nameof(IsCardView));
                }
            }
        }

        public bool IsCardView => !IsCompactView;

        public AccountViewModel? TryGetAccountViewModel(Account? account)
        {
            if (account == null) return null;
            if (_vmByModel.TryGetValue(account, out var byModel))
                return byModel;
            if (account.UserID > 0 && _vmByUserId.TryGetValue(account.UserID, out var byId))
                return byId;
            if (!string.IsNullOrEmpty(account.Username) && _vmByUsername.TryGetValue(account.Username, out var byName))
                return byName;
            return null;
        }

        internal void RebuildLookupIndex()
        {
            if (Avalonia.Application.Current != null && Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(RebuildLookupIndex);
                return;
            }
            AccountListFilterController.RebuildLookupIndex(_accounts, _vmByModel, _vmByUserId, _vmByUsername);
        }

        internal void RemoveAccountViewModels(params AccountViewModel[] toRemove)
        {
            foreach (var vm in toRemove)
            {
                if (vm == null) continue;
                vm.PropertyChanged -= OnAccountViewModelPropertyChanged;
                if (ReferenceEquals(_lastClickedAccount, vm)) _lastClickedAccount = null;
                _accounts.Remove(vm);
            }
            RebuildLookupIndex();
        }

        private void OnAccountViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AccountViewModel.IsSelected) && System.Threading.Volatile.Read(ref _disposed) == 0)
                NotifyCounts();
        }

        public void LoadAccounts(IEnumerable<Account> sourceAccounts)
        {
            if (Avalonia.Application.Current != null && Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(() => LoadAccounts(sourceAccounts));
                return;
            }

            lock (_accountsUiLock)
            {
                if (System.Threading.Volatile.Read(ref _disposed) != 0)
                    return;

                if (sourceAccounts == null)
                {
                    AccountListFilterController.DetachPropertyChanged(_accounts, OnAccountViewModelPropertyChanged);
                    _accounts.Clear();
                    _lastClickedAccount = null;
                    RebuildLookupIndex();
                    RebuildGroupList(applyFilterOnReset: false);
                    ApplyFilter();
                    NotifyCounts();
                    RefreshLiveProcessStatus();
                    return;
                }

                var existingByRef = new Dictionary<Account, AccountViewModel>();
                var existingById = new Dictionary<long, AccountViewModel>();
                var existingByName = new Dictionary<string, AccountViewModel>(StringComparer.OrdinalIgnoreCase);

                foreach (var existing in _accounts)
                {
                    if (existing.Model != null && !existingByRef.ContainsKey(existing.Model))
                        existingByRef[existing.Model] = existing;
                    if (existing.UserId > 0 && !existingById.ContainsKey(existing.UserId))
                        existingById[existing.UserId] = existing;
                    if (!string.IsNullOrEmpty(existing.Username) && !existingByName.ContainsKey(existing.Username))
                        existingByName[existing.Username] = existing;
                }

                var claimed = new HashSet<AccountViewModel>();
                var list = new List<AccountViewModel>();

                foreach (var acc in sourceAccounts)
                {
                    if (acc == null) continue;

                    AccountViewModel? vm = null;
                    if (existingByRef.TryGetValue(acc, out var byRef) && claimed.Add(byRef))
                    {
                        vm = byRef;
                    }
                    else if (acc.UserID > 0 && existingById.TryGetValue(acc.UserID, out var byId) && claimed.Add(byId))
                    {
                        vm = byId;
                    }
                    else if (!string.IsNullOrEmpty(acc.Username) && existingByName.TryGetValue(acc.Username, out var byName) && claimed.Add(byName))
                    {
                        vm = byName;
                    }

                    if (vm != null)
                    {
                        vm.UpdateModel(acc);
                    }
                    else
                    {
                        vm = new AccountViewModel(acc);
                        vm.PropertyChanged += OnAccountViewModelPropertyChanged;
                    }

                    list.Add(vm);
                }

                AccountListFilterController.DetachPropertyChanged(_accounts, OnAccountViewModelPropertyChanged, claimed);
                if (_lastClickedAccount != null && !claimed.Contains(_lastClickedAccount)) _lastClickedAccount = null;
                _accounts.ReplaceAll(list);
                RebuildLookupIndex();

                // Warm up avatar cache for any accounts that don't have local disk cache (only when UI app is active)
                if (Avalonia.Application.Current != null)
                {
                    var missingIds = list.Where(a => a.UserId > 0 && !Services.AvatarService.HasDiskCache(a.UserId)).Select(a => a.UserId).ToList();
                    if (missingIds.Count > 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var updatedIds = await Services.AvatarService.BatchEnsureAvatarsAsync(missingIds).ConfigureAwait(false);
                                if (updatedIds != null && updatedIds.Count > 0)
                                {
                                    var updatedSet = new HashSet<long>(updatedIds);
                                    AppDispatcher.Post(() =>
                                    {
                                        foreach (var acc in _accounts)
                                        {
                                            if (System.Threading.Volatile.Read(ref _disposed) != 0) return;
                                            if (updatedSet.Contains(acc.UserId))
                                            {
                                                acc.InvalidateAvatar();
                                            }
                                        }
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Debug($"[LoadAccounts] Avatar warmup error: {ex.Message}");
                            }
                        });
                    }
                }

                RebuildGroupList(applyFilterOnReset: false);
                ApplyFilter();
                NotifyCounts();
                RefreshLiveProcessStatus();
            }
        }
    }
}
