#nullable enable
using System;
using System.Collections.Generic;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public enum AccountFilterTab
    {
        All,
        InGame,
        Ready,
        Expired,
        Banned,
        Pinned
    }

    public enum AccountSortMode
    {
        NaturalAscending,  // Name (1 -> 9, A -> Z) - Default
        NaturalDescending, // Name (9 -> 1, Z -> A)
        Original,          // Original order
        Status,            // Status (In-game / Online)
        RobuxDescending    // Robux balance (High -> Low)
    }

    public readonly record struct AccountCountsSummary(
        int Total,
        int InGame,
        int Ready,
        int Expired,
        int Banned,
        int Pinned,
        int Selected,
        long Robux);

    internal static class AccountListFilterController
    {
        public static bool IsAllGroupsSentinel(string? group)
        {
            return string.IsNullOrEmpty(group) ||
                   string.Equals(group, "All Groups", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(group, LocalizationService.Get("Dash_AllGroups"), StringComparison.OrdinalIgnoreCase);
        }

        public static bool UpdateAvailableGroups(
            IReadOnlyList<AccountViewModel> accounts,
            IList<string> availableGroups,
            string currentSelectedGroup)
        {
            var seenGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sortedGroups = new List<string>();

            for (int i = 0; i < accounts.Count; i++)
            {
                string g = accounts[i].Group;
                if (!string.IsNullOrWhiteSpace(g) && seenGroups.Add(g))
                    sortedGroups.Add(g);
            }

            sortedGroups.Sort(NaturalStringComparer.Instance);

            bool groupsChanged = availableGroups.Count != sortedGroups.Count + 1 ||
                                 (availableGroups.Count > 0 && availableGroups[0] != "All Groups");
            if (!groupsChanged)
            {
                for (int i = 0; i < sortedGroups.Count; i++)
                {
                    if (!string.Equals(availableGroups[i + 1], sortedGroups[i], StringComparison.Ordinal))
                    {
                        groupsChanged = true;
                        break;
                    }
                }
            }

            if (groupsChanged)
            {
                availableGroups.Clear();
                availableGroups.Add("All Groups");
                for (int i = 0; i < sortedGroups.Count; i++)
                    availableGroups.Add(sortedGroups[i]);
            }

            return !string.Equals(currentSelectedGroup, "All Groups", StringComparison.Ordinal) &&
                   (string.IsNullOrEmpty(currentSelectedGroup) || !seenGroups.Contains(currentSelectedGroup));
        }

        public static List<AccountViewModel> FilterAndSortAccounts(
            IReadOnlyList<AccountViewModel> accounts,
            string searchText,
            string selectedGroup,
            AccountFilterTab activeTab,
            AccountSortMode sortMode)
        {
            var searchQuery = AccountSearchQuery.Create(searchText);
            bool filterByGroup = !IsAllGroupsSentinel(selectedGroup);
            bool hasSearch = !searchQuery.IsEmpty;

            var resultList = new List<AccountViewModel>(accounts.Count);
            for (int i = 0; i < accounts.Count; i++)
            {
                var a = accounts[i];
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
                        if (!a.IsExpiredAndNotBanned) continue;
                        break;
                    case AccountFilterTab.Banned:
                        if (!a.IsBannedOrLocked) continue;
                        break;
                    case AccountFilterTab.Pinned:
                        if (!a.Pinned) continue;
                        break;
                }

                if (hasSearch && !searchQuery.IsMatch(a))
                    continue;

                resultList.Add(a);
            }

            for (int i = 0; i < resultList.Count; i++)
            {
                var item = resultList[i];
                item.SortTitle = item.PrimaryTitle ?? item.Username ?? string.Empty;
            }

            resultList.Sort((x, y) => CompareAccounts(x, y, sortMode));
            return resultList;
        }

        public static int CompareAccounts(AccountViewModel x, AccountViewModel y, AccountSortMode sortMode)
        {
            int pinCmp = y.Pinned.CompareTo(x.Pinned);
            if (pinCmp != 0) return pinCmp;

            switch (sortMode)
            {
                case AccountSortMode.NaturalDescending:
                {
                    int validCmp = y.Valid.CompareTo(x.Valid);
                    if (validCmp != 0) return validCmp;
                    return NaturalStringComparer.Instance.Compare(y.SortTitle, x.SortTitle);
                }
                case AccountSortMode.Original:
                    return y.Valid.CompareTo(x.Valid);
                case AccountSortMode.Status:
                {
                    int gameCmp = y.IsInGame.CompareTo(x.IsInGame);
                    if (gameCmp != 0) return gameCmp;
                    int validCmp = y.Valid.CompareTo(x.Valid);
                    if (validCmp != 0) return validCmp;
                    return NaturalStringComparer.Instance.Compare(x.SortTitle, y.SortTitle);
                }
                case AccountSortMode.RobuxDescending:
                {
                    int robuxCmp = y.RobuxBalance.CompareTo(x.RobuxBalance);
                    if (robuxCmp != 0) return robuxCmp;
                    return NaturalStringComparer.Instance.Compare(x.SortTitle, y.SortTitle);
                }
                case AccountSortMode.NaturalAscending:
                default:
                {
                    int validCmp = y.Valid.CompareTo(x.Valid);
                    if (validCmp != 0) return validCmp;
                    return NaturalStringComparer.Instance.Compare(x.SortTitle, y.SortTitle);
                }
            }
        }

        public static List<AccountRowViewModel> BuildAccountRows(IReadOnlyList<AccountViewModel> accounts, int cardColumns)
        {
            int cols = Math.Clamp(cardColumns, 2, 4);
            var rows = new List<AccountRowViewModel>((accounts.Count + cols - 1) / cols);
            for (int i = 0; i < accounts.Count; i += cols)
            {
                var item1 = accounts[i];
                var item2 = (cols >= 2 && i + 1 < accounts.Count) ? accounts[i + 1] : null;
                var item3 = (cols >= 3 && i + 2 < accounts.Count) ? accounts[i + 2] : null;
                var item4 = (cols >= 4 && i + 3 < accounts.Count) ? accounts[i + 3] : null;
                rows.Add(new AccountRowViewModel(cols, item1, item2, item3, item4));
            }
            return rows;
        }

        public static bool IsSameSequence(IList<AccountViewModel>? current, IList<AccountViewModel>? next)
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

        public static AccountCountsSummary ComputeAccountCounts(IReadOnlyList<AccountViewModel> accounts)
        {
            int total = accounts.Count;
            int inGame = 0, ready = 0, expired = 0, banned = 0, pinned = 0, selected = 0;
            long robux = 0;

            for (int i = 0; i < total; i++)
            {
                var a = accounts[i];
                if (a.IsInGame) inGame++;
                else if (a.Valid) ready++;
                if (a.IsBannedOrLocked) banned++;
                else if (a.IsExpired) expired++;
                if (a.Pinned) pinned++;
                if (a.IsSelected) selected++;
                if (a.RobuxBalance > 0) robux += a.RobuxBalance;
            }

            return new AccountCountsSummary(total, inGame, ready, expired, banned, pinned, selected, robux);
        }

        public static bool UpdateAccountLiveStatuses(
            IReadOnlyList<AccountViewModel> accounts,
            IReadOnlyDictionary<Account, int>? runningMap)
        {
            bool stateChanged = false;
            for (int i = 0; i < accounts.Count; i++)
            {
                var acc = accounts[i];
                bool isRunningLocally = runningMap != null && acc.Model != null && runningMap.ContainsKey(acc.Model);
                acc.IsLocalClientRunning = isRunningLocally;

                bool hasActivePresence = acc.Model?.Presence != null
                    ? acc.Model.Presence.userPresenceType == UserPresenceType.InGame
                    : isRunningLocally;

                if (acc.IsInGame != hasActivePresence)
                {
                    acc.IsInGame = hasActivePresence;
                    stateChanged = true;
                }
            }
            return stateChanged;
        }
    }
}
