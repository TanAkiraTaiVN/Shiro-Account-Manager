#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public interface IServerBrowserStateService
    {
        string CurrentPlaceId { get; set; }
        string CurrentJobId { get; set; }
        bool ShuffleJobId { get; set; }
        string? CurrentClientVersion { get; set; }
        string? WebSocketPassword { get; set; }
        long CachedServersPlaceId { get; set; }
        List<ServerData> CachedServers { get; set; }
        List<Shiro.Game> RecentGames { get; set; }
        Task LoadClientVersionAsync(CancellationToken cancellationToken = default);
    }
}
