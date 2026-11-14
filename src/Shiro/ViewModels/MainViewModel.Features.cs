#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private ObservableCollection<RecentGameItemViewModel> _recentGamesList = new ObservableCollection<RecentGameItemViewModel>();
        public ObservableCollection<RecentGameItemViewModel> RecentGamesList
        {
            get => _recentGamesList;
            set
            {
                if (SetProperty(ref _recentGamesList, value))
                {
                    OnPropertyChanged(nameof(HasRecentGames));
                    ServerListViewModel?.NotifyRecentGamesChanged();
                }
            }
        }

        public bool HasRecentGames => RecentGamesList != null && RecentGamesList.Count > 0;

        public bool IsPotatoMode
        {
            get => _isPotatoMode;
            set
            {
                if (SetProperty(ref _isPotatoMode, value))
                {
                    _settings.Set("EnablePotatoMode", value ? "true" : "false");
                    ClientSettingsPatcher.PatchSettings();
                    StatusMessage = value
                        ? Shiro.Services.LocalizationService.Get("Main.PotatoModeActivated")
                        : Shiro.Services.LocalizationService.Get("Main.PotatoModeDeactivated");
                    NotifyToolStatesChanged();
                }
            }
        }

        public bool IsAntiAfk
        {
            get => _isAntiAfk;
            set
            {
                if (SetProperty(ref _isAntiAfk, value))
                {
                    AntiAfkService.SetConfig(value);
                    StatusMessage = value
                        ? Shiro.Services.LocalizationService.Get("Main.AntiAfkActivated")
                        : Shiro.Services.LocalizationService.Get("Main.AntiAfkDeactivated");
                    NotifyToolStatesChanged();
                }
            }
        }

        public bool IsStreamerModeActive => StreamerModeService.IsActive;

        public bool IsStreamerModeEnabled
        {
            get => StreamerModeService.IsEnabled;
            set
            {
                if (StreamerModeService.IsEnabled != value)
                {
                    StreamerModeService.IsEnabled = value;
                    NotifyToolStatesChanged();
                    StatusMessage = value
                        ? Shiro.Services.LocalizationService.Get("Main.StreamerModeActivated")
                        : Shiro.Services.LocalizationService.Get("MainWindow.StreamerModeDisabled");
                    RefreshAllAccountDisplays();
                }
            }
        }

        public void RefreshAllAccountDisplays()
        {
            if (Accounts == null) return;
            foreach (var acc in Accounts)
            {
                acc.NotifyDisplayNameChanged();
                acc.NotifyRobuxChanged();
            }
        }

        public bool AreWindowsHidden
        {
            get => _areWindowsHidden;
            set
            {
                if (SetProperty(ref _areWindowsHidden, value))
                {
                    if (value)
                    {
                        var (hidden, _) = RobloxWindowService.HideAllWindows();
                        StatusMessage = Shiro.Services.LocalizationService.Format("Main.ZeroGpuActivatedCount", hidden);
                    }
                    else
                    {
                        var (restored, _) = RobloxWindowService.ShowAllWindows();
                        StatusMessage = Shiro.Services.LocalizationService.Format("Main.RestoredRobloxWindows", restored);
                    }
                    NotifyToolStatesChanged();
                }
            }
        }

        public void RefreshStateFromSettings()
        {
            IsAntiAfk = AntiAfkService.IsEnabled;
            AreWindowsHidden = RobloxWindowService.AreWindowsHidden;
            IsPotatoMode = _settings != null &&
                           _settings.Exists("EnablePotatoMode") &&
                           _settings.Get<bool>("EnablePotatoMode");
            NotifyToolStatesChanged();
        }

        private void NotifyLocalizedLabelsChanged()
        {
            OnPropertyChanged(nameof(SubtitleTotalText));
            OnPropertyChanged(nameof(SubtitleReadyText));
            OnPropertyChanged(nameof(SubtitleSelectedText));
            OnPropertyChanged(nameof(TabAllText));
            OnPropertyChanged(nameof(TabInGameText));
            OnPropertyChanged(nameof(TabReadyText));
            OnPropertyChanged(nameof(TabExpiredText));
            OnPropertyChanged(nameof(TabBannedText));
            OnPropertyChanged(nameof(TabPinnedText));
            OnPropertyChanged(nameof(DeselectCountText));
            OnPropertyChanged(nameof(LaunchHomeButtonText));
            OnPropertyChanged(nameof(JoinServerButtonText));
            OnPropertyChanged(nameof(FooterSelectedText));
            OnPropertyChanged(nameof(FooterTotalText));
            OnPropertyChanged(nameof(FooterReadyStatusText));
        }

        public string CloseRobloxButtonText
        {
            get
            {
                if (_selectedCount > 0)
                {
                    string template = Loc.Get("Nav_CloseRobloxSelected");
                    if (!string.IsNullOrEmpty(template) && template.Contains("{0}"))
                        return string.Format(template, _selectedCount);
                    return $"{Loc.Get("Nav_CloseRoblox")} ({_selectedCount})";
                }
                return _runningRobloxProcessCount > 0
                    ? $"{Loc.Get("Nav_CloseRoblox")} ({_runningRobloxProcessCount})"
                    : Loc.Get("Nav_CloseRoblox");
            }
        }

        public string LaunchHomeButtonText => LocalizationService.Format("Main.OpenRobloxHomeAction", SelectedCount);

        public string JoinServerButtonText => LocalizationService.Format("Main.JoinServerAction", SelectedCount);

        public string FooterSelectedText => LocalizationService.Format("Main.AccountsSelectedCount", SelectedCount);

        public string FooterTotalText => LocalizationService.Format("Main.TotalAccountsCount", TotalCount);

        public string FooterReadyStatusText => LocalizationService.Get("Main.ShiroReadyStatus");

        public string SubtitleTotalText => LocalizationService.Format("About.AccountsCount", TotalCount);

        public string SubtitleReadyText => LocalizationService.Format("Main.ReadyAccountsCount", ReadyCount);

        public string SubtitleSelectedText => LocalizationService.Format("Main.SelectedCountShort", SelectedCount);

        public string TabAllText => LocalizationService.Format("Main.TabAllCount", TotalCount);

        public string TabInGameText => LocalizationService.Format("Main.TabPlayingCount", InGameCount);

        public string TabReadyText => LocalizationService.Format("Main.TabReadyCount", ReadyCount);

        public string TabExpiredText => LocalizationService.Format("Main.TabExpiredCount", ExpiredCount);

        public string TabBannedText => LocalizationService.Format("Main.TabBannedCount", BannedCount);

        public string TabPinnedText => LocalizationService.Format("Main.TabPinnedCount", PinnedCount);

        public string DeselectCountText => LocalizationService.Format("Main.DeselectCountAction", SelectedCount);

        public void ReloadRecentGames()
        {
            if (System.Threading.Volatile.Read(ref _disposed) != 0)
                return;

            Task.Run(() =>
            {
                try
                {
                    if (System.Threading.Volatile.Read(ref _disposed) != 0)
                        return;

                    var list = new List<RecentGameItemViewModel>();
                    var recent = AppServices.ServerBrowserState.RecentGames;
                    if (recent != null)
                    {
                        lock (recent)
                        {
                            foreach (var g in recent)
                            {
                                if (g?.Details?.placeId != null)
                                {
                                    string bestName = g.Details.BestDisplayName;
                                    list.Add(new RecentGameItemViewModel
                                    {
                                        PlaceId = g.Details.placeId,
                                        Name = string.IsNullOrWhiteSpace(bestName) ? $"Place {g.Details.placeId}" : bestName,
                                        ImageUrl = g.ImageUrl ?? string.Empty
                                    });
                                }
                            }
                        }
                    }

                    list.Reverse();

                    AppDispatcher.Post(() =>
                    {
                        if (System.Threading.Volatile.Read(ref _disposed) != 0)
                            return;

                        if (RecentGamesList.Count == list.Count)
                        {
                            bool same = true;
                            for (int i = 0; i < list.Count; i++)
                            {
                                if (RecentGamesList[i].PlaceId != list[i].PlaceId)
                                {
                                    same = false;
                                    break;
                                }
                            }
                            if (same) return;
                        }

                        RecentGamesList.Clear();
                        foreach (var item in list)
                        {
                            if (!GameInfoService.IsGenericPlaceholderName(item.PlaceId, item.Name))
                            {
                                GameInfoService.CachePlaceName(item.PlaceId, item.Name);
                            }
                            else if (GameInfoService.TryGetCachedName(item.PlaceId, out string cachedName))
                            {
                                item.Name = cachedName;
                            }
                            else if (Avalonia.Application.Current != null)
                            {
                                var targetItem = item;
                                _ = Task.Run(async () =>
                                {
                                    var resolved = await GameInfoService.ResolvePlaceNameAsync(targetItem.PlaceId).ConfigureAwait(false);
                                    if (!string.IsNullOrWhiteSpace(resolved) && System.Threading.Volatile.Read(ref _disposed) == 0)
                                    {
                                        AppDispatcher.Post(() =>
                                        {
                                            if (System.Threading.Volatile.Read(ref _disposed) != 0)
                                                return;
                                            targetItem.Name = resolved!;
                                            SetKnownGameTitle(targetItem.PlaceId, resolved);
                                        });
                                    }
                                });
                            }
                            RecentGamesList.Add(item);
                        }
                        OnPropertyChanged(nameof(HasRecentGames));
                        ServerListViewModel?.NotifyRecentGamesChanged();
                        if (string.IsNullOrWhiteSpace(CurrentGameName) && !string.IsNullOrWhiteSpace(_placeId))
                        {
                            ScheduleCurrentGameLookup(_placeId);
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Failed to reload recent games: {ex.Message}");
                }
            });
        }
    }

    public class RecentGameItemViewModel : ObservableObject
    {
        private long _placeId;
        private string _name = string.Empty;
        private string _imageUrl = string.Empty;
        private Avalonia.Media.Imaging.Bitmap? _thumbnailBitmap;
        private bool _isLoadingThumbnail;

        public long PlaceId
        {
            get => _placeId;
            set
            {
                if (SetProperty(ref _placeId, value))
                {
                    OnPropertyChanged(nameof(Subtitle));
                    _ = LoadThumbnailAsync();
                }
            }
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value ?? string.Empty);
        }

        public string ImageUrl
        {
            get => _imageUrl;
            set
            {
                if (SetProperty(ref _imageUrl, value ?? string.Empty))
                {
                    _ = LoadThumbnailAsync();
                }
            }
        }

        public Avalonia.Media.Imaging.Bitmap? ThumbnailBitmap
        {
            get
            {
                if (_thumbnailBitmap == null && !_isLoadingThumbnail && PlaceId > 0)
                {
                    _ = LoadThumbnailAsync();
                }
                return _thumbnailBitmap;
            }
            private set => SetProperty(ref _thumbnailBitmap, value);
        }

        public async Task LoadThumbnailAsync()
        {
            if (PlaceId <= 0 || _isLoadingThumbnail) return;
            if (GameThumbnailService.TryGetCachedBitmap(PlaceId, out var cached))
            {
                ThumbnailBitmap = cached;
                return;
            }

            _isLoadingThumbnail = true;
            try
            {
                var bmp = await GameThumbnailService.GetThumbnailBitmapAsync(PlaceId, ImageUrl).ConfigureAwait(false);
                if (bmp != null)
                {
                    AppDispatcher.Post(() => ThumbnailBitmap = bmp);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[RecentGameItemViewModel] LoadThumbnail error for {PlaceId}: {ex.Message}");
            }
            finally
            {
                _isLoadingThumbnail = false;
            }
        }

        public string Subtitle => $"Place ID: {PlaceId}";
    }
}
