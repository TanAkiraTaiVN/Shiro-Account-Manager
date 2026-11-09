#nullable enable
using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;

namespace Shiro.Services
{
    public class ServerRegionPingResult
    {
        public string Ip { get; set; } = string.Empty;
        public int? PingMs { get; set; }
        public string Region { get; set; } = string.Empty;
        public string CountryCode { get; set; } = string.Empty;
        public string FlagEmoji { get; set; } = string.Empty;
        public bool Success { get; set; }
    }

    public class RobloxServerListResult
    {
        public List<ServerData> Servers { get; set; } = new();
        public string NextPageCursor { get; set; } = string.Empty;
        public bool Success { get; set; }
    }

    public interface IRobloxServerQueryService
    {
        Task<ServerRegionPingResult> QueryServerRegionAndPingAsync(
            Account account,
            long placeId,
            string serverId,
            string? ipApiTemplate = null,
            CancellationToken cancellationToken = default);

        Task<RobloxServerListResult> QueryPublicServersAsync(
            long placeId,
            string cursor = "",
            int limit = 100,
            CancellationToken cancellationToken = default);
    }
}
