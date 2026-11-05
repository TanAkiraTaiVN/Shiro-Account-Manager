#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Services;

namespace Shiro
{
    public partial class Account
    {
        // Test seam: lets unit tests supply a fake HttpMessageHandler-backed client.
        internal static Func<Account, HttpClient>? TestHttpClientFactory;

        internal const string DefaultBrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        private object? _cookieLock;
        private object CookieSyncRoot => _cookieLock ?? Interlocked.CompareExchange(ref _cookieLock, new object(), null) ?? _cookieLock!;
        private int _cookieRotationVersion;

        [System.Text.Json.Serialization.JsonIgnore]
        internal int CookieRotationVersion => Volatile.Read(ref _cookieRotationVersion);

        internal string GetCurrentSecurityToken()
        {
            lock (CookieSyncRoot)
            {
                return _SecurityToken ?? string.Empty;
            }
        }

        internal HttpRequestMessage CreateAuthenticatedGetRequest(string url, string accept = "application/json, text/plain, */*", string? tokenOverride = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplySessionFingerprintAndHba(request, bodyContent: null, tokenOverride: tokenOverride);
            request.Headers.TryAddWithoutValidation("Accept", accept);
            return request;
        }

        internal HttpClient GetSessionHttpClient(bool allowAutoRedirect = true) =>
            TestHttpClientFactory?.Invoke(this) ?? (allowAutoRedirect ? ProxyBindingService.GetClient(this) : ProxyBindingService.GetNoRedirectClient(this));

        public async Task<bool> ValidateSessionAsync(CancellationToken cancellationToken = default)
        {
            string initialToken = GetCurrentSecurityToken();
            if (string.IsNullOrWhiteSpace(initialToken))
            {
                if (!IsBannedOrLocked)
                    AccountStatusIssue = string.Empty;
                Valid = false;
                return false;
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Valid;

                try
                {
                    HttpClient httpClient = GetSessionHttpClient();
                    using var request = CreateAuthenticatedGetRequest("https://users.roblox.com/v1/users/authenticated");

                    await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(cancellationToken).ConfigureAwait(false);

                    using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (response == null)
                        return Valid;

                    TryUpdateRotatedCookie(response);

                    if (response.RequestMessage?.RequestUri != null)
                    {
                        string absUri = response.RequestMessage.RequestUri.AbsoluteUri;
                        string host = response.RequestMessage.RequestUri.Host;
                        bool isRobloxHost = host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) ||
                                            host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase);
                        if (!isRobloxHost)
                        {
                            Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: ISP redirected API to external host ({absUri}). Preserving session validity: {Valid}");
                            return Valid;
                        }

                        if (absUri.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            absUri.IndexOf("NewLogin", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: Redirected to login ({absUri}). Cookie expired.");
                            if (!IsBannedOrLocked)
                                AccountStatusIssue = string.Empty;
                            Valid = false;
                            return false;
                        }
                    }

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        // Transient 401s from users.roblox.com have been observed on cookies that
                        // validate fine minutes later. Cross-check before discarding the session.
                        if (await ValidateViaSecondaryEndpointAsync(cancellationToken).ConfigureAwait(false))
                        {
                            Program.Logger?.Debug($"[ValidateSessionAsync] {Username}: users/authenticated returned 401 but secondary endpoint confirmed the session. Keeping it valid.");
                            AccountStatusIssue = string.Empty;
                            Valid = true;
                            return true;
                        }

                        var (modBanned, modBadge, _) = await CheckModerationStatusAsync(GetCurrentSecurityToken(), cancellationToken).ConfigureAwait(false);
                        if (modBanned)
                        {
                            AccountStatusIssue = modBadge ?? Shiro.Services.LocalizationService.Get("FastLogin.LockedOrBannedBadge");
                            Valid = false;
                            return false;
                        }

                        Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: Received 401 Unauthorized. Cookie expired.");
                        if (!IsBannedOrLocked)
                            AccountStatusIssue = string.Empty;
                        Valid = false;
                        return false;
                    }

                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        string trimmedBody = body?.TrimStart() ?? string.Empty;
                        if (trimmedBody.StartsWith("<", StringComparison.Ordinal))
                        {
                            Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: Received HTML block/captive page instead of JSON for 200 OK. Preserving validity: {Valid}");
                            return Valid;
                        }
                        if (string.IsNullOrWhiteSpace(trimmedBody))
                        {
                            if (!IsBannedOrLocked)
                                AccountStatusIssue = string.Empty;
                            Valid = false;
                            return false;
                        }

                        RobloxAuthenticatedUser? authUser = null;
                        if (Utilities.FastJson.TryDeserialize<RobloxAuthenticatedUser>(trimmedBody, out var parsedUser) && parsedUser?.Id > 0)
                        {
                            authUser = parsedUser;
                        }
                        else if (!string.IsNullOrWhiteSpace(trimmedBody))
                        {
                            try
                            {
                                using var uDoc = JsonDocument.Parse(trimmedBody);
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
                            if (UserID > 0 && authId != UserID)
                            {
                                Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: UserID mismatch (Expected: {UserID}, Got: {authId}). Cookie expired or belongs to different user.");
                                AccountStatusIssue = string.Empty;
                                Valid = false;
                                return false;
                            }

                            if (UserID <= 0)
                                UserID = authId;

                            if (!string.IsNullOrEmpty(authUser.Name) && string.IsNullOrEmpty(Username))
                                Username = authUser.Name;

                            // Verify live session store and moderation status concurrently (cuts latency in half)
                            var liveSessionTask = VerifyLiveSessionStoreAsync(cancellationToken);
                            var moderationTask = CheckModerationStatusAsync(GetCurrentSecurityToken(), cancellationToken);
                            await Task.WhenAll(liveSessionTask, moderationTask).ConfigureAwait(false);

                            var (isBanned, issueBadge, reason) = await moderationTask.ConfigureAwait(false);
                            if (isBanned)
                            {
                                string badge = issueBadge ?? Shiro.Services.LocalizationService.Get("FastLogin.LockedOrBannedBadge");
                                Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: Active moderation detected: {badge} ({reason}). Marking invalid.");
                                AccountStatusIssue = badge;
                                Valid = false;
                                return false;
                            }

                            bool liveSessionOk = await liveSessionTask.ConfigureAwait(false);
                            if (!liveSessionOk)
                            {
                                // accountsettings.roblox.com can return 401 for sessions that are
                                // still fully usable (users/authenticated already returned 200 with
                                // the right UserID and launches succeed). Never expire on that single
                                // signal: confirm with an independent authenticated endpoint first.
                                liveSessionOk = await ValidateViaSecondaryEndpointAsync(cancellationToken).ConfigureAwait(false);
                                if (liveSessionOk)
                                    Program.Logger?.Debug($"[ValidateSessionAsync] {Username}: accountsettings returned 401 but secondary endpoint confirmed the session. Keeping it valid.");
                            }
                            if (!liveSessionOk)
                            {
                                Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: Live session store and secondary endpoint both returned 401. Session expired or IP-locked.");
                                if (!IsBannedOrLocked)
                                    AccountStatusIssue = string.Empty;
                                Valid = false;
                                return false;
                            }

                            AccountStatusIssue = string.Empty;
                            Valid = true;
                            RobloxRateLimitCoordinator.Instance.RecordSuccess(RateLimitCategory.SessionCheck);
                            return true;
                        }

                        if (!IsBannedOrLocked)
                            AccountStatusIssue = string.Empty;
                        Valid = false;
                        return false;
                    }

                    if ((int)response.StatusCode == 429)
                    {
                        RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.SessionCheck, response);
                        if (attempt == 0)
                        {
                            await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        bool secondaryRateOk = await ValidateViaSecondaryEndpointAsync(cancellationToken).ConfigureAwait(false);
                        if (secondaryRateOk)
                        {
                            Valid = true;
                            return true;
                        }
                        Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: Rate limited (HTTP 429). Preserving existing session validity: {Valid}");
                        return Valid;
                    }

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        var (modBanned, modBadge, _) = await CheckModerationStatusAsync(GetCurrentSecurityToken(), cancellationToken).ConfigureAwait(false);
                        if (modBanned)
                        {
                            AccountStatusIssue = modBadge ?? Shiro.Services.LocalizationService.Get("FastLogin.LockedOrBannedBadge");
                            Valid = false;
                            return false;
                        }
                        bool secondaryOk = await ValidateViaSecondaryEndpointAsync(cancellationToken).ConfigureAwait(false);
                        if (secondaryOk)
                        {
                            Valid = true;
                            return true;
                        }
                        Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: HTTP 403 Forbidden received. Preserving session validity: {Valid}");
                        return Valid;
                    }

                    if ((int)response.StatusCode >= 500)
                    {
                        Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: Server returned HTTP {(int)response.StatusCode} {response.StatusCode}. Roblox service is temporarily degraded. Preserving session validity: {Valid}");
                        return Valid;
                    }

                    if (!IsBannedOrLocked)
                        AccountStatusIssue = string.Empty;
                    Valid = false;
                    return false;
                }
                catch (OperationCanceledException)
                {
                    return Valid;
                }
                catch (Exception ex)
                {
                    Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: Network/connection error: {ex.Message}");
                    if (attempt == 0)
                    {
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: Network unreachable after retries. Preserving session validity: {Valid}");
                    return Valid;
                }
            }

            return Valid;
        }

        private async Task<bool> VerifyLiveSessionStoreAsync(CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    linkedCts.CancelAfter(TimeSpan.FromSeconds(5));

                    int versionBefore = CookieRotationVersion;
                    HttpClient httpClient = GetSessionHttpClient();
                    using var request = CreateAuthenticatedGetRequest("https://accountsettings.roblox.com/v1/email");

                    using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
                    if (response == null)
                        return true;

                    TryUpdateRotatedCookie(response);

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        if (attempt == 0 && CookieRotationVersion != versionBefore)
                            continue;
                        return false;
                    }

                    return true;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Timeout on secondary store: treat as valid to prevent false positive expiration
                    return true;
                }
                catch
                {
                    return true;
                }
            }
            return false;
        }

        private async Task<bool> ValidateViaSecondaryEndpointAsync(CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    int versionBefore = CookieRotationVersion;
                    HttpClient httpClient = GetSessionHttpClient();
                    using var request = CreateAuthenticatedGetRequest("https://friends.roblox.com/v1/my/friends/count");

                    using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    TryUpdateRotatedCookie(response);
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        if (attempt == 0 && CookieRotationVersion != versionBefore)
                            continue;
                        Program.Logger?.Warn($"[ValidateViaSecondaryEndpointAsync] {Username}: Received 401 on friends.roblox.com. Cookie expired.");
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
                    break;
                }
                catch (Exception ex)
                {
                    Program.Logger?.Warn($"CheckFriendsCountAsync failed: {ex.Message}");
                    break;
                }
            }

            return false;
        }

        public RestRequest MakeRequest(string url, Method method = Method.Get)
        {
            var req = new RestRequest(url, method);
            string token = GetCurrentSecurityToken();
            if (!string.IsNullOrWhiteSpace(token))
            {
                req.AddCookie(".ROBLOSECURITY", token, "/", ".roblox.com");
            }
            return req;
        }

        /// <summary>
        /// Executes a RestRequest using this account's dedicated proxy HttpClient (when configured and enabled)
        /// while preserving the endpoint BaseUrl of the target <see cref="RestClient"/>.
        /// </summary>
        internal async Task<RestResponse?> ExecuteAccountRestAsync(
            RestClient? client,
            string fallbackBaseUrl,
            RestRequest request,
            CancellationToken cancellationToken = default)
        {
            ApplySessionFingerprintAndHbaToRestRequest(request, client?.BaseUrl ?? fallbackBaseUrl);
            HttpClient accountHttpClient = TestHttpClientFactory?.Invoke(this) ?? ProxyBindingService.GetClient(this);
            RestResponse? response;
            if (client != null)
                response = await client.ExecuteAsync(request, accountHttpClient, cancellationToken).ConfigureAwait(false);
            else
            {
                using var fallbackClient = new RestClient(accountHttpClient, fallbackBaseUrl);
                response = await fallbackClient.ExecuteAsync(request, accountHttpClient, cancellationToken).ConfigureAwait(false);
            }

            if (response != null)
            {
                TryUpdateRotatedCookie(response);
            }

            return response;
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

            return NativeAuthRecoverySeconds;
        }

        internal static void MarkNativeAuthRateLimited(int seconds, string username, string source)
        {
            seconds = Math.Max(1, Math.Min(180, seconds));

            Program.Logger?.Warn($"Roblox {source} returned HTTP 429 for {username}. The current launch was stopped; retry after about {seconds} seconds or change network before starting another batch.");
            AppSettings.SetDashboardStatus(
                Shiro.Services.LocalizationService.Format("Account.RateLimitCooldown", username, seconds));
            RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.Authentication, defaultBackoffSeconds: seconds);
        }

        private RestRequest CreateAuthTicketRequest(string token)
        {
            return MakeRequest("/v1/authentication-ticket/", Method.Post)
                .AddHeader("X-CSRF-TOKEN", token)
                .AddHeader("Content-Type", "application/json")
                .AddHeader("Origin", "https://www.roblox.com")
                .AddHeader("Referer", "https://www.roblox.com/games/4924922222/Brookhaven-RP")
                .AddHeader("rbxauthenticationnegotiation", "1")
                .AddHeader("Accept", "application/json, text/plain, */*")
                .AddHeader("Sec-Fetch-Dest", "empty")
                .AddHeader("Sec-Fetch-Mode", "cors")
                .AddHeader("Sec-Fetch-Site", "same-site")
                .AddJsonBody(new EmptyJsonObjectPayload());
        }
    }
}
