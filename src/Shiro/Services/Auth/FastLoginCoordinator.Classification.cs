#nullable enable
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Shiro.Classes;

namespace Shiro.Services
{
    public static partial class FastLoginCoordinator
    {
        public static async Task<(string? IssueBadge, string? Reason)> DetectDomAccountIssueAsync(CoreWebView2? core, bool hasQueueAccounts)
        {
            if (core == null || !hasQueueAccounts)
                return (null, null);

            try
            {
                string currentUrl = core.Source ?? string.Empty;
                bool isModUrl = IsModerationOrLockedUrl(currentUrl);
                string rawJson = await core.ExecuteScriptAsync(@"
                    (() => {
                        try {
                            const url = (window.location.href || '').toLowerCase();
                            const isMod = url.includes('/not-approved') || url.includes('/account-status') || url.includes('/banned') || url.includes('/moderation') || url.includes('/security-notification') || url.includes('/account-locked');
                            const nodes = document.querySelectorAll('#login-form-error, .form-error, .text-error, .not-approved-container');
                            let combined = '';
                            for (const n of nodes) {
                                if (n && n.offsetParent !== null && n.innerText) {
                                    combined += ' ' + n.innerText;
                                }
                            }
                            if (isMod && document.body && document.body.innerText) {
                                combined += ' ' + document.body.innerText;
                            }
                            return combined.trim();
                        } catch (_) { return ''; }
                    })()
                ");

                string? combinedText = null;
                if (!string.IsNullOrWhiteSpace(rawJson) && rawJson != "null")
                {
                    try { combinedText = JsonSerializer.Deserialize(rawJson, ShiroAppJson.Default.String); }
                    catch (Exception) { combinedText = rawJson.Trim('"'); }
                }

                return ClassifyDomIssueText(combinedText, isModUrl);
            }
            catch (Exception ex)
            {
                _logger.Debug($"DetectDomAccountIssueAsync failed: {ex.Message}");
                return (null, null);
            }
        }

        /// <summary>
        /// Deterministically classifies structured Roblox Auth v2 (<c>errors</c> array) and Moderation API
        /// (<c>punishmentTypeData</c> / <c>punishmentType</c>) JSON payloads without fragile DOM innerText scraping.
        /// </summary>
        public static string ClassifyAuthOrModerationApiPayload(string? jsonPayload, int httpStatusCode = 0)
        {
            if (string.IsNullOrWhiteSpace(jsonPayload))
                return string.Empty;

            int braceStart = jsonPayload.IndexOf('{');
            int braceEnd = jsonPayload.LastIndexOf('}');
            if (braceStart < 0 || braceEnd <= braceStart)
                return string.Empty;

            string candidateJson = jsonPayload.Substring(braceStart, braceEnd - braceStart + 1);
            try
            {
                using var doc = JsonDocument.Parse(candidateJson);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return string.Empty;

                if (root.TryGetProperty("errors", out var errorsProp) && errorsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var err in errorsProp.EnumerateArray())
                    {
                        if (err.ValueKind != JsonValueKind.Object)
                            continue;

                        int code = err.TryGetProperty("code", out var codeProp) && codeProp.TryGetInt32(out int c) ? c : -1;
                        string msg = err.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String
                            ? (msgProp.GetString() ?? string.Empty)
                            : string.Empty;

                        if (code == 9002 || msg.Contains("not authenticated", StringComparison.OrdinalIgnoreCase))
                            return "UNAUTHENTICATED_MODERATION";

                        if (code == 1 || msg.Contains("incorrect username or password", StringComparison.OrdinalIgnoreCase))
                            return "WRONG_PASSWORD";

                        if (code == 4 || code == 10 || msg.Contains("locked", StringComparison.OrdinalIgnoreCase))
                            return "ACCOUNT_LOCKED";

                        if (msg.Contains("moderated", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("not approved", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("banned", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("suspended", StringComparison.OrdinalIgnoreCase))
                        {
                            return "ACCOUNT_BANNED";
                        }
                    }
                }

                string? punishmentType = null;
                if (root.TryGetProperty("punishmentTypeData", out var ptd) &&
                    ptd.ValueKind == JsonValueKind.Object &&
                    ptd.TryGetProperty("punishmentType", out var ptProp) &&
                    ptProp.ValueKind == JsonValueKind.String)
                {
                    punishmentType = ptProp.GetString();
                }
                else if (root.TryGetProperty("punishmentType", out var rootPtProp) && rootPtProp.ValueKind == JsonValueKind.String)
                {
                    punishmentType = rootPtProp.GetString();
                }

                if (!string.IsNullOrWhiteSpace(punishmentType))
                {
                    string pt = punishmentType.Trim();
                    if (pt.Contains("14", StringComparison.OrdinalIgnoreCase))
                        return "BAN_14_DAYS";
                    if (pt.Contains("7", StringComparison.OrdinalIgnoreCase))
                        return "BAN_7_DAYS";
                    if (pt.Contains("3", StringComparison.OrdinalIgnoreCase))
                        return "BAN_3_DAYS";
                    if (pt.Contains("1", StringComparison.OrdinalIgnoreCase))
                        return "BAN_1_DAY";
                    if (pt.Contains("Delete", StringComparison.OrdinalIgnoreCase) || pt.Contains("Terminate", StringComparison.OrdinalIgnoreCase))
                        return "TERMINATED";
                    if (pt.Contains("Warn", StringComparison.OrdinalIgnoreCase))
                        return "WARNING";

                    return "ACCOUNT_BANNED";
                }

                bool hasPunishedUser =
                    (root.TryGetProperty("punishedUserId", out var puid) && puid.ValueKind == JsonValueKind.Number && puid.TryGetInt64(out long uidVal) && uidVal > 0) ||
                    (root.TryGetProperty("interventionId", out var intId) && intId.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(intId.GetString())) ||
                    (root.TryGetProperty("messageToUser", out var mtu) && mtu.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mtu.GetString()));

                if (hasPunishedUser)
                {
                    DateTimeOffset? beginDate = null;
                    DateTimeOffset? endDate = null;
                    if (root.TryGetProperty("beginDate", out var bdProp) && bdProp.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(bdProp.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var bd))
                    {
                        beginDate = bd;
                    }
                    if (root.TryGetProperty("endDate", out var edProp) && edProp.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(edProp.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var ed))
                    {
                        endDate = ed;
                    }

                    if (endDate.HasValue)
                    {
                        double days = beginDate.HasValue
                            ? Math.Round((endDate.Value - beginDate.Value).TotalDays)
                            : Math.Ceiling((endDate.Value - DateTimeOffset.UtcNow).TotalDays);
                        if (days >= 13) return "BAN_14_DAYS";
                        if (days >= 6) return "BAN_7_DAYS";
                        if (days >= 2) return "BAN_3_DAYS";
                        if (days >= 0.5) return "BAN_1_DAY";
                        return "ACCOUNT_BANNED";
                    }

                    string msgToUser = root.TryGetProperty("messageToUser", out var mProp) && mProp.ValueKind == JsonValueKind.String
                        ? (mProp.GetString() ?? string.Empty)
                        : string.Empty;
                    if (msgToUser.Contains("deleted", StringComparison.OrdinalIgnoreCase) ||
                        msgToUser.Contains("terminated", StringComparison.OrdinalIgnoreCase) ||
                        msgToUser.Contains("closed", StringComparison.OrdinalIgnoreCase))
                    {
                        return "TERMINATED";
                    }

                    if (root.TryGetProperty("endDate", out var edNullProp) && edNullProp.ValueKind == JsonValueKind.Null)
                    {
                        // Permanent ban or warning: check punishmentId / badUtterances / messageToUser
                        if (msgToUser.Contains("warn", StringComparison.OrdinalIgnoreCase))
                            return "WARNING";
                    }

                    return "ACCOUNT_BANNED";
                }

                if (httpStatusCode == 401 && candidateJson.Contains("9002", StringComparison.Ordinal))
                    return "UNAUTHENTICATED_MODERATION";
            }
            catch (Exception ex)
            {
                _logger.Debug($"[FastLoginCoordinator] ClassifyAuthOrModerationApiPayload parse skipped: {ex.Message}");
            }

            return string.Empty;
        }
    }
}
