#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Http;

namespace Shiro.Services
{
    public class RobloxSessionService : IRobloxSessionService
    {
        private static readonly Lazy<RobloxSessionService> _defaultInstance = new(() => new RobloxSessionService());
        public static RobloxSessionService Default => _defaultInstance.Value;

        public Task<bool> ValidateSessionAsync(Account account, CancellationToken cancellationToken = default)
        {
            if (account == null) return Task.FromResult(false);
            return account.ValidateSessionAsync(cancellationToken);
        }

        public Task<long> FetchRobuxBalanceAsync(Account account, CancellationToken cancellationToken = default)
        {
            if (account == null) return Task.FromResult<long>(-1);
            return account.FetchRobuxBalanceAsync(cancellationToken);
        }

        public Task<(bool Success, string Token, string Error)> GetCSRFTokenAsync(Account account, CancellationToken cancellationToken = default)
        {
            if (account == null) return Task.FromResult((false, string.Empty, "Account is null"));
            return account.GetCSRFTokenAsync(cancellationToken);
        }

        public Task<(bool Success, string Ticket)> GetAuthTicketAsync(Account account, CancellationToken cancellationToken = default)
        {
            if (account == null) return Task.FromResult((false, string.Empty));
            return account.GetAuthTicketAsync(cancellationToken);
        }

        public Task<bool> UnlockPinAsync(Account account, string pin, CancellationToken cancellationToken = default)
        {
            if (account == null) return Task.FromResult(false);
            return account.UnlockPinAsync(pin, cancellationToken);
        }

        public Task<bool> SendFriendRequestAsync(Account account, string username, CancellationToken cancellationToken = default)
        {
            if (account == null) return Task.FromResult(false);
            return account.SendFriendRequestAsync(username, cancellationToken);
        }

        public Task<bool> QuickLogInAsync(Account account, string code, CancellationToken cancellationToken = default)
        {
            if (account == null) return Task.FromResult(false);
            return account.QuickLogInAsync(code, cancellationToken);
        }

        public Task<(bool Success, string Message)> UnblockEveryoneAsync(
            Account account,
            Action<string>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            if (account == null) return Task.FromResult((false, "Account is null"));
            return account.UnblockEveryoneAsync(cancellationToken);
        }

        public void TryUpdateRotatedCookie(Account account, HttpResponseMessage response)
        {
            if (account == null || response == null) return;
            account.TryUpdateRotatedCookie(response);
        }
    }
}
