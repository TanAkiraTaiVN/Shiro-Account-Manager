#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Http;

namespace RBX_Alt_Manager.Services
{
    public static class RobloxPlaceHelper
    {
        public static double ToRobloxTick(this DateTime date) =>
            (date <= DateTime.UnixEpoch)
                ? 0
                : (((date - DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerSecond) + ((double)date.Millisecond / 1000));

        public static double MapValue(double input, double il, double ih, double ol, double oh) =>
            (input - il) / (ih - il) * (oh - ol) + ol;

        public static async Task<string> GetRandomJobId(
            long placeId,
            bool chooseLowestServer = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!chooseLowestServer && AccountManager.General != null && AccountManager.General.Exists("ShuffleChoosesLowestServer"))
            {
                try { chooseLowestServer = AccountManager.General.Get<bool>("ShuffleChoosesLowestServer"); }
                catch (Exception ex) { Program.Logger?.Debug($"Failed reading ShuffleChoosesLowestServer setting: {ex.Message}"); }
            }

            var validServers = new List<string>();
            int stopAt = Math.Max(AccountManager.General?.Get<int>("ShufflePageCount") ?? 1, 1);
            int pageCount = 0;
            string cursor = string.Empty;

            while (pageCount < stopAt)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pageCount++;

                string cursorParam = string.IsNullOrEmpty(cursor) ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}";
                var request = new RestRequest($"v1/games/{placeId}/servers/public?sortOrder=Asc&limit=100{cursorParam}", Method.Get);

                RestResponse? response = null;
                if (AccountManager.GamesClient != null)
                    response = await AccountManager.GamesClient.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

                if (response == null || !response.IsSuccessful || string.IsNullOrWhiteSpace(response.Content))
                    break;

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(response.Content);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != System.Text.Json.JsonValueKind.Array)
                        break;

                    cursor = root.TryGetProperty("nextPageCursor", out var nextCursorElem) && nextCursorElem.ValueKind == System.Text.Json.JsonValueKind.String
                        ? (nextCursorElem.GetString() ?? string.Empty)
                        : string.Empty;

                    foreach (var a in dataArr.EnumerateArray())
                    {
                        int playing = a.TryGetProperty("playing", out var p) && p.TryGetInt32(out int pVal) ? pVal : 0;
                        int maxPlayers = a.TryGetProperty("maxPlayers", out var m) && m.TryGetInt32(out int mVal) ? mVal : 0;
                        if (playing > 0 && maxPlayers > 1 && playing < maxPlayers &&
                            a.TryGetProperty("id", out var idElem) && idElem.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            string? sId = idElem.GetString();
                            if (!string.IsNullOrEmpty(sId))
                            {
                                if (chooseLowestServer)
                                    return sId;

                                validServers.Add(sId);
                            }
                        }
                    }
                }
                catch
                {
                    break;
                }

                if (string.IsNullOrEmpty(cursor) || chooseLowestServer)
                    break;
            }

            if (validServers.Count == 0 && AccountManager.CachedServers != null && AccountManager.CachedServers.Count > 0)
            {
                foreach (var s in AccountManager.CachedServers)
                {
                    if (s != null && !string.IsNullOrEmpty(s.id) && s.playing > 0 && s.maxPlayers > 1 && s.playing < s.maxPlayers)
                    {
                        validServers.Add(s.id);
                    }
                }
            }

            if (validServers.Count == 0) return string.Empty;

            return validServers[chooseLowestServer ? 0 : Random.Shared.Next(validServers.Count)];
        }
    }
}
