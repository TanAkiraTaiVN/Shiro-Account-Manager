#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public partial class Account
    {
        public Task<bool> TryRebindSessionToCurrentNetworkAsync(bool force = false, CancellationToken cancellationToken = default) =>
            AppServices.AccountSessionClient.TryRebindSessionToCurrentNetworkAsync(this, force, cancellationToken);
    }
}
