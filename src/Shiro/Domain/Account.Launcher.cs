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
            long PlaceID, string JobID = "", bool FollowUser = false, bool JoinVIP = false, bool Internal = false, CancellationToken cancellationToken = default) =>
            AccountLaunchService.Instance.JoinServerAsync(this, PlaceID, JobID, FollowUser, JoinVIP, Internal, cancellationToken);

        public Task<LaunchResult> JoinServerResult(
            long PlaceID, string JobID = "", bool FollowUser = false, bool JoinVIP = false, bool Internal = false, CancellationToken cancellationToken = default) =>
            AccountLaunchService.Instance.JoinServerResultAsync(this, PlaceID, JobID, FollowUser, JoinVIP, Internal, cancellationToken);

        public Task<string> LaunchAppHome(bool preferChrome = false, CancellationToken cancellationToken = default) =>
            AccountLaunchService.Instance.LaunchAppHomeAsync(this, preferChrome, cancellationToken);

        public Task<LaunchResult> LaunchAppHomeResult(bool preferChrome = false, CancellationToken cancellationToken = default) =>
            AccountLaunchService.Instance.LaunchAppHomeResultAsync(this, preferChrome, cancellationToken);

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
