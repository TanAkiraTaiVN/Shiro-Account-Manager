#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Shiro.Services
{
    /// <summary>
    /// Resolves and caches human-readable Roblox Experience titles and icons for any Place ID.
    /// Powers the live Game Info Chip next to Place ID inputs in Dashboard and Server List views.
    /// </summary>
    public static class GameInfoService
    {
        private static IAppLogger _logger = AppServices.Logger;
        public static IAppLogger Logger
        {
            get => _logger;
            set => _logger = value ?? throw new ArgumentNullException(nameof(value));
        }

        private static readonly ConcurrentDictionary<long, string> _placeNameCache = new();
        private static readonly ConcurrentDictionary<long, Task<string?>> _inFlightNameQueries = new();

        /// <summary>
        /// Strips supplementary surrogate-pair characters (e.g. color emojis like 🎉) and variation selectors
        /// that trigger a native SkiaSharp SKStream.Read Access Violation (0xc0000005) on Windows when Avalonia
        /// attempts to synthesize SemiBold/Bold glyphs on Segoe UI Emoji (seguiemj.ttf).
        /// </summary>
        public static string SanitizeDisplayTitle(string? rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle))
                return string.Empty;

            var sb = new StringBuilder(rawTitle.Length);
            bool lastWasSpace = false;

            for (int i = 0; i < rawTitle.Length; i++)
            {
                char c = rawTitle[i];

                // Skip UTF-16 surrogate pairs (U+10000..U+10FFFF emojis/pictographs) and variation selectors / zero-width joiners
                if (char.IsSurrogate(c) ||
                    (c >= '\uFE00' && c <= '\uFE0F') ||
                    c == '\u200D' ||
                    c == '\u20E3')
                {
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace && sb.Length > 0)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }
                    continue;
                }

                // Avoid leaving leading space right after an opening bracket when an emoji like "[🎉UPD 11]" is stripped
                if (lastWasSpace && sb.Length > 0 && (sb[^1] == '[' || sb[^1] == '(' || sb[^1] == '{'))
                {
                    sb.Length--;
                }

                sb.Append(c);
                lastWasSpace = false;
            }

            return sb.ToString().Trim();
        }

        public static void CachePlaceName(long placeId, string? name)
        {
            if (placeId <= 0 || string.IsNullOrWhiteSpace(name))
                return;

            string trimmed = SanitizeDisplayTitle(name);
            // Do not cache or overwrite a real title with a generic placeholder like "Unknown", "[TITLE UNAVAILABLE]", "Place 123", etc.
            if (IsGenericPlaceholderName(placeId, trimmed))
                return;

            _placeNameCache[placeId] = trimmed;
            UpdateRecentGamesCachedTitle(placeId, trimmed);
        }

        private static void UpdateRecentGamesCachedTitle(long placeId, string resolvedTitle)
        {
            try
            {
                var recentGames = AppServices.ServerBrowserState.RecentGames;
                if (recentGames == null) return;

                bool updated = false;
                string? serialized = null;
                lock (recentGames)
                {
                    foreach (var rg in recentGames)
                    {
                        if (rg?.Details != null && rg.Details.placeId == placeId &&
                            IsGenericPlaceholderName(placeId, rg.Details.BestDisplayName))
                        {
                            rg.Details.name = resolvedTitle;
                            rg.Details.sourceName = resolvedTitle;
                            updated = true;
                        }
                    }

                    if (updated)
                    {
                        serialized = FastJson.Serialize(recentGames, indented: true);
                    }
                }

                if (updated && !string.IsNullOrEmpty(serialized))
                {
                    StoragePaths.AtomicWriteText(StoragePaths.RecentGamesFile, serialized);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[GameInfoService] UpdateRecentGamesCachedTitle({placeId}) failed: {ex.Message}");
            }
        }

        public static bool IsGenericPlaceholderName(long placeId, string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return true;

            string trimmed = name.Trim();
            return string.Equals(trimmed, $"Place {placeId}", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, $"Game {placeId}", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "Favorite Game", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "???", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "[TITLE UNAVAILABLE]", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "[ Content Deleted ]", StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryGetCachedName(long placeId, out string name)
        {
            name = string.Empty;
            if (placeId <= 0)
                return false;

            if (_placeNameCache.TryGetValue(placeId, out string? cached) &&
                !string.IsNullOrWhiteSpace(cached) &&
                !IsGenericPlaceholderName(placeId, cached))
            {
                name = cached;
                return true;
            }

            var recentGames = AppServices.ServerBrowserState.RecentGames;
            if (recentGames != null)
            {
                lock (recentGames)
                {
                    for (int i = recentGames.Count - 1; i >= 0; i--)
                    {
                        var rg = recentGames[i];
                        if (rg?.Details != null && rg.Details.placeId == placeId)
                        {
                            string candidate = SanitizeDisplayTitle(rg.Details.BestDisplayName);
                            if (!string.IsNullOrWhiteSpace(candidate) && !IsGenericPlaceholderName(placeId, candidate))
                            {
                                name = candidate;
                                _placeNameCache[placeId] = name;
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        public static async Task<string?> ResolvePlaceNameAsync(long placeId, CancellationToken cancellationToken = default)
        {
            if (placeId <= 0)
                return null;

            if (TryGetCachedName(placeId, out string cached))
                return cached;

            var queryTask = _inFlightNameQueries.GetOrAdd(placeId, static id => FetchPlaceNameFromRobloxAsync(id));
            try
            {
                string? resolved = await queryTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(resolved) && !IsGenericPlaceholderName(placeId, resolved))
                {
                    CachePlaceName(placeId, resolved);
                    return resolved;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Debug($"[GameInfoService] ResolvePlaceNameAsync({placeId}) failed: {ex.Message}");
            }
            finally
            {
                if (queryTask.IsCompleted)
                    _inFlightNameQueries.TryRemove(placeId, out _);
            }

            return null;
        }

        public static async Task<(string? Name, Bitmap? Thumbnail)> ResolveGameInfoAsync(
            long placeId,
            CancellationToken cancellationToken = default)
        {
            if (placeId <= 0)
                return (null, null);

            var nameTask = ResolvePlaceNameAsync(placeId, cancellationToken);
            var thumbTask = GameThumbnailService.GetThumbnailBitmapAsync(placeId, cancellationToken: cancellationToken);

            await Task.WhenAll(nameTask, thumbTask).ConfigureAwait(false);
            return (await nameTask.ConfigureAwait(false), await thumbTask.ConfigureAwait(false));
        }

        public static string? ExtractBestGameTitle(JsonElement gameElement)
        {
            if (gameElement.ValueKind != JsonValueKind.Object)
                return null;

            if (gameElement.TryGetProperty("sourceName", out var sourceProp) &&
                sourceProp.ValueKind == JsonValueKind.String)
            {
                string? sourceTitle = SanitizeDisplayTitle(sourceProp.GetString());
                if (!string.IsNullOrWhiteSpace(sourceTitle) && !IsGenericPlaceholderName(0, sourceTitle))
                    return sourceTitle;
            }

            if (gameElement.TryGetProperty("name", out var nameProp) &&
                nameProp.ValueKind == JsonValueKind.String)
            {
                string? title = SanitizeDisplayTitle(nameProp.GetString());
                if (!string.IsNullOrWhiteSpace(title) && !IsGenericPlaceholderName(0, title))
                    return title;
            }

            if (gameElement.TryGetProperty("Name", out var upperNameProp) &&
                upperNameProp.ValueKind == JsonValueKind.String)
            {
                string? upperTitle = SanitizeDisplayTitle(upperNameProp.GetString());
                if (!string.IsNullOrWhiteSpace(upperTitle) && !IsGenericPlaceholderName(0, upperTitle))
                    return upperTitle;
            }

            return null;
        }

        private static async Task<string?> FetchPlaceNameFromRobloxAsync(long placeId)
        {
            try
            {
                // 1. Public unauthenticated 2-step lookup: PlaceId -> UniverseId -> Original Game Name (sourceName)
                string uniUrl = $"https://apis.roblox.com/universes/v1/places/{placeId}/universe";
                using var uniReq = new HttpRequestMessage(HttpMethod.Get, uniUrl);
                using var uniResp = await SharedHttpClient.SendAsync(uniReq, retryTransient: false).ConfigureAwait(false);
                if (uniResp.IsSuccessStatusCode)
                {
                    using var uniDoc = await JsonDocument.ParseAsync(await uniResp.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
                    if (uniDoc.RootElement.TryGetProperty("universeId", out var uniProp) &&
                        uniProp.TryGetInt64(out long universeId) &&
                        universeId > 0)
                    {
                        string gameUrl = $"https://games.roblox.com/v1/games?universeIds={universeId}";
                        using var gameReq = new HttpRequestMessage(HttpMethod.Get, gameUrl);
                        gameReq.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                        using var gameResp = await SharedHttpClient.SendAsync(gameReq, retryTransient: false).ConfigureAwait(false);
                        if (gameResp.IsSuccessStatusCode)
                        {
                            using var gameDoc = await JsonDocument.ParseAsync(await gameResp.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
                            if (gameDoc.RootElement.TryGetProperty("data", out var dataArr) &&
                                dataArr.ValueKind == JsonValueKind.Array &&
                                dataArr.GetArrayLength() > 0)
                            {
                                string? bestTitle = ExtractBestGameTitle(dataArr[0]);
                                if (!string.IsNullOrWhiteSpace(bestTitle) && !IsGenericPlaceholderName(placeId, bestTitle))
                                    return bestTitle;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[GameInfoService] Public universe lookup for {placeId} failed: {ex.Message}");
            }

            // 2. Fallback to economy asset details API (works without authentication even when games.roblox.com returns [TITLE UNAVAILABLE] for sub-places / restricted experiences)
            try
            {
                string assetUrl = $"https://economy.roblox.com/v2/assets/{placeId}/details";
                using var assetReq = new HttpRequestMessage(HttpMethod.Get, assetUrl);
                using var assetResp = await SharedHttpClient.SendAsync(assetReq, retryTransient: false).ConfigureAwait(false);
                if (assetResp.IsSuccessStatusCode)
                {
                    using var assetDoc = await JsonDocument.ParseAsync(await assetResp.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
                    string? assetTitle = ExtractBestGameTitle(assetDoc.RootElement);
                    if (!string.IsNullOrWhiteSpace(assetTitle) && !IsGenericPlaceholderName(placeId, assetTitle))
                        return assetTitle;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[GameInfoService] Economy asset details fallback for {placeId} failed: {ex.Message}");
            }

            return null;
        }

        private static readonly System.Threading.Lock _recentGamesLock = new();

        public static async Task LoadRecentGamesAsync(Action<Game>? onGameAdded = null)
        {
            lock (_recentGamesLock)
            {
                AppServices.ServerBrowserState.RecentGames = new List<Game>();
            }

            string recentGamesFilePath = StoragePaths.RecentGamesFile;
            if (File.Exists(recentGamesFilePath))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(recentGamesFilePath).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        List<Game>? games = FastJson.TryDeserialize<List<Game>>(json, out var gList) ? gList : null;
                        if (games != null && games.Count > 0)
                        {
                            await Task.WhenAll(games.Where(g => g != null).Select(g => g.WaitForDetails())).ConfigureAwait(false);
                            foreach (Game rg in games)
                            {
                                if (rg != null)
                                    await AddRecentGameAsync(rg, loading: true, onGameAdded).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to load recent games: {ex.Message}");
                }
            }
        }

        public static async Task AddRecentGameAsync(Game rg, bool loading = false, Action<Game>? onGameAdded = null)
        {
            if (rg == null) return;
            await rg.WaitForDetails().ConfigureAwait(false);

            int maxRecentGames = AppServices.GeneralSettings.Exists("MaxRecentGames")
                ? AppServices.GeneralSettings.GetInt("MaxRecentGames", 10)
                : 10;
            string? serialized = null;
            string recentGamesFilePath = StoragePaths.RecentGamesFile;

            lock (_recentGamesLock)
            {
                AppServices.ServerBrowserState.RecentGames ??= new List<Game>();
                AppServices.ServerBrowserState.RecentGames.RemoveAll(g => g?.Details?.placeId == rg.Details?.placeId);

                while (AppServices.ServerBrowserState.RecentGames.Count > maxRecentGames && AppServices.ServerBrowserState.RecentGames.Count > 0)
                {
                    AppServices.ServerBrowserState.RecentGames.RemoveAt(0);
                }

                AppServices.ServerBrowserState.RecentGames.Add(rg);

                if (!loading)
                {
                    serialized = FastJson.Serialize(AppServices.ServerBrowserState.RecentGames, indented: true);
                    StoragePaths.AtomicWriteText(recentGamesFilePath, serialized);
                }
            }

            if (!loading)
            {
                onGameAdded?.Invoke(rg);
            }
        }

        public static async Task LoadClientVersionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                string versionJson = await SharedHttpClient.GetStringAsync(
                    "https://clientsettings.roblox.com/v1/client-version/WindowsPlayer",
                    retryTransient: true,
                    cancellationToken: cancellationToken);

                using var vDoc = JsonDocument.Parse(versionJson);
                if (vDoc.RootElement.TryGetProperty("clientVersionUpload", out var token))
                    AppServices.ServerBrowserState.CurrentClientVersion = token.GetString() ?? string.Empty;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown cancelled the optional version probe.
            }
            catch (Exception exception)
            {
                _logger.Debug($"Roblox client version probe failed: {exception.Message}");
            }
        }
    }
}
