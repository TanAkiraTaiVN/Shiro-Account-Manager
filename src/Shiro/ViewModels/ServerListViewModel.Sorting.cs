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
    /// Server List sorting/filtering (Players Asc/Desc, Ping Asc/Desc, Hide Full, Hide VIP),
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

        // Forwarding for Active Game Chip next to Place ID
        public string CurrentGameName => _mainViewModel?.CurrentGameName ?? string.Empty;
        public Bitmap? CurrentGameThumbnail => _mainViewModel?.CurrentGameThumbnail;
        public bool HasCurrentGameInfo => _mainViewModel?.HasCurrentGameInfo == true;

        // Forwarding for Recent Games inside Tab 2
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
            ToggleSortPlayersCommand = new RelayCommand(() =>
            {
                SortMode = SortMode switch
                {
                    ServerSortMode.Default => ServerSortMode.PlayersAsc,
                    ServerSortMode.PlayersAsc => ServerSortMode.PlayersDesc,
                    ServerSortMode.PlayersDesc => ServerSortMode.Default,
                    _ => ServerSortMode.PlayersAsc
                };
            });

            ToggleSortPingCommand = new RelayCommand(() =>
            {
                SortMode = SortMode switch
                {
                    ServerSortMode.Default => ServerSortMode.PingAsc,
                    ServerSortMode.PingAsc => ServerSortMode.PingDesc,
                    ServerSortMode.PingDesc => ServerSortMode.Default,
                    _ => ServerSortMode.PingAsc
                };
            });

            SelectRecentGameInLibraryCommand = new RelayCommand(param =>
            {
                if (param is RecentGameItemViewModel recent)
                {
                    _mainViewModel?.SetKnownGameTitle(recent.PlaceId, recent.Name);
                    PlaceIdInput = recent.PlaceId.ToString();
                    if (_mainViewModel != null)
                    {
                        _mainViewModel.PlaceId = recent.PlaceId.ToString();
                        _mainViewModel.StatusMessage = LocalizationService.IsEnglish
                            ? $"Selected recent game: {recent.Name} ({recent.PlaceId})"
                            : $"Đã chọn game gần đây: {recent.Name} ({recent.PlaceId})";
                    }
                    SelectedSubTabIndex = 0;
                    _ = RefreshServersAsync();
                }
            });

            LaunchRecentGameInLibraryCommand = new RelayCommand(param =>
            {
                if (param is RecentGameItemViewModel recent)
                {
                    _mainViewModel?.SetKnownGameTitle(recent.PlaceId, recent.Name);
                    PlaceIdInput = recent.PlaceId.ToString();
                    if (_mainViewModel != null)
                    {
                        _mainViewModel.PlaceId = recent.PlaceId.ToString();
                        _mainViewModel.JobId = string.Empty;
                        if (_mainViewModel.SelectedCount > 0)
                        {
                            _mainViewModel.StatusMessage = LocalizationService.IsEnglish
                                ? $"Launching recent game: {recent.Name} ({recent.PlaceId})"
                                : $"Đang khởi chạy game gần đây: {recent.Name} ({recent.PlaceId})";
                        }
                        else
                        {
                            LoadingStatus = LocalizationService.IsEnglish
                                ? "Please select at least one account in the Accounts tab first!"
                                : "Vui lòng tick chọn tài khoản ở tab 'Tài khoản' trước khi bấm Chơi!";
                        }
                        _mainViewModel.JoinServerSelectedCommand?.Execute(null);
                    }
                }
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
            if (servers != null)
            {
                _allServers.AddRange(servers);
            }
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
            IEnumerable<ServerItemViewModel> query = _allServers;

            if (_hideFullServers)
            {
                query = query.Where(s => s.MaxPlayers <= 0 || s.Playing < s.MaxPlayers);
            }

            if (_hideVipServers)
            {
                query = query.Where(s =>
                    !s.Type.StartsWith("VIP", StringComparison.OrdinalIgnoreCase) &&
                    !s.JobId.StartsWith("VIP:", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(s.Region, "VIP Private", StringComparison.OrdinalIgnoreCase));
            }

            query = _serverSortMode switch
            {
                ServerSortMode.PlayersAsc => query
                    .OrderBy(s => s.Playing)
                    .ThenBy(s => s.Ping >= 0 ? s.Ping : int.MaxValue),
                ServerSortMode.PlayersDesc => query
                    .OrderByDescending(s => s.Playing)
                    .ThenBy(s => s.Ping >= 0 ? s.Ping : int.MaxValue),
                ServerSortMode.PingAsc => query
                    .OrderBy(s => s.Ping >= 0 ? s.Ping : int.MaxValue)
                    .ThenBy(s => s.Playing),
                ServerSortMode.PingDesc => query
                    .OrderByDescending(s => s.Ping >= 0 ? s.Ping : -1)
                    .ThenBy(s => s.Playing),
                _ => query
            };

            _servers.ReplaceAll(query.ToList());
            NotifyServerCounts();
        }
    }
}
