#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public partial class Account : IComparable<Account>
    {
        public Task<long> FetchRobuxBalanceAsync(CancellationToken cancellationToken = default) =>
            RobloxEconomyService.Instance.FetchRobuxBalanceAsync(this, cancellationToken);

        public Task<(bool Success, string Token, string Error)> GetCSRFTokenAsync(CancellationToken cancellationToken = default) =>
            AppServices.AccountSessionClient.GetCsrfTokenAsync(this, cancellationToken);

        public Task<(bool Success, string Ticket)> GetAuthTicketAsync(CancellationToken cancellationToken = default) =>
            AppServices.AccountSessionClient.GetAuthTicketAsync(this, cancellationToken);

        internal Task<(bool Success, string Ticket)> GetAuthTicketCoreAsync(bool allowIpSyncRetry, CancellationToken cancellationToken = default) =>
            AppServices.AccountSessionClient.GetAuthTicketCoreAsync(this, allowIpSyncRetry, cancellationToken);
    }
}
