using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.ViewModels
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
                    AccountManager.General?.Set("EnablePotatoMode", value ? "true" : "false");
                    ClientSettingsPatcher.PatchSettings();
                    StatusMessage = value
                        ? "🥔 Đã BẬT FastFlags Potato Mode! Game khởi động tiếp theo sẽ siêu nhẹ."
                        : "🥔 Đã TẮT Potato Mode.";
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
                        ? "🛡️ Anti-AFK đã BẬT: Tự động bảo vệ chống kick 20 phút cho tất cả tài khoản!"
                        : "🛡️ Anti-AFK đã TẮT.";
                }
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
                        StatusMessage = $"👻 Đã ẩn {hidden} cửa sổ Roblox (Zero GPU Mode)! Tiết kiệm tối đa điện năng và GPU.";
                    }
                    else
                    {
                        var (restored, _) = RobloxWindowService.ShowAllWindows();
                        StatusMessage = $"👁️ Đã hiển thị lại {restored} cửa sổ Roblox.";
                    }
                }
            }
        }

        private bool _isCommandPaletteOpen;
        public bool IsCommandPaletteOpen
        {
            get => _isCommandPaletteOpen;
            set
            {
                if (SetProperty(ref _isCommandPaletteOpen, value))
                {
                    if (value)
                    {
                        CommandPaletteSearchText = string.Empty;
                        UpdateCommandPaletteResults();
                    }
                }
            }
        }

        private string _commandPaletteSearchText = string.Empty;
        public string CommandPaletteSearchText
        {
            get => _commandPaletteSearchText;
            set
            {
                if (SetProperty(ref _commandPaletteSearchText, value))
                {
                    UpdateCommandPaletteResults();
                }
            }
        }

        private readonly ObservableCollection<AccountViewModel> _commandPaletteResults = new ObservableCollection<AccountViewModel>();
        public ObservableCollection<AccountViewModel> CommandPaletteResults => _commandPaletteResults;

        public void UpdateCommandPaletteResults()
        {
            var searchQuery = AccountSearchQuery.Create(_commandPaletteSearchText);
            _commandPaletteResults.Clear();

            var matches = searchQuery.IsEmpty
                ? _accounts.Take(8)
                : _accounts.Where(searchQuery.IsMatch).Take(8);

            foreach (var item in matches)
            {
                _commandPaletteResults.Add(item);
            }
        }

        public void RefreshStateFromSettings()
        {
            IsAntiAfk = AntiAfkService.IsEnabled;
            AreWindowsHidden = RobloxWindowService.AreWindowsHidden;
            IsPotatoMode = AccountManager.General != null &&
                           AccountManager.General.Exists("EnablePotatoMode") &&
                           AccountManager.General.Get<bool>("EnablePotatoMode");
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

        public string LaunchHomeButtonText => LocalizationService.IsEnglish
            ? $"Launch Home ({SelectedCount})"
            : $"Mở Roblox Home ({SelectedCount})";

        public string JoinServerButtonText => LocalizationService.IsEnglish
            ? $"Join Server ({SelectedCount})"
            : $"Vào Server ({SelectedCount})";

        public string FooterSelectedText => LocalizationService.IsEnglish
            ? $"Selected {SelectedCount} accounts"
            : $"Đã chọn {SelectedCount} tài khoản";

        public string FooterTotalText => LocalizationService.IsEnglish
            ? $"Total: {TotalCount}"
            : $"Tổng số: {TotalCount}";

        public string FooterReadyStatusText => LocalizationService.IsEnglish
            ? "Shiro • Ready"
            : "Shiro • Sẵn sàng";

        public string SubtitleTotalText => LocalizationService.IsEnglish
            ? $"{TotalCount} accounts"
            : $"{TotalCount} tài khoản";

        public string SubtitleReadyText => LocalizationService.IsEnglish
            ? $"{ReadyCount} ready"
            : $"{ReadyCount} sẵn sàng";

        public string SubtitleSelectedText => LocalizationService.IsEnglish
            ? $"{SelectedCount} selected"
            : $"{SelectedCount} đã chọn";

        public string TabAllText => LocalizationService.IsEnglish
            ? $"All ({TotalCount})"
            : $"Tất cả ({TotalCount})";

        public string TabInGameText => LocalizationService.IsEnglish
            ? $"Playing ({InGameCount})"
            : $"Đang chơi ({InGameCount})";

        public string TabReadyText => LocalizationService.IsEnglish
            ? $"Ready ({ReadyCount})"
            : $"Sẵn sàng ({ReadyCount})";

        public string TabExpiredText => LocalizationService.IsEnglish
            ? $"Expired ({ExpiredCount})"
            : $"Hết hạn ({ExpiredCount})";

        public string TabPinnedText => LocalizationService.IsEnglish
            ? $"Pinned ({PinnedCount})"
            : $"Đã ghim ({PinnedCount})";

        public string DeselectCountText => LocalizationService.IsEnglish
            ? $"Deselect ({SelectedCount})"
            : $"Bỏ chọn ({SelectedCount})";

        public void ReloadRecentGames()
        {
            Task.Run(() =>
            {
                try
                {
                    var list = new List<RecentGameItemViewModel>();
                    if (AccountManager.RecentGames != null)
                    {
                        lock (AccountManager.RecentGames)
                        {
                            foreach (var g in AccountManager.RecentGames)
                            {
                                if (g?.Details?.placeId != null)
                                {
                                    list.Add(new RecentGameItemViewModel
                                    {
                                        PlaceId = g.Details.placeId,
                                        Name = string.IsNullOrWhiteSpace(g.Details.name) ? $"Place {g.Details.placeId}" : g.Details.name,
                                        ImageUrl = g.ImageUrl
                                    });
                                }
                            }
                        }
                    }

                    list.Reverse();

                    AppDispatcher.Post(() =>
                    {
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
                            RecentGamesList.Add(item);
                        }
                        OnPropertyChanged(nameof(HasRecentGames));
                    });
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Failed to reload recent games: {ex.Message}");
                }
            });
        }
    }

    public class RecentGameItemViewModel : ObservableObject
    {
        private long _placeId;
        private string _name;
        private string _imageUrl;
        private Avalonia.Media.Imaging.Bitmap _thumbnailBitmap;
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
            set => SetProperty(ref _name, value);
        }

        public string ImageUrl
        {
            get => _imageUrl;
            set
            {
                if (SetProperty(ref _imageUrl, value))
                {
                    _ = LoadThumbnailAsync();
                }
            }
        }

        public Avalonia.Media.Imaging.Bitmap ThumbnailBitmap
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
                Program.Logger?.Debug($"[RecentGameItemViewModel] LoadThumbnail error for {PlaceId}: {ex.Message}");
            }
            finally
            {
                _isLoadingThumbnail = false;
            }
        }

        public string Subtitle => $"Place ID: {PlaceId}";
    }
}
