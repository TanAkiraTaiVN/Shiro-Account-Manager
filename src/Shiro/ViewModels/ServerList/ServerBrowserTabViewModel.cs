#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Http;
using Shiro.Services;

namespace Shiro.ViewModels
{
    /// <summary>
    /// Tab 0 of Server List: public/VIP server loading, region pinging, sorting,
    /// filtering, and player avatar search.
    /// </summary>
    public sealed partial class ServerBrowserTabViewModel : ObservableObject, IDisposable
    {
        private readonly MainViewModel? _mainViewModel;
        private readonly IAppLogger _logger;
        private readonly Func<string> _getPlaceIdInput;
        private readonly Action<string> _syncPlaceIdInput;
        private readonly Action<string> _setLoadingStatus;
        private readonly Action _notifyCancelState;
        private readonly Action<string>? _setPlaceIdFromUser;

        private readonly BulkObservableCollection<ServerItemViewModel> _servers = new();
        private readonly List<ServerItemViewModel> _allServers = new();

        private long _lastLoadedPlaceId;
        private bool _isLoadingServers;
        private bool _isLoadingRegions;
        private ServerItemViewModel? _selectedServer;
        private ServerSortMode _serverSortMode = ServerSortMode.Default;
        private bool _hideFullServers;
        private bool _hideVipServers;
        private CancellationTokenSource? _serversCts;
        private CancellationTokenSource? _regionsCts;
        private CancellationTokenSource? _playerSearchCts;
        private int _disposed;

        public ServerBrowserTabViewModel(
            MainViewModel? mainViewModel = null,
            IAppLogger? logger = null,
            Func<string>? getPlaceIdInput = null,
            Action<string>? syncPlaceIdInput = null,
            Action<string>? setLoadingStatus = null,
            Action? notifyCancelState = null,
            Action<string>? setPlaceIdFromUser = null)
        {
            _mainViewModel = mainViewModel;
            _logger = logger ?? AppServices.Logger;
            _getPlaceIdInput = getPlaceIdInput ?? (() => mainViewModel?.LaunchBar.PlaceId ?? string.Empty);
            _syncPlaceIdInput = syncPlaceIdInput ?? (_ => { });
            _setLoadingStatus = setLoadingStatus ?? (_ => { });
            _notifyCancelState = notifyCancelState ?? (() => { });
            _setPlaceIdFromUser = setPlaceIdFromUser;

            RefreshServersCommand = new AsyncRelayCommand(RefreshServersAsync);
            JoinServerCommand = new AsyncRelayCommand(JoinServerAsync);
            SelectServerJobIdCommand = new RelayCommand(SelectServerJobId);
            CopyJobIdCommand = new RelayCommand(CopyJobId);
            LoadRegionCommand = new AsyncRelayCommand(LoadRegionAsync);
            LoadAllRegionsCommand = new AsyncRelayCommand(LoadAllRegionsAsync);
            SearchPlayerCommand = new AsyncRelayCommand(SearchPlayerAsync);
            ToggleSortPlayersCommand = new RelayCommand(() => SortMode = ServerListFilterHelper.NextPlayersSortMode(SortMode));
            ToggleSortPingCommand = new RelayCommand(() => SortMode = ServerListFilterHelper.NextPingSortMode(SortMode));
        }

        public ICommand RefreshServersCommand { get; }
        public ICommand JoinServerCommand { get; }
        public ICommand SelectServerJobIdCommand { get; }
        public ICommand CopyJobIdCommand { get; }
        public ICommand LoadRegionCommand { get; }
        public ICommand LoadAllRegionsCommand { get; }
        public ICommand SearchPlayerCommand { get; }
        public ICommand ToggleSortPlayersCommand { get; }
        public ICommand ToggleSortPingCommand { get; }

        public BulkObservableCollection<ServerItemViewModel> Servers => _servers;
        public int TotalServersCount => _servers.Count;
        public bool HasServers => _servers.Count > 0;
        public bool IsServersEmpty => !IsLoadingServers && _servers.Count == 0;
        public bool CanRefreshServers => !IsLoadingServers;
        public int RawServersCount => _allServers.Count;

        public bool IsLoadingServers
        {
            get => _isLoadingServers;
            set
            {
                if (SetProperty(ref _isLoadingServers, value))
                {
                    OnPropertyChanged(nameof(CanRefreshServers));
                    _notifyCancelState();
                    NotifyServerCounts();
                }
            }
        }

        public bool IsLoadingRegions
        {
            get => _isLoadingRegions;
            set
            {
                if (SetProperty(ref _isLoadingRegions, value))
                    _notifyCancelState();
            }
        }

        public ServerItemViewModel? SelectedServer
        {
            get => _selectedServer;
            set => SetProperty(ref _selectedServer, value);
        }

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

        public void NotifyServerCounts()
        {
            OnPropertyChanged(nameof(TotalServersCount));
            OnPropertyChanged(nameof(HasServers));
            OnPropertyChanged(nameof(IsServersEmpty));
        }

        internal void NotifyLanguageChanged()
        {
            foreach (var s in _allServers)
                s.NotifyLanguageChanged();
        }

        internal void NotifyStreamerModeChanged()
        {
            foreach (var s in _servers)
                s.NotifyStreamerModeChanged();
        }

        public void EnsureServersLoadedForCurrentPlaceId()
        {
            if (IsLoadingServers) return;

            string currentInput = _getPlaceIdInput();
            string rawPid = !string.IsNullOrWhiteSpace(currentInput)
                ? currentInput
                : (!string.IsNullOrWhiteSpace(_mainViewModel?.PlaceId)
                    ? _mainViewModel!.PlaceId
                    : (AppServices.ServerBrowserState.CurrentPlaceId ?? string.Empty));

            if (string.IsNullOrWhiteSpace(currentInput) && !string.IsNullOrWhiteSpace(rawPid))
                _syncPlaceIdInput(rawPid);

            long placeId = AccountLauncherService.ParsePlaceId(rawPid);
            if (placeId > 0 && (_allServers.Count == 0 || _lastLoadedPlaceId != placeId))
                _ = RefreshServersAsync();
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

        public async Task RefreshServersAsync()
        {
            string currentInput = _getPlaceIdInput();
            string rawPid = !string.IsNullOrWhiteSpace(currentInput) ? currentInput : (_mainViewModel?.PlaceId ?? string.Empty);
            long placeId = AccountLauncherService.ParsePlaceId(rawPid);
            if (placeId <= 0)
            {
                _setLoadingStatus(LocalizationService.Get("ServerList.EnterValidPlaceBeforeLoad"));
                return;
            }

            var cts = ServerListOperationHelper.ReplaceCts(ref _serversCts, createNew: true, "ServerListViewModel.ServersCts")!;
            ServerListOperationHelper.ReplaceCts(ref _regionsCts, createNew: false, "ServerListViewModel.RegionsCts");
            ServerListOperationHelper.ReplaceCts(ref _playerSearchCts, createNew: false, "ServerListViewModel.PlayerSearchCts");
            IsLoadingRegions = false;
            IsSearchingPlayer = false;
            CancellationToken ct = cts.Token;

            _lastLoadedPlaceId = placeId;
            IsLoadingServers = true;
            _setLoadingStatus(LocalizationService.Format("ServerList.FindingServersForPlace", placeId));
            ClearRawServers();
            ServerListOperationHelper.ResetCachedServers();

            try
            {
                await Task.Run(async () =>
                {
                    try
                    {
                        var gamesClient = AppServices.RobloxApi.Games;
                        var vipAuthAccount = ResolveAuthAccount();
                        Task<List<ServerItemViewModel>?> vipTask = vipAuthAccount != null
                            ? ServerListOperationHelper.FetchVipServersAsync(gamesClient, vipAuthAccount, placeId, ct)
                            : Task.FromResult<List<ServerItemViewModel>?>(null);

                        string cursor = "_";
                        int totalLoaded = 0;

                        while (!string.IsNullOrEmpty(cursor) && !ct.IsCancellationRequested)
                        {
                            string url = $"v1/games/{placeId}/servers/public?sortOrder=Asc&limit=100" + (cursor == "_" ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
                            RestRequest request = new RestRequest(url, Method.Get);
                            RestResponse response = await gamesClient.ExecuteAsync(request, ct).ConfigureAwait(false);
                            if (ct.IsCancellationRequested) break;

                            if (!response.IsSuccessful || response.StatusCode != HttpStatusCode.OK || string.IsNullOrEmpty(response.Content))
                            {
                                if (totalLoaded == 0)
                                {
                                    AppDispatcher.Post(() =>
                                    {
                                        if (!ct.IsCancellationRequested) _lastLoadedPlaceId = 0;
                                        if (!ct.IsCancellationRequested)
                                            _setLoadingStatus(LocalizationService.Format("ServerList.LoadServersFailed", response.StatusCode));
                                    });
                                }
                                break;
                            }

                            var serverInfo = FastJson.TryDeserialize<ServersInfo>(response.Content, out var sInfo) ? sInfo : null;
                            if (serverInfo?.data == null || serverInfo.data.Count == 0) break;

                            var batch = new List<ServerItemViewModel>(serverInfo.data.Count);
                            foreach (var s in serverInfo.data)
                            {
                                batch.Add(ServerListOperationHelper.CreatePublicServerItem(s));
                                totalLoaded++;
                            }
                            ServerListOperationHelper.AppendCachedServers(placeId, serverInfo.data);

                            cursor = serverInfo.nextPageCursor ?? string.Empty;
                            int currentCount = totalLoaded;

                            AppDispatcher.Post(() =>
                            {
                                if (ct.IsCancellationRequested) return;
                                AppendRawServers(batch);
                                _setLoadingStatus(LocalizationService.Format("ServerList.LoadedServersCount", currentCount));
                            });

                            if (totalLoaded >= 200) break;
                        }

                        var vipList = await vipTask.ConfigureAwait(false);
                        if (!ct.IsCancellationRequested && vipList != null && vipList.Count > 0)
                        {
                            var realVipList = vipList
                                .Where(v => v.RawData != null && (!string.IsNullOrEmpty(v.RawData.accessCode) || v.RawData.vipServerId > 0 || !string.IsNullOrEmpty(v.RawData.name)))
                                .ToList();
                            if (realVipList.Count > 0)
                            {
                                totalLoaded += realVipList.Count;
                                AppDispatcher.Post(() =>
                                {
                                    if (!ct.IsCancellationRequested) PrependRawServers(realVipList);
                                });
                            }
                        }

                        AppDispatcher.Post(() =>
                        {
                            if (ct.IsCancellationRequested || !ReferenceEquals(Volatile.Read(ref _serversCts), cts)) return;
                            _setLoadingStatus(totalLoaded > 0
                                ? LocalizationService.Format("ServerList.LoadedServersSuccess", totalLoaded)
                                : LocalizationService.Get("ServerList.NoServersFoundForPlace"));
                            NotifyServerCounts();
                            if (totalLoaded > 0) _ = LoadAllRegionsAsync();
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        AppDispatcher.Post(() =>
                        {
                            if (!ReferenceEquals(Volatile.Read(ref _serversCts), cts)) return;
                            _setLoadingStatus(LocalizationService.Get("ServerList.LoadingServersCancelled"));
                            _lastLoadedPlaceId = 0;
                            NotifyServerCounts();
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"[RefreshServersAsync] {ex}");
                        AppDispatcher.Post(() =>
                        {
                            if (!ReferenceEquals(Volatile.Read(ref _serversCts), cts)) return;
                            _lastLoadedPlaceId = 0;
                            _setLoadingStatus(LocalizationService.Format("Common.ErrorFormat", ex.Message));
                            NotifyServerCounts();
                        });
                    }
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                AppDispatcher.Post(() =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref _serversCts), cts)) return;
                    _setLoadingStatus(LocalizationService.Get("ServerList.LoadingServersCancelled"));
                    _lastLoadedPlaceId = 0;
                });
            }
            finally
            {
                AppDispatcher.Post(() =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref _serversCts), cts)) return;
                    IsLoadingServers = false;
                    NotifyServerCounts();
                    ServerListOperationHelper.ReleaseFinishedCts(ref _serversCts, cts, "ServerListViewModel.ServersCts");
                });
            }
        }

        internal void CancelOperations()
        {
            ServerListOperationHelper.ReplaceCts(ref _serversCts, createNew: false, "ServerListViewModel.ServersCts");
            ServerListOperationHelper.ReplaceCts(ref _regionsCts, createNew: false, "ServerListViewModel.RegionsCts");
            ServerListOperationHelper.ReplaceCts(ref _playerSearchCts, createNew: false, "ServerListViewModel.PlayerSearchCts");
            IsLoadingServers = false;
            IsLoadingRegions = false;
            IsSearchingPlayer = false;
            if (_allServers.Count == 0) _lastLoadedPlaceId = 0;
            NotifyServerCounts();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            ServerListOperationHelper.ReplaceCts(ref _serversCts, createNew: false, "ServerListViewModel.ServersCts");
            ServerListOperationHelper.ReplaceCts(ref _regionsCts, createNew: false, "ServerListViewModel.RegionsCts");
            ServerListOperationHelper.ReplaceCts(ref _playerSearchCts, createNew: false, "ServerListViewModel.PlayerSearchCts");
            GC.SuppressFinalize(this);
        }
    }
}
