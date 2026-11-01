#nullable enable
using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public static partial class RobloxHbaService
    {
        /// <summary>
        /// Generates a signed <c>x-bound-auth-token</c> for the given account and request parameters.
        /// Returns null if the account does not possess a valid HBA private key or if signing fails.
        /// </summary>
        public static string? GenerateBoundAuthToken(
            Account account,
            string httpMethod,
            string requestUrl,
            string? timestamp = null)
        {
            if (account == null || !account.HasHbaBinding || string.IsNullOrWhiteSpace(requestUrl))
                return null;

            long? tsOverride = null;
            if (!string.IsNullOrWhiteSpace(timestamp) &&
                long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedTs))
            {
                tsOverride = parsedTs;
            }

            if (TryBuildBoundAuthToken(
                account.HbaPrivateKeyPkcs8,
                requestUrl,
                httpMethod,
                bodyContent: null,
                account.HbaTemplate,
                tsOverride,
                out string token))
            {
                return token;
            }

            return null;
        }

        /// <summary>
        /// Cryptographically signs the outgoing HTTP request using the account's HBA key
        /// and attaches the <c>x-bound-auth-token</c> header synchronously.
        /// When request body is present, callers should supply <paramref name="bodyContent"/> directly
        /// or use <see cref="TryAttachHbaHeaderAsync"/> to prevent sync-over-async blocking.
        /// </summary>
        public static bool TryAttachHbaHeader(HttpRequestMessage request, Account account, string? bodyContent = null)
        {
            if (request == null || account == null || !account.HasHbaBinding || request.RequestUri == null)
                return false;

            if (bodyContent == null && request.Content is StringContent strContent)
            {
                // In-memory StringContent can be read via non-blocking memory stream / task
                bodyContent = strContent.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            if (TryBuildBoundAuthToken(
                account.HbaPrivateKeyPkcs8,
                request.RequestUri.AbsoluteUri,
                request.Method.Method,
                bodyContent,
                account.HbaTemplate,
                unixTimestampOverride: null,
                out string token))
            {
                request.Headers.Remove(BoundAuthHeaderName);
                request.Headers.TryAddWithoutValidation(BoundAuthHeaderName, token);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Asynchronously reads request body content if needed, cryptographically signs the request
        /// using the account's HBA key, and attaches the <c>x-bound-auth-token</c> header without thread-pool starvation.
        /// </summary>
        public static async Task<bool> TryAttachHbaHeaderAsync(
            HttpRequestMessage request,
            Account account,
            CancellationToken cancellationToken = default)
        {
            if (request == null || account == null || !account.HasHbaBinding || request.RequestUri == null)
                return false;

            string? bodyContent = null;
            if (request.Content != null)
            {
                try
                {
                    bodyContent = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    bodyContent = null;
                }
            }

            return TryAttachHbaHeader(request, account, bodyContent);
        }
    }
}
