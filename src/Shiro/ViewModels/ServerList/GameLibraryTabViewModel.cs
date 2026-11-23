#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels
{
    /// <summary>
    /// Tab 2 of Server List: Recent games and saved Favorite games library,
    /// with JSON persistence and thumbnail hydration.
    /// </summary>
    public sealed class GameLibraryTabViewModel : ObservableObject
    {
        private readonly ServerListViewModel _host;
        private readonly MainViewModel? _mainViewModel;
        private readonly IAppLogger _logger;
        private readonly ObservableCollection<FavoriteGameItemViewModel> _favorites = new();

        public GameLibraryTabViewModel(
            ServerListViewModel host,
            MainViewModel? mainViewModel = null,
            IAppLogger? logger = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _mainViewModel = mainViewModel;
            _logger = logger ?? AppServices.Logger;

            AddFavoriteCommand = new RelayCommand(AddFavorite);
            RemoveFavoriteCommand = new RelayCommand(RemoveFavorite);
            SelectFavoriteCommand = new RelayCommand(SelectFavorite);
            LaunchFavoriteCommand = new AsyncRelayCommand(LaunchFavoriteAsync);
            SelectRecentGameInLibraryCommand = new RelayCommand(p => ServerListFilterHelper.SelectRecentGame(_host, _mainViewModel, p));
            LaunchRecentGameInLibraryCommand = new RelayCommand(p => ServerListFilterHelper.LaunchRecentGame(_host, _mainViewModel, p));

            _favorites.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsFavoritesEmpty));
            LoadFavorites();
        }

        public ICommand AddFavoriteCommand { get; }
        public ICommand RemoveFavoriteCommand { get; }
        public ICommand SelectFavoriteCommand { get; }
        public ICommand LaunchFavoriteCommand { get; }
        public ICommand SelectRecentGameInLibraryCommand { get; }
        public ICommand LaunchRecentGameInLibraryCommand { get; }

        public ObservableCollection<FavoriteGameItemViewModel> Favorites => _favorites;
        public bool IsFavoritesEmpty => _favorites.Count == 0;
        public ObservableCollection<RecentGameItemViewModel>? RecentGames => _mainViewModel?.RecentGamesList;
        public bool HasRecentGames => _mainViewModel?.HasRecentGames == true;
        public bool IsRecentGamesEmpty => !HasRecentGames;

        public void NotifyRecentGamesChanged()
        {
            OnPropertyChanged(nameof(RecentGames));
            OnPropertyChanged(nameof(HasRecentGames));
            OnPropertyChanged(nameof(IsRecentGamesEmpty));
        }

        private void AddFavorite(object? param)
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
            else
            {
                long p = AccountLauncherService.ParsePlaceId(_host.PlaceIdInput);
                if (p > 0)
                {
                    pid = p;
                    if (!string.IsNullOrWhiteSpace(_host.CurrentGameName) && !GameInfoService.IsGenericPlaceholderName(p, _host.CurrentGameName))
                        name = _host.CurrentGameName;
                    else if (GameInfoService.TryGetCachedName(p, out string cachedName))
                        name = cachedName;
                    else
                        name = $"Game {p}";
                }
            }

            if (pid <= 0) return;

            if (_favorites.Any(f => f.PlaceId == pid))
            {
                _host.LoadingStatus = LocalizationService.Get("ServerList.GameAlreadyFavorite");
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
            if (GameInfoService.IsGenericPlaceholderName(pid, name) && Avalonia.Application.Current != null)
            {
                _ = Task.Run(async () =>
                {
                    var resolved = await GameInfoService.ResolvePlaceNameAsync(pid).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        AppDispatcher.Post(() =>
                        {
                            item.Name = resolved!;
                            _mainViewModel?.SetKnownGameTitle(pid, resolved);
                            SaveFavorites();
                        });
                    }
                });
            }
            if (string.IsNullOrEmpty(thumb))
                _ = ResolveFavoriteThumbnailsAsync();
            _host.LoadingStatus = LocalizationService.Format("ServerList.AddedToFavorites", name);
        }

        private void RemoveFavorite(object? param)
        {
            if (param is not FavoriteGameItemViewModel fav) return;
            _favorites.Remove(fav);
            SaveFavorites();
            _host.LoadingStatus = LocalizationService.Format("ServerList.RemovedFromFavorites", fav.Name);
        }

        private void SelectFavorite(object? param)
        {
            if (param is not FavoriteGameItemViewModel fav) return;
            _mainViewModel?.SetKnownGameTitle(fav.PlaceId, fav.Name);
            _host.PlaceIdInput = fav.PlaceId.ToString();
            if (_mainViewModel != null)
            {
                _mainViewModel.PlaceId = fav.PlaceId.ToString();
                if (!string.IsNullOrEmpty(fav.PrivateServer))
                    _mainViewModel.JobId = fav.PrivateServer;
                _mainViewModel.StatusMessage = LocalizationService.Format("ServerList.FavoriteGameSelected", fav.Name);
            }
            _host.SelectedSubTabIndex = 0;
            _ = _host.BrowserTab.RefreshServersAsync();
        }

        private Task LaunchFavoriteAsync(object? param)
        {
            if (param is FavoriteGameItemViewModel fav)
            {
                _host.PlaceIdInput = fav.PlaceId.ToString();
                if (_mainViewModel != null)
                {
                    _mainViewModel.PlaceId = fav.PlaceId.ToString();
                    _mainViewModel.JobId = fav.PrivateServer ?? string.Empty;
                    if (_mainViewModel.SelectedCount > 0)
                        _mainViewModel.StatusMessage = LocalizationService.Format("ServerList.LaunchingFavoriteGame", fav.Name);
                    else
                        _host.LoadingStatus = LocalizationService.Get("ServerList.SelectAccountBeforePlay");
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
                    var list = FastJson.TryDeserialize<List<FavoriteGame>>(json, out var fl) ? fl : null;
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
                catch (Exception ex) { _logger.Debug($"[LoadFavorites] {ex.Message}"); }
            }
            OnPropertyChanged(nameof(IsFavoritesEmpty));
            if (_favorites.Count > 0) _ = ResolveFavoriteThumbnailsAsync();
        }

        private void SaveFavorites()
        {
            try
            {
                OnPropertyChanged(nameof(IsFavoritesEmpty));
                string favPath = StoragePaths.GetUserFile("FavoriteGames.json");
                var list = _favorites.Select(f => new FavoriteGame(f.Name, f.PlaceId, f.PrivateServer)).ToList();
                StoragePaths.AtomicWriteText(favPath, FastJson.Serialize(list, indented: true));
            }
            catch (Exception ex) { _logger.Error($"[SaveFavorites] {ex.Message}"); }
        }

        private async Task ResolveFavoriteThumbnailsAsync()
        {
            if (_favorites.Count == 0) return;
            try
            {
                var thumbDict = await GameExplorerTabViewModel.FetchPlaceIconsBatchAsync(_favorites.Select(x => x.PlaceId)).ConfigureAwait(false);
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
            catch (Exception ex) { _logger.Debug($"[ResolveFavoriteThumbnails] {ex.Message}"); }
        }
    }
}
