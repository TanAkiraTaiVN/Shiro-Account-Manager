#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Http;

namespace RBX_Alt_Manager.Services
{
    public class RobloxServerQueryService : IRobloxServerQueryService
    {
        private static readonly Lazy<RobloxServerQueryService> _defaultInstance = new(() => new RobloxServerQueryService());
        public static RobloxServerQueryService Default => _defaultInstance.Value;

        private static readonly ConcurrentDictionary<string, string> _geoIpCache = new(StringComparer.OrdinalIgnoreCase);

        private static string GetIpv4SubnetPrefix(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return string.Empty;
            int lastDot = ip.LastIndexOf('.');
            return lastDot > 0 ? ip.Substring(0, lastDot) : ip;
        }

        private static bool TryParseRegionFromJson(string geoJson, out string region)
        {
            region = string.Empty;
            if (string.IsNullOrWhiteSpace(geoJson)) return false;

            using var geoDoc = JsonDocument.Parse(geoJson);
            var geoJs = geoDoc.RootElement;

            if (geoJs.TryGetProperty("success", out var succProp) && succProp.ValueKind == JsonValueKind.False)
                return false;
            if (geoJs.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String &&
                string.Equals(statusProp.GetString(), "fail", StringComparison.OrdinalIgnoreCase))
                return false;

            string city = geoJs.TryGetProperty("city", out var cVal) && cVal.ValueKind == JsonValueKind.String ? (cVal.GetString() ?? "") : "";
            string country = geoJs.TryGetProperty("countryCode", out var ccVal) && ccVal.ValueKind == JsonValueKind.String
                ? (ccVal.GetString() ?? "")
                : (geoJs.TryGetProperty("country_code", out var cc2Val) && cc2Val.ValueKind == JsonValueKind.String
                    ? (cc2Val.GetString() ?? "")
                    : (geoJs.TryGetProperty("country", out var ctryVal) && ctryVal.ValueKind == JsonValueKind.String ? (ctryVal.GetString() ?? "") : ""));

            region = !string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(country)
                ? $"{city}, {country}"
                : (!string.IsNullOrEmpty(country) ? country : city);

            return !string.IsNullOrWhiteSpace(region);
        }

        public async Task<ServerRegionPingResult> QueryServerRegionAndPingAsync(
            Account account,
            long placeId,
            string serverId,
            string? ipApiTemplate = null,
            CancellationToken cancellationToken = default)
        {
            var result = new ServerRegionPingResult();
            if (account == null || placeId <= 0 || string.IsNullOrWhiteSpace(serverId))
            {
                result.Region = "Cần đăng nhập";
                return result;
            }

            try
            {
                var client = AccountManager.GameJoinClient ?? new RestClient("https://gamejoin.roblox.com");
                var request = account.MakeRequest("v1/join-game-instance", Method.Post)
                    .AddHeader("Content-Type", "application/json")
                    .AddJsonBody(new { gameId = serverId, placeId = placeId });

                RestResponse? response = await account.ExecuteAccountRestAsync(client, "https://gamejoin.roblox.com/", request, cancellationToken).ConfigureAwait(false);
                if (response != null && response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
                {
                    using var jsDoc = JsonDocument.Parse(response.Content);
                    var js = jsDoc.RootElement;
                    string? ip = js.TryGetProperty("joinScript", out var jScript) && jScript.TryGetProperty("MachineAddress", out var ma)
                        ? ma.GetString()
                        : null;

                    if (!string.IsNullOrEmpty(ip))
                    {
                        result.Ip = ip;
                        result.Success = true;

                        Task<int?> pingTask = MeasurePingMsAsync(ip);
                        Task<string> geoTask = ResolveGeoIpRegionAsync(ip, ipApiTemplate, cancellationToken);

                        await Task.WhenAll(pingTask, geoTask).ConfigureAwait(false);

                        result.PingMs = await pingTask.ConfigureAwait(false);
                        string resolvedRegion = await geoTask.ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(resolvedRegion))
                            result.Region = resolvedRegion;
                    }
                    else
                    {
                        result.Region = js.TryGetProperty("status", out var stVal) ? (stVal.GetString() ?? "Không rõ") : "Không rõ";
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[QueryServerRegionAndPingAsync] {ex.Message}");
            }

            return result;
        }

        private static async Task<int?> MeasurePingMsAsync(string ip)
        {
            try
            {
                using var pinger = new Ping();
                var reply = await pinger.SendPingAsync(ip, 600).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                    return (int)reply.RoundtripTime;
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[PingServer] {ip}: {ex.Message}");
            }
            return null;
        }

        private static async Task<string> ResolveGeoIpRegionAsync(string ip, string? ipApiTemplate, CancellationToken cancellationToken)
        {
            string subnetKey = GetIpv4SubnetPrefix(ip);
            if (_geoIpCache.TryGetValue(ip, out string? cachedRegion) ||
                (!string.IsNullOrEmpty(subnetKey) && _geoIpCache.TryGetValue(subnetKey, out cachedRegion) && cachedRegion != null))
            {
                return cachedRegion;
            }

            string ipLink = !string.IsNullOrWhiteSpace(ipApiTemplate)
                ? ipApiTemplate
                : (AccountManager.General?.Get<string>("IPApiLink") ?? "https://ipwho.is/<ip>");

            try
            {
                string geoJson = await SharedHttpClient.GetStringAsync(ipLink.Replace("<ip>", ip), retryTransient: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!TryParseRegionFromJson(geoJson, out string resolvedRegion) && !ipLink.Contains("ipwho.is", StringComparison.OrdinalIgnoreCase))
                {
                    string fallbackJson = await SharedHttpClient.GetStringAsync($"https://ipwho.is/{ip}", retryTransient: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                    TryParseRegionFromJson(fallbackJson, out resolvedRegion);
                }

                if (!string.IsNullOrWhiteSpace(resolvedRegion))
                {
                    if (_geoIpCache.Count > 2000)
                    {
                        foreach (string oldKey in _geoIpCache.Keys)
                        {
                            _geoIpCache.TryRemove(oldKey, out _);
                            if (_geoIpCache.Count < 1000)
                                break;
                        }
                    }
                    _geoIpCache[ip] = resolvedRegion;
                    if (!string.IsNullOrEmpty(subnetKey))
                        _geoIpCache[subnetKey] = resolvedRegion;
                    return resolvedRegion;
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[GeoIP] {ip}: {ex.Message}");
            }

            return string.Empty;
        }

        public async Task<RobloxServerListResult> QueryPublicServersAsync(
            long placeId,
            string cursor = "",
            int limit = 100,
            CancellationToken cancellationToken = default)
        {
            var result = new RobloxServerListResult();
            if (placeId <= 0) return result;

            try
            {
                var client = AccountManager.GamesClient ?? new RestClient("https://games.roblox.com");
                string cursorParam = string.IsNullOrEmpty(cursor) ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}";
                string url = $"v1/games/{placeId}/servers/public?sortOrder=Asc&limit={limit}{cursorParam}";
                var request = new RestRequest(url, Method.Get);

                var response = await client.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                if (response != null && response.IsSuccessful && !string.IsNullOrWhiteSpace(response.Content))
                {
                    using var doc = JsonDocument.Parse(response.Content);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("nextPageCursor", out var nextElem) && nextElem.ValueKind == JsonValueKind.String)
                        result.NextPageCursor = nextElem.GetString() ?? string.Empty;

                    if (root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in dataArr.EnumerateArray())
                        {
                            string id = item.TryGetProperty("id", out var idElem) && idElem.ValueKind == JsonValueKind.String ? (idElem.GetString() ?? "") : "";
                            int maxPlayers = item.TryGetProperty("maxPlayers", out var mpElem) && mpElem.TryGetInt32(out int mpVal) ? mpVal : 0;
                            int playing = item.TryGetProperty("playing", out var plElem) && plElem.TryGetInt32(out int plVal) ? plVal : 0;
                            int ping = item.TryGetProperty("ping", out var pingElem) && pingElem.TryGetInt32(out int pingVal) ? pingVal : 0;
                            string fpsStr = item.TryGetProperty("fps", out var fpsElem) ? fpsElem.ToString() : "0";

                            if (!string.IsNullOrEmpty(id))
                            {
                                result.Servers.Add(new ServerData
                                {
                                    id = id,
                                    maxPlayers = maxPlayers,
                                    playing = playing,
                                    ping = ping,
                                    fps = fpsStr
                                });
                            }
                        }
                        result.Success = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[QueryPublicServersAsync] {ex.Message}");
            }

            return result;
        }
    }
}
