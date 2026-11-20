#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class MainViewModel
    {
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
        public long TotalRobuxCount => _totalRobuxCount;
        public string TotalRobuxText => StreamerModeService.ShouldMaskRobux(_settings) ? "R$ ***" : _totalRobuxText;

        public List<Account> GetSelectedAccounts() => _accounts.Where(a => a.IsSelected && a.Model != null).Select(a => a.Model!).ToList();
        public List<AccountViewModel> GetSelectedAccountViewModels() => _accounts.Where(a => a.IsSelected).ToList();

        public Task<IReadOnlyDictionary<Account, AccountSessionAuditResult>> AuditAccountSessionsAsync(CancellationToken ct = default)
            => SessionAuditorService.AuditAllAccountsAsync(_accounts.Where(a => a.Model != null).Select(a => a.Model!), null, ct);

        public void UpdateAccountValidity(Account account, bool isValid, bool refreshFilterAndCounts = true)
        {
            if (account == null) return;
            if (System.Threading.Volatile.Read(ref _disposed) != 0) return;
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
                if (ActiveTab == AccountFilterTab.Expired ||
                    ActiveTab == AccountFilterTab.Banned ||
                    ActiveTab == AccountFilterTab.Ready)
                {
                    ApplyFilter();
                }
            }
        }

        private int _isRefreshingLiveProcessStatus;
        private volatile bool _hasPendingRefreshRequest;

        public void RefreshLiveProcessStatus()
        {
            if (System.Threading.Volatile.Read(ref _disposed) != 0) return;
            if (Avalonia.Application.Current != null && Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(RefreshLiveProcessStatus);
                return;
            }

            // In headless/test runner without active Avalonia app, execute inline to avoid background race conditions
            if (Avalonia.Application.Current == null)
            {
                int total = RobloxProcessService.GetTotalRunningRobloxCount();
                Dictionary<Account, int>? map = null;
                if (total > 0 && _accounts.Count > 0)
                {
                    map = RobloxWindowArrangerService.ResolveRunningAccountPids(
                        _accounts.Where(a => a.Model != null).Select(a => a.Model));
                }
                ApplyLiveProcessStatusResults(total, map);
                return;
            }

            if (System.Threading.Interlocked.CompareExchange(ref _isRefreshingLiveProcessStatus, 1, 0) != 0)
            {
                _hasPendingRefreshRequest = true;
                return;
            }

            List<Account>? models = null;
            if (_accounts.Count > 0)
            {
                models = new List<Account>(_accounts.Count);
                for (int i = 0; i < _accounts.Count; i++)
                {
                    var m = _accounts[i].Model;
                    if (m != null) models.Add(m);
                }
            }

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    do
                    {
                        _hasPendingRefreshRequest = false;

                        // Heavy Win32 Toolhelp snapshot & PEB command-line queries run purely off-thread
                        int totalRobloxProcs = RobloxProcessService.GetTotalRunningRobloxCount();
                        Dictionary<Account, int>? runningMap = null;
                        if (totalRobloxProcs > 0 && models != null && models.Count > 0)
                        {
                            runningMap = RobloxWindowArrangerService.ResolveRunningAccountPids(models);
                        }

                        AppDispatcher.Post(() => ApplyLiveProcessStatusResults(totalRobloxProcs, runningMap));
                    }
                    while (_hasPendingRefreshRequest);
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[RefreshLiveProcessStatus] Process check error: {ex.Message}");
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _isRefreshingLiveProcessStatus, 0);
                    if (_hasPendingRefreshRequest && System.Threading.Volatile.Read(ref _disposed) == 0)
                    {
                        AppDispatcher.Post(RefreshLiveProcessStatus);
                    }
                }
            });
        }

        private void ApplyLiveProcessStatusResults(int totalRobloxProcs, Dictionary<Account, int>? runningMap)
        {
            try
            {
                if (System.Threading.Volatile.Read(ref _disposed) != 0) return;
                RunningRobloxProcessCount = totalRobloxProcs;
                if (totalRobloxProcs == 0 && _areWindowsHidden)
                {
                    _areWindowsHidden = false;
                    OnPropertyChanged(nameof(AreWindowsHidden));
                    NotifyToolStatesChanged();
                }

                if (_lastRobloxProcessCount > 0 && totalRobloxProcs < _lastRobloxProcessCount)
                {
                    Utilities.QueueRefreshNotificationArea();
                }
                _lastRobloxProcessCount = totalRobloxProcs;

                bool stateChanged = AccountListFilterController.UpdateAccountLiveStatuses(_accounts, runningMap);

                if (stateChanged && (ActiveTab == AccountFilterTab.InGame || ActiveTab == AccountFilterTab.Ready))
                {
                    ApplyFilter();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[ApplyLiveProcessStatusResults] UI update error: {ex.Message}");
            }

            NotifyCounts();
        }

        public void NotifyCounts()
        {
            if (_suppressCountNotifications)
                return;

            AccountCountsSummary c;
            lock (_accountsUiLock)
            {
                c = AccountListFilterController.ComputeAccountCounts(_accounts);
            }

            if (_totalCount != c.Total) { _totalCount = c.Total; OnPropertyChanged(nameof(TotalCount)); }
            if (_inGameCount != c.InGame) { _inGameCount = c.InGame; OnPropertyChanged(nameof(InGameCount)); }
            if (_readyCount != c.Ready) { _readyCount = c.Ready; OnPropertyChanged(nameof(ReadyCount)); }
            if (_expiredCount != c.Expired)
            {
                bool hadExpired = _expiredCount > 0;
                _expiredCount = c.Expired;
                OnPropertyChanged(nameof(ExpiredCount));
                if (hadExpired != (c.Expired > 0)) OnPropertyChanged(nameof(HasExpiredAccounts));
            }
            if (_bannedCount != c.Banned)
            {
                bool hadBanned = _bannedCount > 0;
                _bannedCount = c.Banned;
                OnPropertyChanged(nameof(BannedCount));
                if (hadBanned != (c.Banned > 0)) OnPropertyChanged(nameof(HasBannedAccounts));
            }
            if (_pinnedCount != c.Pinned) { _pinnedCount = c.Pinned; OnPropertyChanged(nameof(PinnedCount)); }
            if (_selectedCount != c.Selected)
            {
                bool hadSelection = _selectedCount > 0;
                _selectedCount = c.Selected;
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(IsAllSelected));
                OnPropertyChanged(nameof(CloseRobloxButtonText));
                if (hadSelection != (c.Selected > 0)) OnPropertyChanged(nameof(HasSelection));
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

        private void SubscribeWeakEvents()
        {
            IAccountRepository repo = _accountRepository;
            _accountsChangedHandler = WeakEventSubscription.Create<MainViewModel, AccountsChangedEventArgs>(
                this,
                static (vm, sender, e) => vm.OnRepositoryAccountsChanged(sender, e),
                h => repo.AccountsChanged -= h);
            repo.AccountsChanged += _accountsChangedHandler;

            _languageChangedHandler = WeakEventSubscription.CreateAction<MainViewModel, AppLanguage>(
                this,
                static (vm, lang) => vm.OnLanguageChanged(lang),
                static h => LocalizationService.LanguageChanged -= h);
            LocalizationService.LanguageChanged += _languageChangedHandler;

            _audioStatusChangedHandler = WeakEventSubscription.CreateAction<MainViewModel>(
                this,
                static vm => vm.OnAudioStatusChanged(),
                static h => RobloxAudioService.StatusChanged -= h);
            RobloxAudioService.StatusChanged += _audioStatusChangedHandler;

            _themeChangedHandler = WeakEventSubscription.CreateAction<MainViewModel, AccentTheme>(
                this,
                static (vm, theme) => vm.OnAccentThemeChanged(theme),
                static h => AccentThemeService.ThemeChanged -= h);
            AccentThemeService.ThemeChanged += _themeChangedHandler;

            _launchProgressChangedHandler = WeakEventSubscription.CreateAction<MainViewModel, LaunchProgress>(
                this,
                static (vm, p) => vm.OnLaunchProgressChanged(p),
                static h => LaunchCoordinator.LaunchProgressChanged -= h);
            LaunchCoordinator.LaunchProgressChanged += _launchProgressChangedHandler;

            _streamerModeChangedHandler = WeakEventSubscription.CreateAction<MainViewModel, bool>(
                this,
                static (vm, active) => vm.OnStreamerModeStateChanged(active),
                static h => StreamerModeService.StateChanged -= h);
            StreamerModeService.StateChanged += _streamerModeChangedHandler;
        }

        private void UnsubscribeWeakEvents()
        {
            if (_accountsChangedHandler != null) _accountRepository.AccountsChanged -= _accountsChangedHandler;
            if (_languageChangedHandler != null) LocalizationService.LanguageChanged -= _languageChangedHandler;
            if (_audioStatusChangedHandler != null) RobloxAudioService.StatusChanged -= _audioStatusChangedHandler;
            if (_themeChangedHandler != null) AccentThemeService.ThemeChanged -= _themeChangedHandler;
            if (_launchProgressChangedHandler != null) LaunchCoordinator.LaunchProgressChanged -= _launchProgressChangedHandler;
            if (_streamerModeChangedHandler != null) StreamerModeService.StateChanged -= _streamerModeChangedHandler;
        }

        private void OnRepositoryAccountsChanged(object? sender, AccountsChangedEventArgs e)
        {
            if (System.Threading.Volatile.Read(ref _disposed) != 0 || e == null) return;

            long version = e.Version;
            if (version > 0)
            {
                long observed = System.Threading.Volatile.Read(ref _highestObservedRepoVersion);
                while (version > observed)
                {
                    long prev = System.Threading.Interlocked.CompareExchange(ref _highestObservedRepoVersion, version, observed);
                    if (prev == observed)
                        break;
                    observed = prev;
                }

                if (version < System.Threading.Volatile.Read(ref _highestObservedRepoVersion))
                    return;
            }

            long seq = System.Threading.Interlocked.Increment(ref _repoSnapshotSeq);
            AppDispatcher.Post(() =>
            {
                if (System.Threading.Volatile.Read(ref _disposed) != 0) return;
                if (version > 0)
                {
                    if (version < System.Threading.Volatile.Read(ref _highestObservedRepoVersion)) return;
                }
                else if (seq != System.Threading.Volatile.Read(ref _repoSnapshotSeq))
                {
                    return;
                }

                ApplyRepositorySnapshot(e.Accounts, version);
            });
        }

        private void ApplyRepositorySnapshot(IReadOnlyList<Account> accounts, long version)
        {
            if (Avalonia.Application.Current != null && Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(() => ApplyRepositorySnapshot(accounts, version));
                return;
            }

            lock (_accountsUiLock)
            {
                if (System.Threading.Volatile.Read(ref _disposed) != 0) return;
                if (version > 0 &&
                    (version < System.Threading.Volatile.Read(ref _highestObservedRepoVersion) ||
                     version <= _lastAppliedRepoVersion))
                {
                    return;
                }

                LoadAccounts(accounts);
                if (version > _lastAppliedRepoVersion)
                    _lastAppliedRepoVersion = version;
            }
        }

        private void OnLanguageChanged(AppLanguage lang)
        {
            AppDispatcher.Post(() =>
            {
                if (System.Threading.Volatile.Read(ref _disposed) != 0) return;
                OnPropertyChanged(nameof(CloseRobloxButtonText));
                OnPropertyChanged(nameof(CurrentSortLabel));
                NotifyLocalizedLabelsChanged();
                string localizedReady = Loc.Get("Status_ReadyDefault");
                if (string.IsNullOrWhiteSpace(_statusMessage)
                    || string.Equals(_statusMessage, _lastDefaultReadyStatus, StringComparison.Ordinal)
                    || string.Equals(_statusMessage, localizedReady, StringComparison.Ordinal))
                {
                    _lastDefaultReadyStatus = localizedReady;
                    StatusMessage = localizedReady;
                }
                if (Accounts != null)
                {
                    foreach (var acc in Accounts.ToArray())
                        acc.NotifyLanguageChanged();
                }
            });
        }

        private void OnAudioStatusChanged()
        {
            AppDispatcher.Post(() => { if (System.Threading.Volatile.Read(ref _disposed) == 0) NotifyToolStatesChanged(); });
        }

        private void OnLaunchProgressChanged(LaunchProgress progress)
        {
            if (progress == null) return;
            AppDispatcher.Post(() =>
            {
                try
                {
                    if (System.Threading.Volatile.Read(ref _disposed) != 0) return;
                    UpdateBatchLaunchBanner(progress);
                    if (!string.IsNullOrWhiteSpace(progress.Message))
                    {
                        StatusMessage = progress.Message;
                    }

                    if (progress.Account != null)
                    {
                        var vm = TryGetAccountViewModel(progress.Account);
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
                catch (Exception ex)
                {
                    _logger.Debug($"Clearing launching state on queue empty threw: {ex.Message}");
                }
            });
        }

        private void OnAccentThemeChanged(AccentTheme theme)
        {
            AppDispatcher.Post(() =>
            {
                try
                {
                    foreach (var acc in _accounts)
                    {
                        if (System.Threading.Volatile.Read(ref _disposed) != 0) return;
                        acc.NotifyThemeChanged();
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"NotifyThemeChanged loop threw: {ex.Message}");
                }
            });
        }
    }
}
