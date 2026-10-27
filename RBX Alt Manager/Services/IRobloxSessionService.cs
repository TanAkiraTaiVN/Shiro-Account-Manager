using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Services
{
    public interface IRobloxSessionService
    {
        Task<bool> ValidateSessionAsync(Account account, CancellationToken cancellationToken = default);
        Task<long> FetchRobuxBalanceAsync(Account account, CancellationToken cancellationToken = default);
        Task<(bool Success, string Token, string Error)> GetCSRFTokenAsync(Account account, CancellationToken cancellationToken = default);
        Task<(bool Success, string Ticket)> GetAuthTicketAsync(Account account, CancellationToken cancellationToken = default);
        Task<bool> UnlockPinAsync(Account account, string pin, CancellationToken cancellationToken = default);
        Task<bool> SendFriendRequestAsync(Account account, string username, CancellationToken cancellationToken = default);
        Task<bool> QuickLogInAsync(Account account, string code, CancellationToken cancellationToken = default);
        Task<(bool Success, string Message)> UnblockEveryoneAsync(Account account, Action<string> progressCallback = null, CancellationToken cancellationToken = default);
        void TryUpdateRotatedCookie(Account account, HttpResponseMessage response);
    }
}
