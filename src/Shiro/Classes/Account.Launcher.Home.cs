#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public partial class Account : IComparable<Account>
    {
        public Task<string> LaunchAppHome(
            bool preferChrome = false,
            CancellationToken cancellationToken = default)
        {
            return AccountLaunchService.Instance.LaunchAppHomeAsync(
                this,
                preferChrome,
                cancellationToken);
        }
    }
}
