#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed class RobloxModerationService
    {
        public static RobloxModerationService Instance { get; } = new();

        public async Task<(bool IsBanned, string? IssueBadge, string? Reason)> CheckModerationStatusAsync(
            Account account,
            string token,
            CancellationToken cancellationToken)
        {
            if (account == null) return (false, null, null);

            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linkedCts.CancelAfter(TimeSpan.FromMilliseconds(2500));

                HttpClient httpClient = account.GetSessionHttpClient(allowAutoRedirect: false);
                using var request = account.CreateAuthenticatedGetRequest(
                    "https://www.roblox.com/home",
                    "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                    token);

                using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
                if (response == null) return (false, null, null);

                account.TryUpdateRotatedCookie(response);

                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    return (false, null, null);
                }

                string redirectLocation = response.Headers.Location?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(redirectLocation))
                {
                    if (redirectLocation.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                        Uri.TryCreate(redirectLocation, UriKind.Absolute, out Uri? redirUri) &&
                        redirUri != null &&
                        !redirUri.Host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) &&
                        !redirUri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase))
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

                    if (redirectLocation.Contains("/not-approved", StringComparison.OrdinalIgnoreCase) ||
                        redirectLocation.Contains("/moderation", StringComparison.OrdinalIgnoreCase) ||
                        redirectLocation.Contains("/banned", StringComparison.OrdinalIgnoreCase) ||
                        redirectLocation.Contains("/account-status", StringComparison.OrdinalIgnoreCase) ||
                        redirectLocation.Contains("/account-locked", StringComparison.OrdinalIgnoreCase) ||
                        redirectLocation.Contains("/security-notification", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string targetUrl = redirectLocation.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                ? redirectLocation
                                : $"https://www.roblox.com{redirectLocation}";

                            using var modReq = account.CreateAuthenticatedGetRequest(targetUrl, "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", tokenOverride: token);
                            using var modRes = await httpClient.SendAsync(modReq, HttpCompletionOption.ResponseContentRead, linkedCts.Token).ConfigureAwait(false);
                            if (modRes != null)
                            {
                                account.TryUpdateRotatedCookie(modRes);
                                if (modRes.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                                {
                                    account.AccountStatusIssue = string.Empty;
                                    return (false, null, null);
                                }

                                string modHtml = await modRes.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
                                if (IsUnauthenticatedModerationResponse(modHtml))
                                {
                                    account.AccountStatusIssue = string.Empty;
                                    return (false, null, null);
                                }

                                if (modRes.IsSuccessStatusCode)
                                {
                                    var parsed = ParseModerationText(modHtml, redirectLocation);
                                    if (parsed.IsBanned)
                                        return DispatchModerationAlertIfBanned(account, parsed);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"[CheckModerationStatusAsync] Moderation target fetch failed: {ex.Message}");
                        }
                        return DispatchModerationAlertIfBanned(account, ParseModerationText(string.Empty, redirectLocation));
                    }
                }

                string finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? string.Empty;

                if (!string.IsNullOrEmpty(finalUrl) &&
                    !finalUrl.Contains("/not-approved", StringComparison.OrdinalIgnoreCase) &&
                    !finalUrl.Contains("/moderation", StringComparison.OrdinalIgnoreCase) &&
                    !finalUrl.Contains("/banned", StringComparison.OrdinalIgnoreCase) &&
                    !finalUrl.Contains("/account-status", StringComparison.OrdinalIgnoreCase) &&
                    !finalUrl.Contains("/account-locked", StringComparison.OrdinalIgnoreCase) &&
                    !finalUrl.Contains("/security-notification", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, null, null);
                }

                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    string html = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
                    if (IsUnauthenticatedModerationResponse(html))
                    {
                        account.AccountStatusIssue = string.Empty;
                        return (false, null, null);
                    }
                    return DispatchModerationAlertIfBanned(account, ParseModerationText(html, finalUrl));
                }

                return (false, null, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Program.Logger?.Debug($"[CheckModerationStatusAsync] {account.Username}: Moderation probe timed out (presumed normal).");
                return (false, null, null);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[CheckModerationStatusAsync] {account.Username}: {ex.Message}");
                return (false, null, null);
            }
        }

        public (bool IsBanned, string? IssueBadge, string? Reason) DispatchModerationAlertIfBanned(
            Account account,
            (bool IsBanned, string? IssueBadge, string? Reason) result)
        {
            if (result.IsBanned)
            {
                string message = result.Reason ?? result.IssueBadge ?? "Tài khoản bị khóa hoặc kiểm duyệt";
                WebhookNotificationService.EnqueueAlert(new WebhookAlert
                {
                    EventType = WebhookEventType.ModerationWarning,
                    AccountUsername = account.Username,
                    Description = $"Account {account.Username} received moderation notice: {message}",
                    Timestamp = DateTimeOffset.UtcNow
                });
            }
            return result;
        }

        public static bool IsUnauthenticatedModerationResponse(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            string text = content.ToLowerInvariant();
            return text.Contains("user is not authenticated", StringComparison.Ordinal) ||
                   text.Contains("\"code\":9002", StringComparison.Ordinal) ||
                   text.Contains("\"code\": 9002", StringComparison.Ordinal);
        }

        public static (bool IsBanned, string? IssueBadge, string? Reason) ParseModerationText(string content, string? url = null)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                if (!string.IsNullOrEmpty(url))
                {
                    string lowUrl = url.ToLowerInvariant();
                    if (lowUrl.Contains("/security-notification") || lowUrl.Contains("/account-locked"))
                        return (true, "Khóa bảo mật", "Tài khoản bị khóa bảo mật (Account Locked)");
                    if (lowUrl.Contains("/not-approved") || lowUrl.Contains("/banned") || lowUrl.Contains("/account-status") || lowUrl.Contains("/moderation"))
                        return (true, "Bị khóa / Ban", "Tài khoản bị khóa hoặc kiểm duyệt");
                }
                return (false, null, null);
            }

            if (IsUnauthenticatedModerationResponse(content))
                return (false, null, null);

            string text = content.ToLowerInvariant();

            bool hasModerationMarker =
                text.Contains("reviewed:") ||
                text.Contains("moderator note") ||
                text.Contains("terms of use violation") ||
                text.Contains("your account has been") ||
                text.Contains("punishment:") ||
                text.Contains("reactivate my account") ||
                text.Contains("tiêu chuẩn cộng đồng") ||
                text.Contains("lý do:") ||
                text.Contains("i agree");

            if (!hasModerationMarker && !text.Contains("banned") && !text.Contains("warning"))
            {
                return (false, null, null);
            }

            if (text.Contains("7 day") || text.Contains("7-day") || text.Contains("7 ngày"))
                return (true, "Ban 7 ngày", "Tài khoản bị cấm 7 ngày do vi phạm Tiêu chuẩn cộng đồng");
            if (text.Contains("3 day") || text.Contains("3-day") || text.Contains("3 ngày"))
                return (true, "Ban 3 ngày", "Tài khoản bị cấm 3 ngày do vi phạm Tiêu chuẩn cộng đồng");
            if (text.Contains("1 day") || text.Contains("1-day") || text.Contains("1 ngày"))
                return (true, "Ban 1 ngày", "Tài khoản bị cấm 1 ngày do vi phạm Tiêu chuẩn cộng đồng");
            if (text.Contains("14 day") || text.Contains("14-day") || text.Contains("14 ngày"))
                return (true, "Ban 14 ngày", "Tài khoản bị cấm 14 ngày do vi phạm Tiêu chuẩn cộng đồng");
            if (text.Contains("deleted") || text.Contains("terminated") || text.Contains("đã bị xóa") || text.Contains("vĩnh viễn"))
                return (true, "Ban vĩnh viễn", "Tài khoản đã bị xóa vĩnh viễn (Terminated)");
            if (text.Contains("warning") || text.Contains("cảnh báo"))
                return (true, "Cảnh báo", "Tài khoản nhận cảnh báo kiểm duyệt, cần xác nhận để mở khóa");
            if (text.Contains("locked") || text.Contains("khóa bảo mật") || text.Contains("security notification") || text.Contains("reset your password"))
                return (true, "Khóa bảo mật", "Tài khoản bị khóa bảo mật (Account Locked)");

            if (hasModerationMarker)
                return (true, "Bị khóa / Ban", "Tài khoản bị khóa hoặc kiểm duyệt");

            return (false, null, null);
        }
    }
}
