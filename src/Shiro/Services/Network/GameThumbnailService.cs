#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Shiro.Services
{
    public static class GameThumbnailService
    {
        private static IAppLogger _logger = AppServices.Logger;
        public static IAppLogger Logger
        {
            get => _logger;
            set => _logger = value ?? throw new ArgumentNullException(nameof(value));
        }

        private static readonly LruCache<long, Bitmap> _bitmapCache = new LruCache<long, Bitmap>(256, disposeOnEvict: false);
        private static readonly ConcurrentDictionary<long, Task<Bitmap?>> _inFlightDownloads = new ConcurrentDictionary<long, Task<Bitmap?>>();
        private static readonly SemaphoreSlim _diskDecodeGate = new SemaphoreSlim(6, 6);

        private static string? EnsureThumbnailDirectory()
        {
            try
            {
                string dir = Path.Combine(StoragePaths.CacheDirectory, "GameThumbnails");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[GameThumbnailService] EnsureThumbnailDirectory failed", ex);
                return null;
            }
        }

        public static void ClearCache()
        {
            _bitmapCache.Clear();
            _inFlightDownloads.Clear();
        }

        public static string? GetDiskCachePath(long placeId)
        {
            try
            {
                string dir = Path.Combine(StoragePaths.CacheDirectory, "GameThumbnails");
                return Path.Combine(dir, $"{placeId}.png");
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[GameThumbnailService] GetDiskCachePath failed", ex);
                return null;
            }
        }

        public static bool HasDiskCache(long placeId)
        {
            if (placeId <= 0) return false;
            string? path = GetDiskCachePath(placeId);
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        public static bool TryGetCachedBitmap(long placeId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Bitmap? bitmap)
        {
            if (placeId > 0 && _bitmapCache.TryGetValue(placeId, out bitmap) && bitmap != null)
                return true;

            bitmap = null;
            return false;
        }

        public static async Task<Bitmap?> GetThumbnailBitmapAsync(long placeId, string? cdnUrl = null, CancellationToken cancellationToken = default)
        {
            if (placeId <= 0) return null;
            cancellationToken.ThrowIfCancellationRequested();

            // Fast O(1) memory-only check on caller thread so UI thread never blocks on disk I/O
            if (_bitmapCache.TryGetValue(placeId, out var memCached) && memCached != null)
                return memCached;

            var downloadTask = _inFlightDownloads.GetOrAdd(
                placeId,
                id => Task.Run(() => DownloadAndCacheThumbnailCoreAsync(id, cdnUrl)));
            try
            {
                return await downloadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (downloadTask.IsCompleted)
                {
                    _inFlightDownloads.TryRemove(new KeyValuePair<long, Task<Bitmap?>>(placeId, downloadTask));
                }
                else
                {
                    _ = downloadTask.ContinueWith(
                        completed => _inFlightDownloads.TryRemove(new KeyValuePair<long, Task<Bitmap?>>(placeId, completed)),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
        }

        private static async Task<Bitmap?> DownloadAndCacheThumbnailCoreAsync(long placeId, string? cdnUrl)
        {
            try
            {
                if (_bitmapCache.TryGetValue(placeId, out var memCached) && memCached != null)
                    return memCached;

                string? diskPath = GetDiskCachePath(placeId);
                if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
                {
                    await _diskDecodeGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (_bitmapCache.TryGetValue(placeId, out var raceCached) && raceCached != null)
                            return raceCached;

                        if (File.Exists(diskPath))
                        {
                            using var diskStream = File.OpenRead(diskPath);
                            var diskBmp = Bitmap.DecodeToWidth(diskStream, 96);
                            _bitmapCache.Set(placeId, diskBmp);
                            return diskBmp;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"[GameThumbnailService] Error decoding disk cache for {placeId}: {ex.Message}");
                    }
                    finally
                    {
                        _diskDecodeGate.Release();
                    }
                }

                string? imageUrl = cdnUrl;

                if (string.IsNullOrWhiteSpace(imageUrl))
                {
                    string apiUrl = $"https://thumbnails.roblox.com/v1/places/gameicons?placeIds={placeId}&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false";
                    using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                    using var resp = await SharedHttpClient.SendAsync(req, true).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
                        if (doc.RootElement.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array && dataElem.GetArrayLength() > 0)
                        {
                            if (dataElem[0].TryGetProperty("imageUrl", out var imgProp))
                                imageUrl = imgProp.GetString();
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(imageUrl))
                {
                    byte[]? bytes = await ThumbnailBatchHelper.DownloadValidatedImageAsync(imageUrl, CancellationToken.None).ConfigureAwait(false);
                    if (bytes != null && !string.IsNullOrEmpty(diskPath))
                    {
                        EnsureThumbnailDirectory();
                        StoragePaths.AtomicWriteBytes(diskPath, bytes);

                        await _diskDecodeGate.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            using var memStream = new MemoryStream(bytes);
                            var bmp = Bitmap.DecodeToWidth(memStream, 96);
                            _bitmapCache.Set(placeId, bmp);
                            return bmp;
                        }
                        finally
                        {
                            _diskDecodeGate.Release();
                        }
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.Debug($"[GameThumbnailService] Failed fetching thumbnail for {placeId}: {ex.Message}");
            }

            return null;
        }

        public static Task BatchEnsureThumbnailsAsync(IEnumerable<long> placeIds, CancellationToken cancellationToken = default)
        {
            EnsureThumbnailDirectory();

            return ThumbnailBatchHelper.BatchFetchAndSaveAsync(
                placeIds,
                HasDiskCache,
                GetDiskCachePath,
                static ids => $"https://thumbnails.roblox.com/v1/places/gameicons?placeIds={ids}&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false",
                _logger,
                nameof(GameThumbnailService),
                cancellationToken);
        }
    }
}
