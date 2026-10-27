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
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;
using RBX_Alt_Manager.Http;

namespace RBX_Alt_Manager
{
    public partial class Account : IComparable<Account>
    {
        public async Task<bool> ValidateSessionAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(SecurityToken))
            {
                Valid = false;
                return false;
            }

            string token = SecurityToken.Trim().Trim('"').Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                Valid = false;
                return false;
            }

            if (HasExpiredShortLivedCookie())
            {
                Program.Logger.Warn($"[ValidateSessionAsync] {Username}: Short-lived 592-byte fallback cookie exceeded 30m TTL. Marking expired.");
                Valid = false;
                return false;
            }

            // Retry loop to handle transient network hiccups or temporary 429 rate limits
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Valid;

                try
                {
                    // Use ProxyBindingService to route via dedicated account proxy if set, or SharedHttpClient default
                    HttpClient httpClient = GetSessionHttpClient();
                    using var request = new HttpRequestMessage(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated");
                    request.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={token}");
                    request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                    request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

                    // Wait for any active rate-limit backoff before sending
                    await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(cancellationToken).ConfigureAwait(false);

                    using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                    if (response == null)
                        return Valid;

                    TryUpdateRotatedCookie(response);

                    // 1. If redirected away from users.roblox.com or to a login page, the session is expired/invalid!
                    if (response.RequestMessage?.RequestUri != null)
                    {
                        string absUri = response.RequestMessage.RequestUri.AbsoluteUri;
                        if (absUri.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            absUri.IndexOf("NewLogin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            !string.Equals(response.RequestMessage.RequestUri.Host, "users.roblox.com", StringComparison.OrdinalIgnoreCase))
                        {
                            Program.Logger.Warn($"[ValidateSessionAsync] {Username}: Redirected to login ({absUri}). Cookie expired.");
                            Valid = false;
                            return false;
                        }
                    }

                    // 2. HTTP 401 Unauthorized -> Definitively expired
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        Program.Logger.Warn($"[ValidateSessionAsync] {Username}: Received 401 Unauthorized. Cookie expired.");
                        Valid = false;
                        return false;
                    }

                    // 3. HTTP 200 OK -> Must strictly verify JSON and identity
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(body) || body.StartsWith("<"))
                        {
                            Program.Logger.Warn($"[ValidateSessionAsync] {Username}: Received HTML instead of JSON for 200 OK, session invalid.");
                            Valid = false;
                            return false;
                        }

                        RobloxAuthenticatedUser? authUser = null;
                        if (Utilities.FastJson.TryDeserialize<RobloxAuthenticatedUser>(body, out var parsedUser) && parsedUser?.Id > 0)
                        {
                            authUser = parsedUser;
                        }
                        else if (!string.IsNullOrWhiteSpace(body))
                        {
                            try
                            {
                                using var uDoc = JsonDocument.Parse(body);
                                if (uDoc.RootElement.TryGetProperty("id", out var idProp))
                                {
                                    authUser = new RobloxAuthenticatedUser
                                    {
                                        Id = idProp.GetInt64(),
                                        Name = (uDoc.RootElement.TryGetProperty("name", out var nProp) ? nProp.GetString() : null) ?? string.Empty
                                    };
                                }
                            }
                            catch (Exception ex)
                            {
                                Program.Logger?.Debug($"Parse authenticated user JSON failed: {ex.Message}");
                            }
                        }

                        if (authUser != null && authUser.Id > 0)
                        {
                            long authId = authUser.Id;
                            // Strict UserID verification: ensure this response matches THIS account!
                            if (UserID > 0 && authId != UserID)
                            {
                                Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: UserID mismatch (Expected: {UserID}, Got: {authId}). Cookie expired or belongs to different user.");
                                Valid = false;
                                return false;
                            }

                            // Update UserID / Username if they were empty
                            if (UserID <= 0)
                                UserID = authId;

                            if (!string.IsNullOrEmpty(authUser.Name) && string.IsNullOrEmpty(Username))
                                Username = authUser.Name;

                            Valid = true;
                            RobloxRateLimitCoordinator.Instance.RecordSuccess(RateLimitCategory.SessionCheck);
                            return true;
                        }

                        // 200 OK but invalid JSON structure without "id"
                        Valid = false;
                        return false;
                    }

                    // 4. Rate-limit (429) -> Delay and retry once, or cross-verify via secondary endpoint
                    if ((int)response.StatusCode == 429)
                    {
                        RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.SessionCheck, response);
                        if (attempt == 0)
                        {
                            await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        bool secondaryRateOk = await ValidateViaSecondaryEndpointAsync(token, cancellationToken).ConfigureAwait(false);
                        if (secondaryRateOk)
                        {
                            Valid = true;
                            return true;
                        }
                        // HTTP 429 is an IP rate limit, NOT an expired session. Preserve existing session validity!
                        Program.Logger.Warn($"[ValidateSessionAsync] {Username}: Rate limited (HTTP 429). Preserving existing session validity: {Valid}");
                        return Valid;
                    }

                    // 5. 403 Forbidden -> Verify via secondary endpoint
                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        bool secondaryOk = await ValidateViaSecondaryEndpointAsync(token, cancellationToken).ConfigureAwait(false);
                        if (secondaryOk)
                        {
                            Valid = true;
                            return true;
                        }
                        // 403 Forbidden can be Cloudflare challenge or temporary IP block. Preserve validity unless confirmed 401.
                        Program.Logger.Warn($"[ValidateSessionAsync] {Username}: HTTP 403 Forbidden received. Preserving session validity: {Valid}");
                        return Valid;
                    }

                    // 6. Temporary server errors (500, 502, 503, 504) -> Preserve current session state!
                    if ((int)response.StatusCode >= 500)
                    {
                        Program.Logger.Warn($"[ValidateSessionAsync] {Username}: Server returned HTTP {(int)response.StatusCode} {response.StatusCode}. Roblox service is temporarily degraded. Preserving session validity: {Valid}");
                        return Valid;
                    }

                    // Any other 4xx client status (e.g. 400 Bad Request, 404 Not Found) indicates the session cannot be authenticated
                    Valid = false;
                    return false;
                }
                catch (OperationCanceledException)
                {
                    return Valid;
                }
                catch (Exception ex)
                {
                    Program.Logger.Warn($"[ValidateSessionAsync] {Username}: Network/connection error: {ex.Message}");
                    if (attempt == 0)
                    {
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    // Transient network errors (DNS resolution, connection timeout, interface reset) must NOT destroy session validity!
                    Program.Logger.Warn($"[ValidateSessionAsync] {Username}: Network unreachable after retries. Preserving session validity: {Valid}");
                    return Valid;
                }
            }

            return Valid;
        }

        // Test seam: lets unit tests supply a fake HttpMessageHandler-backed client.
        internal static Func<Account, HttpClient>? TestHttpClientFactory;

        private HttpClient GetSessionHttpClient() =>
            TestHttpClientFactory?.Invoke(this) ?? ProxyBindingService.GetClient(this);

        private async Task<bool> VerifyLiveSessionStoreAsync(string token, CancellationToken cancellationToken)
        {
            try
            {
                HttpClient httpClient = GetSessionHttpClient();
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://accountsettings.roblox.com/v1/email");
                request.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={token}");
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

                using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (response == null)
                    return true;

                TryUpdateRotatedCookie(response);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return false;

                return true;
            }
            catch
            {
                // Do not false-invalidate on transient network/DNS errors
                return true;
            }
        }

        private async Task<bool> ValidateViaSecondaryEndpointAsync(string token, CancellationToken cancellationToken)
        {
            try
            {
                HttpClient httpClient = GetSessionHttpClient();
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://friends.roblox.com/v1/my/friends/count");
                request.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={token}");
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

                using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                TryUpdateRotatedCookie(response);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Program.Logger.Warn($"[ValidateViaSecondaryEndpointAsync] {Username}: Received 401 on friends.roblox.com. Cookie expired.");
                    return false;
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        if (Utilities.FastJson.TryDeserialize<RobloxFriendsCountResponse>(body, out var friendsRsp) && friendsRsp != null)
                        {
                            return true;
                        }
                        try
                        {
                            using var cDoc = JsonDocument.Parse(body);
                            if (cDoc.RootElement.TryGetProperty("count", out _))
                                return true;
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"Parse friend count JSON failed: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"CheckFriendsCountAsync failed: {ex.Message}");
            }

            return false;
        }

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

        public async Task<(bool Success, string Message)> UnblockEveryoneAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!await CheckPinAsync(true, cancellationToken).ConfigureAwait(false))
                return (false, "Pin is Locked");

            RestResponse? response = await GetBlockedListAsync(cancellationToken).ConfigureAwait(false);
            if (response == null || !response.IsSuccessful || response.StatusCode != HttpStatusCode.OK)
                return (false, "Failed to unblock everyone");

            var blockedIds = ExtractBlockedUserIdsFromJson(response.Content ?? string.Empty);
            if (blockedIds.Count == 0)
                return (true, "No blocked users");

            foreach (long blockedId in blockedIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string userId = blockedId.ToString(CultureInfo.InvariantCulture);

                RestResponse? unblockResponse = await UnblockUserIdAsync(userId, true, cancellationToken).ConfigureAwait(false);
                if (unblockResponse?.IsSuccessful == true)
                    continue;

                // Roblox can briefly reject a burst of block mutations. Keep
                // the previous retry interval, but make it cancellable and
                // keep the operation owned by its caller instead of leaving
                // an unobserved background Task.Run behind.
                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var retryResponse = await UnblockUserIdAsync(userId, true, cancellationToken).ConfigureAwait(false);
                if ((retryResponse == null || !retryResponse.IsSuccessful) && !await CheckPinAsync(true, cancellationToken).ConfigureAwait(false))
                    break;
            }

            return (true, "Unblocked everyone");
        }

        public Task<bool> QuickLogIn(string Code) => QuickLogInAsync(Code);

        public async Task<bool> QuickLogInAsync(string Code, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(Code) || Code.Length != 6) return false;
            var csrf = await GetCSRFTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!csrf.Success || string.IsNullOrEmpty(csrf.Token)) return false;
            string Token = csrf.Token;

            RestRequest enterRequest = MakeRequest("auth-token-service/v1/login/enterCode", Method.Post)
                .AddHeader("X-CSRF-TOKEN", Token)
                .AddJsonBody(new { code = Code });

            var Response = await ExecuteAccountRestAsync(AccountManager.ApiClient, "https://apis.roblox.com/", enterRequest, cancellationToken).ConfigureAwait(false);

            if (Response != null && Response.IsSuccessful && !string.IsNullOrWhiteSpace(Response.Content))
            {
                string deviceInfo = "Unknown";
                string location = "Unknown";
                try
                {
                    using var infoDoc = JsonDocument.Parse(Response.Content);
                    if (infoDoc.RootElement.TryGetProperty("deviceInfo", out var di)) deviceInfo = di.GetString() ?? "Unknown";
                    if (infoDoc.RootElement.TryGetProperty("location", out var loc)) location = loc.GetString() ?? "Unknown";
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Parse QuickLogIn deviceInfo failed: {ex.Message}");
                }

                if (Utilities.YesNoPrompt("Quick Log In", "Please confirm you are logging in with this device", $"Device: {deviceInfo}\nLocation: {location}"))
                {
                    RestRequest validateRequest = MakeRequest("auth-token-service/v1/login/validateCode", Method.Post)
                        .AddHeader("X-CSRF-TOKEN", Token)
                        .AddJsonBody(new { code = Code });

                    var validateResponse = await ExecuteAccountRestAsync(AccountManager.ApiClient, "https://apis.roblox.com/", validateRequest, cancellationToken).ConfigureAwait(false);
                    return validateResponse?.IsSuccessful == true;
                }
            }

            return false;
        }
    }
}
