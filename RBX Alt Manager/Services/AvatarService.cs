using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Services
{
    internal sealed class AvatarService
    {
        private static readonly ConcurrentDictionary<long, Task<string>> InFlightDownloads = new();

        public async Task<byte[]> DownloadAsync(string imageUrl, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
                return null;

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            using HttpResponseMessage response = await SharedHttpClient.SendAsync(request, true, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        private static string EnsureAvatarDirectory()
        {
            string dir = Path.Combine(StoragePaths.CacheDirectory, "Avatars");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }

        public static string GetDiskCachePath(long userId)
        {
            try
            {
                string dir = Path.Combine(StoragePaths.CacheDirectory, "Avatars");
                return Path.Combine(dir, $"{userId}.png");
            }
            catch
            {
                return null;
            }
        }

        public static bool HasDiskCache(long userId)
        {
            if (userId <= 0) return false;
            string path = GetDiskCachePath(userId);
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        public static Task<string> EnsureAvatarAsync(long userId, CancellationToken cancellationToken = default)
        {
            if (userId <= 0) return Task.FromResult<string>(null);

            string diskPath = GetDiskCachePath(userId);
            if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
                return Task.FromResult(diskPath);

            return InFlightDownloads.GetOrAdd(userId, id => DownloadAvatarCoreAsync(id, diskPath, cancellationToken));
        }

        private static async Task<string> DownloadAvatarCoreAsync(long userId, string diskPath, CancellationToken cancellationToken)
        {
            try
            {
                if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
                    return diskPath;

                string apiUrl = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size=150x150&format=Png&isCircular=false";
                using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                using var resp = await SharedHttpClient.SendAsync(req, true, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;

                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
                string imageUrl = null;
                if (doc.RootElement.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array && dataElem.GetArrayLength() > 0)
                {
                    if (dataElem[0].TryGetProperty("imageUrl", out var imgProp))
                        imageUrl = imgProp.GetString();
                }

                if (string.IsNullOrWhiteSpace(imageUrl)) return null;

                using var imgReq = new HttpRequestMessage(HttpMethod.Get, imageUrl);
                using var imgResp = await SharedHttpClient.SendAsync(imgReq, true, cancellationToken).ConfigureAwait(false);
                if (!imgResp.IsSuccessStatusCode) return null;

                byte[] bytes = await imgResp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes != null && bytes.Length > 0 && !string.IsNullOrEmpty(diskPath))
                {
                    EnsureAvatarDirectory();
                    StoragePaths.AtomicWriteBytes(diskPath, bytes);
                    return diskPath;
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"EnsureAvatarHeadshotAsync {userId} failed: {ex.Message}");
            }
            finally
            {
                InFlightDownloads.TryRemove(userId, out _);
            }

            return null;
        }

        public static async Task<List<long>> BatchEnsureAvatarsAsync(IEnumerable<long> userIds, CancellationToken cancellationToken = default)
        {
            var downloaded = new List<long>();
            if (userIds == null) return downloaded;

            var list = userIds.Where(id => id > 0 && !HasDiskCache(id)).Distinct().ToList();
            if (list.Count == 0) return downloaded;

            try { EnsureAvatarDirectory(); }
            catch (Exception ex) { Program.Logger?.Debug($"EnsureAvatarDirectory failed: {ex.Message}"); }

            for (int i = 0; i < list.Count; i += 100)
            {
                int batchSize = Math.Min(100, list.Count - i);
                var chunk = list.GetRange(i, batchSize);
                try
                {
                    string ids = string.Join(",", chunk);
                    string apiUrl = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={ids}&size=150x150&format=Png&isCircular=false";
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
                            string imageUrl = item.TryGetProperty("imageUrl", out var imgProp) ? imgProp.GetString() : null;

                            if (targetId > 0 && !string.IsNullOrWhiteSpace(imageUrl))
                            {
                                entries.Add((targetId, imageUrl));
                            }
                        }

                        if (entries.Count > 0)
                        {
                            using var downloadGate = new SemaphoreSlim(8, 8);
                            var tasks = entries.Select(async entry =>
                            {
                                await downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                                try
                                {
                                    using var imgReq = new HttpRequestMessage(HttpMethod.Get, entry.ImageUrl);
                                    using var imgResp = await SharedHttpClient.SendAsync(imgReq, true, cancellationToken).ConfigureAwait(false);
                                    if (imgResp.IsSuccessStatusCode)
                                    {
                                        byte[] bytes = await imgResp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                                        string diskPath = GetDiskCachePath(entry.TargetId);
                                        if (bytes != null && bytes.Length > 0 && !string.IsNullOrEmpty(diskPath))
                                        {
                                            StoragePaths.AtomicWriteBytes(diskPath, bytes);
                                            lock (downloaded)
                                            {
                                                downloaded.Add(entry.TargetId);
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Program.Logger?.Debug($"Download avatar entry failed: {ex.Message}");
                                }
                                finally
                                {
                                    downloadGate.Release();
                                }
                            }).ToArray();

                            await Task.WhenAll(tasks).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Batch avatar chunk fetch failed: {ex.Message}");
                }
            }

            return downloaded;
        }
    }
}
