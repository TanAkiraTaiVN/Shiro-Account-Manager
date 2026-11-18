using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Shiro.Services;
using Shiro.Http;

namespace Shiro
{
    public partial class Account : IComparable<Account>
    {
        // Session validation, secondary checks, and HttpClient factory extracted to Account.SessionState.cs

        /// <summary>
        /// Fetches the live Robux balance for this account via official Roblox Economy API.
        /// </summary>
        public Task<long> FetchRobuxBalanceAsync(CancellationToken cancellationToken = default) =>
            RobloxEconomyService.Instance.FetchRobuxBalanceAsync(this, cancellationToken);

        /// <summary>
        /// Cancellation-aware CSRF acquisition used by the launch queue. The
        /// older synchronous method remains for legacy menu actions, but a
        /// synchronous RestSharp call could otherwise keep CancelLaunching
        /// blocked until the global 30-second HTTP timeout elapsed.
        /// <summary>
        /// <summary>
        /// Asynchronously fetches an X-CSRF-Token from Roblox.
        /// Uses v1/authentication-ticket/ first (fast, non-login endpoint) before falling back to v2/login,
        /// which reliably returns HTTP 403 Forbidden containing the x-csrf-token response header.
        /// </summary>
        public async Task<(bool Success, string Token, string Error)> GetCSRFTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (CookieSyncRoot)
            {
                if (!string.IsNullOrEmpty(CSRFToken) && (DateTime.Now - TokenSet).TotalMinutes < 4)
                    return (true, CSRFToken, string.Empty);
            }

            string[] endpoints =
            {
                "https://auth.roblox.com/v1/authentication-ticket",
                "https://auth.roblox.com/v2/login"
            };
            HttpClient httpClient = GetSessionHttpClient();

            foreach (string endpoint in endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                ApplySessionFingerprintAndHba(request, "{}");
                request.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
                request.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com");
                request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

                try
                {
                    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (response == null) continue;

                    TryUpdateRotatedCookie(response);

                    if ((int)response.StatusCode == 429)
                    {
                        LastAuthTicketRateLimited = true;
                        MarkNativeAuthRateLimited(60, Username, "CSRF token", response: response);
                        return (false, string.Empty, Shiro.Services.LocalizationService.Get("Account.RateLimited"));
                    }

                    if (response.Headers.TryGetValues("x-csrf-token", out var vals))
                    {
                        string? token = vals.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            DateTime now = DateTime.Now;
                            lock (CookieSyncRoot)
                            {
                                CSRFToken = token;
                                TokenSet = now;
                                LastUse = now;
                            }
                            AppServices.SelectionState.LastValidAccount = this;
                            RecordVolatileLastUse(now);
                            return (true, token, string.Empty);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Warn($"[GetCSRFTokenAsync] Error on {endpoint}: {ex.Message}");
                }
            }

            return (false, string.Empty, "Roblox did not issue an X-CSRF-Token.");
        }

        /// <summary>
        /// 1-step optimistic authentication ticket negotiation.
        /// Posts directly to v1/authentication-ticket/ using any cached CSRF token (or empty on first call),
        /// captures the x-csrf-token directly from the 403 Forbidden challenge response, and self-heals
        /// IP-transition 401s by synchronizing/rotating .ROBLOSECURITY onto the current IP.
        /// </summary>
        public Task<(bool Success, string Ticket)> GetAuthTicketAsync(CancellationToken cancellationToken = default) =>
            GetAuthTicketCoreAsync(allowIpSyncRetry: true, cancellationToken);

        internal async Task<(bool Success, string Ticket)> GetAuthTicketCoreAsync(bool allowIpSyncRetry, CancellationToken cancellationToken = default)
        {
            LastAuthTicketRateLimited = false;
            HttpClient httpClient = GetSessionHttpClient();
            bool attemptedIpSync = false;
            string currentSigAtStart = RobloxHbaService.GetNetworkSignatureForAccount(this);
            bool wasSameNetworkAtStart =
                !string.IsNullOrEmpty(LastBoundIpSignature) &&
                string.Equals(LastBoundIpSignature, currentSigAtStart, StringComparison.Ordinal);

            bool useHba = HasHbaBinding;
            if (allowIpSyncRetry)
            {
                if (string.IsNullOrEmpty(LastBoundIpSignature) &&
                    currentSigAtStart is not ("offline" or "unknown"))
                {
                    if (!useHba)
                    {
                        LastBoundIpSignature = currentSigAtStart;
                        AppServices.AccountRepository.QueueDeferredSave();
                        wasSameNetworkAtStart = true;
                    }
                }
                else if (useHba &&
                    !string.IsNullOrEmpty(LastBoundIpSignature) &&
                    !wasSameNetworkAtStart &&
                    currentSigAtStart is not ("offline" or "unknown"))
                {
                    AppServices.Logger?.Info($"[GetAuthTicketAsync] Network signature changed from '{LastBoundIpSignature}' to '{currentSigAtStart}' for {Username}. Proactively synchronizing session to current network...");
                    await TryRebindSessionToCurrentNetworkAsync(force: true, cancellationToken).ConfigureAwait(false);
                }
            }

            string currentCsrf;
            lock (CookieSyncRoot)
            {
                currentCsrf = (!string.IsNullOrEmpty(CSRFToken) && (DateTime.Now - TokenSet).TotalMinutes < 4)
                    ? CSRFToken
                    : string.Empty;
            }
            for (int attempt = 0; attempt < AuthTicketMaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(cancellationToken).ConfigureAwait(false);
                string ticketBody = await AccountLaunchService.FetchClientAssertionBodyAsync(this, httpClient, includeHba: useHba, cancellationToken).ConfigureAwait(false);
                int rotationBefore = CookieRotationVersion;
                using var request = AccountLaunchService.CreateTicketRequest(this, currentCsrf, body: ticketBody, includeHba: useHba);
                try
                {
                    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (response == null) return (false, string.Empty);

                    bool rotatedOnTicketCall = TryUpdateRotatedCookie(response) || CookieRotationVersion != rotationBefore;
                    bool updatedCsrfFromHeader = false;

                    if (response.Headers.TryGetValues("x-csrf-token", out var csrfVals))
                    {
                        string? freshCsrf = csrfVals.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(freshCsrf))
                        {
                            updatedCsrfFromHeader = true;
                            currentCsrf = freshCsrf;
                            DateTime now = DateTime.Now;
                            lock (CookieSyncRoot)
                            {
                                CSRFToken = freshCsrf;
                                TokenSet = now;
                                LastUse = now;
                            }
                            AppServices.SelectionState.LastValidAccount = this;
                            RecordVolatileLastUse(now);
                        }
                    }

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        if (!updatedCsrfFromHeader)
                        {
                            currentCsrf = string.Empty;
                            lock (CookieSyncRoot)
                            {
                                CSRFToken = null;
                                TokenSet = DateTime.MinValue;
                            }
                            var fallbackCsrf = await GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
                            if (fallbackCsrf.Success)
                                currentCsrf = fallbackCsrf.Token;
                        }
                        continue;
                    }

                    if ((int)response.StatusCode == 429)
                    {
                        LastAuthTicketRateLimited = true;

                        if (attempt + 1 < AuthTicketMaxAttempts)
                        {
                            RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.Authentication, response, defaultBackoffSeconds: 3);
                            var backoff = RobloxRateLimitCoordinator.Instance.RemainingBackoff;
                            AppServices.Logger?.Warn($"Roblox authentication-ticket request was rate limited (HTTP 429) for {Username}. Backing off for {backoff.TotalSeconds:F1}s before retry (attempt {attempt + 1}/{AuthTicketMaxAttempts})...");
                            continue;
                        }

                        MarkNativeAuthRateLimited(30, Username, "authentication-ticket", response: response);
                        AppServices.Logger?.Warn($"Roblox authentication-ticket request was rate limited (HTTP 429) for {Username}; exhausted all {AuthTicketMaxAttempts} attempts.");
                        return (false, string.Empty);
                    }

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        AppServices.Logger?.Warn($"Roblox authentication-ticket request returned HTTP 401 Unauthorized for {Username} (hba={useHba}). Preserving stored session.");
                        currentCsrf = string.Empty;
                        lock (CookieSyncRoot)
                        {
                            CSRFToken = null;
                        }

                        if (rotatedOnTicketCall && attempt + 1 < AuthTicketMaxAttempts)
                            continue;

                        if (allowIpSyncRetry && !attemptedIpSync && attempt + 1 < AuthTicketMaxAttempts)
                        {
                            attemptedIpSync = true;
                            if (await TrySynchronizeSessionForIpChangeAsync(cancellationToken).ConfigureAwait(false))
                            {
                                lock (CookieSyncRoot)
                                {
                                    currentCsrf = (!string.IsNullOrEmpty(CSRFToken) && (DateTime.Now - TokenSet).TotalMinutes < 4)
                                        ? CSRFToken
                                        : string.Empty;
                                }
                                continue;
                            }
                        }

                        // Only retry without HBA if we were already on the same network at the start of ticket negotiation
                        // and the cookie is not a downgraded short-lived token.
                        // On a network transition (e.g. VPN to residential VN IP), stripping HBA causes Roblox
                        // to immediately invalidate the session cookie due to an un-bound cross-region jump.
                        if (useHba && attempt + 1 < AuthTicketMaxAttempts &&
                            wasSameNetworkAtStart &&
                            !IsShortLivedCookie(GetCurrentSecurityToken()))
                        {
                            useHba = false;
                            AppServices.Logger?.Info($"[GetAuthTicketAsync] Retrying authentication-ticket for {Username} without HBA header on same network...");
                            continue;
                        }

                        return (false, string.Empty);
                    }

                    if (response.Headers.TryGetValues("rbx-authentication-ticket", out var ticketVals))
                    {
                        string ticket = ticketVals.FirstOrDefault() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(ticket))
                        {
                            LastAuthTicketRateLimited = false;
                            RobloxRateLimitCoordinator.Instance.RecordSuccess(RateLimitCategory.Authentication);
                            AppServices.AccountRepository.QueueDeferredSave();
                            return (true, ticket);
                        }
                    }

                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Warn($"[GetAuthTicketAsync] Error on attempt {attempt}: {ex.Message}");
                    if (attempt + 1 < AuthTicketMaxAttempts)
                        await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                }
            }

            return (false, string.Empty);
        }

        public async Task<string?> GetEmailJSON(CancellationToken cancellationToken = default)
        {
            RestRequest DataRequest = MakeRequest("v1/email", Method.Get);

            RestResponse? response = await ExecuteAccountRestAsync(AppServices.RobloxApi.Account, "https://accountsettings.roblox.com/", DataRequest, cancellationToken).ConfigureAwait(false);

            return response?.Content;
        }

        public async Task<JsonNode?> GetUserInfo(CancellationToken cancellationToken = default)
        {
            RestRequest DataRequest = MakeRequest($"v1/users/{UserID}", Method.Get);

            RestResponse? response = await ExecuteAccountRestAsync(AppServices.RobloxApi.Users, "https://users.roblox.com/", DataRequest, cancellationToken).ConfigureAwait(false);

            if (response != null && response.StatusCode == HttpStatusCode.OK && !string.IsNullOrWhiteSpace(response.Content))
            {
                try { return JsonNode.Parse(response.Content); }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"Parse GetUserInfo JSON failed: {ex.Message}");
                }
            }

            return null;
        }

        public async Task<long> GetRobux(CancellationToken cancellationToken = default)
        {
            long bal = await FetchRobuxBalanceAsync(cancellationToken).ConfigureAwait(false);
            return bal >= 0 ? bal : 0;
        }

        // Social, Blocking, UnblockEveryone, and QuickLogIn extracted to Account.SocialAndFriendsApi.cs
    }
}
