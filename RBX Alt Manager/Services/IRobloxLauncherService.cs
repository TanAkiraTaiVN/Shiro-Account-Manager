#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Services
{
    public interface IRobloxLauncherService
    {
        string FindRobloxPlayerExecutable();
        bool HasRobloxPlayerProtocolHandler();
        Task<string> LaunchAppHomeAsync(Account account, bool preferChrome = false, CancellationToken cancellationToken = default);
        Task<string> JoinServerAsync(Account account, long placeId, string jobId = "", bool followUser = false, bool joinVip = false, CancellationToken cancellationToken = default);
    }
}
