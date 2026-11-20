#nullable enable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Http;

namespace Shiro.ViewModels
{
    public partial class ServerListViewModel
    {
        public BulkObservableCollection<ServerItemViewModel> Servers => BrowserTab.Servers;
        public int TotalServersCount => BrowserTab.TotalServersCount;
        public bool HasServers => BrowserTab.HasServers;
        public bool IsServersEmpty => BrowserTab.IsServersEmpty;
        public bool IsLoadingServers { get => BrowserTab.IsLoadingServers; set => BrowserTab.IsLoadingServers = value; }
        public bool IsLoadingRegions { get => BrowserTab.IsLoadingRegions; set => BrowserTab.IsLoadingRegions = value; }
        public bool CanRefreshServers => BrowserTab.CanRefreshServers;
        public ServerItemViewModel? SelectedServer { get => BrowserTab.SelectedServer; set => BrowserTab.SelectedServer = value; }
        public ServerSortMode SortMode { get => BrowserTab.SortMode; set => BrowserTab.SortMode = value; }
        public bool HideFullServers { get => BrowserTab.HideFullServers; set => BrowserTab.HideFullServers = value; }
        public bool HideVipServers { get => BrowserTab.HideVipServers; set => BrowserTab.HideVipServers = value; }
        public string PlayersSortHeaderIndicator => BrowserTab.PlayersSortHeaderIndicator;
        public string PingSortHeaderIndicator => BrowserTab.PingSortHeaderIndicator;
        public bool HasPlayersSortIndicator => BrowserTab.HasPlayersSortIndicator;
        public bool HasPingSortIndicator => BrowserTab.HasPingSortIndicator;
        public int RawServersCount => BrowserTab.RawServersCount;
        public string SearchPlayerUsername { get => BrowserTab.SearchPlayerUsername; set => BrowserTab.SearchPlayerUsername = value; }
        public bool IsSearchingPlayer { get => BrowserTab.IsSearchingPlayer; set => BrowserTab.IsSearchingPlayer = value; }
        public string PlayerSearchStatus { get => BrowserTab.PlayerSearchStatus; set => BrowserTab.PlayerSearchStatus = value; }

        public ICommand RefreshServersCommand => BrowserTab.RefreshServersCommand;
        public ICommand JoinServerCommand => BrowserTab.JoinServerCommand;
        public ICommand SelectServerJobIdCommand => BrowserTab.SelectServerJobIdCommand;
        public ICommand CopyJobIdCommand => BrowserTab.CopyJobIdCommand;
        public ICommand LoadRegionCommand => BrowserTab.LoadRegionCommand;
        public ICommand LoadAllRegionsCommand => BrowserTab.LoadAllRegionsCommand;
        public ICommand SearchPlayerCommand => BrowserTab.SearchPlayerCommand;
        public ICommand ToggleSortPlayersCommand => BrowserTab.ToggleSortPlayersCommand;
        public ICommand ToggleSortPingCommand => BrowserTab.ToggleSortPingCommand;

        public ObservableCollection<GameSearchItemViewModel> SearchGames => ExplorerTab.SearchGames;
        public string GameSearchTerm { get => ExplorerTab.GameSearchTerm; set => ExplorerTab.GameSearchTerm = value; }
        public bool IsSearchingGames { get => ExplorerTab.IsSearchingGames; set => ExplorerTab.IsSearchingGames = value; }
        public bool IsSearchGamesEmpty => ExplorerTab.IsSearchGamesEmpty;
        public ICommand SearchGamesCommand => ExplorerTab.SearchGamesCommand;
        public ICommand SelectGameCommand => ExplorerTab.SelectGameCommand;
        public ICommand LaunchGameCommand => ExplorerTab.LaunchGameCommand;

        public ObservableCollection<FavoriteGameItemViewModel> Favorites => LibraryTab.Favorites;
        public bool IsFavoritesEmpty => LibraryTab.IsFavoritesEmpty;
        public ObservableCollection<RecentGameItemViewModel>? RecentGames => LibraryTab.RecentGames;
        public bool HasRecentGames => LibraryTab.HasRecentGames;
        public bool IsRecentGamesEmpty => LibraryTab.IsRecentGamesEmpty;
        public ICommand AddFavoriteCommand => LibraryTab.AddFavoriteCommand;
        public ICommand RemoveFavoriteCommand => LibraryTab.RemoveFavoriteCommand;
        public ICommand SelectFavoriteCommand => LibraryTab.SelectFavoriteCommand;
        public ICommand LaunchFavoriteCommand => LibraryTab.LaunchFavoriteCommand;
        public ICommand SelectRecentGameInLibraryCommand => LibraryTab.SelectRecentGameInLibraryCommand;
        public ICommand LaunchRecentGameInLibraryCommand => LibraryTab.LaunchRecentGameInLibraryCommand;

        public void NotifyServerCounts() => BrowserTab.NotifyServerCounts();
        public void EnsureServersLoadedForCurrentPlaceId() => BrowserTab.EnsureServersLoadedForCurrentPlaceId();
        public Task RefreshServersAsync() => BrowserTab.RefreshServersAsync();
        public void ClearRawServers() => BrowserTab.ClearRawServers();
        public void SetRawServers(IEnumerable<ServerItemViewModel> servers) => BrowserTab.SetRawServers(servers);
        public void AppendRawServers(IEnumerable<ServerItemViewModel> batch) => BrowserTab.AppendRawServers(batch);
        public void PrependRawServers(IEnumerable<ServerItemViewModel> batch) => BrowserTab.PrependRawServers(batch);
        public void ApplyServerSortAndFilter() => BrowserTab.ApplyServerSortAndFilter();
        internal static Task<List<ServerItemViewModel>?> TestFetchVipServersAsync(
            RestClient gamesClient,
            Account vipAuthAccount,
            long placeId,
            CancellationToken ct) => ServerBrowserTabViewModel.TestFetchVipServersAsync(gamesClient, vipAuthAccount, placeId, ct);

        internal Task SearchGamesAsync() => ExplorerTab.SearchGamesAsync();
        public void NotifyRecentGamesChanged() => LibraryTab.NotifyRecentGamesChanged();
    }
}
