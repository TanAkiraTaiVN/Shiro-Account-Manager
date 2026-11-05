#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media.Imaging;
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
    /// Server List sorting/filtering (Players Asc/Desc, Ping Asc/Desc, Hide Full, Hide Low/Empty),
    /// Recent Games library integration, and Active Game Info Chip forwarding.
    /// </summary>
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
                if (SetProperty(ref _serverSortMode, value))
                {
                    OnPropertyChanged(nameof(PlayersSortHeaderIndicator));
                    OnPropertyChanged(nameof(PingSortHeaderIndicator));
                    OnPropertyChanged(nameof(HasPlayersSortIndicator));
                    OnPropertyChanged(nameof(HasPingSortIndicator));
                    ApplyServerSortAndFilter();
                }
            }
        }

        public bool HideFullServers
        {
            get => _hideFullServers;
            set
            {
                if (SetProperty(ref _hideFullServers, value))
                {
                    ApplyServerSortAndFilter();
                }
            }
        }

        public bool HideVipServers
        {
            get => _hideVipServers;
            set
            {
                if (SetProperty(ref _hideVipServers, value))
                {
                    ApplyServerSortAndFilter();
                }
            }
        }

        public string PlayersSortHeaderIndicator => _serverSortMode switch
        {
            ServerSortMode.PlayersAsc => "▲",
            ServerSortMode.PlayersDesc => "▼",
            _ => string.Empty
        };

        public string PingSortHeaderIndicator => _serverSortMode switch
        {
            ServerSortMode.PingAsc => "▲",
            ServerSortMode.PingDesc => "▼",
            _ => string.Empty
        };

        public bool HasPlayersSortIndicator => !string.IsNullOrEmpty(PlayersSortHeaderIndicator);
        public bool HasPingSortIndicator => !string.IsNullOrEmpty(PingSortHeaderIndicator);
        public int RawServersCount => _allServers.Count;

        // Forwarding for Active Game Chip next to Place ID & Recent Games inside Tab 2
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
            ToggleSortPlayersCommand = new RelayCommand(() => SortMode = SortMode switch
            {
                ServerSortMode.Default => ServerSortMode.PlayersAsc,
                ServerSortMode.PlayersAsc => ServerSortMode.PlayersDesc,
                ServerSortMode.PlayersDesc => ServerSortMode.Default,
                _ => ServerSortMode.PlayersAsc
            });

            ToggleSortPingCommand = new RelayCommand(() => SortMode = SortMode switch
            {
                ServerSortMode.Default => ServerSortMode.PingAsc,
                ServerSortMode.PingAsc => ServerSortMode.PingDesc,
                ServerSortMode.PingDesc => ServerSortMode.Default,
                _ => ServerSortMode.PingAsc
            });

            SelectRecentGameInLibraryCommand = new RelayCommand(param =>
            {
                if (param is not RecentGameItemViewModel recent) return;
                _mainViewModel?.SetKnownGameTitle(recent.PlaceId, recent.Name);
                PlaceIdInput = recent.PlaceId.ToString();
                if (_mainViewModel != null)
                {
                    _mainViewModel.PlaceId = recent.PlaceId.ToString();
                    _mainViewModel.StatusMessage = LocalizationService.Format("ServerList.RecentGameSelected", recent.Name, recent.PlaceId);
                }
                SelectedSubTabIndex = 0;
                _ = RefreshServersAsync();
            });

            LaunchRecentGameInLibraryCommand = new RelayCommand(param =>
            {
                if (param is not RecentGameItemViewModel recent) return;
                _mainViewModel?.SetKnownGameTitle(recent.PlaceId, recent.Name);
                PlaceIdInput = recent.PlaceId.ToString();
                if (_mainViewModel == null) return;
                _mainViewModel.PlaceId = recent.PlaceId.ToString();
                _mainViewModel.JobId = string.Empty;
                if (_mainViewModel.SelectedCount > 0)
                    _mainViewModel.StatusMessage = LocalizationService.Format("ServerList.LaunchingRecentGame", recent.Name, recent.PlaceId);
                else
                    LoadingStatus = LocalizationService.Get("ServerList.SelectAccountBeforePlay");
                _mainViewModel.JoinServerSelectedCommand?.Execute(null);
            });
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
            int rawCount = _allServers.Count;
            if (rawCount == 0)
            {
                if (_servers.Count > 0) _servers.Clear();
                NotifyServerCounts();
                return;
            }

            int minPlayersThreshold = 1;
            if (_hideVipServers)
            {
                for (int i = 0; i < rawCount; i++)
                {
                    if (_allServers[i].Playing > 1) { minPlayersThreshold = 2; break; }
                }
            }

            var filtered = new List<ServerItemViewModel>(rawCount);
            for (int i = 0; i < rawCount; i++)
            {
                ServerItemViewModel s = _allServers[i];
                if (_hideFullServers && s.MaxPlayers > 0 && s.Playing >= s.MaxPlayers) continue;
                if (_hideVipServers && (s.Playing < minPlayersThreshold ||
                    s.Type.StartsWith("VIP", StringComparison.OrdinalIgnoreCase) ||
                    s.JobId.StartsWith("VIP:", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Region, "VIP Private", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                filtered.Add(s);
            }

            switch (_serverSortMode)
            {
                case ServerSortMode.PlayersAsc: filtered.Sort(ComparePlayersAsc); break;
                case ServerSortMode.PlayersDesc: filtered.Sort(ComparePlayersDesc); break;
                case ServerSortMode.PingAsc: filtered.Sort(ComparePingAsc); break;
                case ServerSortMode.PingDesc: filtered.Sort(ComparePingDesc); break;
            }

            _servers.Sync(filtered);
            NotifyServerCounts();
        }

        private static int ComparePlayersAsc(ServerItemViewModel a, ServerItemViewModel b)
        {
            int cmp = a.Playing.CompareTo(b.Playing);
            return cmp != 0 ? cmp : (a.Ping >= 0 ? a.Ping : int.MaxValue).CompareTo(b.Ping >= 0 ? b.Ping : int.MaxValue);
        }

        private static int ComparePlayersDesc(ServerItemViewModel a, ServerItemViewModel b)
        {
            int cmp = b.Playing.CompareTo(a.Playing);
            return cmp != 0 ? cmp : (a.Ping >= 0 ? a.Ping : int.MaxValue).CompareTo(b.Ping >= 0 ? b.Ping : int.MaxValue);
        }

        private static int ComparePingAsc(ServerItemViewModel a, ServerItemViewModel b)
        {
            int cmp = (a.Ping >= 0 ? a.Ping : int.MaxValue).CompareTo(b.Ping >= 0 ? b.Ping : int.MaxValue);
            return cmp != 0 ? cmp : a.Playing.CompareTo(b.Playing);
        }

        private static int ComparePingDesc(ServerItemViewModel a, ServerItemViewModel b)
        {
            int cmp = (b.Ping >= 0 ? b.Ping : -1).CompareTo(a.Ping >= 0 ? a.Ping : -1);
            return cmp != 0 ? cmp : a.Playing.CompareTo(b.Playing);
        }
    }
}
