#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    /// <summary>
    /// Shared helper for chunked Roblox thumbnail batch API lookups, image download validation,
    /// and atomic disk cache persistence across avatar and game thumbnail services.
    /// </summary>
    internal static class ThumbnailBatchHelper
    {
        internal static bool IsPlausibleImageBytes(byte[]? bytes, string? contentType = null)
        {
            if (bytes == null || bytes.Length == 0)
                return false;

            if (!string.IsNullOrWhiteSpace(contentType))
            {
                if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                    contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                    contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            int idx = 0;
            while (idx < bytes.Length && (bytes[idx] == ' ' || bytes[idx] == '\t' || bytes[idx] == '\r' || bytes[idx] == '\n'))
                idx++;

            if (idx < bytes.Length && (bytes[idx] == '<' || bytes[idx] == '{'))
                return false;

            return HasValidImageMagicHeader(bytes);
        }

        private static bool HasValidImageMagicHeader(ReadOnlySpan<byte> bytes)
        {
            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (bytes.Length >= 8 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
                bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            {
                return true;
            }

            // JPEG: FF D8 FF
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return true;
            }

            // GIF: GIF87a or GIF89a
            if (bytes.Length >= 6 &&
                bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'8' &&
                (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') && bytes[5] == (byte)'a')
            {
                return true;
            }

            // WEBP: RIFF....WEBP
            if (bytes.Length >= 12 &&
                bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
                bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            {
                return true;
            }

            return false;
        }

        internal static async Task<byte[]?> DownloadValidatedImageAsync(string? imageUrl, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
                return null;

            using var imgReq = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            using var imgResp = await SharedHttpClient.SendAsync(imgReq, true, cancellationToken).ConfigureAwait(false);
            if (!imgResp.IsSuccessStatusCode)
                return null;

            string? contentType = imgResp.Content.Headers.ContentType?.MediaType;
            byte[] bytes = await imgResp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return IsPlausibleImageBytes(bytes, contentType) ? bytes : null;
        }

        internal static async Task<List<long>> BatchFetchAndSaveAsync(
            IEnumerable<long>? targetIds,
            Func<long, bool> hasDiskCache,
            Func<long, string?> getDiskPath,
            Func<string, string> buildBatchApiUrl,
            IAppLogger logger,
            string logTag,
            CancellationToken cancellationToken)
        {
            var downloaded = new List<long>();
            if (targetIds == null)
                return downloaded;

            var list = targetIds.Where(id => id > 0 && !hasDiskCache(id)).Distinct().ToList();
            if (list.Count == 0)
                return downloaded;

            for (int i = 0; i < list.Count; i += 100)
            {
                int batchSize = Math.Min(100, list.Count - i);
                var chunk = list.GetRange(i, batchSize);
                try
                {
                    string ids = string.Join(",", chunk);
                    string apiUrl = buildBatchApiUrl(ids);
                    using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                    using var resp = await SharedHttpClient.SendAsync(req, true, cancellationToken).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        continue;

                    using var doc = await JsonDocument.ParseAsync(
                        await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (!doc.RootElement.TryGetProperty("data", out var dataElem) || dataElem.ValueKind != JsonValueKind.Array)
                        continue;

                    var entries = new List<(long TargetId, string ImageUrl)>();
                    foreach (var item in dataElem.EnumerateArray())
                    {
                        long targetId = item.TryGetProperty("targetId", out var idProp) && idProp.TryGetInt64(out long tid) ? tid : 0;
                        string? imageUrl = item.TryGetProperty("imageUrl", out var imgProp) ? imgProp.GetString() : null;
                        if (targetId > 0 && !string.IsNullOrWhiteSpace(imageUrl))
                            entries.Add((targetId, imageUrl));
                    }

                    if (entries.Count == 0)
                        continue;

                    await Parallel.ForEachAsync(
                        entries,
                        new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
                        async (entry, ct) =>
                        {
                            try
                            {
                                byte[]? bytes = await DownloadValidatedImageAsync(entry.ImageUrl, ct).ConfigureAwait(false);
                                string? diskPath = getDiskPath(entry.TargetId);
                                if (bytes != null && !string.IsNullOrEmpty(diskPath))
                                {
                                    StoragePaths.AtomicWriteBytes(diskPath, bytes);
                                    lock (downloaded)
                                    {
                                        downloaded.Add(entry.TargetId);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.Debug($"[{logTag}] Download entry {entry.TargetId} failed: {ex.Message}");
                            }
                        }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.Debug($"[{logTag}] Batch chunk failed: {ex.Message}");
                }
            }

            return downloaded;
        }
    }
}
