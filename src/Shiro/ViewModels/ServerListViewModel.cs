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
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class ServerListViewModel : ObservableObject
    {
        private readonly MainViewModel? _mainViewModel;
        private readonly BulkObservableCollection<ServerItemViewModel> _servers = new BulkObservableCollection<ServerItemViewModel>();

        private string _placeIdInput = string.Empty;
        private long _lastLoadedPlaceId;
        private bool _isLoadingServers;
        private string _loadingStatus = Shiro.Services.LocalizationService.Get("Status_Ready");
        private ServerItemViewModel? _selectedServer;
        private int _selectedSubTabIndex = 0; // 0: Servers, 1: Games, 2: Recent & Favorites, 3: Watcher

        private CancellationTokenSource? _operationCts;

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
                    {
                        _mainViewModel.PlaceId = value;
                    }
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
                    NotifyServerCounts();
                }
            }
        }

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
                    {
                        EnsureServersLoadedForCurrentPlaceId();
                    }
                    else if (value == 1 && _searchGames.Count == 0 && !IsSearchingGames)
                    {
                        _ = SearchGamesAsync();
                    }
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
                : (!string.IsNullOrWhiteSpace(_mainViewModel?.PlaceId) ? _mainViewModel!.PlaceId : (AppSettings.CurrentPlaceId ?? string.Empty));

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

        public ServerListViewModel(MainViewModel? mainViewModel)
        {
            _mainViewModel = mainViewModel;
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

            LocalizationService.LanguageChanged += lang =>
            {
                if (_loadingStatus == Shiro.Services.LocalizationService.Get("Status_Ready") || _loadingStatus == "Ready")
                {
                    LoadingStatus = Shiro.Services.LocalizationService.Get("Status_Ready");
                }
                foreach (var s in _allServers)
                {
                    s.NotifyLanguageChanged();
                }
            };
        }

        public void SyncPlaceId(string pid)
        {
            if (!string.IsNullOrWhiteSpace(pid) && _placeIdInput != pid)
            {
                _placeIdInput = pid;
                OnPropertyChanged(nameof(PlaceIdInput));
            }
        }

        private CancellationTokenSource? ResetOperationCts(bool createNew)
        {
            var nextCts = createNew ? new CancellationTokenSource() : null;
            var prevCts = Interlocked.Exchange(ref _operationCts, nextCts);
            if (prevCts != null)
            {
                SafeExecuteHelper.Try(() => prevCts.Cancel(), "ServerListViewModel.ResetOperationCts.Cancel");



                SafeExecuteHelper.Try(() => prevCts.Dispose(), "ServerListViewModel.ResetOperationCts.Dispose");

            }
            return nextCts;
        }

        public void CancelOperation()
        {
            ResetOperationCts(createNew: false);
            IsLoadingServers = false;
            IsSearchingPlayer = false;
            if (_allServers.Count == 0) _lastLoadedPlaceId = 0;
            LoadingStatus = Shiro.Services.LocalizationService.Get("Auto_ServerListViewMo_554");
            NotifyServerCounts();
        }

        public async Task RefreshServersAsync()
        {
            string rawPid = !string.IsNullOrWhiteSpace(PlaceIdInput) ? PlaceIdInput : (_mainViewModel?.PlaceId ?? string.Empty);
            long placeId = AccountLauncherService.ParsePlaceId(rawPid);
            if (placeId <= 0)
            {
                LoadingStatus = LocalizationService.IsEnglish
                    ? "Please enter a valid Place ID before loading!"
                    : Shiro.Services.LocalizationService.Get("Auto_ServerListViewMo_555");
                return;
            }

            var cts = ResetOperationCts(createNew: true)!;
            IsSearchingPlayer = false;
            CancellationToken ct = cts.Token;

            _lastLoadedPlaceId = placeId;
            IsLoadingServers = true;
            LoadingStatus = LocalizationService.IsEnglish
                ? $"Searching servers for Place ID {placeId}..."
                : Shiro.Services.LocalizationService.Format("Auto_ServerListViewMo_556", placeId);
            ClearRawServers();

            await Task.Run(async () =>
            {
                try
                {
                    var gamesClient = AppServices.RobloxApi.Games;
                    var vipAuthAccount = ResolveAuthAccount();
                    Task<List<ServerItemViewModel>?> vipTask = vipAuthAccount != null
                        ? FetchVipServersAsync(gamesClient, vipAuthAccount, placeId, ct)
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
                                        LoadingStatus = LocalizationService.IsEnglish
                                            ? $"Failed to load server list: {response.StatusCode}"
                                            : Shiro.Services.LocalizationService.Format("Auto_ServerListViewMo_557", response.StatusCode);
                                });
                            }
                            break;
                        }

                        var serverInfo = Utilities.FastJson.TryDeserialize<ServersInfo>(response.Content, out var sInfo) ? sInfo : null;
                        if (serverInfo?.data == null || serverInfo.data.Count == 0)
                        {
                            break;
                        }

                        var batch = new List<ServerItemViewModel>(serverInfo.data.Count);
                        foreach (var s in serverInfo.data)
                        {
                            batch.Add(new ServerItemViewModel
                            {
                                JobId = s.id,
                                Type = "Public",
                                MaxPlayers = s.maxPlayers,
                                Playing = s.playing,
                                Ping = s.ping ?? -1,
                                Region = string.IsNullOrEmpty(s.region) ? string.Empty : s.region,
                                Ip = s.ip ?? string.Empty,
                                RawData = s
                            });
                            totalLoaded++;
                        }

                        cursor = serverInfo.nextPageCursor ?? string.Empty;
                        int currentCount = totalLoaded;

                        AppDispatcher.Post(() =>
                        {
                            if (ct.IsCancellationRequested) return;
                            AppendRawServers(batch);
                            LoadingStatus = LocalizationService.IsEnglish
                                ? $"Loaded {currentCount} servers..."
                                : Shiro.Services.LocalizationService.Format("Auto_ServerListViewMo_558", currentCount);
                        });

                        if (totalLoaded >= 200) break;
                    }

                    // Await VIP servers that were started in parallel
                    var vipList = await vipTask.ConfigureAwait(false);
                    if (!ct.IsCancellationRequested && vipList != null && vipList.Count > 0)
                    {
                        totalLoaded += vipList.Count;
                        AppDispatcher.Post(() =>
                        {
                            if (ct.IsCancellationRequested) return;
                            PrependRawServers(vipList);
                        });
                    }

                    AppDispatcher.Post(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        LoadingStatus = totalLoaded > 0
                            ? Shiro.Services.LocalizationService.Format("Auto_ServerListViewMo_559", totalLoaded)
                            : Shiro.Services.LocalizationService.Get("Auto_ServerListViewMo_560");
                        NotifyServerCounts();
                    });
                }
                catch (OperationCanceledException)
                {
                    AppDispatcher.Post(() =>
                    {
                        LoadingStatus = Shiro.Services.LocalizationService.Get("Auto_ServerListViewMo_561");
                        _lastLoadedPlaceId = 0;
                        NotifyServerCounts();
                    });
                }
                catch (Exception ex)
                {
                    Program.Logger?.Error($"[RefreshServersAsync] {ex}");
                    AppDispatcher.Post(() =>
                    {
                        LoadingStatus = Shiro.Services.LocalizationService.Format("Proxy_Dead", ex.Message);
                        NotifyServerCounts();
                    });
                }
                finally
                {
                    AppDispatcher.Post(() =>
                    {
                        IsLoadingServers = false;
                        NotifyServerCounts();
                    });
                }
            }, ct);
        }
    }
}
