#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class MainViewModel
    {
        private string _searchText = string.Empty;
        private string _selectedGroup = "All Groups";
        private AccountFilterTab _activeTab = AccountFilterTab.All;
        private AccountSortMode _sortMode = AccountSortMode.NaturalAscending;
        private CancellationTokenSource? _searchCts;
        private long _searchGeneration;

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    long gen = Interlocked.Increment(ref _searchGeneration);
                    ServerListOperationHelper.ReplaceCts(ref _searchCts, createNew: false, "MainViewModel.SearchCts");
                    if (Volatile.Read(ref _disposed) != 0)
                        return;

                    if (string.IsNullOrEmpty(value))
                    {
                        ApplyFilter();
                    }
                    else
                    {
                        var cts = ServerListOperationHelper.ReplaceCts(ref _searchCts, createNew: true, "MainViewModel.SearchCts")!;
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
                            finally
                            {
                                ServerListOperationHelper.ReleaseFinishedCts(ref _searchCts, cts, "MainViewModel.SearchCts");
                            }
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
                    OnPropertyChanged(nameof(IsFilterBanned));
                    OnPropertyChanged(nameof(IsFilterPinned));
                    ApplyFilter();
                }
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
                if (SetProperty(ref _sortMode, value))
                {
                    ApplyFilter();
                    OnPropertyChanged(nameof(IsSortNaturalAsc));
                    OnPropertyChanged(nameof(IsSortNaturalDesc));
                    OnPropertyChanged(nameof(IsSortOriginal));
                    OnPropertyChanged(nameof(IsSortStatus));
                    OnPropertyChanged(nameof(IsSortRobuxDesc));
                    OnPropertyChanged(nameof(CurrentSortLabel));
                }
            }
        }

        public bool IsSortNaturalAsc { get => SortMode == AccountSortMode.NaturalAscending; set { if (value) SortMode = AccountSortMode.NaturalAscending; } }
        public bool IsSortNaturalDesc { get => SortMode == AccountSortMode.NaturalDescending; set { if (value) SortMode = AccountSortMode.NaturalDescending; } }
        public bool IsSortOriginal { get => SortMode == AccountSortMode.Original; set { if (value) SortMode = AccountSortMode.Original; } }
        public bool IsSortStatus { get => SortMode == AccountSortMode.Status; set { if (value) SortMode = AccountSortMode.Status; } }
        public bool IsSortRobuxDesc { get => SortMode == AccountSortMode.RobuxDescending; set { if (value) SortMode = AccountSortMode.RobuxDescending; } }

        public void RebuildGroupList(bool applyFilterOnReset = true)
        {
            bool resetSelection = AccountListFilterController.UpdateAvailableGroups(_accounts, _availableGroups, SelectedGroup);
            if (resetSelection)
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
            if (Avalonia.Application.Current != null && Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(ApplyFilter);
                return;
            }

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

        private void RebuildAccountRows()
        {
            _filteredAccountRows.Sync(AccountListFilterController.BuildAccountRows(_filteredAccounts, _cardColumns));
        }

        public void RefreshAccount(Account? account)
        {
            if (account == null || Volatile.Read(ref _disposed) != 0)
                return;

            if (Avalonia.Application.Current != null && Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(() => RefreshAccount(account));
                return;
            }

            AccountViewModel? vm = null;
            if (!_vmByModel.TryGetValue(account, out vm))
            {
                if (account.UserID > 0)
                    _vmByUserId.TryGetValue(account.UserID, out vm);
                if (vm == null && !string.IsNullOrEmpty(account.Username))
                    _vmByUsername.TryGetValue(account.Username, out vm);
            }

            if (vm != null)
            {
                vm.UpdateModel(account);
                RebuildLookupIndex();
                RebuildGroupList(applyFilterOnReset: false);
                ApplyFilter();
                NotifyCounts();
            }
        }
    }
}
