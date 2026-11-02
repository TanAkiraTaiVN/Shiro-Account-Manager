#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Shiro.Classes;

namespace Shiro.Services
{
    /// <summary>
    /// Resolves and caches human-readable Roblox Experience titles and icons for any Place ID.
    /// Powers the live Game Info Chip next to Place ID inputs in Dashboard and Server List views.
    /// </summary>
    public static class GameInfoService
    {
        private static readonly ConcurrentDictionary<long, string> _placeNameCache = new();
        private static readonly ConcurrentDictionary<long, Task<string?>> _inFlightNameQueries = new();

        public static void CachePlaceName(long placeId, string? name)
        {
            if (placeId <= 0 || string.IsNullOrWhiteSpace(name))
                return;

            string trimmed = name.Trim();
            // Do not overwrite a real title with a generic placeholder like "Place 123" or "Game 123"
            if (IsGenericPlaceholderName(placeId, trimmed) && _placeNameCache.ContainsKey(placeId))
                return;

            _placeNameCache[placeId] = trimmed;
        }

        public static bool IsGenericPlaceholderName(long placeId, string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return true;

            string trimmed = name.Trim();
            return string.Equals(trimmed, $"Place {placeId}", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, $"Game {placeId}", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "Favorite Game", StringComparison.OrdinalIgnoreCase);
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

            var recentGames = AppSettings.RecentGames;
            if (recentGames != null)
            {
                lock (recentGames)
                {
                    for (int i = recentGames.Count - 1; i >= 0; i--)
                    {
                        var rg = recentGames[i];
                        if (rg?.Details != null && rg.Details.placeId == placeId)
                        {
                            string candidate = rg.Details.BestDisplayName;
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
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    _placeNameCache[placeId] = resolved;
                    return resolved;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[GameInfoService] ResolvePlaceNameAsync({placeId}) failed: {ex.Message}");
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
                string? sourceTitle = sourceProp.GetString();
                if (!string.IsNullOrWhiteSpace(sourceTitle))
                    return sourceTitle.Trim();
            }

            if (gameElement.TryGetProperty("name", out var nameProp) &&
                nameProp.ValueKind == JsonValueKind.String)
            {
                string? title = nameProp.GetString();
                if (!string.IsNullOrWhiteSpace(title))
                    return title.Trim();
            }

            return null;
        }

        private static async Task<string?> FetchPlaceNameFromRobloxAsync(long placeId)
        {
            try
            {
                // Public unauthenticated 2-step lookup: PlaceId -> UniverseId -> Original Game Name (sourceName)
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
                                if (!string.IsNullOrWhiteSpace(bestTitle))
                                    return bestTitle;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[GameInfoService] Public universe lookup for {placeId} failed: {ex.Message}");
            }

            return null;
        }
    }
}
