#nullable enable
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public partial class FastLoginDialog
    {
        private static bool IsModerationOrLockedUrl(string url)
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

        private async Task<(string? IssueBadge, string? Reason)> DetectDomAccountIssueAsync(CoreWebView2? core)
        {
            if (core == null || _accountQueue.Count == 0)
                return (null, null);

            try
            {
                string rawJson = await core.ExecuteScriptAsync(@"
                    (() => {
                        try {
                            const url = (window.location.href || '').toLowerCase();
                            const isModUrl = url.includes('/not-approved') || url.includes('/account-status') || url.includes('/moderation') || url.includes('/security-notification') || url.includes('/account-locked');
                            const errSelectors = '#login-form-error, .form-error, .alert-warning, .text-error, [role=""alert""], .modal-body, .not-approved-container';
                            const nodes = document.querySelectorAll(errSelectors);
                            let combined = '';
                            for (const n of nodes) {
                                if (n && n.offsetParent !== null && n.innerText) {
                                    combined += ' ' + n.innerText.toLowerCase();
                                }
                            }
                            if (isModUrl && document.body && document.body.innerText) {
                                combined += ' ' + document.body.innerText.toLowerCase();
                            }
                            if (!combined) return isModUrl ? 'LOCKED_OR_BANNED' : '';
                            if (combined.includes('7 day') || combined.includes('7-day') || combined.includes('7 ngày')) {
                                return 'BAN_7_DAYS';
                            }
                            if (combined.includes('3 day') || combined.includes('3-day') || combined.includes('3 ngày')) {
                                return 'BAN_3_DAYS';
                            }
                            if (combined.includes('1 day') || combined.includes('1-day') || combined.includes('1 ngày')) {
                                return 'BAN_1_DAY';
                            }
                            if (combined.includes('14 day') || combined.includes('14-day') || combined.includes('14 ngày')) {
                                return 'BAN_14_DAYS';
                            }
                            if (combined.includes('deleted') || combined.includes('terminated') || combined.includes('đã bị xóa') || combined.includes('vĩnh viễn')) {
                                return 'TERMINATED';
                            }
                            if (combined.includes('warning') || combined.includes('cảnh báo')) {
                                return 'WARNING';
                            }
                            if (combined.includes('locked') || combined.includes('bị khóa') || combined.includes('reset your password') || combined.includes('security notification')) {
                                return 'ACCOUNT_LOCKED';
                            }
                            if (combined.includes('not approved') || combined.includes('banned') || combined.includes('moderated') || combined.includes('suspended')) {
                                return 'ACCOUNT_BANNED';
                            }
                            if (combined.includes('incorrect username or password') || combined.includes('invalid username or password') || combined.includes('sai mật khẩu') || combined.includes('tên người dùng hoặc mật khẩu không đúng')) {
                                return 'WRONG_PASSWORD';
                            }
                            return isModUrl ? 'LOCKED_OR_BANNED' : '';
                        } catch (_) {
                            return '';
                        }
                    })()
                ");

                if (string.IsNullOrWhiteSpace(rawJson))
                    return (null, null);

                string code = rawJson.Trim().Trim('"');
                return code switch
                {
                    "BAN_7_DAYS" => ("Ban 7 ngày", "Tài khoản bị cấm 7 ngày"),
                    "BAN_3_DAYS" => ("Ban 3 ngày", "Tài khoản bị cấm 3 ngày"),
                    "BAN_1_DAY" => ("Ban 1 ngày", "Tài khoản bị cấm 1 ngày"),
                    "BAN_14_DAYS" => ("Ban 14 ngày", "Tài khoản bị cấm 14 ngày"),
                    "TERMINATED" => ("Ban vĩnh viễn", "Tài khoản đã bị xóa vĩnh viễn"),
                    "WARNING" => ("Cảnh báo", "Tài khoản nhận cảnh báo kiểm duyệt"),
                    "ACCOUNT_LOCKED" => ("Khóa bảo mật", "Tài khoản bị khóa bảo mật"),
                    "ACCOUNT_BANNED" => ("Bị khóa / Ban", "Tài khoản bị Ban / Đình chỉ"),
                    "LOCKED_OR_BANNED" => ("Bị khóa / Ban", "Tài khoản bị khóa hoặc kiểm duyệt"),
                    "WRONG_PASSWORD" => ("Sai mật khẩu", "Sai tài khoản hoặc mật khẩu"),
                    _ => (null, null)
                };
            }
            catch
            {
                return (null, null);
            }
        }

        private async Task ConfigureCoreWebViewAsync(CoreWebView2 core)
        {
            if (core == null) return;

            await ClearAuthCookiesAsync();
            await InstallHbaCaptureHookAsync(core);

            await core.AddScriptToExecuteOnDocumentCreatedAsync(@"
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
                        try { if (window.OneTrust && window.OneTrust.AllowAll) window.OneTrust.AllowAll(); } catch(_) {}
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
                        } catch(_) {}
                    });
                } catch(_) {}
            ");

            core.WebMessageReceived -= OnWebMessageReceived;
            core.WebMessageReceived += OnWebMessageReceived;

            core.NavigationCompleted -= OnNavigationCompleted;
            core.SourceChanged -= OnSourceChanged;
            core.NavigationCompleted += OnNavigationCompleted;
            core.SourceChanged += OnSourceChanged;
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string? raw = null;
                try { raw = e.TryGetWebMessageAsString(); } catch (Exception) { }
                if (string.IsNullOrEmpty(raw))
                {
                    try { raw = e.WebMessageAsJson; } catch (Exception) { }
                }

                if (!string.IsNullOrEmpty(raw))
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (TryHandleHbaWebMessage(doc.RootElement))
                        return;

                    if (doc.RootElement.TryGetProperty("type", out var typeEl) &&
                        typeEl.GetString() == "shiro_password_captured" &&
                        doc.RootElement.TryGetProperty("password", out var passEl))
                    {
                        string? pass = passEl.GetString();
                        if (!string.IsNullOrEmpty(pass))
                        {
                            _password = pass;
                            var acc = GetCurrentQueueAccount();
                            if (acc != null)
                            {
                                acc.Password = pass;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"OnWebMessageReceived threw: {ex.Message}");
            }
        }

        private async Task ClearAuthCookiesAsync()
        {
            var core = WvBrowser?.CoreWebView2;
            if (core == null) return;
            try
            {
                try { core.Stop(); } catch (Exception) { }

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
                    Program.Logger?.Debug($"Navigate to about:blank failed: {ex.Message}");
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
                                _ignoredCookieValues.Add(Fingerprint(c.Value));
                            }
                            cookieManager.DeleteCookie(c);
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Clear cookies for {url} threw: {ex.Message}");
                    }
                }

                try { cookieManager.DeleteCookiesWithDomainAndPath(".ROBLOSECURITY", ".roblox.com", "/"); } catch (Exception) { }
                try { cookieManager.DeleteCookiesWithDomainAndPath(".RBXIDCHECK", ".roblox.com", "/"); } catch (Exception) { }
                try { cookieManager.DeleteCookiesWithDomainAndPath(".RBXSESSIONTRACKER", ".roblox.com", "/"); } catch (Exception) { }
                try { cookieManager.DeleteCookiesWithDomainAndPath("RBXEventTrackerV2", ".roblox.com", "/"); } catch (Exception) { }

                try { await core.Profile.ClearBrowsingDataAsync(); } catch (Exception) { }
                try { await core.CallDevToolsProtocolMethodAsync("Network.clearBrowserCookies", "{}"); } catch (Exception) { }
                try
                {
                    await core.CallDevToolsProtocolMethodAsync(
                        "Storage.clearDataForOrigin",
                        "{\"origin\":\"https://www.roblox.com\",\"storageTypes\":\"cookies,local_storage,session_storage,indexeddb\"}");
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"DevTools clearDataForOrigin roblox.com threw: {ex.Message}");
                }
                try
                {
                    await core.CallDevToolsProtocolMethodAsync(
                        "Storage.clearDataForOrigin",
                        "{\"origin\":\"https://auth.roblox.com\",\"storageTypes\":\"cookies,local_storage,session_storage,indexeddb\"}");
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"DevTools clearDataForOrigin auth.roblox.com threw: {ex.Message}");
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
                    Program.Logger?.Debug($"GDPR cookie create threw: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"ClearSessionAsync threw: {ex.Message}");
            }
        }

        private async Task TryFillCredentialsAsync(bool forceClick = false)
        {
            var core = WvBrowser?.CoreWebView2;
            if (core == null || string.IsNullOrEmpty(_username)) return;

            try
            {
                string userJson = JsonSerializer.Serialize(_username, ShiroAppJson.Default.String);
                string passJson = JsonSerializer.Serialize(_password ?? "", ShiroAppJson.Default.String);
                string hasPassJs = !string.IsNullOrEmpty(_password) ? "true" : "false";
                string forceClickJs = forceClick ? "true" : "false";

                string js = $@"
                    (() => {{
                        try {{
                            const targetUser = {userJson};
                            const targetPass = {passJson};
                            const hasPass = {hasPassJs};
                            const forceClick = {forceClickJs};

                            if (forceClick) {{
                                window.__shiroAutoSubmittedForUser = '';
                            }}

                            const setReactValue = (el, val) => {{
                                if (!el || val === undefined || val === null) return false;
                                if (el.value === val) return true;
                                try {{
                                    el.focus();
                                    if (el._valueTracker) {{
                                        el._valueTracker.setValue('');
                                    }}
                                    const proto = window.HTMLInputElement ? window.HTMLInputElement.prototype : Object.getPrototypeOf(el);
                                    const desc = Object.getOwnPropertyDescriptor(proto, 'value');
                                    if (desc && desc.set) {{
                                        desc.set.call(el, val);
                                    }} else {{
                                        el.value = val;
                                    }}
                                    try {{
                                        el.dispatchEvent(new InputEvent('input', {{ bubbles: true, cancelable: true, inputType: 'insertText', data: val }}));
                                    }} catch (_) {{
                                        el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                                    }}
                                    el.dispatchEvent(new Event('change', {{ bubbles: true }}));
                                    el.blur();
                                    return el.value === val;
                                }} catch (_) {{
                                    return false;
                                }}
                            }};

                            const clickLoginButton = () => {{
                                const btn = document.querySelector('#login-button') ||
                                    Array.from(document.querySelectorAll('button, input[type=""submit""], [role=""button""]'))
                                        .find(b => {{
                                            const t = (b.innerText || b.value || '').trim().toLowerCase();
                                            return t === 'log in' || t === 'login' || t === 'đăng nhập';
                                        }});
                                if (btn && !btn.disabled) {{
                                    btn.click();
                                    return true;
                                }}
                                return false;
                            }};

                            let attempts = 0;
                            if (window.__shiroFillInterval) {{
                                clearInterval(window.__shiroFillInterval);
                                window.__shiroFillInterval = null;
                            }}

                            const tick = () => {{
                                attempts++;
                                const u = document.querySelector('#login-username') || document.querySelector('input[name=""username""]');
                                const p = document.querySelector('#login-password') || document.querySelector('input[type=""password""]');

                                if (!u || (hasPass && !p)) {{
                                    if (attempts > 40 && window.__shiroFillInterval) {{
                                        clearInterval(window.__shiroFillInterval);
                                        window.__shiroFillInterval = null;
                                    }}
                                    return;
                                }}

                                const uOk = setReactValue(u, targetUser);
                                const pOk = !hasPass || setReactValue(p, targetPass);

                                if (uOk && pOk && hasPass) {{
                                    if (window.__shiroFillInterval) {{
                                        clearInterval(window.__shiroFillInterval);
                                        window.__shiroFillInterval = null;
                                    }}
                                    if (window.__shiroAutoSubmittedForUser !== targetUser) {{
                                        window.__shiroAutoSubmittedForUser = targetUser;
                                        setTimeout(() => {{
                                            setReactValue(u, targetUser);
                                            setReactValue(p, targetPass);
                                            clickLoginButton();
                                        }}, 280);
                                    }}
                                    return;
                                }}

                                if (attempts > 40 && window.__shiroFillInterval) {{
                                    clearInterval(window.__shiroFillInterval);
                                    window.__shiroFillInterval = null;
                                }}
                            }};

                            tick();
                            window.__shiroFillInterval = setInterval(tick, 220);
                        }} catch(_) {{}}
                    }})();
                ";
                await core.ExecuteScriptAsync(js);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Script execution in TryFillCredentialsAsync threw: {ex.Message}");
            }
        }
    }
}
