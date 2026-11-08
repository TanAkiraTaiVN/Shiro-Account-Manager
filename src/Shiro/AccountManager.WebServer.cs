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
    }
}
