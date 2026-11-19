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

            return true;
        }

        internal static async Task<byte[]?> DownloadValidatedImageAsync(string imageUrl, CancellationToken cancellationToken)
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
