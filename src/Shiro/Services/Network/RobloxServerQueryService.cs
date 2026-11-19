#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;

namespace Shiro.Services
{
    public class RobloxServerQueryService : IRobloxServerQueryService
    {
        internal static Func<System.Net.Http.HttpClient>? TestHttpClientFactory { get; set; }
        internal static Func<string, Task<int?>>? TestPingProbe { get; set; }
        internal static Func<string, CancellationToken, Task<string>>? TestGeoHttpGetString { get; set; }

        private static readonly Lazy<RobloxServerQueryService> _defaultInstance = new(() => new RobloxServerQueryService(AppServices.Logger, AppServices.GeneralSettings));
        public static RobloxServerQueryService Default => _defaultInstance.Value;

        private readonly IAppLogger _logger;
        private readonly ISettingsService _settings;

        public RobloxServerQueryService() : this(AppServices.Logger, AppServices.GeneralSettings) { }

        public RobloxServerQueryService(IAppLogger logger, ISettingsService settings)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

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

        internal static bool TryValidatePublicTargetIpAndGeoUrl(string ip, string url, out IPAddress? parsedIp)
        {
            parsedIp = null;
            if (string.IsNullOrWhiteSpace(ip) ||
                !IPAddress.TryParse(ip.Trim(), out IPAddress? candidate) ||
                !IsPublicIpAddress(candidate))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (IPAddress.TryParse(uri.DnsSafeHost, out IPAddress? hostIp) && !IsPublicIpAddress(hostIp))
                return false;

            parsedIp = candidate.IsIPv4MappedToIPv6 ? candidate.MapToIPv4() : candidate;
            return true;
        }

        private static bool IsPublicIpAddress(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            if (IPAddress.IsLoopback(address))
                return false;

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                Span<byte> b = stackalloc byte[4];
                address.TryWriteBytes(b, out _);
                return b[0] != 0 &&
                       b[0] != 10 &&
                       b[0] != 127 &&
                       !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) &&
                       !(b[0] == 169 && b[1] == 254) &&
                       !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
                       !(b[0] == 192 && b[1] == 168) &&
                       b[0] < 224;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return !address.Equals(IPAddress.IPv6Any) &&
                       !address.IsIPv6LinkLocal &&
                       !address.IsIPv6Multicast &&
                       !address.IsIPv6SiteLocal &&
                       !address.IsIPv6UniqueLocal;
            }

            return false;
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
                result.Region = Shiro.Services.LocalizationService.Get("ServerQuery.FetchingRegion");
                return result;
            }

            try
            {
                var client = AppServices.RobloxApi.GameJoin;
                RestResponse? response = await RobloxCsrfRetryHelper.ExecuteWithCsrfRetryAsync(
                    account,
                    client,
                    "https://gamejoin.roblox.com/",
                    initialCsrfToken: null,
                    tok =>
                    {
                        var req = account.MakeRequest("v1/join-game-instance", Method.Post)
                            .AddHeader("Content-Type", "application/json")
                            .AddJsonBody(new JoinGameInstancePayload(serverId, placeId));
                        if (!string.IsNullOrEmpty(tok))
                            req.AddHeader("X-CSRF-TOKEN", tok);
                        return req;
                    },
                    cancellationToken).ConfigureAwait(false);
                if (response != null && response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
                {
                    using var jsDoc = JsonDocument.Parse(response.Content);
                    var js = jsDoc.RootElement;
                    string? ip = js.TryGetProperty("joinScript", out var jScript) && jScript.TryGetProperty("MachineAddress", out var ma)
                        ? ma.GetString()
                        : null;

                    if (!string.IsNullOrEmpty(ip) &&
                        TryValidatePublicTargetIpAndGeoUrl(ip, "https://ipwho.is/", out _))
                    {
                        result.Ip = ip;
                        result.Success = true;

                        int? dcId = null;
                        if (jScript.TryGetProperty("DataCenterId", out var dcVal) && dcVal.TryGetInt32(out int id1))
                            dcId = id1;
                        else if (js.TryGetProperty("dataCenterId", out var dcVal2) && dcVal2.TryGetInt32(out int id2))
                            dcId = id2;

                        Task<int?> pingTask = MeasurePingMsAsync(ip, cancellationToken);

                        // Check offline datacenter resolution first to avoid rate-limiting
                        if (RobloxDatacenterService.Default.TryResolve(ip, dcId, out var dcResolution) && dcResolution != null)
                        {
                            result.Region = dcResolution.FormattedRegion;
                            result.CountryCode = dcResolution.CountryCode;
                            result.FlagEmoji = dcResolution.FlagEmoji;

                            result.PingMs = await pingTask.ConfigureAwait(false);
                            // If direct ping failed, try fallback sample ping IP from datacenter
                            if (!result.PingMs.HasValue && !string.IsNullOrWhiteSpace(dcResolution.SamplePingIp) && !string.Equals(dcResolution.SamplePingIp, ip, StringComparison.OrdinalIgnoreCase))
                            {
                                result.PingMs = await MeasurePingMsAsync(dcResolution.SamplePingIp, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            Task<string> geoTask = ResolveGeoIpRegionAsync(ip, ipApiTemplate, cancellationToken);
                            await Task.WhenAll(pingTask, geoTask).ConfigureAwait(false);

                            result.PingMs = await pingTask.ConfigureAwait(false);
                            string resolvedRegion = await geoTask.ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(resolvedRegion))
                            {
                                result.Region = resolvedRegion;
                                int commaIdx = resolvedRegion.LastIndexOf(',');
                                if (commaIdx > 0 && commaIdx < resolvedRegion.Length - 1)
                                {
                                    string countryPart = resolvedRegion[(commaIdx + 1)..].Trim();
                                    if (countryPart.Length == 2)
                                    {
                                        result.CountryCode = countryPart.ToUpperInvariant();
                                        result.FlagEmoji = RobloxDatacenterService.GetCountryFlagEmoji(result.CountryCode);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        string unknown = Shiro.Services.LocalizationService.Get("ServerQuery.UnknownRegion");
                        result.Region = js.TryGetProperty("status", out var stVal)
                            ? (stVal.ValueKind == JsonValueKind.String ? (stVal.GetString() ?? unknown) : $"{unknown} ({stVal})")
                            : unknown;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[QueryServerRegionAndPingAsync] {ex.Message}");
            }

            return result;
        }

        internal static async Task<int?> RacePingProbesAsync(
            Func<CancellationToken, Task<int?>> icmpProbe,
            Func<CancellationToken, Task<int?>> tcpProbe,
            CancellationToken cancellationToken = default)
        {
            using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<int?> icmpTask = SafeRunProbeAsync(icmpProbe, raceCts.Token);
            Task<int?> tcpTask = SafeRunProbeAsync(tcpProbe, raceCts.Token);

            Task<int?> firstCompleted = await Task.WhenAny(icmpTask, tcpTask).ConfigureAwait(false);
            int? firstResult = await firstCompleted.ConfigureAwait(false);
            Task<int?> secondTask = ReferenceEquals(firstCompleted, icmpTask) ? tcpTask : icmpTask;
            if (firstResult.HasValue)
            {
                SafeExecuteHelper.Try(raceCts.Cancel);
                await secondTask.ConfigureAwait(false);
                return firstResult;
            }

            return await secondTask.ConfigureAwait(false);
        }

        private static async Task<int?> SafeRunProbeAsync(Func<CancellationToken, Task<int?>> probe, CancellationToken ct)
        {
            try
            {
                return await probe(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[RobloxServerQueryService] SafeRunProbeAsync failed", ex);
                return null;
            }
        }

        private async Task<int?> MeasurePingMsAsync(string ip, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ip) ||
                !TryValidatePublicTargetIpAndGeoUrl(ip, "https://ipwho.is/", out _))
            {
                return null;
            }
            if (TestPingProbe != null) return await TestPingProbe(ip).ConfigureAwait(false);

            return await RacePingProbesAsync(
                icmpProbe: async ct =>
                {
                    try
                    {
                        using var pinger = new Ping();
                        var reply = await pinger.SendPingAsync(ip, TimeSpan.FromMilliseconds(250), cancellationToken: ct).ConfigureAwait(false);
                        if (reply.Status == IPStatus.Success)
                            return (int)reply.RoundtripTime;
                    }
                    catch (OperationCanceledException) { /* canceled by winning probe */ }
                    catch (Exception ex)
                    {
                        _logger.Debug($"[PingServer ICMP] {ip}: {ex.Message}");
                    }
                    return null;
                },
                tcpProbe: async ct =>
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        using var tcp = new TcpClient();
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(400);
                        await tcp.ConnectAsync(ip, 443, timeoutCts.Token).ConfigureAwait(false);
                        sw.Stop();
                        return (int)sw.ElapsedMilliseconds;
                    }
                    catch (OperationCanceledException) { /* canceled by winning probe or timeout */ }
                    catch (Exception ex)
                    {
                        _logger.Debug($"[PingServer TCP] {ip}: {ex.Message}");
                    }
                    return null;
                },
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> ResolveGeoIpRegionAsync(string ip, string? ipApiTemplate, CancellationToken cancellationToken)
        {
            string subnetKey = GetIpv4SubnetPrefix(ip);
            if (_geoIpCache.TryGetValue(ip, out string? cachedRegion) ||
                (!string.IsNullOrEmpty(subnetKey) && _geoIpCache.TryGetValue(subnetKey, out cachedRegion) && cachedRegion != null))
            {
                return cachedRegion;
            }

            string ipLink = !string.IsNullOrWhiteSpace(ipApiTemplate)
                ? ipApiTemplate
                : (_settings.Get<string>("IPApiLink") ?? "https://ipwho.is/<ip>");

            try
            {
                string geoJson = string.Empty;
                string primaryUrl = ipLink.Replace("<ip>", ip);
                if (TryValidatePublicTargetIpAndGeoUrl(ip, primaryUrl, out _))
                {
                    try
                    {
                        geoJson = TestGeoHttpGetString != null
                            ? await TestGeoHttpGetString(primaryUrl, cancellationToken).ConfigureAwait(false)
                            : await SharedHttpClient.GetStringAsync(primaryUrl, retryTransient: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"[GeoIP primary] {ip}: {ex.Message}");
                    }
                }
                else
                {
                    _logger.Debug($"[GeoIP primary] Blocked invalid/non-HTTPS target: {ip}");
                }

                if (!TryParseRegionFromJson(geoJson, out string resolvedRegion) && !ipLink.Contains("ipwho.is", StringComparison.OrdinalIgnoreCase))
                {
                    string fbUrl = $"https://ipwho.is/{ip}";
                    if (TryValidatePublicTargetIpAndGeoUrl(ip, fbUrl, out _))
                    {
                        string fallbackJson = TestGeoHttpGetString != null
                            ? await TestGeoHttpGetString(fbUrl, cancellationToken).ConfigureAwait(false)
                            : await SharedHttpClient.GetStringAsync(fbUrl, retryTransient: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                        TryParseRegionFromJson(fallbackJson, out resolvedRegion);
                    }
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
                _logger.Debug($"[GeoIP] {ip}: {ex.Message}");
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
                var client = AppServices.RobloxApi.Games;
                string cursorParam = string.IsNullOrEmpty(cursor) ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}";
                string url = $"v1/games/{placeId}/servers/public?sortOrder=Asc&limit={limit}{cursorParam}";
                var request = new RestRequest(url, Method.Get);

                var overrideClient = TestHttpClientFactory?.Invoke();
                var response = await (overrideClient != null
                    ? client.ExecuteAsync(request, overrideClient, cancellationToken)
                    : client.ExecuteAsync(request, cancellationToken)).ConfigureAwait(false);
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
                _logger.Debug($"[QueryPublicServersAsync] {ex.Message}");
            }

            return result;
        }
    }
}
