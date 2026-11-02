#nullable enable
using System;
using System.Net.Http;
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

        public bool TryAttachHbaHeader(HttpRequestMessage request, string? bodyContent = null) =>
            RobloxHbaService.TryAttachHbaHeader(request, this, bodyContent);
    }
}
