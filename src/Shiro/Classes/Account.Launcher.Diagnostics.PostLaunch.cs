#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public partial class Account : IComparable<Account>
    {
        public Task AdjustWindowPosition(int targetPid = 0, CancellationToken cancellationToken = default)
        {
            return AccountLaunchService.AdjustWindowPositionAsync(this, targetPid, cancellationToken);
        }
    }
}
