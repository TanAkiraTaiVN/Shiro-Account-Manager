#nullable enable
using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public class GameSearchItemViewModel : ObservableObject
    {
        private long _placeId;
        private string _name = "Game";
        private int _playerCount;
        private string _thumbnailUrl = string.Empty;
        private Bitmap? _thumbnailBitmap;
        private bool _isLoadingThumbnail;

        public long PlaceId
        {
            get => _placeId;
            set
            {
                if (SetProperty(ref _placeId, value))
                {
                    _ = LoadThumbnailAsync();
                }
            }
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value ?? string.Empty);
        }

        public int PlayerCount
        {
            get => _playerCount;
            set
            {
                if (SetProperty(ref _playerCount, value))
                    OnPropertyChanged(nameof(PlayerCountDisplay));
            }
        }

        public string PlayerCountDisplay => PlayerCount > 0 ? Shiro.Services.LocalizationService.Format("Auto_GameSearchItemVi_359", PlayerCount) : "Roblox Experience";

        public string ThumbnailUrl
        {
            get => _thumbnailUrl;
            set
            {
                if (SetProperty(ref _thumbnailUrl, value ?? string.Empty))
                {
                    _ = LoadThumbnailAsync();
                }
            }
        }

        public Bitmap? ThumbnailBitmap
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
                var bmp = await GameThumbnailService.GetThumbnailBitmapAsync(PlaceId, ThumbnailUrl).ConfigureAwait(false);
                if (bmp != null)
                {
                    AppDispatcher.Post(() => ThumbnailBitmap = bmp);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[GameSearchItemViewModel] LoadThumbnail error for {PlaceId}: {ex.Message}");
            }
            finally
            {
                _isLoadingThumbnail = false;
            }
        }
    }
}
