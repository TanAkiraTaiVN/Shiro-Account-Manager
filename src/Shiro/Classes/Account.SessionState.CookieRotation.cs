#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Services;

namespace Shiro
{
    public partial class Account
    {
        public bool TryUpdateRotatedCookie(HttpResponseMessage response)
        {
            if (response == null) return false;
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
                        Program.Logger?.Debug($"NonValidated Set-Cookie header extraction error: {ex.Message}");
                    }
                }

                if (setCookieHeaders != null)
                {
                    foreach (string header in setCookieHeaders)
                    {
                        string? newCookie = ExtractRoblosecurityFromSetCookie(header);
                        if (!string.IsNullOrWhiteSpace(newCookie) && ApplyRotatedSecurityToken(newCookie, "HttpResponseMessage Set-Cookie"))
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[CookieRotation] Error inspecting Set-Cookie header for {Username ?? "account"}: {ex.Message}");
            }
            return false;
        }

        public bool TryUpdateRotatedCookie(RestResponse response)
        {
            if (response == null) return false;
            try
            {
                if (response.Cookies != null)
                {
                    var cookie = response.Cookies.FirstOrDefault(c => string.Equals(c.Name, ".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase));
                    if (cookie != null && !string.IsNullOrWhiteSpace(cookie.Value))
                    {
                        string? sanitized = ExtractRoblosecurityFromSetCookie($".ROBLOSECURITY={cookie.Value}");
                        if (!string.IsNullOrEmpty(sanitized) && ApplyRotatedSecurityToken(sanitized, "RestResponse cookies"))
                            return true;
                    }
                }

                if (response.Headers != null)
                {
                    var setCookieHeaders = response.Headers.Where(h => string.Equals(h.Name, "Set-Cookie", StringComparison.OrdinalIgnoreCase));
                    foreach (var h in setCookieHeaders)
                    {
                        string? str = Convert.ToString(h.Value);
                        string? newCookie = ExtractRoblosecurityFromSetCookie(str);
                        if (!string.IsNullOrEmpty(newCookie) && ApplyRotatedSecurityToken(newCookie, "RestResponse headers"))
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[CookieRotation] Error inspecting RestResponse cookies for {Username ?? "account"}: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Signs and attaches the HBA token header to a cookie rotation or session re-binding request.
        /// </summary>
        public bool TryAttachCookieRotationHbaHeader(HttpRequestMessage request, string? bodyContent = null) =>
            RobloxHbaService.TryAttachHbaHeader(request, this, bodyContent);

        public static string? ExtractRoblosecurityFromSetCookie(string? header)
        {
            if (string.IsNullOrWhiteSpace(header)) return null;
            if (header.IndexOf("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase) >= 0 ||
                header.IndexOf("max-age=0", StringComparison.OrdinalIgnoreCase) >= 0)
                return null;

            ReadOnlySpan<char> span = header.AsSpan();
            int idx = span.IndexOf(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            span = span.Slice(idx + ".ROBLOSECURITY=".Length);
            int end = span.IndexOf(';');
            if (end >= 0)
                span = span.Slice(0, end);

            span = span.Trim().Trim('"').Trim();
            if (span.Length < 100 || !span.Contains("_|WARNING:-DO-NOT-SHARE-THIS".AsSpan(), StringComparison.Ordinal))
                return null;

            return span.ToString();
        }

        internal bool ApplyRotatedSecurityToken(string newCookie, string source)
        {
            string sanitized = SanitizeCookie(newCookie);
            if (string.IsNullOrWhiteSpace(sanitized) || !sanitized.Contains("_|WARNING:-DO-NOT-SHARE-THIS", StringComparison.Ordinal))
                return false;

            lock (CookieSyncRoot)
            {
                if (string.Equals(_SecurityToken, sanitized, StringComparison.Ordinal))
                    return false;

                _SecurityToken = sanitized;
                IsDirty = true;
                TokenSet = DateTime.MinValue;
                CSRFToken = null;
                LastUse = DateTime.Now;
                System.Threading.Interlocked.Increment(ref _cookieRotationVersion);
            }

            Program.Logger?.Info($"[CookieRotation] Captured rotated .ROBLOSECURITY from {source} for {Username ?? "account"}. Updating stored session.");
            string netSig = RobloxHbaService.GetLocalNetworkSignature();
            if (netSig is not ("offline" or "unknown"))
                LastBoundIpSignature = netSig;
            try
            {
                AppServices.AccountRepository.QueueDeferredSave();
                if (!string.IsNullOrEmpty(Username) || UserID > 0)
                {
                    AppDispatcher.Post(() =>
                    {
                        AppSettings.RefreshAccountView(this);
                    });
                }
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
