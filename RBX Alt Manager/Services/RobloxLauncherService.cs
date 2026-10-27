#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Services
{
    public class RobloxLauncherService : IRobloxLauncherService
    {
        private static readonly Lazy<RobloxLauncherService> _defaultInstance = new(() => new RobloxLauncherService());
        public static RobloxLauncherService Default => _defaultInstance.Value;

        public string FindRobloxPlayerExecutable() => Account.FindRobloxPlayerExecutablePublic();

        public bool HasRobloxPlayerProtocolHandler() => Account.HasRobloxPlayerProtocolHandlerPublic();

        public async Task<string> LaunchAppHomeAsync(Account account, bool preferChrome = false, CancellationToken cancellationToken = default)
        {
            if (account == null)
                return "Tài khoản không hợp lệ.";

            return await account.LaunchAppHome(preferChrome, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> JoinServerAsync(
            Account account,
            long placeId,
            string jobId = "",
            bool followUser = false,
            bool joinVip = false,
            CancellationToken cancellationToken = default)
        {
            if (account == null)
                return "Tài khoản không hợp lệ.";

            return await account.JoinServer(placeId, jobId, followUser, joinVip, false, cancellationToken).ConfigureAwait(false);
        }
    }
}
