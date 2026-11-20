#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Text.Json;
using Shiro.Http;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class ServerListViewModel : ObservableObject, IDisposable
    {
        private readonly MainViewModel? _mainViewModel;
        private readonly IAppLogger _logger;
        private readonly ISettingsService _settings;
        private readonly BulkObservableCollection<ServerItemViewModel> _servers = new BulkObservableCollection<ServerItemViewModel>();

        private string _placeIdInput = string.Empty;
        private long _lastLoadedPlaceId;
        private bool _isLoadingServers;
        private bool _isLoadingRegions;
        private string _loadingStatus = Shiro.Services.LocalizationService.Get("Status_Ready");
        private string _lastDefaultReadyStatus = Shiro.Services.LocalizationService.Get("Status_Ready");
        private ServerItemViewModel? _selectedServer;
        private int _selectedSubTabIndex = 0; // 0: Servers, 1: Games, 2: Recent & Favorites, 3: Watcher
        private int _disposed;

        private CancellationTokenSource? _serversCts;
        private CancellationTokenSource? _regionsCts;
        private CancellationTokenSource? _playerSearchCts;

        public BulkObservableCollection<ServerItemViewModel> Servers => _servers;
        public int TotalServersCount => _servers.Count;
        public bool HasServers => _servers.Count > 0;
        public bool IsServersEmpty => !IsLoadingServers && _servers.Count == 0;

        public string PlaceIdInput
        {
            get => _placeIdInput;
            set
            {
                if (SetProperty(ref _placeIdInput, value))
                {
                    if (_mainViewModel != null && !string.IsNullOrWhiteSpace(value) && _mainViewModel.PlaceId != value)
                        _mainViewModel.PlaceId = value;
                }
            }
        }

        public bool IsLoadingServers
        {
            get => _isLoadingServers;
            set
            {
                if (SetProperty(ref _isLoadingServers, value))
                {
                    OnPropertyChanged(nameof(CanRefreshServers));
                    OnPropertyChanged(nameof(CanCancelOperation));
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
                    OnPropertyChanged(nameof(CanCancelOperation));
            }
        }

        public bool CanCancelOperation => IsLoadingServers || IsLoadingRegions || IsSearchingPlayer;

        public void NotifyServerCounts()
        {
            OnPropertyChanged(nameof(TotalServersCount));
            OnPropertyChanged(nameof(HasServers));
            OnPropertyChanged(nameof(IsServersEmpty));
        }

        public bool CanRefreshServers => !IsLoadingServers;

        public string LoadingStatus
        {
            get => _loadingStatus;
            set => SetProperty(ref _loadingStatus, value);
        }

        public ServerItemViewModel? SelectedServer
        {
            get => _selectedServer;
            set => SetProperty(ref _selectedServer, value);
        }

        public int SelectedSubTabIndex
        {
            get => _selectedSubTabIndex;
            set
            {
                if (SetProperty(ref _selectedSubTabIndex, value))
                {
                    if (value == 0)
                        EnsureServersLoadedForCurrentPlaceId();
                    else if (value == 1 && _searchGames.Count == 0 && !IsSearchingGames)
                        _ = SearchGamesAsync();
                    else if (value == 2)
                    {
                        _mainViewModel?.ReloadRecentGames();
                        NotifyRecentGamesChanged();
                    }
                }
            }
        }

        public void EnsureServersLoadedForCurrentPlaceId()
        {
            if (IsLoadingServers)
                return;

            string rawPid = !string.IsNullOrWhiteSpace(PlaceIdInput)
                ? PlaceIdInput
                : (!string.IsNullOrWhiteSpace(_mainViewModel?.PlaceId) ? _mainViewModel!.PlaceId : (AppServices.ServerBrowserState.CurrentPlaceId ?? string.Empty));

            if (string.IsNullOrWhiteSpace(PlaceIdInput) && !string.IsNullOrWhiteSpace(rawPid))
                SyncPlaceId(rawPid);

            long placeId = AccountLauncherService.ParsePlaceId(rawPid);
            if (placeId > 0 && (_allServers.Count == 0 || _lastLoadedPlaceId != placeId))
                _ = RefreshServersAsync();
        }

        public ICommand RefreshServersCommand { get; }
        public ICommand CancelOperationCommand { get; }
        public ICommand JoinServerCommand { get; }
        public ICommand SelectServerJobIdCommand { get; }
        public ICommand CopyJobIdCommand { get; }
        public ICommand LoadRegionCommand { get; }
        public ICommand LoadAllRegionsCommand { get; }
        public ICommand SearchPlayerCommand { get; }
        public ICommand SearchGamesCommand { get; }
        public ICommand SelectGameCommand { get; }
        public ICommand LaunchGameCommand { get; }
        public ICommand AddFavoriteCommand { get; }
        public ICommand RemoveFavoriteCommand { get; }
        public ICommand SelectFavoriteCommand { get; }
        public ICommand LaunchFavoriteCommand { get; }

        public ServerListViewModel(MainViewModel? mainViewModel = null, IAppLogger? logger = null, ISettingsService? settings = null)
        {
            _mainViewModel = mainViewModel;
            _logger = logger ?? AppServices.Logger;
            _settings = settings ?? AppServices.GeneralSettings;
            _placeIdInput = mainViewModel?.PlaceId ?? string.Empty;

            RefreshServersCommand = new AsyncRelayCommand(RefreshServersAsync);
            CancelOperationCommand = new RelayCommand(CancelOperation);
            JoinServerCommand = new AsyncRelayCommand(JoinServerAsync);
            SelectServerJobIdCommand = new RelayCommand(SelectServerJobId);
            CopyJobIdCommand = new RelayCommand(CopyJobId);
            LoadRegionCommand = new AsyncRelayCommand(LoadRegionAsync);
            LoadAllRegionsCommand = new AsyncRelayCommand(LoadAllRegionsAsync);
            SearchPlayerCommand = new AsyncRelayCommand(SearchPlayerAsync);
            SearchGamesCommand = new AsyncRelayCommand(SearchGamesAsync);
            SelectGameCommand = new RelayCommand(SelectGame);
            LaunchGameCommand = new AsyncRelayCommand(LaunchGameAsync);
            AddFavoriteCommand = new RelayCommand(AddFavorite);
            RemoveFavoriteCommand = new RelayCommand(RemoveFavorite);
            SelectFavoriteCommand = new RelayCommand(SelectFavorite);
            LaunchFavoriteCommand = new AsyncRelayCommand(LaunchFavoriteAsync);

            InitializeSortingAndLibraryCommands();
            LoadWatcherSettings();
            LoadFavorites();

            _languageChangedHandler = WeakEventSubscription.CreateAction<ServerListViewModel, AppLanguage>(this, static (vm, lang) => vm.OnLanguageChanged(lang), static h => LocalizationService.LanguageChanged -= h);
            LocalizationService.LanguageChanged += _languageChangedHandler;
        }
        private Action<AppLanguage>? _languageChangedHandler;

        private void OnLanguageChanged(AppLanguage lang)
        {
            AppDispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                string localizedReady = Shiro.Services.LocalizationService.Get("Status_Ready");
                if (string.IsNullOrWhiteSpace(_loadingStatus)
                    || string.Equals(_loadingStatus, _lastDefaultReadyStatus, StringComparison.Ordinal)
                    || string.Equals(_loadingStatus, localizedReady, StringComparison.Ordinal))
                {
                    _lastDefaultReadyStatus = localizedReady;
                    LoadingStatus = localizedReady;
                }
                foreach (var s in _allServers)
                    s.NotifyLanguageChanged();
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (_languageChangedHandler != null) LocalizationService.LanguageChanged -= _languageChangedHandler;
            CancelAllOperations();
            GC.SuppressFinalize(this);
        }

        public void SyncPlaceId(string pid)
        {
            if (!string.IsNullOrWhiteSpace(pid) && _placeIdInput != pid)
            {
                _placeIdInput = pid;
                OnPropertyChanged(nameof(PlaceIdInput));
            }
        }

        private void CancelAllOperations()
        {
            ServerListOperationHelper.ReplaceCts(ref _serversCts, createNew: false, "ServerListViewModel.ServersCts");
            ServerListOperationHelper.ReplaceCts(ref _regionsCts, createNew: false, "ServerListViewModel.RegionsCts");
            ServerListOperationHelper.ReplaceCts(ref _playerSearchCts, createNew: false, "ServerListViewModel.PlayerSearchCts");
            ServerListOperationHelper.ReplaceCts(ref _gamesSearchCts, createNew: false, "ServerListViewModel.GamesSearchCts");
        }

        public void CancelOperation()
        {
            CancelAllOperations();
            IsLoadingServers = false;
            IsLoadingRegions = false;
            IsSearchingPlayer = false;
            IsSearchingGames = false;
            if (_allServers.Count == 0) _lastLoadedPlaceId = 0;
            LoadingStatus = Shiro.Services.LocalizationService.Get("ServerList.OperationCancelled");
            NotifyServerCounts();
        }

        public async Task RefreshServersAsync()
        {
            string rawPid = !string.IsNullOrWhiteSpace(PlaceIdInput) ? PlaceIdInput : (_mainViewModel?.PlaceId ?? string.Empty);
            long placeId = AccountLauncherService.ParsePlaceId(rawPid);
            if (placeId <= 0)
            {
                LoadingStatus = LocalizationService.Get("ServerList.EnterValidPlaceBeforeLoad");
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
            LoadingStatus = LocalizationService.Format("ServerList.FindingServersForPlace", placeId);
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
                                        LoadingStatus = LocalizationService.Format("ServerList.LoadServersFailed", response.StatusCode);
                                });
                            }
                            break;
                        }

                        var serverInfo = FastJson.TryDeserialize<ServersInfo>(response.Content, out var sInfo) ? sInfo : null;
                        if (serverInfo?.data == null || serverInfo.data.Count == 0)
                            break;

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
                            LoadingStatus = LocalizationService.Format("ServerList.LoadedServersCount", currentCount);
                        });

                        if (totalLoaded >= 200) break;
                    }

                    // Await VIP servers that were started in parallel, keeping only genuine VIP entries
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
                                if (ct.IsCancellationRequested) return;
                                PrependRawServers(realVipList);
                            });
                        }
                    }

                    AppDispatcher.Post(() =>
                    {
                        if (ct.IsCancellationRequested || !ReferenceEquals(Volatile.Read(ref _serversCts), cts)) return;
                        LoadingStatus = totalLoaded > 0
                            ? Shiro.Services.LocalizationService.Format("ServerList.LoadedServersSuccess", totalLoaded)
                            : Shiro.Services.LocalizationService.Get("ServerList.NoServersFoundForPlace");
                        NotifyServerCounts();
                        if (totalLoaded > 0)
                            _ = LoadAllRegionsAsync();
                    });
                }
                catch (OperationCanceledException)
                {
                    AppDispatcher.Post(() =>
                    {
                        if (!ReferenceEquals(Volatile.Read(ref _serversCts), cts)) return;
                        LoadingStatus = Shiro.Services.LocalizationService.Get("ServerList.LoadingServersCancelled");
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
                        LoadingStatus = Shiro.Services.LocalizationService.Format("Common.ErrorFormat", ex.Message);
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
                    LoadingStatus = Shiro.Services.LocalizationService.Get("ServerList.LoadingServersCancelled");
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
    }
}
