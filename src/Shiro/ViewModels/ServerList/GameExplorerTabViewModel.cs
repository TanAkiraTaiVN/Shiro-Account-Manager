#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels
{
    /// <summary>
    /// Tab 1 of Server List: Roblox game omni-search, game icon resolution,
    /// and Select/Launch actions for discovered games.
    /// </summary>
    public sealed class GameExplorerTabViewModel : ObservableObject, IDisposable
    {
        private readonly MainViewModel? _mainViewModel;
        private readonly IAppLogger _logger;
        private readonly Action<string> _setPlaceIdInput;
        private readonly Action<string> _setLoadingStatus;
        private readonly Action<int> _setSelectedSubTabIndex;
        private readonly Func<Task> _refreshServersAsync;
        private readonly ObservableCollection<GameSearchItemViewModel> _searchGames = new();

        private string _gameSearchTerm = string.Empty;
        private bool _isSearchingGames;
        private CancellationTokenSource? _gamesSearchCts;
        private int _disposed;

        public GameExplorerTabViewModel(
            MainViewModel? mainViewModel = null,
            IAppLogger? logger = null,
            Action<string>? setPlaceIdInput = null,
            Action<string>? setLoadingStatus = null,
            Action<int>? setSelectedSubTabIndex = null,
            Func<Task>? refreshServersAsync = null)
        {
            _mainViewModel = mainViewModel;
            _logger = logger ?? AppServices.Logger;
            _setPlaceIdInput = setPlaceIdInput ?? (_ => { });
            _setLoadingStatus = setLoadingStatus ?? (_ => { });
            _setSelectedSubTabIndex = setSelectedSubTabIndex ?? (_ => { });
            _refreshServersAsync = refreshServersAsync ?? (() => Task.CompletedTask);

            SearchGamesCommand = new AsyncRelayCommand(SearchGamesAsync);
            SelectGameCommand = new RelayCommand(SelectGame);
            LaunchGameCommand = new AsyncRelayCommand(LaunchGameAsync);
        }

        public ICommand SearchGamesCommand { get; }
        public ICommand SelectGameCommand { get; }
        public ICommand LaunchGameCommand { get; }

        public ObservableCollection<GameSearchItemViewModel> SearchGames => _searchGames;
        public bool IsSearchGamesEmpty => !IsSearchingGames && _searchGames.Count == 0;

        public string GameSearchTerm
        {
            get => _gameSearchTerm;
            set => SetProperty(ref _gameSearchTerm, value);
        }

        public bool IsSearchingGames
        {
            get => _isSearchingGames;
            set
            {
                if (SetProperty(ref _isSearchingGames, value))
                    OnPropertyChanged(nameof(IsSearchGamesEmpty));
            }
        }

        internal async Task SearchGamesAsync()
        {
            var cts = ServerListOperationHelper.ReplaceCts(ref _gamesSearchCts, createNew: true, "ServerListViewModel.GamesSearchCts")!;
            CancellationToken ct = cts.Token;
            IsSearchingGames = true;
            _searchGames.Clear();

            try
            {
                string term = (GameSearchTerm ?? string.Empty).Trim();
                string query = string.IsNullOrWhiteSpace(term) ? "popular" : term;
                string sessionId = Guid.NewGuid().ToString("D");

                string url = $"https://apis.roblox.com/search-api/omni-search?searchQuery={Uri.EscapeDataString(query)}&sessionId={sessionId}&pageType=all";
                using var searchReq = new HttpRequestMessage(HttpMethod.Get, url);
                searchReq.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                using var searchResp = await SharedHttpClient.SendAsync(searchReq, retryTransient: true, ct).ConfigureAwait(false);
                string content = searchResp.IsSuccessStatusCode
                    ? await searchResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(content))
                {
                    var gamesList = ParseSearchResults(content);
                    if (gamesList.Count > 0 && !ct.IsCancellationRequested)
                    {
                        var thumbDict = await FetchPlaceIconsBatchAsync(gamesList.Select(x => x.PlaceId)).ConfigureAwait(false);
                        foreach (var g in gamesList)
                        {
                            if (thumbDict.TryGetValue(g.PlaceId, out var directImg))
                                g.ThumbnailUrl = directImg;
                        }
                    }

                    AppDispatcher.Post(() =>
                    {
                        if (ct.IsCancellationRequested || !ReferenceEquals(Volatile.Read(ref _gamesSearchCts), cts))
                            return;

                        _searchGames.Clear();
                        foreach (var g in gamesList)
                            _searchGames.Add(g);
                    });
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Superseded by a newer game search or disposal
            }
            catch (Exception ex)
            {
                _logger.Error($"[SearchGamesAsync] {ex.Message}");
            }
            finally
            {
                AppDispatcher.Post(() =>
                {
                    var current = Volatile.Read(ref _gamesSearchCts);
                    if (current == null || ReferenceEquals(current, cts))
                        IsSearchingGames = false;

                    if (ReferenceEquals(current, cts))
                        ServerListOperationHelper.ReleaseFinishedCts(ref _gamesSearchCts, cts, "ServerListViewModel.GamesSearchCts");
                });
            }
        }

        private List<GameSearchItemViewModel> ParseSearchResults(string content)
        {
            var gamesList = new List<GameSearchItemViewModel>();
            var seenPlaceIds = new HashSet<long>();
            try
            {
                using var jsDoc = JsonDocument.Parse(content);
                if (!jsDoc.RootElement.TryGetProperty("searchResults", out var results) || results.ValueKind != JsonValueKind.Array)
                    return gamesList;

                foreach (var group in results.EnumerateArray())
                {
                    if (!group.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var item in contents.EnumerateArray())
                    {
                        long pid = 0;
                        if (item.TryGetProperty("rootPlaceId", out var rpElem) && rpElem.TryGetInt64(out var rpid)) pid = rpid;
                        else if (item.TryGetProperty("placeId", out var pElem) && pElem.TryGetInt64(out var pVal)) pid = pVal;

                        string name = (item.TryGetProperty("name", out var nProp) ? nProp.GetString() : "Game") ?? "Game";
                        int playerCount = item.TryGetProperty("playerCount", out var pcProp) && pcProp.TryGetInt32(out var pc) ? pc : 0;
                        if (pid > 0 && seenPlaceIds.Add(pid))
                        {
                            GameInfoService.CachePlaceName(pid, name);
                            gamesList.Add(new GameSearchItemViewModel
                            {
                                PlaceId = pid,
                                Name = name,
                                PlayerCount = playerCount,
                                ThumbnailUrl = string.Empty
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[ParseGamesBatch] {ex.Message}");
            }
            return gamesList;
        }

        private void SelectGame(object? param)
        {
            if (param is not GameSearchItemViewModel game) return;
            _mainViewModel?.SetKnownGameTitle(game.PlaceId, game.Name);
            _setPlaceIdInput(game.PlaceId.ToString());
            if (_mainViewModel != null)
            {
                _mainViewModel.PlaceId = game.PlaceId.ToString();
                _mainViewModel.StatusMessage = LocalizationService.Format("ServerList.GameSelected", game.Name, game.PlaceId);
            }
            _setSelectedSubTabIndex(0);
            _ = _refreshServersAsync();
        }

        private Task LaunchGameAsync(object? param)
        {
            if (param is GameSearchItemViewModel game)
            {
                _mainViewModel?.SetKnownGameTitle(game.PlaceId, game.Name);
                _setPlaceIdInput(game.PlaceId.ToString());
                if (_mainViewModel != null)
                {
                    _mainViewModel.PlaceId = game.PlaceId.ToString();
                    _mainViewModel.JobId = string.Empty;
                    if (_mainViewModel.SelectedCount > 0)
                        _mainViewModel.StatusMessage = LocalizationService.Format("ServerList.LaunchingGame", game.Name, game.PlaceId);
                    else
                        _setLoadingStatus(LocalizationService.Get("ServerList.SelectAccountBeforePlay"));
                }
                _mainViewModel?.JoinServerSelectedCommand?.Execute(null);
            }
            return Task.CompletedTask;
        }

        internal static async Task<Dictionary<long, string>> FetchPlaceIconsBatchAsync(IEnumerable<long> placeIds)
        {
            var result = new Dictionary<long, string>();
            if (placeIds == null) return result;

            var missingPids = new List<long>();
            var seen = new HashSet<long>();
            foreach (long pid in placeIds)
            {
                if (pid <= 0 || !seen.Add(pid)) continue;
                if (Batch.GameIcons.TryGetValue(pid, out string? cachedUrl) && !string.IsNullOrEmpty(cachedUrl))
                    result[pid] = cachedUrl;
                else if (missingPids.Count < 100)
                    missingPids.Add(pid);
            }

            if (missingPids.Count == 0) return result;

            try
            {
                string thumbUrl = $"https://thumbnails.roblox.com/v1/places/gameicons?placeIds={string.Join(",", missingPids)}&returnPolicy=PlaceHolder&size=256x256&format=Png&isCircular=false";
                string thumbJson = await SharedHttpClient.GetStringAsync(thumbUrl, retryTransient: false).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(thumbJson))
                {
                    using var tDoc = JsonDocument.Parse(thumbJson);
                    if (tDoc.RootElement.TryGetProperty("data", out var tArr) && tArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tItem in tArr.EnumerateArray())
                        {
                            if (tItem.TryGetProperty("targetId", out var tidElem) && tidElem.TryGetInt64(out var tid) &&
                                tItem.TryGetProperty("imageUrl", out var imgElem) && imgElem.ValueKind == JsonValueKind.String)
                            {
                                string? imgUrl = imgElem.GetString();
                                if (!string.IsNullOrEmpty(imgUrl))
                                {
                                    result[tid] = imgUrl;
                                    Batch.GameIcons[tid] = imgUrl;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { AppServices.Logger.Debug($"[FetchPlaceIconsBatchAsync] {ex.Message}"); }

            return result;
        }

        internal void CancelSearch()
        {
            ServerListOperationHelper.ReplaceCts(ref _gamesSearchCts, createNew: false, "ServerListViewModel.GamesSearchCts");
            IsSearchingGames = false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            CancelSearch();
            GC.SuppressFinalize(this);
        }
    }
}
