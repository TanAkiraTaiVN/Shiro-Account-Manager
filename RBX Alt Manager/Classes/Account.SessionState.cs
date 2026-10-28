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
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Http;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager
{
    public partial class Account
    {
        // Test seam: lets unit tests supply a fake HttpMessageHandler-backed client.
        internal static Func<Account, HttpClient>? TestHttpClientFactory;

        internal HttpClient GetSessionHttpClient() =>
            TestHttpClientFactory?.Invoke(this) ?? ProxyBindingService.GetClient(this);

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
                Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: Short-lived 592-byte fallback cookie exceeded 30m TTL. Marking expired.");
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
                    using var request = new HttpRequestMessage(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated");
                    request.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={token}");
                    request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                    request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

                    await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(cancellationToken).ConfigureAwait(false);

                    using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                    if (response == null)
                        return Valid;

                    TryUpdateRotatedCookie(response);

                    if (response.RequestMessage?.RequestUri != null)
                    {
                        string absUri = response.RequestMessage.RequestUri.AbsoluteUri;
                        if (absUri.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            absUri.IndexOf("NewLogin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            !string.Equals(response.RequestMessage.RequestUri.Host, "users.roblox.com", StringComparison.OrdinalIgnoreCase))
                        {
                            Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: Redirected to login ({absUri}). Cookie expired.");
                            Valid = false;
                            return false;
                        }
                    }

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: Received 401 Unauthorized. Cookie expired.");
                        Valid = false;
                        return false;
                    }

                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(body) || body.StartsWith("<"))
                        {
                            Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: Received HTML instead of JSON for 200 OK, session invalid.");
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
                            if (UserID > 0 && authId != UserID)
                            {
                                Program.Logger?.Warn($"[ValidateSessionAsync] {Username}: UserID mismatch (Expected: {UserID}, Got: {authId}). Cookie expired or belongs to different user.");
                                Valid = false;
                                return false;
                            }

                            if (UserID <= 0)
                                UserID = authId;

                            if (!string.IsNullOrEmpty(authUser.Name) && string.IsNullOrEmpty(Username))
                                Username = authUser.Name;

                            Valid = true;
                            RobloxRateLimitCoordinator.Instance.RecordSuccess(RateLimitCategory.SessionCheck);
                            return true;
                        }

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
                        bool secondaryRateOk = await ValidateViaSecondaryEndpointAsync(token, cancellationToken).ConfigureAwait(false);
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
                        bool secondaryOk = await ValidateViaSecondaryEndpointAsync(token, cancellationToken).ConfigureAwait(false);
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
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"CheckFriendsCountAsync failed: {ex.Message}");
            }

            return false;
        }

        public RestRequest MakeRequest(string url, Method method = Method.Get)
        {
            var req = new RestRequest(url, method);
            if (!string.IsNullOrWhiteSpace(SecurityToken))
            {
                string token = SecurityToken.Trim().Trim('"').Trim();
                req.AddCookie(".ROBLOSECURITY", token, "/", ".roblox.com");
                req.AddHeader("Cookie", $".ROBLOSECURITY={token}");
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
            HttpClient accountHttpClient = ProxyBindingService.GetClient(this);
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

        private bool ApplyRotatedSecurityToken(string newCookie, string source)
        {
            if (string.IsNullOrWhiteSpace(newCookie) || string.Equals(SecurityToken, newCookie, StringComparison.Ordinal))
                return false;

            if (!newCookie.Contains("_|WARNING:-DO-NOT-SHARE-THIS", StringComparison.Ordinal))
                return false;

            Program.Logger?.Info($"[CookieRotation] Captured rotated .ROBLOSECURITY from {source} for {Username ?? "account"}. Updating stored session.");
            SecurityToken = newCookie;
            TokenSet = DateTime.MinValue;
            CSRFToken = null;
            LastUse = DateTime.Now;
            Valid = true;
            try
            {
                AccountManager.SaveAccounts(true);
                AccountManager.Instance?.BeginInvoke(new Action(() =>
                {
                    AccountManager.NotifyAccountValidity(this, true);
                    AccountManager.Instance?.RefreshView(this);
                }));
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"UI notification update error: {ex.Message}");
            }
            return true;
        }

        private static int GetRateLimitRecoverySeconds(RestResponse? response)
        {
            string? retryAfter = response?.Headers?
                .FirstOrDefault(x => string.Equals(x.Name, "Retry-After", StringComparison.OrdinalIgnoreCase))?
                .Value?.ToString()?.Trim();

            if (int.TryParse(retryAfter, out int serverDelay) && serverDelay > 0)
                return Math.Min(serverDelay, 180);

            // Retry-After is also allowed to be an HTTP-date. Roblox's edge
            // sometimes uses that form for auth throttling, and treating it
            // as an absent header made Shiro display the generic 90-second
            // estimate even when the server supplied a more useful window.
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

            // A few Roblox gateways expose a Unix timestamp instead of
            // Retry-After. Accept it as a final, bounded fallback without
            // changing the no-blind-retry behavior of auth-ticket calls.
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
                catch (ArgumentOutOfRangeException) { }
            }

            return NativeAuthRecoverySeconds;
        }

        private static void MarkNativeAuthRateLimited(int seconds, string username, string source)
        {
            seconds = Math.Max(1, Math.Min(180, seconds));

            Program.Logger?.Warn($"Roblox {source} returned HTTP 429 for {username}. The current launch was stopped; retry after about {seconds} seconds or change network before starting another batch.");
            AccountManager.Instance?.SetDashboardStatus(
                $"Roblox giới hạn xác thực {username} (HTTP 429). Thử lại sau khoảng {seconds} giây hoặc đổi máy chủ VPN.");
            RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.Authentication, defaultBackoffSeconds: seconds);
        }

        private RestRequest CreateAuthTicketRequest(string token)
        {
            return MakeRequest("/v1/authentication-ticket/", Method.Post)
                .AddHeader("X-CSRF-TOKEN", token)
                // Roblox now rejects an empty POST body with HTTP 415.  Keep
                // the request shaped like the web client: JSON content type
                // and an empty JSON object are required before the service
                // will issue the rbx-authentication-ticket header.
                .AddHeader("Content-Type", "application/json")
                .AddHeader("Origin", "https://www.roblox.com")
                .AddHeader("Referer", "https://www.roblox.com/games/4924922222/Brookhaven-RP")
                .AddHeader("rbxauthenticationnegotiation", "1")
                .AddHeader("Accept", "application/json, text/plain, */*")
                .AddHeader("Sec-Fetch-Dest", "empty")
                .AddHeader("Sec-Fetch-Mode", "cors")
                .AddHeader("Sec-Fetch-Site", "same-site")
                .AddJsonBody(new { });
        }
    }
}
