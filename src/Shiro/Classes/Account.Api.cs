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
using Shiro.Classes;
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
        public async Task<long> FetchRobuxBalanceAsync(CancellationToken cancellationToken = default)
        {
            if (UserID <= 0 || string.IsNullOrWhiteSpace(SecurityToken) || !Valid)
                return RobuxBalance;

            try
            {
                string token = SecurityToken.Trim().Trim('"').Trim();
                long balance = await RobloxRateLimitCoordinator.Instance.ExecuteWithPacingAsync(
                    RateLimitCategory.General,
                    async () =>
                    {
                        HttpClient httpClient = GetSessionHttpClient();
                        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://economy.roblox.com/v1/users/{UserID}/currency");
                        request.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={token}");
                        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0 Safari/537.36");
                        request.Headers.TryAddWithoutValidation("Accept", "application/json");

                        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                        TryUpdateRotatedCookie(response);
                        if (response.IsSuccessStatusCode)
                        {
                            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                            if (Utilities.FastJson.TryDeserialize<RobloxCurrencyResponse>(json, out var res) && res != null)
                            {
                                RobuxBalance = res.Robux;
                                LastEconomyCheck = DateTime.UtcNow;
                                RobloxRateLimitCoordinator.Instance.RecordSuccess(RateLimitCategory.General);
                                return RobuxBalance;
                            }
                        }
                        else if ((int)response.StatusCode == 429)
                        {
                            RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.General, response);
                        }
                        return RobuxBalance;
                    },
                    minIntervalMs: 250,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return balance;
            }
            catch (Exception ex)
            {
                Program.Logger.Debug($"[FetchRobuxBalanceAsync] {Username}: {ex.Message}");
                return RobuxBalance;
            }
        }

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

            if (!string.IsNullOrEmpty(CSRFToken) && (DateTime.Now - TokenSet).TotalMinutes < 4)
                return (true, CSRFToken, string.Empty);

            string[] endpoints =
            {
                "https://auth.roblox.com/v1/authentication-ticket/",
                "https://auth.roblox.com/v2/login"
            };
            HttpClient httpClient = GetSessionHttpClient();

            foreach (string endpoint in endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? cookie = SecurityToken?.Trim().Trim('"').Trim();
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                if (!string.IsNullOrEmpty(cookie))
                    request.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={cookie}");
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
                        RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.Authentication, response);
                        LastAuthTicketRateLimited = true;
                        MarkNativeAuthRateLimited(60, Username, "CSRF token");
                        return (false, string.Empty, "Roblox giới hạn xác thực (HTTP 429).");
                    }

                    if (response.Headers.TryGetValues("x-csrf-token", out var vals))
                    {
                        string? token = vals.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            CSRFToken = token;
                            TokenSet = DateTime.Now;
                            LastUse = DateTime.Now;
                            AccountManager.LastValidAccount = this;
                            AccountManager.QueueDeferredSave();
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
                    Program.Logger.Warn($"[GetCSRFTokenAsync] Error on {endpoint}: {ex.Message}");
                }
            }

            return (false, string.Empty, "Roblox did not issue an X-CSRF-Token.");
        }

        /// <summary>
        /// 1-step optimistic authentication ticket negotiation.
        /// Posts directly to v1/authentication-ticket/ using any cached CSRF token (or empty on first call),
        /// and captures the x-csrf-token directly from the 403 Forbidden challenge response without hitting v2/login.
        /// </summary>
        public async Task<(bool Success, string Ticket)> GetAuthTicketAsync(CancellationToken cancellationToken = default)
        {
            LastAuthTicketRateLimited = false;
            HttpClient httpClient = GetSessionHttpClient();

            string currentCsrf = (!string.IsNullOrEmpty(CSRFToken) && (DateTime.Now - TokenSet).TotalMinutes < 4)
                ? CSRFToken
                : string.Empty;

            for (int attempt = 0; attempt < AuthTicketMaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(cancellationToken).ConfigureAwait(false);

                string? cookie = SecurityToken?.Trim().Trim('"').Trim();
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket/");
                if (!string.IsNullOrEmpty(cookie))
                    request.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={cookie}");
                if (!string.IsNullOrEmpty(currentCsrf))
                    request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", currentCsrf);
                request.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
                request.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
                request.Headers.TryAddWithoutValidation("rbxauthenticationnegotiation", "1");
                request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

                try
                {
                    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (response == null) return (false, string.Empty);

                    TryUpdateRotatedCookie(response);

                    if (response.Headers.TryGetValues("x-csrf-token", out var csrfVals))
                    {
                        string? freshCsrf = csrfVals.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(freshCsrf))
                        {
                            currentCsrf = freshCsrf;
                            CSRFToken = freshCsrf;
                            TokenSet = DateTime.Now;
                            LastUse = DateTime.Now;
                            AccountManager.LastValidAccount = this;
                        }
                    }

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        if (string.IsNullOrEmpty(currentCsrf))
                        {
                            var fallbackCsrf = await GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
                            if (fallbackCsrf.Success)
                                currentCsrf = fallbackCsrf.Token;
                        }
                        continue;
                    }

                    if ((int)response.StatusCode == 429)
                    {
                        RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.Authentication, response, defaultBackoffSeconds: 3);
                        LastAuthTicketRateLimited = true;

                        if (attempt + 1 < AuthTicketMaxAttempts)
                        {
                            var backoff = RobloxRateLimitCoordinator.Instance.RemainingBackoff;
                            Program.Logger.Warn($"Roblox authentication-ticket request was rate limited (HTTP 429) for {Username}. Backing off for {backoff.TotalSeconds:F1}s before retry (attempt {attempt + 1}/{AuthTicketMaxAttempts})...");
                            continue;
                        }

                        MarkNativeAuthRateLimited(30, Username, "authentication-ticket");
                        Program.Logger.Warn($"Roblox authentication-ticket request was rate limited (HTTP 429) for {Username}; exhausted all {AuthTicketMaxAttempts} attempts.");
                        return (false, string.Empty);
                    }

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        Program.Logger.Warn($"Roblox authentication-ticket request returned HTTP 401 Unauthorized for {Username}. Preserving stored session.");
                        currentCsrf = string.Empty;
                        CSRFToken = null;
                        if (attempt + 1 < AuthTicketMaxAttempts)
                        {
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
                            AccountManager.QueueDeferredSave();
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
                    Program.Logger.Warn($"[GetAuthTicketAsync] Error on attempt {attempt}: {ex.Message}");
                }
            }

            return (false, string.Empty);
        }

        public async Task<string?> GetEmailJSON(CancellationToken cancellationToken = default)
        {
            RestRequest DataRequest = MakeRequest("v1/email", Method.Get);

            RestResponse? response = await ExecuteAccountRestAsync(AccountManager.AccountClient, "https://accountsettings.roblox.com/", DataRequest, cancellationToken).ConfigureAwait(false);

            return response?.Content;
        }

        public async Task<JsonNode?> GetUserInfo(CancellationToken cancellationToken = default)
        {
            RestRequest DataRequest = MakeRequest($"v1/users/{UserID}", Method.Get);

            RestResponse? response = await ExecuteAccountRestAsync(AccountManager.UsersClient, "https://users.roblox.com/", DataRequest, cancellationToken).ConfigureAwait(false);

            if (response != null && response.StatusCode == HttpStatusCode.OK && !string.IsNullOrWhiteSpace(response.Content))
            {
                try { return JsonNode.Parse(response.Content); }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Parse GetUserInfo JSON failed: {ex.Message}");
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
