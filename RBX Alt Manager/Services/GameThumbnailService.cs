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
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.Services
{
    public static class GameThumbnailService
    {
        private static readonly LruCache<long, Bitmap> _bitmapCache = new LruCache<long, Bitmap>(256);
        private static readonly ConcurrentDictionary<long, Task<Bitmap>> _inFlightDownloads = new ConcurrentDictionary<long, Task<Bitmap>>();

        private static string EnsureThumbnailDirectory()
        {
            try
            {
                string dir = Path.Combine(StoragePaths.CacheDirectory, "GameThumbnails");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                return null;
            }
        }

        public static void ClearCache()
        {
            _bitmapCache.Clear();
        }

        public static string GetDiskCachePath(long placeId)
        {
            try
            {
                string dir = Path.Combine(StoragePaths.CacheDirectory, "GameThumbnails");
                return Path.Combine(dir, $"{placeId}.png");
            }
            catch
            {
                return null;
            }
        }

        public static bool HasDiskCache(long placeId)
        {
            if (placeId <= 0) return false;
            string path = GetDiskCachePath(placeId);
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        public static bool TryGetCachedBitmap(long placeId, out Bitmap bitmap)
        {
            if (_bitmapCache.TryGetValue(placeId, out bitmap))
                return true;

            string diskPath = GetDiskCachePath(placeId);
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
                    Program.Logger?.Debug($"[GameThumbnailService] Error decoding cache for {placeId}: {ex.Message}");
                }
            }

            bitmap = null;
            return false;
        }

        public static async Task<Bitmap> GetThumbnailBitmapAsync(long placeId, string cdnUrl = null, CancellationToken cancellationToken = default)
        {
            if (placeId <= 0) return null;

            if (TryGetCachedBitmap(placeId, out var cached))
                return cached;

            var downloadTask = _inFlightDownloads.GetOrAdd(placeId, id => DownloadAndCacheThumbnailCoreAsync(id, cdnUrl));
            try
            {
                return await downloadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (downloadTask.IsCompleted)
                    _inFlightDownloads.TryRemove(new KeyValuePair<long, Task<Bitmap>>(placeId, downloadTask));
            }
        }

        private static async Task<Bitmap> DownloadAndCacheThumbnailCoreAsync(long placeId, string cdnUrl)
        {
            try
            {
                string diskPath = GetDiskCachePath(placeId);
                string imageUrl = cdnUrl;

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

                            using var memStream = new MemoryStream(bytes);
                            var bmp = Bitmap.DecodeToWidth(memStream, 96);
                            _bitmapCache.Set(placeId, bmp);
                            return bmp;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[GameThumbnailService] Failed fetching thumbnail for {placeId}: {ex.Message}");
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
                        using var downloadGate = new SemaphoreSlim(8, 8);
                        var tasks = new List<Task>();
                        foreach (var item in dataElem.EnumerateArray())
                        {
                            long targetId = item.TryGetProperty("targetId", out var idProp) && idProp.TryGetInt64(out long tid) ? tid : 0;
                            string imageUrl = item.TryGetProperty("imageUrl", out var imgProp) ? imgProp.GetString() : null;

                            if (targetId > 0 && !string.IsNullOrWhiteSpace(imageUrl))
                            {
                                tasks.Add(DownloadBatchThumbnailItemAsync(targetId, imageUrl, downloadGate, cancellationToken));
                            }
                        }
                        await Task.WhenAll(tasks).ConfigureAwait(false);
                    }
                }
                catch { }
            }
        }

        private static async Task DownloadBatchThumbnailItemAsync(long targetId, string imageUrl, SemaphoreSlim downloadGate, CancellationToken cancellationToken)
        {
            await downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var imgReq = new HttpRequestMessage(HttpMethod.Get, imageUrl);
                using var imgResp = await SharedHttpClient.SendAsync(imgReq, true, cancellationToken).ConfigureAwait(false);
                if (imgResp.IsSuccessStatusCode)
                {
                    byte[] bytes = await imgResp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    string diskPath = GetDiskCachePath(targetId);
                    if (bytes != null && bytes.Length > 0 && !string.IsNullOrEmpty(diskPath))
                    {
                        StoragePaths.AtomicWriteBytes(diskPath, bytes);
                    }
                }
            }
            catch { }
            finally
            {
                try { downloadGate.Release(); } catch { }
            }
        }
    }
}
