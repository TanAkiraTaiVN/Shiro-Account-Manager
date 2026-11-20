#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Shiro.Services;

namespace Shiro.ViewModels
{
    /// <summary>
    /// Manages dashboard account collections, filtering, sorting, selection,
    /// aggregate metrics, and monotonic weak-event synchronization with <see cref="IAccountRepository"/>.
    /// </summary>
    public sealed partial class AccountCollectionViewModel : ObservableObject, IDisposable, IAccountWorkflowHost
    {
        private static readonly string[] s_localizedCountLabels =
        {
            nameof(SubtitleTotalText),
            nameof(SubtitleReadyText),
            nameof(SubtitleSelectedText),
            nameof(TabAllText),
            nameof(TabInGameText),
            nameof(TabReadyText),
            nameof(TabExpiredText),
            nameof(TabBannedText),
            nameof(TabPinnedText),
            nameof(DeselectCountText),
            nameof(FooterSelectedText),
            nameof(FooterTotalText)
        };

        private readonly IAppLogger _logger;
        private readonly ISettingsService _settings;
        private readonly IAccountRepository _accountRepository;
        private readonly Action<string> _setStatus;
        private readonly Func<string> _getPlaceId;
        private readonly Func<string> _getJobId;
        private readonly Action? _onSelectionChanged;
        private readonly Action? _onAccountsLoaded;

        private readonly BulkObservableCollection<AccountViewModel> _accounts = new();
        private readonly BulkObservableCollection<AccountViewModel> _filteredAccounts = new();
        private readonly BulkObservableCollection<AccountRowViewModel> _filteredAccountRows = new();
        private readonly ObservableCollection<string> _availableGroups = new();
        private readonly Dictionary<Account, AccountViewModel> _vmByModel = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<long, AccountViewModel> _vmByUserId = new();
        private readonly Dictionary<string, AccountViewModel> _vmByUsername = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _accountsUiLock = new();

        private EventHandler<AccountsChangedEventArgs>? _accountsChangedHandler;
        private CancellationTokenSource? _searchCts;
        private AccountViewModel? _lastClickedAccount;
        private string _searchText = string.Empty;
        private string _selectedGroup = "All Groups";
        private AccountFilterTab _activeTab = AccountFilterTab.All;
        private AccountSortMode _sortMode = AccountSortMode.NaturalAscending;
        private int _cardColumns = 3;
        private bool _isCompactView;
        private int _totalCount;
        private int _inGameCount;
        private int _readyCount;
        private int _expiredCount;
        private int _bannedCount;
        private int _pinnedCount;
        private int _selectedCount;
        private long _totalRobuxCount;
        private string _totalRobuxText = "R$ 0";
        private bool _suppressCountNotifications;
        private long _searchGeneration;
        private long _repoSnapshotSeq;
        private long _highestObservedRepoVersion;
        private long _lastAppliedRepoVersion;
        private int _disposed;

        public AccountCollectionViewModel(
            IAppLogger? logger = null,
            ISettingsService? settings = null,
            IAccountRepository? accountRepository = null,
            Action<string>? setStatus = null,
            Func<string>? getPlaceId = null,
            Func<string>? getJobId = null,
            Action? onSelectionChanged = null,
            Action? onAccountsLoaded = null)
        {
            _logger = logger ?? AppServices.Logger;
            _settings = settings ?? AppServices.GeneralSettings;
            _accountRepository = accountRepository ?? AppServices.AccountRepository;
            _setStatus = setStatus ?? (_ => { });
            _getPlaceId = getPlaceId ?? (() => string.Empty);
            _getJobId = getJobId ?? (() => string.Empty);
            _onSelectionChanged = onSelectionChanged;
            _onAccountsLoaded = onAccountsLoaded;

            InitializeCommands();
            SubscribeWeakEvents();
        }

        public ObservableCollection<AccountViewModel> Accounts => _accounts;
        public ObservableCollection<AccountViewModel> FilteredAccounts => _filteredAccounts;
        public ObservableCollection<AccountRowViewModel> FilteredAccountRows => _filteredAccountRows;
        public ObservableCollection<string> AvailableGroups => _availableGroups;

        public int TotalCount => _totalCount;
        public int InGameCount => _inGameCount;
        public int ReadyCount => _readyCount;
        public int ExpiredCount => _expiredCount;
        public bool HasExpiredAccounts => _expiredCount > 0;
        public int BannedCount => _bannedCount;
        public bool HasBannedAccounts => _bannedCount > 0;
        public int PinnedCount => _pinnedCount;
        public int SelectedCount => _selectedCount;
        public bool HasSelection => _selectedCount > 0;
        public long TotalRobuxCount => _totalRobuxCount;
        public string TotalRobuxText => StreamerModeService.ShouldMaskRobux(_settings) ? "R$ ***" : _totalRobuxText;
        public bool IsCardView => !IsCompactView;

        public bool IsCompactView
        {
            get => _isCompactView;
            set { if (SetProperty(ref _isCompactView, value)) OnPropertyChanged(nameof(IsCardView)); }
        }

        public int CardColumns
        {
            get => _cardColumns;
            set { if (SetProperty(ref _cardColumns, Math.Clamp(value, 2, 4))) RebuildAccountRows(); }
        }

        public static int CalculateResponsiveCardColumns(double availableWidth)
        {
            if (double.IsNaN(availableWidth) || availableWidth <= 0) return 3;
            if (availableWidth < 820) return 2;
            if (availableWidth >= 1240) return 4;
            return 3;
        }

        public bool? IsAllSelected
        {
            get
            {
                if (_filteredAccounts.Count == 0) return false;
                int count = _filteredAccounts.Count(a => a.IsSelected);
                return count == 0 ? false : (count == _filteredAccounts.Count ? true : null);
            }
            set
            {
                bool select = value == true;
                _suppressCountNotifications = true;
                try { foreach (var acc in _filteredAccounts) acc.IsSelected = select; }
                finally { _suppressCountNotifications = false; }
                NotifyCounts();
                OnPropertyChanged(nameof(IsAllSelected));
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (!SetProperty(ref _searchText, value)) return;
                long gen = Interlocked.Increment(ref _searchGeneration);
                ServerListOperationHelper.ReplaceCts(ref _searchCts, createNew: false, "AccountCollection.SearchCts");
                if (Volatile.Read(ref _disposed) != 0) return;
                if (string.IsNullOrEmpty(value)) { ApplyFilter(); return; }

                var cts = ServerListOperationHelper.ReplaceCts(ref _searchCts, createNew: true, "AccountCollection.SearchCts")!;
                var token = cts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(120, token).ConfigureAwait(false);
                        if (!token.IsCancellationRequested && Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _searchGeneration) == gen)
                        {
                            AppDispatcher.Post(() =>
                            {
                                if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _searchGeneration) == gen && !token.IsCancellationRequested)
                                    ApplyFilter();
                            });
                        }
                    }
                    catch (OperationCanceledException) { /* debounce canceled */ }
                    catch (ObjectDisposedException) { /* cts disposed */ }
                    finally { ServerListOperationHelper.ReleaseFinishedCts(ref _searchCts, cts, "AccountCollection.SearchCts"); }
                });
            }
        }

        public string SelectedGroup
        {
            get => _selectedGroup;
            set { if (SetProperty(ref _selectedGroup, value)) ApplyFilter(); }
        }

        public AccountFilterTab ActiveTab
        {
            get => _activeTab;
            set
            {
                if (!SetProperty(ref _activeTab, value)) return;
                OnPropertyChanged(nameof(IsFilterAll));
                OnPropertyChanged(nameof(IsFilterInGame));
                OnPropertyChanged(nameof(IsFilterReady));
                OnPropertyChanged(nameof(IsFilterExpired));
                OnPropertyChanged(nameof(IsFilterBanned));
                OnPropertyChanged(nameof(IsFilterPinned));
                ApplyFilter();
            }
        }

        public bool IsFilterAll { get => ActiveTab == AccountFilterTab.All; set { if (value) ActiveTab = AccountFilterTab.All; } }
        public bool IsFilterInGame { get => ActiveTab == AccountFilterTab.InGame; set { if (value) ActiveTab = AccountFilterTab.InGame; } }
        public bool IsFilterReady { get => ActiveTab == AccountFilterTab.Ready; set { if (value) ActiveTab = AccountFilterTab.Ready; } }
        public bool IsFilterExpired { get => ActiveTab == AccountFilterTab.Expired; set { if (value) ActiveTab = AccountFilterTab.Expired; } }
        public bool IsFilterBanned { get => ActiveTab == AccountFilterTab.Banned; set { if (value) ActiveTab = AccountFilterTab.Banned; } }
        public bool IsFilterPinned { get => ActiveTab == AccountFilterTab.Pinned; set { if (value) ActiveTab = AccountFilterTab.Pinned; } }

        public AccountSortMode SortMode
        {
            get => _sortMode;
            set
            {
                if (!SetProperty(ref _sortMode, value)) return;
                ApplyFilter();
                OnPropertyChanged(nameof(IsSortNaturalAsc));
                OnPropertyChanged(nameof(IsSortNaturalDesc));
                OnPropertyChanged(nameof(IsSortOriginal));
                OnPropertyChanged(nameof(IsSortStatus));
                OnPropertyChanged(nameof(IsSortRobuxDesc));
                OnPropertyChanged(nameof(CurrentSortLabel));
            }
        }

        public bool IsSortNaturalAsc { get => SortMode == AccountSortMode.NaturalAscending; set { if (value) SortMode = AccountSortMode.NaturalAscending; } }
        public bool IsSortNaturalDesc { get => SortMode == AccountSortMode.NaturalDescending; set { if (value) SortMode = AccountSortMode.NaturalDescending; } }
        public bool IsSortOriginal { get => SortMode == AccountSortMode.Original; set { if (value) SortMode = AccountSortMode.Original; } }
        public bool IsSortStatus { get => SortMode == AccountSortMode.Status; set { if (value) SortMode = AccountSortMode.Status; } }
        public bool IsSortRobuxDesc { get => SortMode == AccountSortMode.RobuxDescending; set { if (value) SortMode = AccountSortMode.RobuxDescending; } }

        public string CurrentSortLabel => SortMode switch
        {
            AccountSortMode.NaturalDescending => LocalizationService.Get("Main.SortNameDesc"),
            AccountSortMode.Original => LocalizationService.Get("Main.SortOriginal"),
            AccountSortMode.Status => LocalizationService.Get("Main.SortPlayingFirst"),
            AccountSortMode.RobuxDescending => LocalizationService.Get("Main.SortRobuxDesc"),
            _ => LocalizationService.Get("Main.SortNameAsc")
        };

        public string FooterSelectedText => LocalizationService.Format("Main.AccountsSelectedCount", SelectedCount);
        public string FooterTotalText => LocalizationService.Format("Main.TotalAccountsCount", TotalCount);
        public string SubtitleTotalText => LocalizationService.Format("About.AccountsCount", TotalCount);
        public string SubtitleReadyText => LocalizationService.Format("Main.ReadyAccountsCount", ReadyCount);
        public string SubtitleSelectedText => LocalizationService.Format("Main.SelectedCountShort", SelectedCount);
        public string TabAllText => LocalizationService.Format("Main.TabAllCount", TotalCount);
        public string TabInGameText => LocalizationService.Format("Main.TabPlayingCount", InGameCount);
        public string TabReadyText => LocalizationService.Format("Main.TabReadyCount", ReadyCount);
        public string TabExpiredText => LocalizationService.Format("Main.TabExpiredCount", ExpiredCount);
        public string TabBannedText => LocalizationService.Format("Main.TabBannedCount", BannedCount);
        public string TabPinnedText => LocalizationService.Format("Main.TabPinnedCount", PinnedCount);
        public string DeselectCountText => LocalizationService.Format("Main.DeselectCountAction", SelectedCount);

        public AccountViewModel? TryGetAccountViewModel(Account? account)
        {
            if (account == null) return null;
            if (_vmByModel.TryGetValue(account, out var byModel)) return byModel;
            if (account.UserID > 0 && _vmByUserId.TryGetValue(account.UserID, out var byId)) return byId;
            if (!string.IsNullOrEmpty(account.Username) && _vmByUsername.TryGetValue(account.Username, out var byName)) return byName;
            return null;
        }

        public List<Account> GetSelectedAccounts() => _accounts.Where(a => a.IsSelected && a.Model != null).Select(a => a.Model!).ToList();
        public List<AccountViewModel> GetSelectedAccountViewModels() => _accounts.Where(a => a.IsSelected).ToList();

        public void ApplyFilter()
        {
            if (Avalonia.Application.Current != null && Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(ApplyFilter);
                return;
            }

            lock (_accountsUiLock)
            {
                var resultList = AccountListFilterController.FilterAndSortAccounts(
                    _accounts, SearchText, SelectedGroup, ActiveTab, SortMode);

                if (AccountListFilterController.IsSameSequence(_filteredAccounts, resultList))
                {
                    int cols = Math.Clamp(_cardColumns, 2, 4);
                    int expectedRows = (resultList.Count + cols - 1) / cols;
                    if (_filteredAccountRows.Count != expectedRows ||
                        (_filteredAccountRows.Count > 0 && _filteredAccountRows[0].ColumnCount != cols))
                    {
                        RebuildAccountRows();
                    }
                    return;
                }

                _filteredAccounts.Sync(resultList);
                RebuildAccountRows();
                OnPropertyChanged(nameof(IsAllSelected));
            }
        }

        private void RebuildAccountRows() =>
            _filteredAccountRows.Sync(AccountListFilterController.BuildAccountRows(_filteredAccounts, _cardColumns));

        public void RefreshAccount(Account? account)
        {
            if (account == null || Volatile.Read(ref _disposed) != 0) return;
            if (Avalonia.Application.Current != null && Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(() => RefreshAccount(account));
                return;
            }

            AccountViewModel? vm = TryGetAccountViewModel(account);
            if (vm != null)
            {
                vm.UpdateModel(account);
                RebuildLookupIndex();
                RebuildGroupList(applyFilterOnReset: false);
                ApplyFilter();
                NotifyCounts();
            }
        }

        public void UpdateAccountValidity(Account account, bool isValid, bool refreshFilterAndCounts = true)
        {
            if (account == null || Volatile.Read(ref _disposed) != 0) return;
            if (Avalonia.Application.Current != null && Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(() => UpdateAccountValidity(account, isValid, refreshFilterAndCounts));
                return;
            }

            var vm = TryGetAccountViewModel(account);
            if (vm != null)
            {
                vm.Valid = isValid;
                vm.NotifyValidityChanged();
            }
            if (refreshFilterAndCounts)
            {
                NotifyCounts();
                if (ActiveTab == AccountFilterTab.Expired || ActiveTab == AccountFilterTab.Banned || ActiveTab == AccountFilterTab.Ready)
                    ApplyFilter();
            }
        }

        public void NotifyCounts()
        {
            if (_suppressCountNotifications) return;

            AccountCountsSummary c;
            lock (_accountsUiLock)
                c = AccountListFilterController.ComputeAccountCounts(_accounts);

            if (_totalCount != c.Total) { _totalCount = c.Total; OnPropertyChanged(nameof(TotalCount)); }
            if (_inGameCount != c.InGame) { _inGameCount = c.InGame; OnPropertyChanged(nameof(InGameCount)); }
            if (_readyCount != c.Ready) { _readyCount = c.Ready; OnPropertyChanged(nameof(ReadyCount)); }
            if (_expiredCount != c.Expired)
            {
                bool had = _expiredCount > 0;
                _expiredCount = c.Expired;
                OnPropertyChanged(nameof(ExpiredCount));
                if (had != (c.Expired > 0)) OnPropertyChanged(nameof(HasExpiredAccounts));
            }
            if (_bannedCount != c.Banned)
            {
                bool had = _bannedCount > 0;
                _bannedCount = c.Banned;
                OnPropertyChanged(nameof(BannedCount));
                if (had != (c.Banned > 0)) OnPropertyChanged(nameof(HasBannedAccounts));
            }
            if (_pinnedCount != c.Pinned) { _pinnedCount = c.Pinned; OnPropertyChanged(nameof(PinnedCount)); }
            if (_selectedCount != c.Selected)
            {
                bool had = _selectedCount > 0;
                _selectedCount = c.Selected;
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(IsAllSelected));
                if (had != (c.Selected > 0)) OnPropertyChanged(nameof(HasSelection));
                _onSelectionChanged?.Invoke();
            }
            if (_totalRobuxCount != c.Robux)
            {
                _totalRobuxCount = c.Robux;
                _totalRobuxText = $"R$ {c.Robux:N0}";
                OnPropertyChanged(nameof(TotalRobuxCount));
                OnPropertyChanged(nameof(TotalRobuxText));
            }

            NotifyLocalizedLabelsChanged();
        }

        private void NotifyLocalizedLabelsChanged()
        {
            foreach (string prop in s_localizedCountLabels)
                OnPropertyChanged(prop);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            if (_accountsChangedHandler != null)
                _accountRepository.AccountsChanged -= _accountsChangedHandler;

            Interlocked.Increment(ref _searchGeneration);
            AccountListFilterController.DetachPropertyChanged(_accounts, OnAccountViewModelPropertyChanged);

            var searchCts = Interlocked.Exchange(ref _searchCts, null);
            SafeExecuteHelper.Try(() => searchCts?.Cancel(), "AccountCollectionViewModel.Dispose.SearchCtsCancel");
            SafeExecuteHelper.Try(() => searchCts?.Dispose(), "AccountCollectionViewModel.Dispose.SearchCtsDispose");
            GC.SuppressFinalize(this);
        }
    }
}
