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
using Shiro.Classes;

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
            if (_bitmapCache.TryGetValue(placeId, out bitmap) && bitmap != null)
                return true;

            string? diskPath = GetDiskCachePath(placeId);
            if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
            {
                try
                {
                    using var stream = File.OpenRead(diskPath);
                    bitmap = Bitmap.DecodeToWidth(stream, 96);
                    _bitmapCache.Set(placeId, bitmap);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[GameThumbnailService] Error decoding cache for {placeId}: {ex.Message}");
                }
            }

            bitmap = null;
            return false;
        }

        public static async Task<Bitmap?> GetThumbnailBitmapAsync(long placeId, string? cdnUrl = null, CancellationToken cancellationToken = default)
        {
            if (placeId <= 0) return null;

            // Fast O(1) memory-only check on caller thread so UI thread never blocks on disk I/O
            if (_bitmapCache.TryGetValue(placeId, out var memCached) && memCached != null)
                return memCached;

            var downloadTask = _inFlightDownloads.GetOrAdd(placeId, id => DownloadAndCacheThumbnailCoreAsync(id, cdnUrl));
            try
            {
                return await downloadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (downloadTask.IsCompleted)
                    _inFlightDownloads.TryRemove(new KeyValuePair<long, Task<Bitmap?>>(placeId, downloadTask));
            }
        }

        private static async Task<Bitmap?> DownloadAndCacheThumbnailCoreAsync(long placeId, string? cdnUrl)
        {
            await Task.Yield();
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
                    using var imgReq = new HttpRequestMessage(HttpMethod.Get, imageUrl);
                    using var imgResp = await SharedHttpClient.SendAsync(imgReq, true).ConfigureAwait(false);
                    if (imgResp.IsSuccessStatusCode)
                    {
                        byte[] bytes = await imgResp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        if (bytes != null && bytes.Length > 0 && !string.IsNullOrEmpty(diskPath))
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
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[GameThumbnailService] Failed fetching thumbnail for {placeId}: {ex.Message}");
            }
            finally
            {
                _inFlightDownloads.TryRemove(placeId, out _);
            }

            return null;
        }

        public static async Task BatchEnsureThumbnailsAsync(IEnumerable<long> placeIds, CancellationToken cancellationToken = default)
        {
            if (placeIds == null) return;
            var list = placeIds.Where(id => id > 0 && !HasDiskCache(id)).Distinct().ToList();
            if (list.Count == 0) return;

            EnsureThumbnailDirectory();

            for (int i = 0; i < list.Count; i += 100)
            {
                int batchSize = Math.Min(100, list.Count - i);
                var chunk = list.GetRange(i, batchSize);
                try
                {
                    string ids = string.Join(",", chunk);
                    string apiUrl = $"https://thumbnails.roblox.com/v1/places/gameicons?placeIds={ids}&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false";
                    using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                    using var resp = await SharedHttpClient.SendAsync(req, true, cancellationToken).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) continue;

                    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (doc.RootElement.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array)
                    {
                        var entries = new List<(long TargetId, string ImageUrl)>();
                        foreach (var item in dataElem.EnumerateArray())
                        {
                            long targetId = item.TryGetProperty("targetId", out var idProp) && idProp.TryGetInt64(out long tid) ? tid : 0;
                            string? imageUrl = item.TryGetProperty("imageUrl", out var imgProp) ? imgProp.GetString() : null;

                            if (targetId > 0 && !string.IsNullOrWhiteSpace(imageUrl))
                            {
                                entries.Add((targetId, imageUrl));
                            }
                        }

                        if (entries.Count > 0)
                        {
                            await Parallel.ForEachAsync(
                                entries,
                                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
                                async (entry, ct) =>
                                {
                                    await DownloadBatchThumbnailItemAsync(entry.TargetId, entry.ImageUrl, ct).ConfigureAwait(false);
                                }).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Batch game thumbnail chunk failed: {ex.Message}");
                }
            }
        }

        private static async Task DownloadBatchThumbnailItemAsync(long targetId, string imageUrl, CancellationToken cancellationToken)
        {
            try
            {
                using var imgReq = new HttpRequestMessage(HttpMethod.Get, imageUrl);
                using var imgResp = await SharedHttpClient.SendAsync(imgReq, true, cancellationToken).ConfigureAwait(false);
                if (imgResp.IsSuccessStatusCode)
                {
                    byte[] bytes = await imgResp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    string? diskPath = GetDiskCachePath(targetId);
                    if (bytes != null && bytes.Length > 0 && !string.IsNullOrEmpty(diskPath))
                    {
                        StoragePaths.AtomicWriteBytes(diskPath, bytes);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Download game thumbnail item {targetId} failed: {ex.Message}");
            }
        }
    }
}
