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
using RBX_Alt_Manager.Http;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.ViewModels
{
    public partial class ServerListViewModel : ObservableObject
    {
        private readonly MainViewModel _mainViewModel;
        private readonly BulkObservableCollection<ServerItemViewModel> _servers = new BulkObservableCollection<ServerItemViewModel>();

        private string _placeIdInput = string.Empty;
        private bool _isLoadingServers;
        private string _loadingStatus = LocalizationService.IsEnglish ? "Ready" : "Sẵn sàng";
        private ServerItemViewModel _selectedServer;
        private int _selectedSubTabIndex = 0; // 0: Servers, 1: Games, 2: Favorites, 3: Watcher

        private CancellationTokenSource _operationCts;

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

        public ServerItemViewModel SelectedServer
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
                    if (value == 1 && _searchGames.Count == 0 && !IsSearchingGames)
                    {
                        _ = SearchGamesAsync();
                    }
                }
            }
        }

        // Commands
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

        public ServerListViewModel(MainViewModel mainViewModel)
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

            LoadWatcherSettings();
            LoadFavorites();

            LocalizationService.LanguageChanged += lang =>
            {
                if (_loadingStatus == "Sẵn sàng" || _loadingStatus == "Ready")
                {
                    LoadingStatus = LocalizationService.IsEnglish ? "Ready" : "Sẵn sàng";
                }
                foreach (var s in _servers)
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

        private CancellationTokenSource ResetOperationCts(bool createNew)
        {
            var nextCts = createNew ? new CancellationTokenSource() : null;
            var prevCts = Interlocked.Exchange(ref _operationCts, nextCts);
            if (prevCts != null)
            {
                try { prevCts.Cancel(); }
                catch (ObjectDisposedException) { }
                catch (Exception) { }

                try { prevCts.Dispose(); }
                catch (Exception) { }
            }
            return nextCts;
        }

        public void CancelOperation()
        {
            ResetOperationCts(createNew: false);
            IsLoadingServers = false;
            IsSearchingPlayer = false;
            LoadingStatus = LocalizationService.IsEnglish ? "Operation cancelled." : "Đã hủy thao tác.";
            NotifyServerCounts();
        }

        public async Task RefreshServersAsync()
        {
            string rawPid = !string.IsNullOrWhiteSpace(PlaceIdInput) ? PlaceIdInput : _mainViewModel?.PlaceId;
            long placeId = AccountLauncherService.ParsePlaceId(rawPid);
            if (placeId <= 0)
            {
                LoadingStatus = LocalizationService.IsEnglish
                    ? "⚠️ Please enter a valid Place ID before loading!"
                    : "⚠️ Vui lòng nhập Place ID hợp lệ trước khi tải danh sách!";
                return;
            }

            var cts = ResetOperationCts(createNew: true);
            IsSearchingPlayer = false;
            CancellationToken ct = cts.Token;

            IsLoadingServers = true;
            LoadingStatus = LocalizationService.IsEnglish
                ? $"🔍 Searching servers for Place ID {placeId}..."
                : $"🔍 Đang tìm máy chủ cho Place ID {placeId}...";
            _servers.Clear();
            NotifyServerCounts();

            await Task.Run(async () =>
            {
                try
                {
                    var gamesClient = AccountManager.GamesClient ?? new RestClient("https://games.roblox.com");
                    var vipAuthAccount = ResolveAuthAccount();
                    Task<List<ServerItemViewModel>> vipTask = vipAuthAccount != null
                        ? FetchVipServersAsync(gamesClient, vipAuthAccount, placeId, ct)
                        : Task.FromResult<List<ServerItemViewModel>>(null);

                    string cursor = "_";
                    int totalLoaded = 0;

                    while (!string.IsNullOrEmpty(cursor) && !ct.IsCancellationRequested)
                    {
                        string url = $"v1/games/{placeId}/servers/public?sortOrder=Asc&limit=100" + (cursor == "_" ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
                        RestRequest request = new RestRequest(url, Method.Get);
                        RestResponse response = await gamesClient.ExecuteAsync(request, ct).ConfigureAwait(false);

                        if (ct.IsCancellationRequested) break;

                        if (!response.IsSuccessful || response.StatusCode != HttpStatusCode.OK)
                        {
                            if (totalLoaded == 0)
                            {
                                AppDispatcher.Post(() =>
                                {
                                    if (!ct.IsCancellationRequested)
                                        LoadingStatus = LocalizationService.IsEnglish
                                            ? $"⚠️ Failed to load server list: {response.StatusCode}"
                                            : $"⚠️ Không thể tải danh sách server: {response.StatusCode}";
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
                                Ip = s.ip,
                                RawData = s
                            });
                            totalLoaded++;
                        }

                        cursor = serverInfo.nextPageCursor;
                        int currentCount = totalLoaded;

                        AppDispatcher.Post(() =>
                        {
                            if (ct.IsCancellationRequested) return;
                            _servers.AddRange(batch);
                            NotifyServerCounts();
                            LoadingStatus = LocalizationService.IsEnglish
                                ? $"Loaded {currentCount} servers..."
                                : $"Đã tải {currentCount} máy chủ...";
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
                            _servers.InsertRange(0, vipList);
                            NotifyServerCounts();
                        });
                    }

                    AppDispatcher.Post(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        LoadingStatus = totalLoaded > 0
                            ? (LocalizationService.IsEnglish ? $"✅ Successfully loaded {totalLoaded} servers!" : $"✅ Đã tải thành công {totalLoaded} máy chủ!")
                            : (LocalizationService.IsEnglish ? "⚠️ No servers found for this Place ID." : "⚠️ Không tìm thấy máy chủ nào cho Place ID này.");
                        NotifyServerCounts();
                    });
                }
                catch (OperationCanceledException)
                {
                    AppDispatcher.Post(() =>
                    {
                        LoadingStatus = LocalizationService.IsEnglish ? "Server loading cancelled." : "Đã hủy tải máy chủ.";
                        NotifyServerCounts();
                    });
                }
                catch (Exception ex)
                {
                    Program.Logger?.Error($"[RefreshServersAsync] {ex}");
                    AppDispatcher.Post(() =>
                    {
                        LoadingStatus = LocalizationService.IsEnglish ? $"⚠️ Error: {ex.Message}" : $"⚠️ Lỗi: {ex.Message}";
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

        private static async Task<List<ServerItemViewModel>> FetchVipServersAsync(
            RestClient gamesClient,
            Account vipAuthAccount,
            long placeId,
            CancellationToken ct)
        {
            try
            {
                var vipReq = vipAuthAccount.MakeRequest($"v1/games/{placeId}/servers/VIP?sortOrder=Asc&limit=25", Method.Get);
                var vipResp = await vipAuthAccount.ExecuteAccountRestAsync(gamesClient, "https://games.roblox.com/", vipReq, ct).ConfigureAwait(false);
                if (vipResp != null && vipResp.IsSuccessful && !string.IsNullOrEmpty(vipResp.Content) && !ct.IsCancellationRequested)
                {
                    var vipInfo = Utilities.FastJson.TryDeserialize<ServersInfo>(vipResp.Content, out var vInfo) ? vInfo : null;
                    if (vipInfo?.data != null && vipInfo.data.Count > 0)
                    {
                        var vipList = new List<ServerItemViewModel>(vipInfo.data.Count);
                        foreach (var v in vipInfo.data)
                        {
                            string jobId = !string.IsNullOrEmpty(v.accessCode)
                                ? $"VIP:{v.accessCode}"
                                : (!string.IsNullOrEmpty(v.id)
                                    ? v.id
                                    : (!string.IsNullOrEmpty(v.name) ? v.name : "VIP Server"));

                            vipList.Add(new ServerItemViewModel
                            {
                                JobId = jobId,
                                Type = "VIP",
                                MaxPlayers = v.maxPlayers,
                                Playing = v.playing,
                                Ping = v.ping ?? -1,
                                Region = !string.IsNullOrEmpty(v.region) ? v.region : "VIP Private",
                                Ip = v.ip,
                                RawData = v
                            });
                        }
                        return vipList;
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[LoadVIPServers] {ex.Message}");
            }

            return null;
        }

        private void SelectServerJobId(object param)
        {
            if (param is ServerItemViewModel server)
            {
                string targetId = server.Type == "VIP" && server.RawData != null && !string.IsNullOrEmpty(server.RawData.accessCode)
                    ? $"VIP:{server.RawData.accessCode}"
                    : server.JobId;

                if (_mainViewModel != null)
                {
                    if (!string.IsNullOrWhiteSpace(PlaceIdInput))
                        _mainViewModel.PlaceId = PlaceIdInput;
                    _mainViewModel.JobId = targetId;
                    _mainViewModel.StatusMessage = LocalizationService.IsEnglish ? $"🎯 Selected Server: {targetId}" : $"🎯 Đã chọn Server: {targetId}";
                }

                ClipboardHelper.SetText(targetId);
                LoadingStatus = LocalizationService.IsEnglish ? $"📋 Selected and copied Job ID: {targetId}" : $"📋 Đã chọn và sao chép Job ID: {targetId}";
            }
        }

        private void CopyJobId(object param)
        {
            if (param is ServerItemViewModel server)
            {
                string targetId = server.Type == "VIP" && server.RawData != null && !string.IsNullOrEmpty(server.RawData.accessCode)
                    ? $"VIP:{server.RawData.accessCode}"
                    : server.JobId;
                ClipboardHelper.SetText(targetId);
                LoadingStatus = LocalizationService.IsEnglish ? $"📋 Copied Job ID: {targetId}" : $"📋 Đã sao chép Job ID: {targetId}";
            }
        }

        private async Task JoinServerAsync(object param)
        {
            if (param is not ServerItemViewModel server) return;

            string targetServer = server.Type == "VIP" && server.RawData != null && !string.IsNullOrEmpty(server.RawData.accessCode)
                ? $"VIP:{server.RawData.accessCode}"
                : server.JobId;

            string pidText = !string.IsNullOrWhiteSpace(PlaceIdInput) ? PlaceIdInput : _mainViewModel?.PlaceId;
            long placeId = AccountLauncherService.ParsePlaceId(pidText);
            if (placeId <= 0)
            {
                LoadingStatus = LocalizationService.IsEnglish ? "⚠️ Invalid Place ID!" : "⚠️ Place ID không hợp lệ!";
                return;
            }

            if (_mainViewModel != null)
            {
                _mainViewModel.PlaceId = placeId.ToString();
                _mainViewModel.JobId = targetServer;
            }

            var selectedAccounts = _mainViewModel?.Accounts?.Where(a => a.IsSelected && a.Model != null).Select(a => a.Model).ToList();
            if (selectedAccounts == null || selectedAccounts.Count == 0)
            {
                if (AccountManager.SelectedAccount != null)
                    selectedAccounts = new List<Account> { AccountManager.SelectedAccount };
                else if (AccountManager.LastValidAccount != null)
                    selectedAccounts = new List<Account> { AccountManager.LastValidAccount };
                else
                {
                    var firstAcc = _mainViewModel?.Accounts?.FirstOrDefault(a => a.Model != null)?.Model
                                   ?? AccountManager.GetAccountsSnapshot()?.FirstOrDefault();
                    if (firstAcc != null)
                        selectedAccounts = new List<Account> { firstAcc };
                }
            }

            if (selectedAccounts == null || selectedAccounts.Count == 0)
            {
                LoadingStatus = LocalizationService.IsEnglish
                    ? "⚠️ Please add accounts in the Accounts tab before joining!"
                    : "⚠️ Vui lòng thêm tài khoản trong tab Tài khoản trước khi vào server!";
                return;
            }

            string serverDesc = string.IsNullOrEmpty(targetServer) ? (LocalizationService.IsEnglish ? "random" : "ngẫu nhiên") : targetServer;
            LoadingStatus = LocalizationService.IsEnglish
                ? $"🚀 Joining Server {serverDesc} with {selectedAccounts.Count} account(s)..."
                : $"🚀 Đang vào Server {serverDesc} với {selectedAccounts.Count} tài khoản...";
            if (AccountManager.Instance != null)
            {
                await AccountManager.Instance.ModernJoinServerDirect(selectedAccounts, placeId, targetServer).ConfigureAwait(true);
            }
        }

        private Account ResolveAuthAccount()
        {
            var selectedFromVm = _mainViewModel?.Accounts?.FirstOrDefault(a => a.IsSelected && a.Valid && a.Model != null)?.Model;
            if (selectedFromVm != null)
                return selectedFromVm;

            if (AccountManager.SelectedAccount != null && AccountManager.SelectedAccount.Valid)
                return AccountManager.SelectedAccount;

            if (AccountManager.LastValidAccount != null && AccountManager.LastValidAccount.Valid)
                return AccountManager.LastValidAccount;

            return AccountManager.GetAccountsSnapshot().FirstOrDefault(a => a != null && a.Valid)
                   ?? AccountManager.SelectedAccount
                   ?? AccountManager.LastValidAccount;
        }

        private Task LoadRegionAsync(object param) => LoadRegionCoreAsync(param as ServerItemViewModel, CancellationToken.None);

        private async Task LoadRegionCoreAsync(ServerItemViewModel server, CancellationToken ct)
        {
            if (server == null || server.RawData == null) return;
            server.IsBusyPinging = true;

            try
            {
                var acc = ResolveAuthAccount();
                if (acc == null)
                {
                    server.Region = LocalizationService.IsEnglish ? "Login required" : "Cần đăng nhập";
                    return;
                }

                string rawPid = !string.IsNullOrWhiteSpace(PlaceIdInput) ? PlaceIdInput : _mainViewModel?.PlaceId;
                long placeId = AccountLauncherService.ParsePlaceId(rawPid);
                if (placeId <= 0) return;

                var result = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(acc, placeId, server.RawData.id, cancellationToken: ct).ConfigureAwait(true);
                if (result.Success)
                {
                    server.Ip = result.Ip;
                    if (result.PingMs.HasValue) server.Ping = result.PingMs.Value;
                    server.Region = result.Region;
                }
                else
                {
                    server.Region = !string.IsNullOrEmpty(result.Region) ? result.Region : (LocalizationService.IsEnglish ? "Unknown" : "Không rõ");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                server.Region = LocalizationService.IsEnglish ? "Load error" : "Lỗi nạp";
                Program.Logger?.Warn($"[LoadRegionAsync] {ex.Message}");
            }
            finally
            {
                server.IsBusyPinging = false;
            }
        }

        private async Task LoadAllRegionsAsync()
        {
            if (_servers.Count == 0) return;

            var cts = ResetOperationCts(createNew: true);
            var ct = cts.Token;

            var pendingServers = _servers
                .Take(25)
                .Where(server => string.IsNullOrEmpty(server.Region) || server.Region == "Chưa nạp" || server.Region == "Not loaded" || string.IsNullOrEmpty(server.Ip))
                .ToList();

            if (pendingServers.Count == 0)
            {
                LoadingStatus = LocalizationService.IsEnglish
                    ? "✅ Server regions loaded."
                    : "✅ Đã nạp vị trí máy chủ hoàn tất.";
                return;
            }

            LoadingStatus = LocalizationService.IsEnglish
                ? $"🌍 Loading server regions and measuring ping ({pendingServers.Count} servers)..."
                : $"🌍 Đang nạp vị trí vùng và đo ping ({pendingServers.Count} máy chủ)...";

            try
            {
                using var gate = new SemaphoreSlim(4, 4);
                var tasks = pendingServers.Select(async server =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        if (ct.IsCancellationRequested) return;
                        await LoadRegionCoreAsync(server, ct);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
                LoadingStatus = LocalizationService.IsEnglish
                    ? "✅ Server regions loaded."
                    : "✅ Đã nạp vị trí máy chủ hoàn tất.";
            }
            catch (OperationCanceledException)
            {
                LoadingStatus = LocalizationService.IsEnglish
                    ? "⏹️ Region loading cancelled."
                    : "⏹️ Đã hủy nạp vị trí máy chủ.";
            }
        }
    }
}
