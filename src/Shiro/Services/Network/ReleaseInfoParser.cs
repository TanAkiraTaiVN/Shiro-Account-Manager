#nullable enable
using System;
using System.Text.Json;

namespace Shiro.Services;

public sealed record ReleaseInfo(string Tag, Version Version, string ZipUrl, long Size);

public static class ReleaseInfoParser
{
    public static ReleaseInfo? TryGetNewer(string json, Version current)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string? tagName = root.TryGetProperty("tag_name", out var tagElem) && tagElem.ValueKind == JsonValueKind.String
                ? tagElem.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(tagName))
                return null;

            string cleanTag = tagName.TrimStart('v', 'V').Trim();
            if (!Version.TryParse(cleanTag, out var remoteVer) || remoteVer <= current)
                return null;

            if (!root.TryGetProperty("assets", out var assetsElem) || assetsElem.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var asset in assetsElem.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                    continue;

                string? name = asset.TryGetProperty("name", out var nElem) && nElem.ValueKind == JsonValueKind.String
                    ? nElem.GetString()
                    : null;
                if (string.IsNullOrEmpty(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? downloadUrl = asset.TryGetProperty("browser_download_url", out var dlElem) && dlElem.ValueKind == JsonValueKind.String
                    ? dlElem.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(downloadUrl))
                    continue;

                long size = asset.TryGetProperty("size", out var sElem) && sElem.TryGetInt64(out long sVal) ? sVal : 0;
                return new ReleaseInfo(cleanTag, remoteVer, downloadUrl, size);
            }
        }
        catch (Exception ex)
        {
            AppServices.Logger.Debug($"[ReleaseInfoParser.TryGetNewer] {ex.Message}");
        }

        return null;
    }
}
