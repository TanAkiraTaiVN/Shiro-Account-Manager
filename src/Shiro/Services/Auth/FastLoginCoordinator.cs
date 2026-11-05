#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Shiro.Classes;

namespace Shiro.Services
{
    public enum FastLoginCaptureStatus
    {
        RetryPolling = 0,
        Success = 1,
        UsernameMismatch = 2,
        UnauthenticatedModeration = 3,
        AccountIssue = 4
    }

    public enum IgnoredCookieAction
    {
        DeleteCookie = 0,
        InspectModerationDom = 1,
        WaitForModerationRedirect = 2,
        RedirectToLogin = 3,
        CaptureReactivatedSession = 4
    }

    public sealed record FastLoginCaptureResult(
        FastLoginCaptureStatus Status,
        Account? Account = null,
        string? IssueBadge = null,
        string? Reason = null);

    /// <summary>
    /// Coordinates WebView2 session capture, DOM moderation classification, cookie hygiene,
    /// and credential auto-fill for <see cref="AvaloniaUI.Views.FastLoginDialog"/>.
    /// Extracted from UI code-behind so authentication and moderation edge cases can be unit-tested.
    /// </summary>
    public static partial class FastLoginCoordinator
    {
        private static IAppLogger _logger = AppServices.Logger;
        public static IAppLogger Logger { get => _logger; set => _logger = value ?? throw new ArgumentNullException(nameof(value)); }

        public static string FingerprintCookie(string? value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

        public static bool IsModerationOrLockedUrl(string? url)
        {
            if (string.IsNullOrEmpty(url))
                return false;

            return url.Contains("/not-approved", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/account-status", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/banned", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/moderation", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/account-locked", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/security-notification", StringComparison.OrdinalIgnoreCase);
        }

        public static IgnoredCookieAction EvaluateIgnoredCookieAction(
            string? currentUrl,
            bool isRestoredModerationCookie,
            bool hasSeenModerationPage)
        {
            if (isRestoredModerationCookie &&
                !string.IsNullOrEmpty(currentUrl) &&
                (currentUrl.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
                 currentUrl.Contains("/newlogin", StringComparison.OrdinalIgnoreCase)))
            {
                return IgnoredCookieAction.RedirectToLogin;
            }

            if (IsModerationOrLockedUrl(currentUrl))
                return IgnoredCookieAction.InspectModerationDom;

            if (isRestoredModerationCookie)
            {
                if (hasSeenModerationPage &&
                    !string.IsNullOrEmpty(currentUrl) &&
                    (currentUrl.Contains("/home", StringComparison.OrdinalIgnoreCase) ||
                     currentUrl.Contains("/discover", StringComparison.OrdinalIgnoreCase) ||
                     currentUrl.Contains("/charts", StringComparison.OrdinalIgnoreCase) ||
                     currentUrl.Contains("/games", StringComparison.OrdinalIgnoreCase) ||
                     currentUrl.Contains("/users", StringComparison.OrdinalIgnoreCase)))
                {
                    return IgnoredCookieAction.CaptureReactivatedSession;
                }

                return IgnoredCookieAction.WaitForModerationRedirect;
            }

            return IgnoredCookieAction.DeleteCookie;
        }

        public static string ClassifyDomIssueCode(string? combinedText, bool isModUrl)
        {
            if (string.IsNullOrWhiteSpace(combinedText))
                return isModUrl ? "LOCKED_OR_BANNED" : string.Empty;

            if (combinedText.Contains('{', StringComparison.Ordinal))
            {
                string structured = ClassifyAuthOrModerationApiPayload(combinedText);
                if (!string.IsNullOrEmpty(structured))
                    return structured;
            }

            string combined = combinedText.ToLowerInvariant();

            if (combined.Contains("user is not authenticated", StringComparison.Ordinal) ||
                combined.Contains("\"code\":9002", StringComparison.Ordinal) ||
                combined.Contains("\"code\": 9002", StringComparison.Ordinal))
            {
                return "UNAUTHENTICATED_MODERATION";
            }

            if (combined.Contains("incorrect username or password", StringComparison.Ordinal) ||
                combined.Contains("invalid username or password", StringComparison.Ordinal) ||
                combined.Contains(Shiro.Services.LocalizationService.Get("FastLogin.WrongPasswordKeyword"), StringComparison.Ordinal) ||
                combined.Contains(Shiro.Services.LocalizationService.Get("FastLogin.InvalidCredentialsKeyword"), StringComparison.Ordinal))
            {
                return "WRONG_PASSWORD";
            }

            if (isModUrl)
            {
                if (combined.Contains("14 day", StringComparison.Ordinal) || combined.Contains("14-day", StringComparison.Ordinal) || combined.Contains(Shiro.Services.LocalizationService.Get("FastLogin.FourteenDaysKeyword"), StringComparison.Ordinal))
                    return "BAN_14_DAYS";
                if (combined.Contains("7 day", StringComparison.Ordinal) || combined.Contains("7-day", StringComparison.Ordinal) || combined.Contains(Shiro.Services.LocalizationService.Get("FastLogin.SevenDaysKeyword"), StringComparison.Ordinal))
                    return "BAN_7_DAYS";
                if (combined.Contains("3 day", StringComparison.Ordinal) || combined.Contains("3-day", StringComparison.Ordinal) || combined.Contains(Shiro.Services.LocalizationService.Get("FastLogin.ThreeDaysKeyword"), StringComparison.Ordinal))
                    return "BAN_3_DAYS";
                if (combined.Contains("1 day", StringComparison.Ordinal) || combined.Contains("1-day", StringComparison.Ordinal) || combined.Contains(Shiro.Services.LocalizationService.Get("FastLogin.OneDayKeyword"), StringComparison.Ordinal))
                    return "BAN_1_DAY";
                if (combined.Contains("deleted", StringComparison.Ordinal) || combined.Contains("terminated", StringComparison.Ordinal) || combined.Contains(Shiro.Services.LocalizationService.Get("FastLogin.DeletedKeyword"), StringComparison.Ordinal) || combined.Contains(Shiro.Services.LocalizationService.Get("FastLogin.PermanentKeyword"), StringComparison.Ordinal))
                    return "TERMINATED";
                if (combined.Contains("warning", StringComparison.Ordinal) || combined.Contains(Shiro.Services.LocalizationService.Get("FastLogin.WarningKeyword"), StringComparison.Ordinal))
                    return "WARNING";
                if (combined.Contains("locked", StringComparison.Ordinal) || combined.Contains(Shiro.Services.LocalizationService.Get("FastLogin.LockedKeyword"), StringComparison.Ordinal) || combined.Contains("reset your password", StringComparison.Ordinal) || combined.Contains("security notification", StringComparison.Ordinal))
                    return "ACCOUNT_LOCKED";
                if (combined.Contains("not approved", StringComparison.Ordinal) || combined.Contains("banned", StringComparison.Ordinal) || combined.Contains("moderated", StringComparison.Ordinal) || combined.Contains("suspended", StringComparison.Ordinal))
                    return "ACCOUNT_BANNED";
                return "LOCKED_OR_BANNED";
            }

            if (combined.Contains("locked", StringComparison.Ordinal) || combined.Contains(Shiro.Services.LocalizationService.Get("FastLogin.LockedKeyword"), StringComparison.Ordinal) || combined.Contains("reset your password", StringComparison.Ordinal) || combined.Contains("security notification", StringComparison.Ordinal))
                return "ACCOUNT_LOCKED";

            return string.Empty;
        }

        public static (string? IssueBadge, string? Reason) MapDomIssueCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return (null, null);

            return code.Trim().Trim('"') switch
            {
                "UNAUTHENTICATED_MODERATION" => ("UNAUTHENTICATED_MODERATION", Shiro.Services.LocalizationService.Get("FastLogin.CookieExpiredOpeningBan")),
                "BAN_7_DAYS" => (Shiro.Services.LocalizationService.Get("FastLogin.BanSevenDaysShort"), Shiro.Services.LocalizationService.Get("FastLogin.BannedSevenDays")),
                "BAN_3_DAYS" => (Shiro.Services.LocalizationService.Get("FastLogin.BanThreeDaysShort"), Shiro.Services.LocalizationService.Get("FastLogin.BannedThreeDays")),
                "BAN_1_DAY" => (Shiro.Services.LocalizationService.Get("FastLogin.BanOneDayShort"), Shiro.Services.LocalizationService.Get("FastLogin.BannedOneDay")),
                "BAN_14_DAYS" => (Shiro.Services.LocalizationService.Get("FastLogin.BanFourteenDaysShort"), Shiro.Services.LocalizationService.Get("FastLogin.BannedFourteenDays")),
                "TERMINATED" => (Shiro.Services.LocalizationService.Get("FastLogin.BanPermanentShort"), Shiro.Services.LocalizationService.Get("FastLogin.AccountDeletedPermanent")),
                "WARNING" => (Shiro.Services.LocalizationService.Get("Msg_WarningTitle"), Shiro.Services.LocalizationService.Get("FastLogin.ModerationWarning")),
                "ACCOUNT_LOCKED" => (Shiro.Services.LocalizationService.Get("FastLogin.SecurityLockoutShort"), Shiro.Services.LocalizationService.Get("FastLogin.SecurityLockout")),
                "ACCOUNT_BANNED" => (Shiro.Services.LocalizationService.Get("FastLogin.LockedOrBannedBadge"), Shiro.Services.LocalizationService.Get("FastLogin.AccountBannedOrSuspended")),
                "LOCKED_OR_BANNED" => (Shiro.Services.LocalizationService.Get("FastLogin.LockedOrBannedBadge"), Shiro.Services.LocalizationService.Get("FastLogin.AccountLockedOrModerated")),
                "WRONG_PASSWORD" => (Shiro.Services.LocalizationService.Get("MainWindow.WrongPasswordTitle"), Shiro.Services.LocalizationService.Get("FastLogin.IncorrectUsernameOrPassword")),
                _ => (null, null)
            };
        }

        public static (string? IssueBadge, string? Reason) ClassifyDomIssueText(string? currentUrlOrFlag, string? combinedText) =>
            MapDomIssueCode(ClassifyDomIssueCode(combinedText, IsModerationOrLockedUrl(currentUrlOrFlag)));

        public static (string? IssueBadge, string? Reason) ClassifyDomIssueText(string? combinedText, bool isModUrl) =>
            MapDomIssueCode(ClassifyDomIssueCode(combinedText, isModUrl));

        public static string GetLoginPageEnhancementScript() => @"
            try {
                const style = document.createElement('style');
                style.innerHTML = `
                    html, body { background-color: #0D0D11 !important; color: #E4E4E7; }
                    #onetrust-consent-sdk, #onetrust-banner-sdk, .cookie-banner, [aria-label*=""Cookie""], [id*=""cookie""] { display: none !important; visibility: hidden !important; }
                    #header, .navbar, #footer-container, .footer-container { display: none !important; }
                    .login-base-container { padding-top: 10px !important; margin: 0 auto !important; }
                    body { overflow-x: hidden !important; }
                `;
                document.head ? document.head.appendChild(style) : document.addEventListener('DOMContentLoaded', () => document.head.appendChild(style));
                window.addEventListener('DOMContentLoaded', () => {
                    try { if (window.OneTrust && window.OneTrust.AllowAll) window.OneTrust.AllowAll(); } catch(_) { /* ignore */ }
                    try {
                        const hookPass = () => {
                            const p = document.querySelector('#login-password') || document.querySelector('input[type=""password""]');
                            if (p && !p.__shiroHooked) {
                                p.__shiroHooked = true;
                                const postPass = () => {
                                    if (p.value && window.chrome && window.chrome.webview) {
                                        window.chrome.webview.postMessage(JSON.stringify({ type: 'shiro_password_captured', password: p.value }));
                                    }
                                };
                                p.addEventListener('input', postPass);
                                p.addEventListener('change', postPass);
                                const btn = document.querySelector('#login-button');
                                if (btn) btn.addEventListener('click', postPass, true);
                            }
                        };
                        hookPass();
                        setInterval(hookPass, 400);
                    } catch(_) { /* ignore */ }
                });
            } catch(_) { /* ignore */ }
        ";

        public static string BuildCredentialFillScript(string username, string? password, bool forceClick)
        {
            string userJson = JsonSerializer.Serialize(username, ShiroAppJson.Default.String);
            string passJson = JsonSerializer.Serialize(password ?? string.Empty, ShiroAppJson.Default.String);
            string hasPassJs = !string.IsNullOrEmpty(password) ? "true" : "false";
            string forceClickJs = forceClick ? "true" : "false";

            return $@"(() => {{
                try {{
                    const targetUser = {userJson}, targetPass = {passJson}, hasPass = {hasPassJs}, forceClick = {forceClickJs};
                    if (forceClick) window.__shiroAutoSubmittedForUser = '';

                    const setReactValue = (el, val, forceEvents = false) => {{
                        if (!el || val === undefined || val === null) return false;
                        if (!forceEvents && el.value === val) return true;
                        try {{
                            el.focus();
                            if (el._valueTracker) el._valueTracker.setValue('__shiro_unset__');
                            const proto = window.HTMLInputElement ? window.HTMLInputElement.prototype : Object.getPrototypeOf(el);
                            const desc = Object.getOwnPropertyDescriptor(proto, 'value');
                            if (desc && desc.set) desc.set.call(el, val); else el.value = val;
                            try {{ el.dispatchEvent(new InputEvent('input', {{ bubbles: true, cancelable: true, inputType: 'insertText', data: val }})); }}
                            catch (_) {{ el.dispatchEvent(new Event('input', {{ bubbles: true }})); }}
                            el.dispatchEvent(new Event('change', {{ bubbles: true }}));
                            return el.value === val;
                        }} catch (_) {{ return false; }}
                    }};

                    const findLoginButton = () => document.querySelector('#login-button') ||
                        document.querySelector('button[id*=""login-button""]') ||
                        Array.from(document.querySelectorAll('button, input[type=""submit""], [role=""button""]')).find(b => {{
                            const t = (b.innerText || b.textContent || b.value || '').trim().toLowerCase();
                            return t === 'log in' || t === 'login' || t === 'sign in' || t === 'đăng nhập';
                        }});

                    const isButtonDisabled = (btn) => !btn || btn.disabled || (btn.getAttribute('aria-disabled') || '').toLowerCase() === 'true';

                    const clickLoginButton = () => {{
                        const btn = findLoginButton();
                        if (btn && !isButtonDisabled(btn)) {{
                            try {{ btn.focus(); }} catch (_) {{}}
                            btn.click();
                            return true;
                        }}
                        return false;
                    }};

                    let attempts = 0, firstFilledAt = 0;
                    const stopWatching = () => {{
                        if (window.__shiroFillInterval) {{ clearInterval(window.__shiroFillInterval); window.__shiroFillInterval = null; }}
                        if (window.__shiroFillObserver) {{ try {{ window.__shiroFillObserver.disconnect(); }} catch (_) {{}} window.__shiroFillObserver = null; }}
                    }};
                    stopWatching();

                    const tick = (fromInterval = false) => {{
                        if (fromInterval) attempts++;
                        const u = document.querySelector('#login-username') || document.querySelector('input[name=""username""]') || document.querySelector('input[autocomplete=""username""]');
                        const p = document.querySelector('#login-password') || document.querySelector('input[name=""password""]') || document.querySelector('input[type=""password""]');
                        if (!u || (hasPass && !p)) {{ if (attempts > 50) stopWatching(); return; }}

                        const btn = findLoginButton();
                        const needForceEvents = (firstFilledAt === 0) || (u.value !== targetUser) || (hasPass && p.value !== targetPass) || (hasPass && isButtonDisabled(btn));
                        const uOk = setReactValue(u, targetUser, needForceEvents);
                        const pOk = !hasPass || setReactValue(p, targetPass, needForceEvents);

                        if (uOk && pOk) {{
                            if (firstFilledAt === 0) firstFilledAt = Date.now();
                            if (!hasPass) {{ if (Date.now() - firstFilledAt >= 300) stopWatching(); return; }}
                            if (window.__shiroAutoSubmittedForUser === targetUser && !forceClick) {{ stopWatching(); return; }}
                            if (Date.now() - firstFilledAt >= 180) {{
                                setReactValue(u, targetUser, true);
                                setReactValue(p, targetPass, true);
                                if (clickLoginButton()) {{ window.__shiroAutoSubmittedForUser = targetUser; stopWatching(); return; }}
                            }}
                        }}
                        if (attempts > 50) stopWatching();
                    }};

                    tick(false);
                    if (window.MutationObserver && document.documentElement) {{
                        let mutationScheduled = false;
                        window.__shiroFillObserver = new MutationObserver(() => {{
                            if (!mutationScheduled) {{
                                mutationScheduled = true;
                                setTimeout(() => {{ mutationScheduled = false; tick(false); }}, 80);
                            }}
                        }});
                        window.__shiroFillObserver.observe(document.documentElement, {{ childList: true, subtree: true }});
                    }}
                    window.__shiroFillInterval = setInterval(() => tick(true), 200);
                }} catch(_) {{}}
            }})();";
        }

        public static async Task ClearWebViewAuthCookiesAsync(CoreWebView2? core, ISet<string> ignoredCookieValues)
        {
            if (core == null)
                return;

            try
            {
                try { core.Stop(); } catch (Exception ex) { _logger.Debug($"WebView stop ignored: {ex.Message}"); }

                try
                {
                    string currentSrc = core.Source ?? string.Empty;
                    if (!string.IsNullOrEmpty(currentSrc) &&
                        !currentSrc.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                    {
                        core.Navigate("about:blank");
                        await Task.Delay(80);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Navigate to about:blank failed: {ex.Message}");
                }

                var cookieManager = core.CookieManager;
                string[] urls =
                {
                    "https://www.roblox.com",
                    "https://roblox.com",
                    "https://auth.roblox.com",
                    "https://apis.roblox.com",
                    "https://users.roblox.com"
                };

                foreach (string url in urls)
                {
                    try
                    {
                        var existingCookies = await cookieManager.GetCookiesAsync(url);
                        foreach (var c in existingCookies)
                        {
                            if (c.Name.Equals(".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(c.Value))
                            {
                                ignoredCookieValues?.Add(FingerprintCookie(c.Value));
                            }
                            cookieManager.DeleteCookie(c);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"Clear cookies for {url} threw: {ex.Message}");
                    }
                }

                try { cookieManager.DeleteCookiesWithDomainAndPath(".ROBLOSECURITY", ".roblox.com", "/"); } catch (Exception ex) { _logger.Debug($"Delete .ROBLOSECURITY ignored: {ex.Message}"); }
                try { cookieManager.DeleteCookiesWithDomainAndPath(".RBXIDCHECK", ".roblox.com", "/"); } catch (Exception ex) { _logger.Debug($"Delete .RBXIDCHECK ignored: {ex.Message}"); }
                try { cookieManager.DeleteCookiesWithDomainAndPath(".RBXSESSIONTRACKER", ".roblox.com", "/"); } catch (Exception ex) { _logger.Debug($"Delete .RBXSESSIONTRACKER ignored: {ex.Message}"); }
                try { cookieManager.DeleteCookiesWithDomainAndPath("RBXEventTrackerV2", ".roblox.com", "/"); } catch (Exception ex) { _logger.Debug($"Delete RBXEventTrackerV2 ignored: {ex.Message}"); }

                try { await core.Profile.ClearBrowsingDataAsync(); } catch (Exception ex) { _logger.Debug($"ClearBrowsingDataAsync ignored: {ex.Message}"); }
                try { await core.CallDevToolsProtocolMethodAsync("Network.clearBrowserCookies", "{}"); } catch (Exception ex) { _logger.Debug($"clearBrowserCookies ignored: {ex.Message}"); }
                try
                {
                    await core.CallDevToolsProtocolMethodAsync(
                        "Storage.clearDataForOrigin",
                        "{\"origin\":\"https://www.roblox.com\",\"storageTypes\":\"cookies,local_storage,session_storage,indexeddb\"}");
                }
                catch (Exception ex)
                {
                    _logger.Debug($"DevTools clearDataForOrigin roblox.com threw: {ex.Message}");
                }
                try
                {
                    await core.CallDevToolsProtocolMethodAsync(
                        "Storage.clearDataForOrigin",
                        "{\"origin\":\"https://auth.roblox.com\",\"storageTypes\":\"cookies,local_storage,session_storage,indexeddb\"}");
                }
                catch (Exception ex)
                {
                    _logger.Debug($"DevTools clearDataForOrigin auth.roblox.com threw: {ex.Message}");
                }

                try
                {
                    var consentCookie1 = cookieManager.CreateCookie("OptanonAlertBoxClosed", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), ".roblox.com", "/");
                    cookieManager.AddOrUpdateCookie(consentCookie1);

                    var consentCookie2 = cookieManager.CreateCookie("OptanonConsent", "isGpcEnabled=0&datestamp=" + Uri.EscapeDataString(DateTime.UtcNow.ToString()) + "&version=202401.1.0&browserGpcFlag=0&isIABGlobal=false&hosts=&consentId=shiro-fast-login&interactionCount=1&isAnonUser=1&landingPath=NotLandingPage&groups=C0001%3A1%2CC0002%3A1%2CC0003%3A1%2CC0004%3A1", ".roblox.com", "/");
                    cookieManager.AddOrUpdateCookie(consentCookie2);
                }
                catch (Exception ex)
                {
                    _logger.Debug($"GDPR cookie create threw: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"ClearWebViewAuthCookiesAsync threw: {ex.Message}");
            }
        }

        public static async Task<FastLoginCaptureResult> ProcessCapturedSessionAsync(
            Account? capturedAccount,
            string cookieValue,
            string? expectedUsername,
            string? password,
            string? alias,
            string? group,
            int queueCount,
            Account? currentQueueAccount,
            Func<Task<(string? IssueBadge, string? Reason)>>? detectDomIssueAsync = null)
        {
            if (capturedAccount != null && capturedAccount.Valid)
            {
                if (!string.IsNullOrEmpty(password))
                    capturedAccount.Password = password;

                if (!string.IsNullOrEmpty(expectedUsername) &&
                    !string.Equals(capturedAccount.Username, expectedUsername, StringComparison.OrdinalIgnoreCase))
                {
                    return new FastLoginCaptureResult(FastLoginCaptureStatus.UsernameMismatch, capturedAccount);
                }

                capturedAccount.AccountStatusIssue = string.Empty;
                if (!string.IsNullOrEmpty(alias))
                    capturedAccount.Alias = alias;
                if (!string.IsNullOrEmpty(group))
                    capturedAccount.Group = group;

                try { await AppServices.AccountRepository.SaveAsync(true).ConfigureAwait(false); }
                catch (Exception ex) { _logger.Warn($"SaveAccountsAsync after fast login failed: {ex.Message}"); }

                return new FastLoginCaptureResult(FastLoginCaptureStatus.Success, capturedAccount);
            }

            if (queueCount > 0)
            {
                if (capturedAccount?.IsBannedOrLocked == true)
                {
                    if (currentQueueAccount != null && string.IsNullOrEmpty(currentQueueAccount.SecurityToken))
                        currentQueueAccount.SecurityToken = cookieValue;
                    return new FastLoginCaptureResult(
                        FastLoginCaptureStatus.AccountIssue,
                        capturedAccount,
                        capturedAccount.AccountStatusIssue,
                        Shiro.Services.LocalizationService.Get("FastLogin.SessionLockedOrModerated"));
                }

                if (detectDomIssueAsync != null)
                {
                    var (domBadge, domReason) = await detectDomIssueAsync().ConfigureAwait(false);
                    if (domBadge == "UNAUTHENTICATED_MODERATION")
                    {
                        return new FastLoginCaptureResult(FastLoginCaptureStatus.UnauthenticatedModeration, currentQueueAccount);
                    }
                    if (!string.IsNullOrEmpty(domBadge))
                    {
                        if (currentQueueAccount != null && string.IsNullOrEmpty(currentQueueAccount.SecurityToken))
                            currentQueueAccount.SecurityToken = cookieValue;
                        return new FastLoginCaptureResult(
                            FastLoginCaptureStatus.AccountIssue,
                            currentQueueAccount,
                            domBadge,
                            domReason ?? Shiro.Services.LocalizationService.Get("FastLogin.SessionLockedOrModerated"));
                    }
                }
            }

            return new FastLoginCaptureResult(FastLoginCaptureStatus.RetryPolling, capturedAccount);
        }
    }
}
