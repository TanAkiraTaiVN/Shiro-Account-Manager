#nullable enable
using System;
using System.Text.Json;

namespace Shiro.Services;

public sealed record ReleaseInfo(
    string Tag,
    Version Version,
    string ZipUrl,
    long Size,
    string? ExpectedSha256 = null,
    string? ChecksumsUrl = null,
    string AssetName = "Shiro-win-x64.zip");

public static class ReleaseInfoParser
{
    private const string Sha256Prefix = "sha256:";

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

            string? zipName = null;
            string? zipUrl = null;
            long zipSize = 0;
            string? expectedSha256 = null;
            string? checksumsUrl = null;

            foreach (var asset in assetsElem.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                    continue;

                string? name = asset.TryGetProperty("name", out var nElem) && nElem.ValueKind == JsonValueKind.String
                    ? nElem.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                string? downloadUrl = asset.TryGetProperty("browser_download_url", out var dlElem) && dlElem.ValueKind == JsonValueKind.String
                    ? dlElem.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(downloadUrl))
                    continue;

                if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    checksumsUrl ??= downloadUrl;
                    continue;
                }

                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (zipUrl == null || string.Equals(name, "Shiro-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipName = name;
                    zipUrl = downloadUrl;
                    zipSize = asset.TryGetProperty("size", out var sElem) && sElem.TryGetInt64(out long sVal) ? sVal : 0;
                    expectedSha256 = TryParseGitHubDigest(asset);
                }
            }

            if (zipUrl != null && zipName != null)
            {
                return new ReleaseInfo(cleanTag, remoteVer, zipUrl, zipSize, expectedSha256, checksumsUrl, zipName);
            }
        }
        catch (Exception ex)
        {
            AppServices.Logger.Debug($"[ReleaseInfoParser.TryGetNewer] {ex.Message}");
        }

        return null;
    }

    private static string? TryParseGitHubDigest(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out var digestElem) || digestElem.ValueKind != JsonValueKind.String)
            return null;

        string? raw = digestElem.GetString()?.Trim();
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        ReadOnlySpan<char> hex = raw.AsSpan(Sha256Prefix.Length).Trim();
        if (hex.Length != 64)
            return null;

        foreach (char c in hex)
        {
            if (!char.IsAsciiHexDigit(c))
                return null;
        }

        return hex.ToString().ToLowerInvariant();
    }
}
