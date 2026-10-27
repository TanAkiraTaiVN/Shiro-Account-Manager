#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.ViewModels
{
    public partial class MainViewModel
    {
        private int _totalCount;
        private int _inGameCount;
        private int _readyCount;
        private int _expiredCount;
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
        public int PinnedCount => _pinnedCount;
        public int SelectedCount => _selectedCount;
        public bool HasSelection => _selectedCount > 0;

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
