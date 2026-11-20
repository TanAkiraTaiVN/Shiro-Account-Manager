#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    /// <summary>
    /// Session validation, cookie rotation, auth-ticket negotiation, and IP-rebind transport client
    /// for <see cref="Account"/> domain entities.
    /// </summary>
    public sealed partial class RobloxAccountSessionClient
    {
        private static readonly Lazy<RobloxAccountSessionClient> _defaultInstance = new(() => new RobloxAccountSessionClient());
        public static RobloxAccountSessionClient Default => _defaultInstance.Value;
        public static RobloxAccountSessionClient Instance => Default;

        // Test seam: lets unit tests supply a fake HttpMessageHandler-backed client.
        internal static Func<Account, HttpClient>? TestHttpClientFactory;

        // Retry up to 4 attempts on transient network glitches, stale CSRF (403), or short burst 429s
        // with backoff to guarantee high-concurrency multi-launch reliability.
        private const int AuthTicketMaxAttempts = 4;

        // The native client redeems the ticket after RAM has started it. That
        // second request can be rate-limited even when ticket creation worked,
        // so keep a separate adaptive recovery window for confirmed 429s.
        internal const int NativeAuthRecoverySeconds = 90;

        private readonly IAppLogger? _logger;
        private readonly RobloxAccountApiService? _apiService;

        public RobloxAccountSessionClient() { }

        public RobloxAccountSessionClient(IAppLogger logger, RobloxAccountApiService? apiService = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _apiService = apiService;
        }

        private IAppLogger? Logger => _logger ?? AppServices.Logger;
        private RobloxAccountApiService ApiService => _apiService ?? AppServices.AccountApiService;

        public HttpClient GetSessionHttpClient(Account account, bool allowAutoRedirect = true)
        {
            ArgumentNullException.ThrowIfNull(account);
            return TestHttpClientFactory?.Invoke(account)
                ?? (allowAutoRedirect ? ProxyBindingService.GetClient(account) : ProxyBindingService.GetNoRedirectClient(account));
        }

        internal HttpRequestMessage CreateAuthenticatedGetRequest(
            Account account,
            string url,
            string accept = "application/json, text/plain, */*",
            string? tokenOverride = null)
        {
            ArgumentNullException.ThrowIfNull(account);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApiService.ApplySessionFingerprintAndHba(account, request, bodyContent: null, tokenOverride: tokenOverride);
            request.Headers.TryAddWithoutValidation("Accept", accept);
            return request;
        }

        internal async Task<Account> CreateValidatedAccountAsync(
            string cookie,
            string? accountJson,
            CancellationToken ct,
            RobloxHbaService.CaptureSnapshot? hbaCapture,
            string? expectedUsername)
        {
            var account = new Account { SecurityToken = cookie, Valid = false, LastUse = DateTime.Now };
            account.ApplyHbaCapture(hbaCapture);

            if (!string.IsNullOrEmpty(accountJson) && Utilities.TryParseJson(accountJson, out AccountJson? data) && data != null && data.UserId > 0)
            {
                account.Username = data.Name;
                account.UserID = data.UserId;
                account.Valid = true;
                account.LastUse = DateTime.Now;
            }
            else if (await ValidateSessionAsync(account, ct).ConfigureAwait(false) && account.UserID > 0)
            {
                account.LastUse = DateTime.Now;
            }

            if (account.Valid &&
                (string.IsNullOrEmpty(expectedUsername) ||
                 string.Equals(account.Username, expectedUsername, StringComparison.OrdinalIgnoreCase)))
            {
                AppServices.SelectionState.LastValidAccount = account;
            }
            return account;
        }

        public bool TryUpdateRotatedCookie(Account account, HttpResponseMessage? response)
        {
            if (account == null || response == null) return false;
            try
            {
                IEnumerable<string>? setCookieHeaders = null;
                if (!response.Headers.TryGetValues("Set-Cookie", out setCookieHeaders))
                {
                    try
                    {
                        if (response.Headers.NonValidated.TryGetValues("Set-Cookie", out var nonValidated))
                            setCookieHeaders = nonValidated;
                    }
                    catch (Exception ex)
                    {
                        Logger?.Debug($"NonValidated Set-Cookie header extraction error: {ex.Message}");
                    }
                }

                if (setCookieHeaders != null)
                {
                    foreach (string header in setCookieHeaders)
                    {
                        string? newCookie = RobloxCookieHeaderParser.ExtractRoblosecurityFromSetCookie(header);
                        if (!string.IsNullOrWhiteSpace(newCookie) && ApplyRotatedSecurityToken(account, newCookie, "HttpResponseMessage Set-Cookie"))
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.Warn($"[CookieRotation] Error inspecting Set-Cookie header for {account.Username ?? "account"}: {ex.Message}");
            }
            return false;
        }

        internal bool ApplyRotatedSecurityToken(Account account, string newCookie, string source)
        {
            if (account == null) return false;

            string sanitized = Account.SanitizeCookie(newCookie);
            if (string.IsNullOrWhiteSpace(sanitized) || !sanitized.Contains("_|WARNING:-DO-NOT-SHARE-THIS", StringComparison.Ordinal))
                return false;

            if (!account.TryCommitRotatedSecurityToken(sanitized))
                return false;

            Logger?.Info($"[CookieRotation] Captured rotated .ROBLOSECURITY from {source} for {account.Username ?? "account"}. Updating stored session.");
            string netSig = RobloxHbaService.GetNetworkSignatureForAccount(account);
            if (netSig is not ("offline" or "unknown"))
                account.LastBoundIpSignature = netSig;
            try
            {
                AppServices.AccountRepository.QueueDeferredSave();
                if (!string.IsNullOrEmpty(account.Username) || account.UserID > 0)
                {
                    AppDispatcher.Post(() =>
                    {
                        AppServices.HostUiBridge.RefreshAccountView(account);
                    });
                }
            }
            catch (Exception ex)
            {
                Logger?.Debug($"UI notification update error: {ex.Message}");
            }
            return true;
        }

        internal static void MarkNativeAuthRateLimited(
            int seconds,
            string username,
            string source,
            HttpResponseMessage? response = null,
            bool recordCoordinatorHit = true)
        {
            seconds = Math.Max(1, Math.Min(180, seconds));

            AppServices.Logger?.Warn($"Roblox {source} returned HTTP 429 for {username}. The current launch was stopped; retry after about {seconds} seconds or change network before starting another batch.");
            AppServices.HostUiBridge.NotifyWindowStatus(
                LocalizationService.Format("Account.RateLimitCooldown", username, seconds));
            if (recordCoordinatorHit)
                RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.Authentication, response, defaultBackoffSeconds: seconds);
        }
    }
}
