#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager
    {
        internal sealed class ApiRequestContext : AccountApiDispatcher.ApiRequestContext
        {
        }

        public async Task<string> SendKestrelResponse(HttpContext context) =>
            await AccountApiDispatcher.Instance.SendKestrelResponseAsync(context).ConfigureAwait(false);

        internal async Task<string> ProcessApiRequestAsync(AccountApiDispatcher.ApiRequestContext req) =>
            await AccountApiDispatcher.Instance.ProcessApiRequestAsync(req).ConfigureAwait(false);

        internal bool ValidateWebSocketAuth(HttpContext context) =>
            AccountApiDispatcher.Instance.ValidateWebSocketAuth(context);

        internal static bool IsLoopbackHostName(string host) =>
            AccountApiDispatcher.IsLoopbackHostName(host);

        internal static bool IsTrustedLocalOrigin(string origin) =>
            AccountApiDispatcher.IsTrustedLocalOrigin(origin);

        internal static bool FixedTimeEqualsPassword(string provided, string expected) =>
            AccountApiDispatcher.FixedTimeEqualsPassword(provided, expected);

        internal static string? ExtractHeaderPassword(Func<string, string?> getHeader) =>
            AccountApiDispatcher.ExtractHeaderPassword(getHeader);

        internal static string? ExtractQueryPassword(Func<string, string?> getQuery) =>
            AccountApiDispatcher.ExtractQueryPassword(getQuery);

        internal static string ExtractRequestPassword(Func<string, string?> getHeader, Func<string, string?> getQuery) =>
            AccountApiDispatcher.ExtractRequestPassword(getHeader, getQuery);
    }
}
