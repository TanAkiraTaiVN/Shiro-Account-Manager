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

namespace Shiro.Services
{
    internal sealed class AvatarService
    {
        private static IAppLogger _logger = AppServices.Logger;
        public static IAppLogger Logger
        {
            get => _logger;
            set => _logger = value ?? throw new ArgumentNullException(nameof(value));
        }

        private static readonly ConcurrentDictionary<long, Task<string?>> InFlightDownloads = new();

        public Task<byte[]?> DownloadAsync(string? imageUrl, CancellationToken cancellationToken = default(CancellationToken)) =>
            ThumbnailBatchHelper.DownloadValidatedImageAsync(imageUrl, cancellationToken);

        private static string EnsureAvatarDirectory()
        {
            string dir = Path.Combine(StoragePaths.CacheDirectory, "Avatars");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }

        public static string? GetDiskCachePath(long userId)
        {
            try
            {
                string dir = Path.Combine(StoragePaths.CacheDirectory, "Avatars");
                return Path.Combine(dir, $"{userId}.png");
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[AvatarService] GetDiskCachePath failed", ex);
                return null;
            }
        }

        public static bool HasDiskCache(long userId)
        {
            if (userId <= 0) return false;
            string? path = GetDiskCachePath(userId);
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        public static Task<string?> EnsureAvatarAsync(long userId, CancellationToken cancellationToken = default)
        {
            if (userId <= 0) return Task.FromResult<string?>(null);
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<string?>(cancellationToken);

            string? diskPath = GetDiskCachePath(userId);
            if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
                return Task.FromResult<string?>(diskPath);

            Task<string?> downloadTask = InFlightDownloads.GetOrAdd(
                userId,
                id => DownloadAvatarCoreAsync(id, diskPath, CancellationToken.None));

            return cancellationToken.CanBeCanceled
                ? downloadTask.WaitAsync(cancellationToken)
                : downloadTask;
        }

        private static async Task<string?> DownloadAvatarCoreAsync(long userId, string? diskPath, CancellationToken cancellationToken)
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
                string? imageUrl = null;
                if (doc.RootElement.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array && dataElem.GetArrayLength() > 0)
                {
                    if (dataElem[0].TryGetProperty("imageUrl", out var imgProp))
                        imageUrl = imgProp.GetString();
                }

                if (string.IsNullOrWhiteSpace(imageUrl)) return null;

                byte[]? bytes = await ThumbnailBatchHelper.DownloadValidatedImageAsync(imageUrl, cancellationToken).ConfigureAwait(false);
                if (bytes != null && !string.IsNullOrEmpty(diskPath))
                {
                    EnsureAvatarDirectory();
                    StoragePaths.AtomicWriteBytes(diskPath, bytes);
                    return diskPath;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"EnsureAvatarHeadshotAsync {userId} failed: {ex.Message}");
            }
            finally
            {
                InFlightDownloads.TryRemove(userId, out _);
            }

            return null;
        }

        public static async Task<List<long>> BatchEnsureAvatarsAsync(IEnumerable<long> userIds, CancellationToken cancellationToken = default)
        {
            try { EnsureAvatarDirectory(); }
            catch (Exception ex) { _logger.Debug($"EnsureAvatarDirectory failed: {ex.Message}"); }

            return await ThumbnailBatchHelper.BatchFetchAndSaveAsync(
                userIds,
                HasDiskCache,
                GetDiskCachePath,
                static ids => $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={ids}&size=150x150&format=Png&isCircular=false",
                _logger,
                nameof(AvatarService),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
