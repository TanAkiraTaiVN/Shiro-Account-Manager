#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;

namespace Shiro.Services
{
    /// <summary>
    /// Network transport and request-signing service for account-scoped Roblox REST calls.
    /// Owns RestRequest creation, HBA/fingerprint header attachment, proxy HTTP execution,
    /// and Set-Cookie rotation capture for <see cref="Account"/> instances.
    /// </summary>
    public sealed class RobloxAccountApiService
    {
        private static readonly Lazy<RobloxAccountApiService> _defaultInstance = new(() => new RobloxAccountApiService());
        public static RobloxAccountApiService Default => _defaultInstance.Value;
        public static RobloxAccountApiService Instance => Default;

        private readonly IAppLogger? _logger;

        public RobloxAccountApiService() { }

        public RobloxAccountApiService(IAppLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private IAppLogger? Logger => _logger ?? AppServices.Logger;

        public RestRequest MakeRequest(Account account, string url, Method method = Method.Get)
        {
            ArgumentNullException.ThrowIfNull(account);
            var req = new RestRequest(url, method);
            string token = account.GetCurrentSecurityToken();
            if (!string.IsNullOrWhiteSpace(token))
            {
                req.AddCookie(".ROBLOSECURITY", token, "/", ".roblox.com");
            }
            return req;
        }

        /// <summary>
        /// Executes a RestRequest using the account's dedicated proxy HttpClient (when configured and enabled)
        /// while preserving the endpoint BaseUrl of the target <see cref="RestClient"/>.
        /// </summary>
        public async Task<RestResponse?> ExecuteAccountRestAsync(
            Account account,
            RestClient? client,
            string fallbackBaseUrl,
            RestRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(account);
            ArgumentNullException.ThrowIfNull(request);

            ApplySessionFingerprintAndHbaToRestRequest(account, request, client?.BaseUrl ?? fallbackBaseUrl);
            HttpClient accountHttpClient = RobloxAccountSessionClient.TestHttpClientFactory?.Invoke(account) ?? ProxyBindingService.GetClient(account);
            RestResponse? response;
            if (client != null)
            {
                response = await client.ExecuteAsync(request, accountHttpClient, ct).ConfigureAwait(false);
            }
            else
            {
                using var fallbackClient = new RestClient(accountHttpClient, fallbackBaseUrl);
                response = await fallbackClient.ExecuteAsync(request, accountHttpClient, ct).ConfigureAwait(false);
            }

            if (response != null)
            {
                TryUpdateRotatedCookie(account, response);
            }

            return response;
        }

        public void ApplySessionFingerprintAndHba(
            Account account,
            HttpRequestMessage request,
            string? bodyContent = null,
            string? tokenOverride = null,
            bool includeHba = true)
        {
            if (account == null || request == null)
                return;

            string cookieHeader = account.BuildCookieHeaderValue(tokenOverride);
            request.Headers.Remove("Cookie");
            if (!string.IsNullOrEmpty(cookieHeader))
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", account.GetEffectiveUserAgent());

            if (includeHba)
                RobloxHbaService.TryAttachHbaHeader(request, account, bodyContent);
        }

        public void ApplySessionFingerprintAndHbaToRestRequest(Account account, RestRequest request, string baseUrl)
        {
            if (account == null || request == null)
                return;

            if (!request.Headers.TryGetValue("User-Agent", out var reqUa) || string.IsNullOrEmpty(reqUa))
            {
                request.Headers["User-Agent"] = account.GetEffectiveUserAgent();
            }

            if (!string.IsNullOrWhiteSpace(account.BrowserTrackerID) &&
                request.Headers.TryGetValue("Cookie", out string? existingCookie) &&
                existingCookie != null &&
                !existingCookie.Contains("RBXEventTrackerV2=", StringComparison.OrdinalIgnoreCase))
            {
                string uidPart = account.UserID > 0 ? account.UserID.ToString(CultureInfo.InvariantCulture) : string.Empty;
                string nowUtc = DateTime.UtcNow.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
                request.Headers["Cookie"] = $"{existingCookie}; RBXEventTrackerV2=CreateDate={nowUtc}&rbxid={uidPart}&browserid={account.BrowserTrackerID}";
            }

            string privKey = account.HbaPrivateKey;
            if (string.IsNullOrWhiteSpace(privKey))
                return;

            Uri finalUri = RestClient.BuildRequestUri(request, baseUrl);
            bool isBodyMethod = RestClient.IsBodyMethod(request.Method);
            string? bodyStr = isBodyMethod ? RestClient.SerializeRequestBodyForSigning(request) : null;

            if (RobloxHbaService.TryBuildBoundAuthToken(
                privKey,
                finalUri.AbsoluteUri,
                request.Method.ToString().ToUpperInvariant(),
                bodyStr,
                account.HbaTemplate,
                unixTimestampOverride: null,
                out string batToken))
            {
                request.Headers[RobloxHbaService.BoundAuthHeaderName] = batToken;
            }
        }

        public bool TryUpdateRotatedCookie(Account account, RestResponse? response)
        {
            if (account == null || response == null) return false;
            try
            {
                bool sawRoblosecuritySetCookie = false;
                if (response.Headers != null)
                {
                    foreach (var h in response.Headers)
                    {
                        if (!string.Equals(h.Name, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string? str = Convert.ToString(h.Value);
                        if (RobloxCookieHeaderParser.HasRoblosecurityCookiePair(str))
                            sawRoblosecuritySetCookie = true;

                        string? newCookie = RobloxCookieHeaderParser.ExtractRoblosecurityFromSetCookie(str);
                        if (!string.IsNullOrEmpty(newCookie) && account.ApplyRotatedSecurityToken(newCookie, "RestResponse headers"))
                            return true;
                    }
                }

                if (!sawRoblosecuritySetCookie && response.Cookies != null)
                {
                    var cookie = response.Cookies.FirstOrDefault(c => string.Equals(c.Name, ".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase));
                    if (cookie != null && !string.IsNullOrWhiteSpace(cookie.Value))
                    {
                        string? sanitized = RobloxCookieHeaderParser.ExtractRoblosecurityFromSetCookie($".ROBLOSECURITY={cookie.Value}");
                        if (!string.IsNullOrEmpty(sanitized) && account.ApplyRotatedSecurityToken(sanitized, "RestResponse cookies"))
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.Warn($"[CookieRotation] Error inspecting RestResponse cookies for {account.Username ?? "account"}: {ex.Message}");
            }
            return false;
        }

        private static int GetRateLimitRecoverySeconds(RestResponse? response)
        {
            string? retryAfter = response?.Headers?
                .FirstOrDefault(x => string.Equals(x.Name, "Retry-After", StringComparison.OrdinalIgnoreCase))?
                .Value?.ToString()?.Trim();

            if (int.TryParse(retryAfter, out int serverDelay) && serverDelay > 0)
                return Math.Min(serverDelay, 180);

            if (DateTimeOffset.TryParse(
                retryAfter,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset retryAt))
            {
                double dateDelay = (retryAt.ToUniversalTime() - DateTimeOffset.UtcNow).TotalSeconds;
                if (dateDelay > 0)
                    return Math.Min(180, Math.Max(1, (int)Math.Ceiling(dateDelay)));
            }

            string? reset = response?.Headers?
                .FirstOrDefault(x => string.Equals(x.Name, "X-RateLimit-Reset", StringComparison.OrdinalIgnoreCase))?
                .Value?.ToString()?.Trim();
            if (long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out long resetUnix))
            {
                try
                {
                    double resetDelay = (DateTimeOffset.FromUnixTimeSeconds(resetUnix) - DateTimeOffset.UtcNow).TotalSeconds;
                    if (resetDelay > 0)
                        return Math.Min(180, Math.Max(1, (int)Math.Ceiling(resetDelay)));
                }
                catch (ArgumentOutOfRangeException) { /* invalid unix timestamp */ }
            }

            return RobloxAccountSessionClient.NativeAuthRecoverySeconds;
        }
    }
}
