#nullable enable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;

namespace Shiro.ViewModels
{
    public partial class ServerListViewModel
    {
        private readonly List<ServerItemViewModel> _allServers = new();
        private ServerSortMode _serverSortMode = ServerSortMode.Default;
        private bool _hideFullServers;
        private bool _hideVipServers;

        public ICommand ToggleSortPlayersCommand { get; private set; } = null!;
        public ICommand ToggleSortPingCommand { get; private set; } = null!;
        public ICommand SelectRecentGameInLibraryCommand { get; private set; } = null!;
        public ICommand LaunchRecentGameInLibraryCommand { get; private set; } = null!;

        public ServerSortMode SortMode
        {
            get => _serverSortMode;
            set
            {
                if (!SetProperty(ref _serverSortMode, value)) return;
                OnPropertyChanged(nameof(PlayersSortHeaderIndicator));
                OnPropertyChanged(nameof(PingSortHeaderIndicator));
                OnPropertyChanged(nameof(HasPlayersSortIndicator));
                OnPropertyChanged(nameof(HasPingSortIndicator));
                ApplyServerSortAndFilter();
            }
        }

        public bool HideFullServers
        {
            get => _hideFullServers;
            set { if (SetProperty(ref _hideFullServers, value)) ApplyServerSortAndFilter(); }
        }

        public bool HideVipServers
        {
            get => _hideVipServers;
            set { if (SetProperty(ref _hideVipServers, value)) ApplyServerSortAndFilter(); }
        }

        public string PlayersSortHeaderIndicator => ServerListFilterHelper.GetPlayersSortIndicator(_serverSortMode);
        public string PingSortHeaderIndicator => ServerListFilterHelper.GetPingSortIndicator(_serverSortMode);
        public bool HasPlayersSortIndicator => !string.IsNullOrEmpty(PlayersSortHeaderIndicator);
        public bool HasPingSortIndicator => !string.IsNullOrEmpty(PingSortHeaderIndicator);
        public int RawServersCount => _allServers.Count;

        public string CurrentGameName => _mainViewModel?.CurrentGameName ?? string.Empty;
        public Bitmap? CurrentGameThumbnail => _mainViewModel?.CurrentGameThumbnail;
        public bool HasCurrentGameInfo => _mainViewModel?.HasCurrentGameInfo == true;
        public ObservableCollection<RecentGameItemViewModel>? RecentGames => _mainViewModel?.RecentGamesList;
        public bool HasRecentGames => _mainViewModel?.HasRecentGames == true;
        public bool IsRecentGamesEmpty => !HasRecentGames;

        public void NotifyCurrentGameChanged()
        {
            OnPropertyChanged(nameof(CurrentGameName));
            OnPropertyChanged(nameof(CurrentGameThumbnail));
            OnPropertyChanged(nameof(HasCurrentGameInfo));
        }

        public void NotifyRecentGamesChanged()
        {
            OnPropertyChanged(nameof(RecentGames));
            OnPropertyChanged(nameof(HasRecentGames));
            OnPropertyChanged(nameof(IsRecentGamesEmpty));
        }

        private void InitializeSortingAndLibraryCommands()
        {
            ToggleSortPlayersCommand = new RelayCommand(() => SortMode = ServerListFilterHelper.NextPlayersSortMode(SortMode));
            ToggleSortPingCommand = new RelayCommand(() => SortMode = ServerListFilterHelper.NextPingSortMode(SortMode));
            SelectRecentGameInLibraryCommand = new RelayCommand(p => ServerListFilterHelper.SelectRecentGame(this, _mainViewModel, p));
            LaunchRecentGameInLibraryCommand = new RelayCommand(p => ServerListFilterHelper.LaunchRecentGame(this, _mainViewModel, p));
        }

        public void ClearRawServers()
        {
            _allServers.Clear();
            _servers.Clear();
            OnPropertyChanged(nameof(RawServersCount));
            NotifyServerCounts();
        }

        public void SetRawServers(IEnumerable<ServerItemViewModel> servers)
        {
            _allServers.Clear();
            if (servers != null) _allServers.AddRange(servers);
            OnPropertyChanged(nameof(RawServersCount));
            ApplyServerSortAndFilter();
        }

        public void AppendRawServers(IEnumerable<ServerItemViewModel> batch)
        {
            if (batch == null) return;
            _allServers.AddRange(batch);
            OnPropertyChanged(nameof(RawServersCount));
            ApplyServerSortAndFilter();
        }

        public void PrependRawServers(IEnumerable<ServerItemViewModel> batch)
        {
            if (batch == null) return;
            _allServers.InsertRange(0, batch);
            OnPropertyChanged(nameof(RawServersCount));
            ApplyServerSortAndFilter();
        }

        public void ApplyServerSortAndFilter()
        {
            if (_allServers.Count == 0)
            {
                if (_servers.Count > 0) _servers.Clear();
                NotifyServerCounts();
                return;
            }

            _servers.Sync(ServerListFilterHelper.FilterAndSort(_allServers, _hideFullServers, _hideVipServers, _serverSortMode));
            NotifyServerCounts();
        }
    }
}
