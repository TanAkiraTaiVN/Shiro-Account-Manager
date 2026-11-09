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
        public string TotalRobuxText => _totalRobuxText;

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

                bool stateChanged = false;
                foreach (var acc in _accounts)
                {
                    bool isRunningLocally = runningMap != null && acc.Model != null && runningMap.ContainsKey(acc.Model);
                    acc.IsLocalClientRunning = isRunningLocally;

                    // If Presence from Roblox API exists, it is authoritative; otherwise preserve current state
                    bool hasActivePresence = acc.Model?.Presence != null
                        ? acc.Model.Presence.userPresenceType == UserPresenceType.InGame
                        : (acc.IsInGame || isRunningLocally);

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
                _logger.Debug($"[ApplyLiveProcessStatusResults] UI update error: {ex.Message}");
            }

            NotifyCounts();
        }

        public void NotifyCounts()
        {
            if (_suppressCountNotifications)
                return;

            int total = _accounts.Count;
            int inGame = 0, ready = 0, expired = 0, banned = 0, pinned = 0, selected = 0;
            long robux = 0;

            for (int i = 0; i < total; i++)
            {
                var a = _accounts[i];
                if (a.IsInGame) inGame++;
                else if (a.Valid) ready++;
                if (a.IsBannedOrLocked) banned++;
                else if (a.IsExpired) expired++;
                if (a.Pinned) pinned++;
                if (a.IsSelected) selected++;
                if (a.RobuxBalance > 0) robux += a.RobuxBalance;
            }

            if (_totalCount != total) { _totalCount = total; OnPropertyChanged(nameof(TotalCount)); }
            if (_inGameCount != inGame) { _inGameCount = inGame; OnPropertyChanged(nameof(InGameCount)); }
            if (_readyCount != ready) { _readyCount = ready; OnPropertyChanged(nameof(ReadyCount)); }
            if (_expiredCount != expired)
            {
                bool hadExpired = _expiredCount > 0;
                _expiredCount = expired;
                OnPropertyChanged(nameof(ExpiredCount));
                if (hadExpired != (expired > 0)) OnPropertyChanged(nameof(HasExpiredAccounts));
            }
            if (_bannedCount != banned)
            {
                bool hadBanned = _bannedCount > 0;
                _bannedCount = banned;
                OnPropertyChanged(nameof(BannedCount));
                if (hadBanned != (banned > 0)) OnPropertyChanged(nameof(HasBannedAccounts));
            }
            if (_pinnedCount != pinned) { _pinnedCount = pinned; OnPropertyChanged(nameof(PinnedCount)); }
            if (_selectedCount != selected)
            {
                bool hadSelection = _selectedCount > 0;
                _selectedCount = selected;
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(IsAllSelected));
                OnPropertyChanged(nameof(CloseRobloxButtonText));
                if (hadSelection != (selected > 0)) OnPropertyChanged(nameof(HasSelection));
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
