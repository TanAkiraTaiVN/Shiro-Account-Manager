using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.ViewModels
{
    public partial class ServerListViewModel
    {
        private readonly ObservableCollection<GameSearchItemViewModel> _searchGames = new ObservableCollection<GameSearchItemViewModel>();
        private readonly ObservableCollection<FavoriteGameItemViewModel> _favorites = new ObservableCollection<FavoriteGameItemViewModel>();
        private string _gameSearchTerm = string.Empty;
        private bool _isSearchingGames;

        public bool IsSearchGamesEmpty => !IsSearchingGames && _searchGames.Count == 0;
        public bool IsFavoritesEmpty => _favorites.Count == 0;
        public ObservableCollection<GameSearchItemViewModel> SearchGames => _searchGames;
        public ObservableCollection<FavoriteGameItemViewModel> Favorites => _favorites;

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
                {
                    OnPropertyChanged(nameof(IsSearchGamesEmpty));
                }
            }
        }

        private async Task SearchGamesAsync()
        {
            IsSearchingGames = true;
            _searchGames.Clear();

            try
            {
                string term = (GameSearchTerm ?? "").Trim();
                string query = string.IsNullOrWhiteSpace(term) ? "popular" : term;
                string sessionId = Guid.NewGuid().ToString("D");

                string url = $"https://apis.roblox.com/search-api/omni-search?searchQuery={Uri.EscapeDataString(query)}&sessionId={sessionId}&pageType=all";
                string content = await SharedHttpClient.GetStringAsync(url, retryTransient: true).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(content))
                {
                    var gamesList = new List<GameSearchItemViewModel>();
                    var seenPlaceIds = new HashSet<long>();
                    try
                    {
                        using var jsDoc = JsonDocument.Parse(content);
                        if (jsDoc.RootElement.TryGetProperty("searchResults", out var results) && results.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var group in results.EnumerateArray())
                            {
                                if (group.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var item in contents.EnumerateArray())
                                    {
                                        long pid = 0;
                                        if (item.TryGetProperty("rootPlaceId", out var rpElem) && rpElem.TryGetInt64(out var rpid))
                                            pid = rpid;
                                        else if (item.TryGetProperty("placeId", out var pElem) && pElem.TryGetInt64(out var pVal))
                                            pid = pVal;

                                        string name = item.TryGetProperty("name", out var nProp) ? nProp.GetString() : "Game";
                                        int playerCount = item.TryGetProperty("playerCount", out var pcProp) && pcProp.TryGetInt32(out var pc) ? pc : 0;
                                        if (pid > 0 && seenPlaceIds.Add(pid))
                                        {
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
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"[ParseGamesBatch] {ex.Message}");
                    }

                    if (gamesList.Count > 0)
                    {
                        var thumbDict = await FetchPlaceIconsBatchAsync(gamesList.Select(x => x.PlaceId)).ConfigureAwait(false);
                        if (thumbDict.Count > 0)
                        {
                            foreach (var g in gamesList)
                            {
                                if (thumbDict.TryGetValue(g.PlaceId, out var directImg))
                                    g.ThumbnailUrl = directImg;
                            }
                        }
                    }

                    AppDispatcher.Post(() =>
                    {
                        foreach (var g in gamesList)
                            _searchGames.Add(g);
                    });
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[SearchGamesAsync] {ex.Message}");
            }
            finally
            {
                AppDispatcher.Post(() =>
                {
                    IsSearchingGames = false;
                });
            }
        }

        private void SelectGame(object param)
        {
            if (param is GameSearchItemViewModel game)
            {
                PlaceIdInput = game.PlaceId.ToString();
                if (_mainViewModel != null)
                {
                    _mainViewModel.PlaceId = game.PlaceId.ToString();
                    _mainViewModel.StatusMessage = LocalizationService.IsEnglish
                        ? $"🎮 Selected game: {game.Name} ({game.PlaceId})"
                        : $"🎮 Đã chọn game: {game.Name} ({game.PlaceId})";
                }
                SelectedSubTabIndex = 0;
                _ = RefreshServersAsync();
            }
        }

        private Task LaunchGameAsync(object param)
        {
            if (param is GameSearchItemViewModel game)
            {
                PlaceIdInput = game.PlaceId.ToString();
                if (_mainViewModel != null)
                {
                    _mainViewModel.PlaceId = game.PlaceId.ToString();
                    _mainViewModel.JobId = string.Empty;
                    _mainViewModel.StatusMessage = LocalizationService.IsEnglish
                        ? $"🎮 Launching game: {game.Name} ({game.PlaceId})"
                        : $"🎮 Đang khởi chạy game: {game.Name} ({game.PlaceId})";
                }
                _mainViewModel?.JoinServerSelectedCommand?.Execute(null);
            }
            return Task.CompletedTask;
        }

        private void AddFavorite(object param)
        {
            long pid = 0;
            string name = "Favorite Game";
            string thumb = string.Empty;

            if (param is GameSearchItemViewModel game)
            {
                pid = game.PlaceId;
                name = game.Name;
                if (!string.IsNullOrEmpty(game.ThumbnailUrl) && game.ThumbnailUrl.Contains("rbxcdn.com"))
                    thumb = game.ThumbnailUrl;
            }
            else if (long.TryParse(PlaceIdInput, out long p))
            {
                pid = p;
                name = $"Game {p}";
            }

            if (pid <= 0) return;

            if (_favorites.Any(f => f.PlaceId == pid))
            {
                LoadingStatus = LocalizationService.IsEnglish
                    ? "⚠️ This game is already in Favorites!"
                    : "⚠️ Game này đã có trong danh sách Yêu Thích!";
                return;
            }

            var item = new FavoriteGameItemViewModel
            {
                PlaceId = pid,
                Name = name,
                ThumbnailUrl = thumb
            };
            _favorites.Add(item);
            SaveFavorites();
            if (string.IsNullOrEmpty(thumb))
            {
                _ = ResolveFavoriteThumbnailsAsync();
            }
            LoadingStatus = LocalizationService.IsEnglish
                ? $"⭐ Added '{name}' to Favorites!"
                : $"⭐ Đã thêm '{name}' vào Yêu Thích!";
        }

        private void RemoveFavorite(object param)
        {
            if (param is FavoriteGameItemViewModel fav)
            {
                _favorites.Remove(fav);
                SaveFavorites();
                LoadingStatus = LocalizationService.IsEnglish
                    ? $"🗑️ Removed '{fav.Name}' from Favorites."
                    : $"🗑️ Đã xóa '{fav.Name}' khỏi Yêu Thích.";
            }
        }

        private void SelectFavorite(object param)
        {
            if (param is FavoriteGameItemViewModel fav)
            {
                PlaceIdInput = fav.PlaceId.ToString();
                if (_mainViewModel != null)
                {
                    _mainViewModel.PlaceId = fav.PlaceId.ToString();
                    if (!string.IsNullOrEmpty(fav.PrivateServer))
                        _mainViewModel.JobId = fav.PrivateServer;
                    _mainViewModel.StatusMessage = LocalizationService.IsEnglish
                        ? $"⭐ Selected Favorite game: {fav.Name}"
                        : $"⭐ Đã chọn game Yêu Thích: {fav.Name}";
                }
                SelectedSubTabIndex = 0;
                _ = RefreshServersAsync();
            }
        }

        private Task LaunchFavoriteAsync(object param)
        {
            if (param is FavoriteGameItemViewModel fav)
            {
                PlaceIdInput = fav.PlaceId.ToString();
                if (_mainViewModel != null)
                {
                    _mainViewModel.PlaceId = fav.PlaceId.ToString();
                    _mainViewModel.JobId = fav.PrivateServer ?? string.Empty;
                    _mainViewModel.StatusMessage = LocalizationService.IsEnglish
                        ? $"⭐ Launching Favorite game: {fav.Name}"
                        : $"⭐ Đang khởi chạy game Yêu Thích: {fav.Name}";
                }
                _mainViewModel?.JoinServerSelectedCommand?.Execute(null);
            }
            return Task.CompletedTask;
        }

        private void LoadFavorites()
        {
            string favPath = StoragePaths.GetUserFile("FavoriteGames.json");
            if (File.Exists(favPath))
            {
                try
                {
                    string json = File.ReadAllText(favPath);
                    var list = Utilities.FastJson.TryDeserialize<List<FavoriteGame>>(json, out var fl) ? fl : null;
                    if (list != null)
                    {
                        foreach (var g in list)
                        {
                            _favorites.Add(new FavoriteGameItemViewModel
                            {
                                PlaceId = g.PlaceID,
                                Name = string.IsNullOrEmpty(g.Name) ? $"Place {g.PlaceID}" : g.Name,
                                PrivateServer = g.PrivateServer,
                                ThumbnailUrl = string.Empty
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[LoadFavorites] {ex.Message}");
                }
            }
            OnPropertyChanged(nameof(IsFavoritesEmpty));
            if (_favorites.Count > 0)
            {
                _ = ResolveFavoriteThumbnailsAsync();
            }
        }

        private void SaveFavorites()
        {
            try
            {
                OnPropertyChanged(nameof(IsFavoritesEmpty));
                string favPath = StoragePaths.GetUserFile("FavoriteGames.json");
                var list = _favorites.Select(f => new FavoriteGame(f.Name, f.PlaceId, f.PrivateServer)).ToList();
                StoragePaths.AtomicWriteText(favPath, Utilities.FastJson.Serialize(list, indented: true));
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[SaveFavorites] {ex.Message}");
            }
        }

        private static async Task<Dictionary<long, string>> FetchPlaceIconsBatchAsync(IEnumerable<long> placeIds)
        {
            var result = new Dictionary<long, string>();
            if (placeIds == null) return result;

            var missingPids = new List<long>();
            var seen = new HashSet<long>();
            foreach (long pid in placeIds)
            {
                if (pid <= 0 || !seen.Add(pid)) continue;
                if (Batch.GameIcons.TryGetValue(pid, out string cachedUrl) && !string.IsNullOrEmpty(cachedUrl))
                {
                    result[pid] = cachedUrl;
                }
                else if (missingPids.Count < 100)
                {
                    missingPids.Add(pid);
                }
            }

            if (missingPids.Count == 0)
                return result;

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
                                string imgUrl = imgElem.GetString();
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
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[FetchPlaceIconsBatchAsync] {ex.Message}");
            }

            return result;
        }

        private async Task ResolveFavoriteThumbnailsAsync()
        {
            if (_favorites.Count == 0) return;
            try
            {
                var thumbDict = await FetchPlaceIconsBatchAsync(_favorites.Select(x => x.PlaceId)).ConfigureAwait(false);
                if (thumbDict.Count > 0)
                {
                    AppDispatcher.Post(() =>
                    {
                        foreach (var f in _favorites)
                        {
                            if (thumbDict.TryGetValue(f.PlaceId, out var directImg))
                                f.ThumbnailUrl = directImg;
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[ResolveFavoriteThumbnails] {ex.Message}");
            }
        }
    }
}
