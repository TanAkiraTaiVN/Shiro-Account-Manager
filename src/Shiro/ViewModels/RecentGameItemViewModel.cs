#nullable enable
using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public class RecentGameItemViewModel : ObservableObject
    {
        private long _placeId;
        private string _name = string.Empty;
        private string _imageUrl = string.Empty;
        private Bitmap? _thumbnailBitmap;
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
            if (PlaceId <= 0 || _isLoadingThumbnail)
                return;

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
