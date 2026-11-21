#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed class RobloxModerationService
    {
        private static readonly Lazy<RobloxModerationService> _defaultInstance = new(() => new RobloxModerationService(AppServices.Logger));
        public static RobloxModerationService Default => _defaultInstance.Value;
        public static RobloxModerationService Instance => Default;

        private readonly IAppLogger _logger;

        public RobloxModerationService() : this(AppServices.Logger) { }

        public RobloxModerationService(IAppLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<(bool IsBanned, string? IssueBadge, string? Reason)> CheckModerationStatusAsync(
            Account account,
            string token,
            CancellationToken cancellationToken) =>
            CheckModerationStatusAsync(account, token, null, cancellationToken, dispatchAlert: true);

        internal async Task<(bool IsBanned, string? IssueBadge, string? Reason)> CheckModerationStatusAsync(
            Account account,
            string token,
            HttpClient? httpClientOverride,
            CancellationToken cancellationToken,
            bool dispatchAlert = true)
        {
            if (account == null) return (false, null, null);

            try
            {
                HttpClient httpClient = httpClientOverride ?? AppServices.AccountSessionClient.GetSessionHttpClient(account, allowAutoRedirect: false);

                // 1. Probe official JSON moderation endpoint (usermoderation.roblox.com/v1/not-approved) first.
                // Unlike www.roblox.com/home, this API endpoint is not blocked by Vietnam ISP SNI filters and returns
                // structured punishment details (punishedUserId, beginDate, endDate, messageToUser) in ~250ms.
                using (var apiCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    apiCts.CancelAfter(TimeSpan.FromMilliseconds(2500));
                    try
                    {
                        using var apiReq = AppServices.AccountSessionClient.CreateAuthenticatedGetRequest(
                            account,
                            "https://usermoderation.roblox.com/v1/not-approved",
                            "application/json",
                            token);
                        using HttpResponseMessage apiRes = await httpClient.SendAsync(apiReq, HttpCompletionOption.ResponseContentRead, apiCts.Token).ConfigureAwait(false);
                        if (apiRes != null)
                        {
                            AppServices.AccountSessionClient.TryUpdateRotatedCookie(account, apiRes);
                            if (apiRes.IsSuccessStatusCode)
                            {
                                string apiBody = await apiRes.Content.ReadAsStringAsync(apiCts.Token).ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(apiBody) && apiBody.Contains('{', StringComparison.Ordinal))
                                {
                                    string apiCode = FastLoginCoordinator.ClassifyAuthOrModerationApiPayload(apiBody, (int)apiRes.StatusCode);
                                    if (!string.IsNullOrEmpty(apiCode) && apiCode != "UNAUTHENTICATED_MODERATION" && apiCode != "WRONG_PASSWORD")
                                    {
                                        var (badge, reason) = FastLoginCoordinator.MapDomIssueCode(apiCode);
                                        if (!string.IsNullOrEmpty(badge))
                                        {
                                            return FinalizeModerationResult(account, (true, badge, reason), dispatchAlert);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"[CheckModerationStatusAsync] usermoderation API probe skipped: {ex.Message}");
                    }
                }

                // 2. Fallback / secondary check via www.roblox.com/home redirect inspection with its own timeout budget
                using var webCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                webCts.CancelAfter(TimeSpan.FromMilliseconds(2500));

                using var request = AppServices.AccountSessionClient.CreateAuthenticatedGetRequest(
                    account,
                    "https://www.roblox.com/home",
                    "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                    token);

                using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, webCts.Token).ConfigureAwait(false);
                if (response == null) return (false, null, null);

                AppServices.AccountSessionClient.TryUpdateRotatedCookie(account, response);

                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    string okUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? string.Empty;
                    string html = await response.Content.ReadAsStringAsync(webCts.Token).ConfigureAwait(false);
                    if (IsUnauthenticatedModerationResponse(html) || IsLoginPageHtml(html))
                    {
                        account.AccountStatusIssue = string.Empty;
                        return (false, null, null);
                    }
                    return FinalizeModerationResult(account, ParseModerationText(html, okUrl), dispatchAlert);
                }

                string redirectLocation = response.Headers.Location?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(redirectLocation))
                {
                    if (!Uri.TryCreate(new Uri("https://www.roblox.com/home"), redirectLocation, out Uri? redirUri) ||
                        redirUri.Scheme != Uri.UriSchemeHttps ||
                        (!redirUri.Host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) &&
                         !redirUri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase)))
                    {
                        return (false, null, null);
                    }

                    if (redirectLocation.Contains("/home", StringComparison.OrdinalIgnoreCase) ||
                        redirectLocation.Contains("/discover", StringComparison.OrdinalIgnoreCase) ||
                        redirectLocation.Contains("/games", StringComparison.OrdinalIgnoreCase) ||
                        redirectLocation.Equals("/", StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, null, null);
                    }

                    if (redirectLocation.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
                        redirectLocation.Contains("NewLogin", StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, null, null);
                    }

                    if (RobloxModerationHelper.IsModerationOrLockedUrl(redirectLocation))
                    {
                        try
                        {
                            if (!Uri.TryCreate(new Uri("https://www.roblox.com/home"), redirectLocation, out Uri? safeModUri) ||
                                safeModUri.Scheme != Uri.UriSchemeHttps ||
                                (!safeModUri.Host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) &&
                                 !safeModUri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase)))
                            {
                                return (false, null, null);
                            }
                            string targetUrl = safeModUri.AbsoluteUri;

                            using var modReq = AppServices.AccountSessionClient.CreateAuthenticatedGetRequest(account, targetUrl, "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", tokenOverride: token);
                            using var modRes = await httpClient.SendAsync(modReq, HttpCompletionOption.ResponseContentRead, webCts.Token).ConfigureAwait(false);
                            if (modRes != null)
                            {
                                AppServices.AccountSessionClient.TryUpdateRotatedCookie(account, modRes);
                                if (modRes.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                                {
                                    account.AccountStatusIssue = string.Empty;
                                    return (false, null, null);
                                }

                                string modRedirect = modRes.Headers.Location?.ToString() ?? string.Empty;
                                if (modRedirect.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
                                    modRedirect.Contains("NewLogin", StringComparison.OrdinalIgnoreCase))
                                {
                                    account.AccountStatusIssue = string.Empty;
                                    return (false, null, null);
                                }

                                string modHtml = await modRes.Content.ReadAsStringAsync(webCts.Token).ConfigureAwait(false);
                                if (IsUnauthenticatedModerationResponse(modHtml) || IsLoginPageHtml(modHtml))
                                {
                                    account.AccountStatusIssue = string.Empty;
                                    return (false, null, null);
                                }

                                if (modRes.IsSuccessStatusCode)
                                {
                                    var parsed = ParseModerationText(modHtml, redirectLocation);
                                    if (parsed.IsBanned)
                                        return FinalizeModerationResult(account, parsed, dispatchAlert);
                                }
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug($"[CheckModerationStatusAsync] Moderation target fetch failed: {ex.Message}");
                        }
                        return FinalizeModerationResult(account, ParseModerationText(string.Empty, redirectLocation), dispatchAlert);
                    }
                }

                return (false, null, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.Debug($"[CheckModerationStatusAsync] {account.Username}: Moderation probe timed out (presumed normal).");
                return (false, null, null);
            }
            catch (Exception ex)
            {
                _logger.Debug($"[CheckModerationStatusAsync] {account.Username}: {ex.Message}");
                return (false, null, null);
            }
        }

        private (bool IsBanned, string? IssueBadge, string? Reason) FinalizeModerationResult(
            Account account,
            (bool IsBanned, string? IssueBadge, string? Reason) result,
            bool dispatchAlert) =>
            dispatchAlert ? DispatchModerationAlertIfBanned(account, result) : result;

        internal static WebhookAlert BuildModerationAlert(Account account, string? issueBadge, string? reason)
        {
            string message = reason ?? issueBadge ?? Shiro.Services.LocalizationService.Get("FastLogin.AccountLockedOrModerated");
            return new WebhookAlert
            {
                EventType = WebhookEventType.ModerationWarning,
                AccountUsername = account.Username,
                Description = $"Account {account.Username} received moderation notice: {message}",
                Timestamp = DateTimeOffset.UtcNow
            };
        }

        public (bool IsBanned, string? IssueBadge, string? Reason) DispatchModerationAlertIfBanned(
            Account account,
            (bool IsBanned, string? IssueBadge, string? Reason) result)
        {
            if (result.IsBanned)
            {
                WebhookNotificationService.EnqueueAlert(BuildModerationAlert(account, result.IssueBadge, result.Reason));
            }
            return result;
        }

        public static bool IsUnauthenticatedModerationResponse(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            return content.Contains("user is not authenticated", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("\"code\":9002", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("\"code\": 9002", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsLoginPageHtml(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            return content.Contains("id=\"login-username\"", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("id=\"login-password\"", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("class=\"login-base-container\"", StringComparison.OrdinalIgnoreCase);
        }

        public static (bool IsBanned, string? IssueBadge, string? Reason) ParseModerationText(string content, string? url = null)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                if (!string.IsNullOrEmpty(url))
                {
                    string lowUrl = url.ToLowerInvariant();
                    if (lowUrl.Contains("/security-notification") || lowUrl.Contains("/account-locked"))
                        return (true, Shiro.Services.LocalizationService.Get("FastLogin.SecurityLockoutShort"), Shiro.Services.LocalizationService.Get("Moderation.AccountLockedReason"));
                    if (lowUrl.Contains("/not-approved") || lowUrl.Contains("/banned") || lowUrl.Contains("/account-status") || lowUrl.Contains("/moderation"))
                        return (true, Shiro.Services.LocalizationService.Get("FastLogin.LockedOrBannedBadge"), Shiro.Services.LocalizationService.Get("FastLogin.AccountLockedOrModerated"));
                }
                return (false, null, null);
            }

            if (IsUnauthenticatedModerationResponse(content))
                return (false, null, null);

            if (content.Contains('{', StringComparison.Ordinal))
            {
                string structuredCode = FastLoginCoordinator.ClassifyAuthOrModerationApiPayload(content);
                if (!string.IsNullOrEmpty(structuredCode) &&
                    structuredCode != "UNAUTHENTICATED_MODERATION" &&
                    structuredCode != "WRONG_PASSWORD")
                {
                    var (badge, reason) = FastLoginCoordinator.MapDomIssueCode(structuredCode);
                    if (!string.IsNullOrEmpty(badge))
                        return (true, badge, reason);
                }
            }

            string text = content.ToLowerInvariant();

            bool hasModerationMarker =
                text.Contains("reviewed:", StringComparison.Ordinal) ||
                text.Contains("moderator note", StringComparison.Ordinal) ||
                text.Contains("terms of use violation", StringComparison.Ordinal) ||
                text.Contains("your account has been", StringComparison.Ordinal) ||
                text.Contains("punishment:", StringComparison.Ordinal) ||
                text.Contains("reactivate my account", StringComparison.Ordinal) ||
                text.Contains("not-approved-page", StringComparison.Ordinal) ||
                text.Contains("account locked", StringComparison.Ordinal) ||
                (text.Contains("community standards", StringComparison.Ordinal) && text.Contains("violation", StringComparison.Ordinal)) ||
                (text.Contains("i agree", StringComparison.Ordinal) && text.Contains("warning", StringComparison.Ordinal));

            if (!hasModerationMarker && !text.Contains("banned", StringComparison.Ordinal))
            {
                return (false, null, null);
            }

            if (ContainsExactBanDays(text, 14))
                return (true, Shiro.Services.LocalizationService.Get("FastLogin.BanFourteenDaysShort"), Shiro.Services.LocalizationService.Get("Moderation.BanFourteenDaysReason"));
            if (ContainsExactBanDays(text, 7))
                return (true, Shiro.Services.LocalizationService.Get("FastLogin.BanSevenDaysShort"), Shiro.Services.LocalizationService.Get("Moderation.BanSevenDaysReason"));
            if (ContainsExactBanDays(text, 3))
                return (true, Shiro.Services.LocalizationService.Get("FastLogin.BanThreeDaysShort"), Shiro.Services.LocalizationService.Get("Moderation.BanThreeDaysReason"));
            if (ContainsExactBanDays(text, 1))
                return (true, Shiro.Services.LocalizationService.Get("FastLogin.BanOneDayShort"), Shiro.Services.LocalizationService.Get("Moderation.BanOneDayReason"));
            if (text.Contains("deleted", StringComparison.Ordinal) || text.Contains("terminated", StringComparison.Ordinal) || text.Contains("permanently", StringComparison.Ordinal))
                return (true, Shiro.Services.LocalizationService.Get("FastLogin.BanPermanentShort"), Shiro.Services.LocalizationService.Get("Moderation.BanPermanentReason"));
            if (hasModerationMarker && text.Contains("warning", StringComparison.Ordinal))
                return (true, Shiro.Services.LocalizationService.Get("Msg_WarningTitle"), Shiro.Services.LocalizationService.Get("Moderation.WarningNoticeReason"));
            if (text.Contains("locked", StringComparison.Ordinal) || text.Contains("security notification", StringComparison.Ordinal) || text.Contains("reset your password", StringComparison.Ordinal))
                return (true, Shiro.Services.LocalizationService.Get("FastLogin.SecurityLockoutShort"), Shiro.Services.LocalizationService.Get("Moderation.AccountLockedReason"));

            if (hasModerationMarker)
                return (true, Shiro.Services.LocalizationService.Get("FastLogin.LockedOrBannedBadge"), Shiro.Services.LocalizationService.Get("FastLogin.AccountLockedOrModerated"));

            return (false, null, null);
        }

        private static bool ContainsExactBanDays(string text, int days) =>
            ContainsStandaloneToken(text, $"{days} day") || ContainsStandaloneToken(text, $"{days}-day");

        private static bool ContainsStandaloneToken(string text, string token)
        {
            int idx = 0;
            while ((idx = text.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
            {
                if (idx == 0 || !char.IsAsciiDigit(text[idx - 1]))
                    return true;
                idx += token.Length;
            }
            return false;
        }
    }
}
