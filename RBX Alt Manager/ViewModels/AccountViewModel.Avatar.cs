#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.ViewModels
{
    public partial class AccountViewModel
    {
        private const int MaxAvatarBitmapCacheEntries = 512;
        private static readonly ConcurrentDictionary<long, byte> _requestedAvatarDownloads = new ConcurrentDictionary<long, byte>();
        private static readonly LruCache<long, Bitmap> _avatarBitmapCache = new LruCache<long, Bitmap>(MaxAvatarBitmapCacheEntries);
        private string? _avatarUrl;

        public static void ClearAvatarMemoryCache()
        {
            _avatarBitmapCache.Clear();
            _requestedAvatarDownloads.Clear();
        }

        public Bitmap? AvatarImage
        {
            get
            {
                if (UserId <= 0) return null;
                if (_avatarBitmapCache.TryGetValue(UserId, out var cached))
                    return cached;

                string diskPath = AvatarUrl;
                if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
                {
                    try
                    {
                        using var stream = File.OpenRead(diskPath);
                        var bmp = Bitmap.DecodeToWidth(stream, 72);
                        _avatarBitmapCache[UserId] = bmp;
                        return bmp;
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"[AvatarImage] Error decoding avatar for {UserId}: {ex.Message}");
                    }
                }
                return null;
            }
        }

        public string AvatarUrl
        {
            get
            {
                if (_avatarUrl != null) return _avatarUrl;
                if (UserId <= 0) return string.Empty;
                try
                {
                    string diskPath = Path.Combine(StoragePaths.CacheDirectory, "Avatars", $"{UserId}.png");
                    if (File.Exists(diskPath))
                    {
                        _avatarUrl = diskPath;
                        return _avatarUrl;
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[AvatarUrl] Could not read cached avatar for {UserId}: {ex.Message}");
                }

                // Automatically trigger download in background if missing from disk
                if (_requestedAvatarDownloads.TryAdd(UserId, 0))
                {
                    _ = FetchAndCacheAvatarAsync();
                }

                return string.Empty;
            }
        }

        public async Task<string> FetchAndCacheAvatarAsync()
        {
            try
            {
                string? path = await AvatarService.EnsureAvatarAsync(UserId).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(path))
                {
                    _avatarUrl = path;
                    _avatarBitmapCache.TryRemove(UserId, out _);
                    AppDispatcher.Post(() =>
                    {
                        OnPropertyChanged(nameof(AvatarUrl));
                        OnPropertyChanged(nameof(AvatarImage));
                    });
                    return path;
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[FetchAndCacheAvatarAsync] Could not fetch avatar for {UserId}: {ex.Message}");
            }
            finally
            {
                // Remove from tracking if avatar was not retrieved so retry is possible
                if (string.IsNullOrEmpty(_avatarUrl))
                {
                    _requestedAvatarDownloads.TryRemove(UserId, out _);
                }
            }
            return string.Empty;
        }

        public void InvalidateAvatar()
        {
            _avatarUrl = null;
            _avatarBitmapCache.TryRemove(UserId, out _);
            _requestedAvatarDownloads.TryRemove(UserId, out _);
            OnPropertyChanged(nameof(AvatarUrl));
            OnPropertyChanged(nameof(AvatarImage));
        }
    }
}
