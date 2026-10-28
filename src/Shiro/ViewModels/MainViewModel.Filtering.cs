#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public enum AccountFilterTab
    {
        All,
        InGame,
        Ready,
        Expired,
        Pinned
    }

    public enum AccountSortMode
    {
        NaturalAscending,  // Tên (1 → 9, A → Z) - Mặc định
        NaturalDescending, // Tên (9 → 1, Z → A)
        Original,          // Thứ tự ban đầu
        Status,            // Trạng thái (Đang chơi / Online)
        RobuxDescending    // Số dư Robux (Nhiều → Ít)
    }

    public partial class MainViewModel
    {
        private string _searchText = string.Empty;
        private string _selectedGroup = "All Groups";
        private AccountFilterTab _activeTab = AccountFilterTab.All;
        private AccountSortMode _sortMode = AccountSortMode.NaturalAscending;
        private CancellationTokenSource? _searchCts;

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
                        try { previousCts.Cancel(); }
                        catch (ObjectDisposedException) { }
                        catch (Exception) { }

                        try { previousCts.Dispose(); }
                        catch (Exception) { }
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
    }
}
