#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro
{
    public partial class Account
    {
        private async Task<(bool IsBanned, string? IssueBadge, string? Reason)> CheckModerationStatusAsync(string token, CancellationToken cancellationToken)
        {
            try
            {
                HttpClient httpClient = GetSessionHttpClient();
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.roblox.com/not-approved");
                request.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={token}");
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

                using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (response == null) return (false, null, null);

                TryUpdateRotatedCookie(response);

                string finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? string.Empty;

                // When account is not under moderation, Roblox redirects to /home or /discover
                if (!finalUrl.Contains("/not-approved", StringComparison.OrdinalIgnoreCase) &&
                    !finalUrl.Contains("/moderation", StringComparison.OrdinalIgnoreCase) &&
                    !finalUrl.Contains("/banned", StringComparison.OrdinalIgnoreCase) &&
                    !finalUrl.Contains("/account-status", StringComparison.OrdinalIgnoreCase) &&
                    !finalUrl.Contains("/account-locked", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, null, null);
                }

                string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseModerationText(html, finalUrl);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[CheckModerationStatusAsync] {Username}: {ex.Message}");
                return (false, null, null);
            }
        }

        public static (bool IsBanned, string? IssueBadge, string? Reason) ParseModerationText(string content, string? url = null)
        {
            string combined = (content + " " + (url ?? "")).ToLowerInvariant();

            if (combined.Contains("7 day") || combined.Contains("7-day") || combined.Contains("7 ngày"))
                return (true, "⛔ Ban 7 ngày", "Tài khoản bị cấm 7 ngày do vi phạm Tiêu chuẩn cộng đồng");
            if (combined.Contains("3 day") || combined.Contains("3-day") || combined.Contains("3 ngày"))
                return (true, "⛔ Ban 3 ngày", "Tài khoản bị cấm 3 ngày do vi phạm Tiêu chuẩn cộng đồng");
            if (combined.Contains("1 day") || combined.Contains("1-day") || combined.Contains("1 ngày"))
                return (true, "⛔ Ban 1 ngày", "Tài khoản bị cấm 1 ngày do vi phạm Tiêu chuẩn cộng đồng");
            if (combined.Contains("14 day") || combined.Contains("14-day") || combined.Contains("14 ngày"))
                return (true, "⛔ Ban 14 ngày", "Tài khoản bị cấm 14 ngày do vi phạm Tiêu chuẩn cộng đồng");
            if (combined.Contains("deleted") || combined.Contains("terminated") || combined.Contains("đã bị xóa") || combined.Contains("vĩnh viễn"))
                return (true, "⛔ Ban vĩnh viễn", "Tài khoản đã bị xóa vĩnh viễn (Terminated)");
            if (combined.Contains("warning") || combined.Contains("cảnh báo"))
                return (true, "⚠️ Cảnh báo", "Tài khoản nhận cảnh báo kiểm duyệt, cần xác nhận để mở khóa");
            if (combined.Contains("locked") || combined.Contains("khóa bảo mật") || combined.Contains("security notification") || combined.Contains("reset your password"))
                return (true, "⛔ Khóa bảo mật", "Tài khoản bị khóa bảo mật (Account Locked)");
            if (combined.Contains("not-approved") || combined.Contains("moderation") || combined.Contains("banned") || combined.Contains("suspended"))
                return (true, "⛔ Bị khóa / Ban", "Tài khoản bị khóa hoặc kiểm duyệt");

            return (false, null, null);
        }
    }
}
