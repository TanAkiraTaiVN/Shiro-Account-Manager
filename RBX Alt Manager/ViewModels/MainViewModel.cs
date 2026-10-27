using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.ViewModels
{
    public enum AccountFilterTab
    {
        All,
        InGame,
        Ready,
        Expired,
        Pinned
    }

    public enum NavigationPage
    {
        Accounts,
        ServerList,
        RecentGames,
        Settings
    }

    public enum AccountSortMode
    {
        NaturalAscending,  // Tên (1 → 9, A → Z) - Mặc định
        NaturalDescending, // Tên (9 → 1, Z → A)
        Original,          // Thứ tự ban đầu
        Status,            // Trạng thái (Đang chơi / Online)
        RobuxDescending    // Số dư Robux (Nhiều → Ít)
    }

    public partial class MainViewModel : ObservableObject
    {
        private readonly BulkObservableCollection<AccountViewModel> _accounts = new BulkObservableCollection<AccountViewModel>();
        private readonly BulkObservableCollection<AccountViewModel> _filteredAccounts = new BulkObservableCollection<AccountViewModel>();
        private readonly BulkObservableCollection<AccountRowViewModel> _filteredAccountRows = new BulkObservableCollection<AccountRowViewModel>();
        private readonly ObservableCollection<string> _availableGroups = new ObservableCollection<string>();

        private string _searchText = string.Empty;
        private string _selectedGroup = "All Groups";
        private AccountFilterTab _activeTab = AccountFilterTab.All;
        private NavigationPage _currentPage = NavigationPage.Accounts;
        private string _statusMessage = string.Empty;
        private AccountSortMode _sortMode = AccountSortMode.NaturalAscending;
        private string _placeId = string.Empty;
        private string _jobId = string.Empty;
        private bool _isPotatoMode;
        private bool _isAntiAfk;
        private bool _areWindowsHidden;
        private int _lastRobloxProcessCount = -1;

        private int _totalCount;
        private int _inGameCount;
        private int _readyCount;
        private int _expiredCount;
        private int _pinnedCount;
        private int _selectedCount;
        private long _totalRobuxCount;
        private string _totalRobuxText = "R$ 0";
        private CancellationTokenSource _searchCts;

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
                    OnPageNavigated?.Invoke(value);
                }
            }
        }

        public bool IsAccountsActive => CurrentPage == NavigationPage.Accounts;
        public bool IsServerListActive => CurrentPage == NavigationPage.ServerList;
        public bool IsRecentGamesActive => CurrentPage == NavigationPage.RecentGames;
        public bool IsSettingsActive => CurrentPage == NavigationPage.Settings;

        // Callback when navigation page changes
        public Action<NavigationPage> OnPageNavigated { get; set; }

        public ObservableCollection<AccountViewModel> Accounts => _accounts;
        public ObservableCollection<AccountViewModel> FilteredAccounts => _filteredAccounts;
        public ObservableCollection<AccountRowViewModel> FilteredAccountRows => _filteredAccountRows;
        public ObservableCollection<string> AvailableGroups => _availableGroups;

        public ServerListViewModel ServerListViewModel { get; private set; }

        public MainViewModel()
        {
            string savedPid = AccountManager.General?.Get("SavedPlaceId");
            if (!string.IsNullOrWhiteSpace(savedPid))
            {
                _placeId = savedPid.Trim();
                AccountManager.CurrentPlaceId = _placeId;
            }
            else if (!string.IsNullOrWhiteSpace(AccountManager.CurrentPlaceId))
            {
                _placeId = AccountManager.CurrentPlaceId;
            }
            else
            {
                _placeId = string.Empty;
            }
            _statusMessage = Loc.Get("Status_ReadyDefault");
            LocalizationService.LanguageChanged += lang =>
            {
                OnPropertyChanged(nameof(CloseRobloxButtonText));
                NotifyLocalizedLabelsChanged();
                if (string.IsNullOrWhiteSpace(_statusMessage) || _statusMessage.Contains("Double-click"))
                {
                    StatusMessage = Loc.Get("Status_ReadyDefault");
                }
                if (Accounts != null)
                {
                    foreach (var acc in Accounts)
                        acc.NotifyLanguageChanged();
                }
            };

            ServerListViewModel = new ServerListViewModel(this);
            InitializeCommands();
            RefreshStateFromSettings();
            AccentThemeService.ThemeChanged += OnAccentThemeChanged;
            LaunchCoordinator.LaunchProgressChanged += OnLaunchProgressChanged;
        }

        private void OnLaunchProgressChanged(LaunchProgress progress)
        {
            if (progress == null) return;
            AppDispatcher.Post(() =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(progress.Message))
                    {
                        StatusMessage = progress.Message;
                    }

                    if (progress.Account != null)
                    {
                        var vm = _accounts.FirstOrDefault(a =>
                            ReferenceEquals(a.Model, progress.Account) ||
                            (a.UserId > 0 && a.UserId == progress.Account.UserID) ||
                            (!string.IsNullOrEmpty(a.Username) && a.Username.Equals(progress.Account.Username, StringComparison.OrdinalIgnoreCase)));

                        if (vm != null)
                        {
                            bool isActivelyLaunching = !progress.Completed &&
                                progress.Status != LaunchOutcome.Cancelled &&
                                progress.Status != LaunchOutcome.Failed &&
                                progress.Status != LaunchOutcome.TicketError;

                            vm.IsLaunching = isActivelyLaunching;
                        }
                    }
                    else if (progress.Completed)
                    {
                        foreach (var acc in _accounts)
                        {
                            acc.IsLaunching = false;
                        }
                    }
                }
                catch { }
            });
        }

        private void OnAccentThemeChanged(AccentTheme theme)
        {
            try
            {
                foreach (var acc in _accounts)
                {
                    acc.NotifyThemeChanged();
                }
            }
            catch { }
        }

        public bool IsShuffleJobId
        {
            get => AccountManager.ShuffleJobID;
            set
            {
                if (AccountManager.ShuffleJobID != value)
                {
                    AccountManager.ShuffleJobID = value;
                    OnPropertyChanged();
                    StatusMessage = value
                        ? "🎲 Shuffle Job ID đã BẬT: Tham gia server ngẫu nhiên khi vào game."
                        : "🎲 Shuffle Job ID đã TẮT.";
                }
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    var previousCts = System.Threading.Interlocked.Exchange(ref _searchCts, null);
                    if (previousCts != null)
                    {
                        try { previousCts.Cancel(); } catch { }
                        try { previousCts.Dispose(); } catch { }
                    }

                    if (string.IsNullOrEmpty(value))
                    {
                        ApplyFilter();
                    }
                    else
                    {
                        var cts = new CancellationTokenSource();
                        var token = cts.Token;
                        _searchCts = cts;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(120, token).ConfigureAwait(false);
                                if (!token.IsCancellationRequested)
                                {
                                    AppDispatcher.Post(() => ApplyFilter());
                                }
                            }
                            catch (OperationCanceledException) { }
                            catch (ObjectDisposedException) { }
                        });
                    }
                }
            }
        }

        public string SelectedGroup
        {
            get => _selectedGroup;
            set
            {
                if (SetProperty(ref _selectedGroup, value))
                    ApplyFilter();
            }
        }

        public AccountFilterTab ActiveTab
        {
            get => _activeTab;
            set
            {
                if (SetProperty(ref _activeTab, value))
                {
                    OnPropertyChanged(nameof(IsFilterAll));
                    OnPropertyChanged(nameof(IsFilterInGame));
                    OnPropertyChanged(nameof(IsFilterReady));
                    OnPropertyChanged(nameof(IsFilterExpired));
                    OnPropertyChanged(nameof(IsFilterPinned));
                    ApplyFilter();
                }
            }
        }

        public bool IsFilterAll
        {
            get => ActiveTab == AccountFilterTab.All;
            set { if (value) ActiveTab = AccountFilterTab.All; }
        }

        public bool IsFilterInGame
        {
            get => ActiveTab == AccountFilterTab.InGame;
            set { if (value) ActiveTab = AccountFilterTab.InGame; }
        }

        public bool IsFilterReady
        {
            get => ActiveTab == AccountFilterTab.Ready;
            set { if (value) ActiveTab = AccountFilterTab.Ready; }
        }

        public bool IsFilterExpired
        {
            get => ActiveTab == AccountFilterTab.Expired;
            set { if (value) ActiveTab = AccountFilterTab.Expired; }
        }

        public bool IsFilterPinned
        {
            get => ActiveTab == AccountFilterTab.Pinned;
            set { if (value) ActiveTab = AccountFilterTab.Pinned; }
        }

        public AccountSortMode SortMode
        {
            get => _sortMode;
            set
            {
                if (SetProperty(ref _sortMode, value))
                {
                    ApplyFilter();
                    OnPropertyChanged(nameof(IsSortNaturalAsc));
                    OnPropertyChanged(nameof(IsSortNaturalDesc));
                    OnPropertyChanged(nameof(IsSortOriginal));
                    OnPropertyChanged(nameof(IsSortStatus));
                    OnPropertyChanged(nameof(IsSortRobuxDesc));
                }
            }
        }

        public bool IsSortNaturalAsc
        {
            get => SortMode == AccountSortMode.NaturalAscending;
            set { if (value) SortMode = AccountSortMode.NaturalAscending; }
        }
        public bool IsSortNaturalDesc
        {
            get => SortMode == AccountSortMode.NaturalDescending;
            set { if (value) SortMode = AccountSortMode.NaturalDescending; }
        }
        public bool IsSortOriginal
        {
            get => SortMode == AccountSortMode.Original;
            set { if (value) SortMode = AccountSortMode.Original; }
        }
        public bool IsSortStatus
        {
            get => SortMode == AccountSortMode.Status;
            set { if (value) SortMode = AccountSortMode.Status; }
        }
        public bool IsSortRobuxDesc
        {
            get => SortMode == AccountSortMode.RobuxDescending;
            set { if (value) SortMode = AccountSortMode.RobuxDescending; }
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
                        AccountManager.CurrentPlaceId = value.Trim();
                        AccountManager.General?.Set("SavedPlaceId", value.Trim());
                        AccountManager.QueueDeferredSaveSettings();
                    }
                    ServerListViewModel?.SyncPlaceId(value);
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
                    AccountManager.CurrentJobId = value?.Trim() ?? string.Empty;
                }
            }
        }

        public int TotalCount => _totalCount;
        public int InGameCount => _inGameCount;
        public int ReadyCount => _readyCount;
        public int ExpiredCount => _expiredCount;
        public bool HasExpiredAccounts => _expiredCount > 0;
        public int PinnedCount => _pinnedCount;
        public int SelectedCount => _selectedCount;
        public bool HasSelection => _selectedCount > 0;

        private bool _suppressCountNotifications;

        public bool? IsAllSelected
        {
            get
            {
                if (_filteredAccounts.Count == 0) return false;
                int count = 0;
                foreach (var a in _filteredAccounts)
                {
                    if (a.IsSelected) count++;
                }
                if (count == 0) return false;
                if (count == _filteredAccounts.Count) return true;
                return null;
            }
            set
            {
                bool select = value == true;
                _suppressCountNotifications = true;
                try
                {
                    foreach (var acc in _filteredAccounts)
                        acc.IsSelected = select;
                }
                finally
                {
                    _suppressCountNotifications = false;
                }
                NotifyCounts();
                OnPropertyChanged(nameof(IsAllSelected));
            }
        }
        public long TotalRobuxCount => _totalRobuxCount;
        public string TotalRobuxText => _totalRobuxText;

        private int _runningRobloxProcessCount;
        public int RunningRobloxProcessCount
        {
            get => _runningRobloxProcessCount;
            set
            {
                if (SetProperty(ref _runningRobloxProcessCount, value))
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

        public void LoadAccounts(IEnumerable<Account> sourceAccounts)
        {
            if (Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(() => LoadAccounts(sourceAccounts));
                return;
            }

            if (sourceAccounts == null)
            {
                _accounts.Clear();
                RebuildGroupList();
                ApplyFilter();
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

                AccountViewModel vm = null;
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
                    vm.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(AccountViewModel.IsSelected))
                            NotifyCounts();
                    };
                }

                list.Add(vm);
            }

            _accounts.ReplaceAll(list);

            // Warm up avatar cache for any accounts that don't have local disk cache
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
                        Program.Logger?.Debug($"[LoadAccounts] Avatar warmup error: {ex.Message}");
                    }
                });
            }

            RebuildGroupList(applyFilterOnReset: false);
            ApplyFilter();
            RefreshLiveProcessStatus();
        }

        public List<Account> GetSelectedAccounts()
        {
            return _accounts.Where(a => a.IsSelected && a.Model != null).Select(a => a.Model).ToList();
        }

        public List<AccountViewModel> GetSelectedAccountViewModels()
        {
            return _accounts.Where(a => a.IsSelected).ToList();
        }

        public void UpdateAccountValidity(Account account, bool isValid, bool refreshFilterAndCounts = true)
        {
            if (account == null) return;
            if (Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(() => UpdateAccountValidity(account, isValid, refreshFilterAndCounts));
                return;
            }

            var vm = _accounts.FirstOrDefault(a => a.Model == account || (a.UserId > 0 && a.UserId == account.UserID) || (!string.IsNullOrEmpty(a.Username) && a.Username.Equals(account.Username, StringComparison.OrdinalIgnoreCase)));
            if (vm != null)
            {
                vm.Valid = isValid;
                vm.NotifyValidityChanged();
            }
            if (refreshFilterAndCounts)
            {
                NotifyCounts();
                if (ActiveTab == AccountFilterTab.Expired || ActiveTab == AccountFilterTab.Ready)
                {
                    ApplyFilter();
                }
            }
        }

        public void RebuildGroupList(bool applyFilterOnReset = true)
        {
            var seenGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sortedGroups = new List<string>();

            foreach (var acc in _accounts)
            {
                string g = acc.Group;
                if (!string.IsNullOrWhiteSpace(g) && seenGroups.Add(g))
                    sortedGroups.Add(g);
            }

            sortedGroups.Sort(NaturalStringComparer.Instance);

            bool groupsChanged = _availableGroups.Count != sortedGroups.Count + 1 ||
                                 (_availableGroups.Count > 0 && _availableGroups[0] != "All Groups");
            if (!groupsChanged)
            {
                for (int i = 0; i < sortedGroups.Count; i++)
                {
                    if (!string.Equals(_availableGroups[i + 1], sortedGroups[i], StringComparison.Ordinal))
                    {
                        groupsChanged = true;
                        break;
                    }
                }
            }

            if (groupsChanged)
            {
                _availableGroups.Clear();
                _availableGroups.Add("All Groups");
                foreach (var group in sortedGroups)
                    _availableGroups.Add(group);
            }

            if (!string.Equals(SelectedGroup, "All Groups", StringComparison.Ordinal) &&
                (string.IsNullOrEmpty(SelectedGroup) || !seenGroups.Contains(SelectedGroup)))
            {
                if (applyFilterOnReset)
                {
                    SelectedGroup = "All Groups";
                }
                else
                {
                    _selectedGroup = "All Groups";
                    OnPropertyChanged(nameof(SelectedGroup));
                }
            }
            else
            {
                OnPropertyChanged(nameof(SelectedGroup));
            }
        }

        public void ApplyFilter()
        {
            if (Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(ApplyFilter);
                return;
            }

            var searchQuery = AccountSearchQuery.Create(SearchText);
            bool filterByGroup = !string.IsNullOrEmpty(SelectedGroup) && SelectedGroup != "All Groups";
            bool hasSearch = !searchQuery.IsEmpty;
            var activeTab = ActiveTab;
            string selectedGroup = SelectedGroup;

            var resultList = new List<AccountViewModel>(_accounts.Count);
            for (int i = 0; i < _accounts.Count; i++)
            {
                var a = _accounts[i];
                if (filterByGroup && a.Group != selectedGroup)
                    continue;

                switch (activeTab)
                {
                    case AccountFilterTab.InGame:
                        if (!a.IsInGame) continue;
                        break;
                    case AccountFilterTab.Ready:
                        if (!a.Valid || a.IsInGame) continue;
                        break;
                    case AccountFilterTab.Expired:
                        if (!a.IsExpired) continue;
                        break;
                    case AccountFilterTab.Pinned:
                        if (!a.Pinned) continue;
                        break;
                }

                if (hasSearch && !searchQuery.IsMatch(a))
                    continue;

                resultList.Add(a);
            }

            // Sorting logic: Pinned accounts always display first, then sort according to SortMode
            var sortMode = SortMode;
            resultList.Sort((x, y) =>
            {
                int pinCmp = y.Pinned.CompareTo(x.Pinned);
                if (pinCmp != 0) return pinCmp;

                switch (sortMode)
                {
                    case AccountSortMode.NaturalDescending:
                    {
                        int validCmp = y.Valid.CompareTo(x.Valid);
                        if (validCmp != 0) return validCmp;
                        return NaturalStringComparer.Instance.Compare(
                            y.PrimaryTitle ?? y.Username ?? string.Empty,
                            x.PrimaryTitle ?? x.Username ?? string.Empty);
                    }
                    case AccountSortMode.Original:
                        return y.Valid.CompareTo(x.Valid);
                    case AccountSortMode.Status:
                    {
                        int gameCmp = y.IsInGame.CompareTo(x.IsInGame);
                        if (gameCmp != 0) return gameCmp;
                        int validCmp = y.Valid.CompareTo(x.Valid);
                        if (validCmp != 0) return validCmp;
                        return NaturalStringComparer.Instance.Compare(
                            x.PrimaryTitle ?? x.Username ?? string.Empty,
                            y.PrimaryTitle ?? y.Username ?? string.Empty);
                    }
                    case AccountSortMode.RobuxDescending:
                    {
                        int robuxCmp = y.RobuxBalance.CompareTo(x.RobuxBalance);
                        if (robuxCmp != 0) return robuxCmp;
                        return NaturalStringComparer.Instance.Compare(
                            x.PrimaryTitle ?? x.Username ?? string.Empty,
                            y.PrimaryTitle ?? y.Username ?? string.Empty);
                    }
                    case AccountSortMode.NaturalAscending:
                    default:
                    {
                        int validCmp = y.Valid.CompareTo(x.Valid);
                        if (validCmp != 0) return validCmp;
                        return NaturalStringComparer.Instance.Compare(
                            x.PrimaryTitle ?? x.Username ?? string.Empty,
                            y.PrimaryTitle ?? y.Username ?? string.Empty);
                    }
                }
            });

            if (IsSameSequence(_filteredAccounts, resultList))
                return;

            _filteredAccounts.Sync(resultList);
            OnPropertyChanged(nameof(IsAllSelected));
        }

        private void RebuildAccountRows()
        {
            var rows = new List<AccountRowViewModel>((_filteredAccounts.Count + 2) / 3);
            for (int i = 0; i < _filteredAccounts.Count; i += 3)
            {
                var item1 = _filteredAccounts[i];
                var item2 = (i + 1 < _filteredAccounts.Count) ? _filteredAccounts[i + 1] : null;
                var item3 = (i + 2 < _filteredAccounts.Count) ? _filteredAccounts[i + 2] : null;
                rows.Add(new AccountRowViewModel(item1, item2, item3));
            }
            _filteredAccountRows.Sync(rows);
        }

        private static bool IsSameSequence(IList<AccountViewModel> current, IList<AccountViewModel> next)
        {
            if (current == null || next == null) return current == next;
            if (current.Count != next.Count) return false;
            for (int i = 0; i < current.Count; i++)
            {
                if (!ReferenceEquals(current[i], next[i]))
                    return false;
            }
            return true;
        }

        public void RefreshLiveProcessStatus()
        {
            if (Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(RefreshLiveProcessStatus);
                return;
            }

            try
            {
                int totalRobloxProcs = RobloxProcessService.GetTotalRunningRobloxCount();
                RunningRobloxProcessCount = totalRobloxProcs;

                if (_lastRobloxProcessCount > 0 && totalRobloxProcs < _lastRobloxProcessCount)
                {
                    // A Roblox process has exited naturally; clean up ghost icons in system tray
                    Utilities.QueueRefreshNotificationArea();
                }
                _lastRobloxProcessCount = totalRobloxProcs;

                bool stateChanged = false;
                foreach (var acc in _accounts)
                {
                    // Check presence status: ONLY truly in game if userPresenceType == InGame!
                    // Do NOT use lastLocation check, as Roblox presence API often returns "Website" or cached location
                    // which mistakenly marks offline/browsing accounts as InGame.
                    bool hasActivePresence = acc.Model?.Presence != null &&
                                             acc.Model.Presence.userPresenceType == UserPresenceType.InGame;
                    
                    if (acc.IsInGame != hasActivePresence)
                    {
                        acc.IsInGame = hasActivePresence;
                        stateChanged = true;
                    }
                }

                if (stateChanged && (ActiveTab == AccountFilterTab.InGame || ActiveTab == AccountFilterTab.Ready))
                {
                    ApplyFilter();
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[RefreshLiveProcessStatus] Process check error: {ex.Message}");
            }

            NotifyCounts();
        }

        public void NotifyCounts()
        {
            if (_suppressCountNotifications)
                return;

            int total = _accounts.Count;
            int inGame = 0;
            int ready = 0;
            int expired = 0;
            int pinned = 0;
            int selected = 0;
            long robux = 0;

            for (int i = 0; i < total; i++)
            {
                var a = _accounts[i];
                if (a.IsInGame) inGame++;
                else if (a.Valid) ready++;
                if (a.IsExpired) expired++;
                if (a.Pinned) pinned++;
                if (a.IsSelected) selected++;
                if (a.RobuxBalance > 0) robux += a.RobuxBalance;
            }

            if (_totalCount != total)
            {
                _totalCount = total;
                OnPropertyChanged(nameof(TotalCount));
            }
            if (_inGameCount != inGame)
            {
                _inGameCount = inGame;
                OnPropertyChanged(nameof(InGameCount));
            }
            if (_readyCount != ready)
            {
                _readyCount = ready;
                OnPropertyChanged(nameof(ReadyCount));
            }
            if (_expiredCount != expired)
            {
                bool hadExpired = _expiredCount > 0;
                _expiredCount = expired;
                OnPropertyChanged(nameof(ExpiredCount));
                if (hadExpired != (expired > 0))
                    OnPropertyChanged(nameof(HasExpiredAccounts));
            }
            if (_pinnedCount != pinned)
            {
                _pinnedCount = pinned;
                OnPropertyChanged(nameof(PinnedCount));
            }
            if (_selectedCount != selected)
            {
                bool hadSelection = _selectedCount > 0;
                _selectedCount = selected;
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(IsAllSelected));
                OnPropertyChanged(nameof(CloseRobloxButtonText));
                if (hadSelection != (selected > 0))
                    OnPropertyChanged(nameof(HasSelection));
            }
            if (_totalRobuxCount != robux)
            {
                _totalRobuxCount = robux;
                _totalRobuxText = $"R$ {robux:N0}";
                OnPropertyChanged(nameof(TotalRobuxCount));
                OnPropertyChanged(nameof(TotalRobuxText));
            }

            NotifyLocalizedLabelsChanged();
        }
    }
}
