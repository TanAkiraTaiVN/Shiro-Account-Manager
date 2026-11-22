#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public partial class Account : IComparable<Account>
    {
        public Task<string> JoinServer(
            long PlaceID, string JobID = "", bool FollowUser = false, bool JoinVIP = false, CancellationToken cancellationToken = default) =>
            AccountLaunchService.Instance.JoinServerAsync(this, PlaceID, JobID, FollowUser, JoinVIP, cancellationToken);

        public Task<LaunchResult> JoinServerResult(
            long PlaceID, string JobID = "", bool FollowUser = false, bool JoinVIP = false, CancellationToken cancellationToken = default) =>
            AccountLaunchService.Instance.JoinServerResultAsync(this, PlaceID, JobID, FollowUser, JoinVIP, cancellationToken);

        public Task<string> LaunchAppHome(CancellationToken cancellationToken = default) =>
            AccountLaunchService.Instance.LaunchAppHomeAsync(this, cancellationToken);

        public Task<LaunchResult> LaunchAppHomeResult(CancellationToken cancellationToken = default) =>
            AccountLaunchService.Instance.LaunchAppHomeResultAsync(this, cancellationToken);

        public Task AdjustWindowPosition(int targetPid = 0, CancellationToken cancellationToken = default) =>
            AccountLaunchService.AdjustWindowPositionAsync(this, targetPid, cancellationToken);

        public static void InvalidateExecutableCache() =>
            AccountLaunchService.InvalidateExecutableCache();

        public static string? FindRobloxPlayerExecutablePublic() =>
            AccountLaunchService.FindRobloxPlayerExecutable();

        public static bool HasRobloxPlayerProtocolHandlerPublic() =>
            AccountLaunchService.HasRobloxPlayerProtocolHandler();
    }
}
