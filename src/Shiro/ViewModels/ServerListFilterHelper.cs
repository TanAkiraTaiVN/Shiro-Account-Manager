#nullable enable
using System;
using System.Collections.Generic;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public enum ServerSortMode
    {
        Default,
        PlayersAsc,
        PlayersDesc,
        PingAsc,
        PingDesc
    }

    /// <summary>
    /// Stateless filtering, sorting, and sort-mode cycling helpers for <see cref="ServerListViewModel"/>.
    /// </summary>
    internal static class ServerListFilterHelper
    {
        public static string GetPlayersSortIndicator(ServerSortMode mode) => mode switch
        {
            ServerSortMode.PlayersAsc => "▲",
            ServerSortMode.PlayersDesc => "▼",
            _ => string.Empty
        };

        public static string GetPingSortIndicator(ServerSortMode mode) => mode switch
        {
            ServerSortMode.PingAsc => "▲",
            ServerSortMode.PingDesc => "▼",
            _ => string.Empty
        };

        public static ServerSortMode NextPlayersSortMode(ServerSortMode mode) => mode switch
        {
            ServerSortMode.Default => ServerSortMode.PlayersAsc,
            ServerSortMode.PlayersAsc => ServerSortMode.PlayersDesc,
            ServerSortMode.PlayersDesc => ServerSortMode.Default,
            _ => ServerSortMode.PlayersAsc
        };

        public static ServerSortMode NextPingSortMode(ServerSortMode mode) => mode switch
        {
            ServerSortMode.Default => ServerSortMode.PingAsc,
            ServerSortMode.PingAsc => ServerSortMode.PingDesc,
            ServerSortMode.PingDesc => ServerSortMode.Default,
            _ => ServerSortMode.PingAsc
        };

        public static void SelectRecentGame(ServerListViewModel vm, MainViewModel? mainVm, object? param)
        {
            if (param is not RecentGameItemViewModel recent) return;
            mainVm?.SetKnownGameTitle(recent.PlaceId, recent.Name);
            vm.PlaceIdInput = recent.PlaceId.ToString();
            if (mainVm != null)
            {
                mainVm.PlaceId = recent.PlaceId.ToString();
                mainVm.StatusMessage = LocalizationService.Format("ServerList.RecentGameSelected", recent.Name, recent.PlaceId);
            }
            vm.SelectedSubTabIndex = 0;
            _ = vm.RefreshServersAsync();
        }

        public static void LaunchRecentGame(ServerListViewModel vm, MainViewModel? mainVm, object? param)
        {
            if (param is not RecentGameItemViewModel recent) return;
            mainVm?.SetKnownGameTitle(recent.PlaceId, recent.Name);
            vm.PlaceIdInput = recent.PlaceId.ToString();
            if (mainVm == null) return;
            mainVm.PlaceId = recent.PlaceId.ToString();
            mainVm.JobId = string.Empty;
            if (mainVm.SelectedCount > 0)
                mainVm.StatusMessage = LocalizationService.Format("ServerList.LaunchingRecentGame", recent.Name, recent.PlaceId);
            else
                vm.LoadingStatus = LocalizationService.Get("ServerList.SelectAccountBeforePlay");
            mainVm.JoinServerSelectedCommand?.Execute(null);
        }

        public static List<ServerItemViewModel> FilterAndSort(
            IReadOnlyList<ServerItemViewModel> allServers,
            bool hideFullServers,
            bool hideVipServers,
            ServerSortMode sortMode)
        {
            int rawCount = allServers?.Count ?? 0;
            if (rawCount == 0 || allServers == null)
                return new List<ServerItemViewModel>(0);

            int minPlayersThreshold = 1;
            if (hideVipServers)
            {
                for (int i = 0; i < rawCount; i++)
                {
                    if (allServers[i].Playing > 1)
                    {
                        minPlayersThreshold = 2;
                        break;
                    }
                }
            }

            var filtered = new List<ServerItemViewModel>(rawCount);
            for (int i = 0; i < rawCount; i++)
            {
                ServerItemViewModel s = allServers[i];
                if (hideFullServers && s.MaxPlayers > 0 && s.Playing >= s.MaxPlayers)
                    continue;
                if (hideVipServers && (s.Playing < minPlayersThreshold ||
                    s.Type.StartsWith("VIP", StringComparison.OrdinalIgnoreCase) ||
                    s.JobId.StartsWith("VIP:", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Region, "VIP Private", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                filtered.Add(s);
            }

            switch (sortMode)
            {
                case ServerSortMode.PlayersAsc: filtered.Sort(ComparePlayersAsc); break;
                case ServerSortMode.PlayersDesc: filtered.Sort(ComparePlayersDesc); break;
                case ServerSortMode.PingAsc: filtered.Sort(ComparePingAsc); break;
                case ServerSortMode.PingDesc: filtered.Sort(ComparePingDesc); break;
            }

            return filtered;
        }

        public static int ComparePlayersAsc(ServerItemViewModel a, ServerItemViewModel b)
        {
            int cmp = a.Playing.CompareTo(b.Playing);
            return cmp != 0 ? cmp : (a.Ping >= 0 ? a.Ping : int.MaxValue).CompareTo(b.Ping >= 0 ? b.Ping : int.MaxValue);
        }

        public static int ComparePlayersDesc(ServerItemViewModel a, ServerItemViewModel b)
        {
            int cmp = b.Playing.CompareTo(a.Playing);
            return cmp != 0 ? cmp : (a.Ping >= 0 ? a.Ping : int.MaxValue).CompareTo(b.Ping >= 0 ? b.Ping : int.MaxValue);
        }

        public static int ComparePingAsc(ServerItemViewModel a, ServerItemViewModel b)
        {
            int cmp = (a.Ping >= 0 ? a.Ping : int.MaxValue).CompareTo(b.Ping >= 0 ? b.Ping : int.MaxValue);
            return cmp != 0 ? cmp : a.Playing.CompareTo(b.Playing);
        }

        public static int ComparePingDesc(ServerItemViewModel a, ServerItemViewModel b)
        {
            int cmp = (b.Ping >= 0 ? b.Ping : -1).CompareTo(a.Ping >= 0 ? a.Ping : -1);
            return cmp != 0 ? cmp : a.Playing.CompareTo(b.Playing);
        }
    }
}
