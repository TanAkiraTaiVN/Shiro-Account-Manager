#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;

namespace Shiro.Services
{
    /// <summary>
    /// Executes authenticated account REST requests with automatic one-time retry
    /// when Roblox rejects a stale/mismatched CSRF token with HTTP 403 and returns
    /// a fresh <c>x-csrf-token</c> response header.
    /// </summary>
    internal static class RobloxCsrfRetryHelper
    {
        internal static string? TryExtractCsrfToken(RestResponse? response)
        {
            if (response?.Headers == null)
                return null;

            string? token = response.Headers
                .FirstOrDefault(h => string.Equals(h.Name, "x-csrf-token", StringComparison.OrdinalIgnoreCase))
                ?.Value?.ToString();

            return string.IsNullOrWhiteSpace(token) ? null : token;
        }

        internal static async Task<RestResponse?> ExecuteWithCsrfRetryAsync(
            Account account,
            RestClient? client,
            string fallbackBaseUrl,
            string? initialCsrfToken,
            Func<string, RestRequest> buildRequest,
            CancellationToken cancellationToken = default)
        {
            string csrf = string.IsNullOrEmpty(initialCsrfToken)
                ? (account.CSRFToken ?? string.Empty)
                : initialCsrfToken;
            RestResponse? response = null;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                RestRequest request = buildRequest(csrf);
                response = await AppServices.AccountApiService.ExecuteAccountRestAsync(account, client, fallbackBaseUrl, request, cancellationToken)
                    .ConfigureAwait(false);

                if (response == null || response.StatusCode != HttpStatusCode.Forbidden || attempt > 0)
                    return response;

                string? refreshed = TryExtractCsrfToken(response);
                if (string.IsNullOrWhiteSpace(refreshed))
                    return response;

                csrf = refreshed;
                account.SetCsrfToken(refreshed);
            }

            return response;
        }
    }
}
