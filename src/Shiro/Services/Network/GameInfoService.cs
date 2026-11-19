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

        internal static Func<HttpClient>? HttpClientFactoryForTests { get; set; }

        private static async Task<HttpResponseMessage> SendHttpAsync(HttpRequestMessage req, CancellationToken ct = default)
        {
            if (HttpClientFactoryForTests != null)
                return await HttpClientFactoryForTests().SendAsync(req, ct).ConfigureAwait(false);
            return await SharedHttpClient.SendAsync(req, retryTransient: false, ct).ConfigureAwait(false);
        }

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
                lock (_recentGamesLock)
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
                lock (_recentGamesLock)
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
                _ = queryTask.ContinueWith(
                    static (t, state) => _inFlightNameQueries.TryRemove(new KeyValuePair<long, Task<string?>>((long)state!, t)),
                    placeId,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
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
            long universeId = 0;
            try
            {
                // 1. Resolve PlaceId -> UniverseId once, then query games.roblox.com
                string uniUrl = $"https://apis.roblox.com/universes/v1/places/{placeId}/universe";
                using var uniReq = new HttpRequestMessage(HttpMethod.Get, uniUrl);
                using var uniResp = await SendHttpAsync(uniReq).ConfigureAwait(false);
                if (uniResp.IsSuccessStatusCode)
                {
                    using var uniDoc = await JsonDocument.ParseAsync(await uniResp.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
                    if (uniDoc.RootElement.TryGetProperty("universeId", out var uniProp) &&
                        uniProp.TryGetInt64(out long resolvedUniId) &&
                        resolvedUniId > 0)
                    {
                        universeId = resolvedUniId;
                        string gameUrl = $"https://games.roblox.com/v1/games?universeIds={universeId}";
                        using var gameReq = new HttpRequestMessage(HttpMethod.Get, gameUrl);
                        gameReq.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                        using var gameResp = await SendHttpAsync(gameReq).ConfigureAwait(false);
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

            if (universeId <= 0)
                return null;

            // 2. Fallback to unauthenticated develop universe lookup reusing resolved universeId
            try
            {
                string devUrl = $"https://develop.roblox.com/v1/universes/{universeId}";
                using var devReq = new HttpRequestMessage(HttpMethod.Get, devUrl);
                using var devResp = await SendHttpAsync(devReq).ConfigureAwait(false);
                if (devResp.IsSuccessStatusCode)
                {
                    using var devDoc = await JsonDocument.ParseAsync(await devResp.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
                    if (devDoc.RootElement.TryGetProperty("name", out var nameProp) &&
                        nameProp.ValueKind == JsonValueKind.String)
                    {
                        string? devTitle = SanitizeDisplayTitle(nameProp.GetString());
                        if (!string.IsNullOrWhiteSpace(devTitle) && !IsGenericPlaceholderName(placeId, devTitle))
                            return devTitle;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.Debug($"[GameInfoService] Develop universe fallback for {placeId} failed: {ex.Message}");
            }
            catch (JsonException ex)
            {
                _logger.Debug($"[GameInfoService] Develop universe JSON parse for {placeId} failed: {ex.Message}");
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

                int effectiveMax = Math.Max(1, maxRecentGames);
                while (AppServices.ServerBrowserState.RecentGames.Count >= effectiveMax && AppServices.ServerBrowserState.RecentGames.Count > 0)
                {
                    AppServices.ServerBrowserState.RecentGames.RemoveAt(0);
                }

                AppServices.ServerBrowserState.RecentGames.Add(rg);

                if (!loading)
                {
                    serialized = FastJson.Serialize(AppServices.ServerBrowserState.RecentGames, indented: true);
                    if (!string.IsNullOrEmpty(serialized))
                        StoragePaths.AtomicWriteText(recentGamesFilePath, serialized);
                }
            }

            if (!loading)
            {
                onGameAdded?.Invoke(rg);
            }
        }

        private static readonly string[] ClientVersionEndpoints =
        [
            "https://clientsettings.roblox.com/v2/client-version/WindowsPlayer",
            "https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer"
        ];

        public static async Task LoadClientVersionAsync(CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            foreach (string endpoint in ClientVersionEndpoints)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                    using var resp = await SendHttpAsync(req, cancellationToken).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        continue;

                    string versionJson = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var vDoc = JsonDocument.Parse(versionJson);
                    if (vDoc.RootElement.TryGetProperty("clientVersionUpload", out var token) &&
                        token.GetString() is { Length: > 0 } version)
                    {
                        AppServices.ServerBrowserState.CurrentClientVersion = version;
                        return;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.Debug($"Roblox client version probe failed for {endpoint}: {exception.Message}");
                }
            }
        }
    }
}
