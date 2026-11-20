#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public sealed partial class AccountCollectionViewModel
    {
        private void PostStatusMessage(string message) =>
            AppDispatcher.Post(() => { if (Volatile.Read(ref _disposed) == 0) _setStatus(message); });

        private void SubscribeWeakEvents()
        {
            IAccountRepository repo = _accountRepository;
            _accountsChangedHandler = WeakEventSubscription.Create<AccountCollectionViewModel, AccountsChangedEventArgs>(
                this,
                static (vm, sender, e) => vm.OnRepositoryAccountsChanged(sender, e),
                h => repo.AccountsChanged -= h);
            repo.AccountsChanged += _accountsChangedHandler;
        }

        private void OnAccountViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AccountViewModel.IsSelected) && Volatile.Read(ref _disposed) == 0)
                NotifyCounts();
        }

        internal List<Account>? GetAccountModelsSnapshot()
        {
            lock (_accountsUiLock)
            {
                if (_accounts.Count == 0) return null;
                var models = new List<Account>(_accounts.Count);
                for (int i = 0; i < _accounts.Count; i++)
                {
                    if (_accounts[i].Model is { } m) models.Add(m);
                }
                return models;
            }
        }

        internal void ApplyLiveProcessStatuses(Dictionary<Account, int>? runningMap)
        {
            bool stateChanged;
            lock (_accountsUiLock)
                stateChanged = AccountListFilterController.UpdateAccountLiveStatuses(_accounts, runningMap);

            if (stateChanged && (ActiveTab == AccountFilterTab.InGame || ActiveTab == AccountFilterTab.Ready))
                ApplyFilter();
            NotifyCounts();
        }

        internal void ClearLaunchingFlags()
        {
            foreach (var acc in _accounts)
                acc.IsLaunching = false;
        }

        public void RebuildLookupIndex()
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

        public void RebuildGroupList(bool applyFilterOnReset = true)
        {
            bool resetSelection = AccountListFilterController.UpdateAvailableGroups(_accounts, _availableGroups, SelectedGroup);
            if (resetSelection)
            {
                if (applyFilterOnReset) SelectedGroup = "All Groups";
                else { _selectedGroup = "All Groups"; OnPropertyChanged(nameof(SelectedGroup)); }
            }
            else
            {
                OnPropertyChanged(nameof(SelectedGroup));
            }
        }

        private void OnRepositoryAccountsChanged(object? sender, AccountsChangedEventArgs e)
        {
            if (Volatile.Read(ref _disposed) != 0 || e == null) return;

            long version = e.Version;
            if (version > 0)
            {
                long observed = Volatile.Read(ref _highestObservedRepoVersion);
                while (version > observed)
                {
                    long prev = Interlocked.CompareExchange(ref _highestObservedRepoVersion, version, observed);
                    if (prev == observed) break;
                    observed = prev;
                }
                if (version < Volatile.Read(ref _highestObservedRepoVersion)) return;
            }

            long seq = Interlocked.Increment(ref _repoSnapshotSeq);
            AppDispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                if (version > 0) { if (version < Volatile.Read(ref _highestObservedRepoVersion)) return; }
                else if (seq != Volatile.Read(ref _repoSnapshotSeq)) return;

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
                if (Volatile.Read(ref _disposed) != 0) return;
                if (version > 0 &&
                    (version < Volatile.Read(ref _highestObservedRepoVersion) ||
                     version <= _lastAppliedRepoVersion))
                {
                    return;
                }

                LoadAccounts(accounts);
                if (version > _lastAppliedRepoVersion) _lastAppliedRepoVersion = version;
            }
        }

        public void LoadAccounts(IEnumerable<Account> sourceAccounts, Action? afterLoad = null)
        {
            if (Avalonia.Application.Current != null && Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(() => LoadAccounts(sourceAccounts, afterLoad));
                return;
            }

            lock (_accountsUiLock)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                if (sourceAccounts == null)
                {
                    AccountListFilterController.DetachPropertyChanged(_accounts, OnAccountViewModelPropertyChanged);
                    _accounts.Clear();
                    _lastClickedAccount = null;
                    RebuildLookupIndex();
                    RebuildGroupList(applyFilterOnReset: false);
                    ApplyFilter();
                    NotifyCounts();
                    (afterLoad ?? _onAccountsLoaded)?.Invoke();
                    return;
                }

                var existingByRef = new Dictionary<Account, AccountViewModel>();
                var existingById = new Dictionary<long, AccountViewModel>();
                var existingByName = new Dictionary<string, AccountViewModel>(StringComparer.OrdinalIgnoreCase);

                foreach (var existing in _accounts)
                {
                    if (existing.Model != null && !existingByRef.ContainsKey(existing.Model)) existingByRef[existing.Model] = existing;
                    if (existing.UserId > 0 && !existingById.ContainsKey(existing.UserId)) existingById[existing.UserId] = existing;
                    if (!string.IsNullOrEmpty(existing.Username) && !existingByName.ContainsKey(existing.Username)) existingByName[existing.Username] = existing;
                }

                var claimed = new HashSet<AccountViewModel>();
                var list = new List<AccountViewModel>();

                foreach (var acc in sourceAccounts)
                {
                    if (acc == null) continue;
                    AccountViewModel? vm = null;
                    if (existingByRef.TryGetValue(acc, out var byRef) && claimed.Add(byRef)) vm = byRef;
                    else if (acc.UserID > 0 && existingById.TryGetValue(acc.UserID, out var byId) && claimed.Add(byId)) vm = byId;
                    else if (!string.IsNullOrEmpty(acc.Username) && existingByName.TryGetValue(acc.Username, out var byName) && claimed.Add(byName)) vm = byName;

                    if (vm != null) vm.UpdateModel(acc);
                    else { vm = new AccountViewModel(acc); vm.PropertyChanged += OnAccountViewModelPropertyChanged; }
                    list.Add(vm);
                }

                AccountListFilterController.DetachPropertyChanged(_accounts, OnAccountViewModelPropertyChanged, claimed);
                if (_lastClickedAccount != null && !claimed.Contains(_lastClickedAccount)) _lastClickedAccount = null;
                _accounts.ReplaceAll(list);
                RebuildLookupIndex();
                WarmMissingAvatars(list);
                RebuildGroupList(applyFilterOnReset: false);
                ApplyFilter();
                NotifyCounts();
                (afterLoad ?? _onAccountsLoaded)?.Invoke();
            }
        }

        private void WarmMissingAvatars(List<AccountViewModel> list)
        {
            if (Avalonia.Application.Current == null) return;
            var missingIds = list.Where(a => a.UserId > 0 && !AvatarService.HasDiskCache(a.UserId)).Select(a => a.UserId).ToList();
            if (missingIds.Count == 0) return;

            var weakSelf = new WeakReference<AccountCollectionViewModel>(this);
            IAppLogger logger = _logger;
            _ = Task.Run(async () =>
            {
                try
                {
                    var updatedIds = await AvatarService.BatchEnsureAvatarsAsync(missingIds).ConfigureAwait(false);
                    if (updatedIds != null && updatedIds.Count > 0 && weakSelf.TryGetTarget(out var target))
                    {
                        var updatedSet = new HashSet<long>(updatedIds);
                        AppDispatcher.Post(() =>
                        {
                            if (Volatile.Read(ref target._disposed) != 0) return;
                            foreach (var acc in target._accounts)
                                if (updatedSet.Contains(acc.UserId)) acc.InvalidateAvatar();
                        });
                    }
                }
                catch (Exception ex) { logger.Debug($"[LoadAccounts] Avatar warmup error: {ex.Message}"); }
            });
        }

        public Task<IReadOnlyDictionary<Account, AccountSessionAuditResult>> AuditAccountSessionsAsync(CancellationToken ct = default)
            => SessionAuditorService.AuditAllAccountsAsync(_accounts.Where(a => a.Model != null).Select(a => a.Model!), null, ct);

        public void HandleAccountClickSelection(AccountViewModel? target, bool isShiftPressed, bool? explicitTargetState = null)
        {
            if (target == null) return;
            if (isShiftPressed && _lastClickedAccount != null && !ReferenceEquals(_lastClickedAccount, target))
            {
                int idxA = _filteredAccounts.IndexOf(_lastClickedAccount);
                int idxB = _filteredAccounts.IndexOf(target);
                if (idxA >= 0 && idxB >= 0)
                {
                    int start = Math.Min(idxA, idxB);
                    int end = Math.Max(idxA, idxB);
                    bool desiredState = explicitTargetState ?? _lastClickedAccount.IsSelected;
                    RunSuppressedSelection(() => { for (int i = start; i <= end; i++) _filteredAccounts[i].IsSelected = desiredState; });
                    _lastClickedAccount = target;
                    return;
                }
            }

            target.IsSelected = explicitTargetState ?? !target.IsSelected;
            _lastClickedAccount = target;
        }

        public int SelectFirstReadyAccounts(int count)
        {
            if (count <= 0) return 0;
            int picked = 0;
            RunSuppressedSelection(() =>
            {
                foreach (var acc in _accounts) acc.IsSelected = false;
                for (int i = 0; i < _filteredAccounts.Count && picked < count; i++)
                {
                    var candidate = _filteredAccounts[i];
                    if (candidate.Valid && !candidate.IsBannedOrLocked && !candidate.IsInGame && !candidate.IsLocalClientRunning)
                    {
                        candidate.IsSelected = true;
                        _lastClickedAccount = candidate;
                        picked++;
                    }
                }
            });
            return picked;
        }

        public int InvertFilteredSelection()
        {
            int nowSelected = 0;
            RunSuppressedSelection(() =>
            {
                for (int i = 0; i < _filteredAccounts.Count; i++)
                {
                    bool next = !_filteredAccounts[i].IsSelected;
                    _filteredAccounts[i].IsSelected = next;
                    if (next) nowSelected++;
                }
            });
            return nowSelected;
        }

        public int SelectRangeExpression(string? expression)
        {
            var indices = AccountCollectionCommandActions.ParseRangeIndices(expression, _filteredAccounts.Count);
            if (indices.Count == 0) return 0;

            RunSuppressedSelection(() =>
            {
                foreach (var acc in _accounts) acc.IsSelected = false;
                foreach (int idx in indices)
                {
                    _filteredAccounts[idx].IsSelected = true;
                    _lastClickedAccount = _filteredAccounts[idx];
                }
            });
            return indices.Count;
        }

        private void RunSuppressedSelection(Action action)
        {
            _suppressCountNotifications = true;
            try { action(); }
            finally { _suppressCountNotifications = false; }
            NotifyCounts();
            OnPropertyChanged(nameof(IsAllSelected));
        }

        internal void NotifyLanguageChanged()
        {
            OnPropertyChanged(nameof(CurrentSortLabel));
            NotifyLocalizedLabelsChanged();
            foreach (var acc in _accounts.ToArray()) acc.NotifyLanguageChanged();
        }

        internal void NotifyThemeChanged()
        {
            foreach (var acc in _accounts)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                acc.NotifyThemeChanged();
            }
        }

        internal void NotifyDisplayAndRobuxChanged()
        {
            OnPropertyChanged(nameof(TotalRobuxText));
            foreach (var acc in _accounts) { acc.NotifyDisplayNameChanged(); acc.NotifyRobuxChanged(); }
        }

        string IAccountWorkflowHost.PlaceId => _getPlaceId();
        IReadOnlyList<IAccountWorkflowItem> IAccountWorkflowHost.GetSelectedWorkflowItems() => GetSelectedAccountViewModels();
        IReadOnlyList<Account> IAccountWorkflowHost.GetSelectedAccounts() => GetSelectedAccounts();
        void IAccountWorkflowHost.RebuildLookupIndex() => RebuildLookupIndex();
        void IAccountWorkflowHost.RemoveWorkflowItems(params IAccountWorkflowItem[] toRemove)
        {
            if (toRemove != null && toRemove.Length > 0)
                RemoveAccountViewModels(toRemove.OfType<AccountViewModel>().ToArray());
        }
    }
}
