#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public partial class Account : IComparable<Account>
    {
        public Task<string> LaunchAppHome(bool preferChrome = false, CancellationToken cancellationToken = default) =>
            AccountLaunchService.Instance.LaunchAppHomeAsync(this, preferChrome, cancellationToken);

        public Task<LaunchResult> LaunchAppHomeResult(bool preferChrome = false, CancellationToken cancellationToken = default) =>
            AccountLaunchService.Instance.LaunchAppHomeResultAsync(this, preferChrome, cancellationToken);
    }
}
