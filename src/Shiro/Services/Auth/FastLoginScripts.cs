#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Shiro.Classes;

namespace Shiro.Services
{
    /// <summary>
    /// JavaScript payloads and WebView2 DOM/cookie helpers for FastLogin credential auto-fill and session capture.
    /// </summary>
    internal static class FastLoginScripts
    {
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

                    const isQuickOrAltButton = (b) => {{
                        if (!b) return true;
                        const id = (b.id || '').toLowerCase(), t = (b.innerText || b.textContent || b.value || '').trim().toLowerCase();
                        return id.includes('cross-device') || id.includes('quick') || id.includes('otp') || t.includes('quick') || t.includes('one-time') || t.includes('code') || t.includes('email');
                    }};
                    const findLoginButton = () => {{
                        const byId = document.querySelector('#login-button');
                        if (byId && !isQuickOrAltButton(byId)) return byId;
                        return Array.from(document.querySelectorAll('button, input[type=""submit""], [role=""button""]')).find(b => {{
                            if (isQuickOrAltButton(b)) return false;
                            const t = (b.innerText || b.textContent || b.value || '').trim().toLowerCase();
                            return t === 'log in' || t === 'login' || t === 'sign in' || t === 'đăng nhập';
                        }});
                    }};
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
                        try {{
                            const qModal = Array.from(document.querySelectorAll('.modal-dialog, [role=""dialog""], .quick-sign-in-container')).find(d => {{
                                const text = (d.innerText || d.textContent || '').toLowerCase();
                                return text.includes('quick sign in') || text.includes('quick sign-in') || text.includes('đăng nhập nhanh');
                            }});
                            if (qModal) {{
                                const closeBtn = qModal.querySelector('button[aria-label=""Close""], button.close, .modal-close, [data-testid=""modal-close-button""], button.modal-close-btn');
                                if (closeBtn) closeBtn.click();
                            }}
                        }} catch (_) {{}}
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
                            if (Date.now() - firstFilledAt >= 300) {{
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

        public static async Task<string> ResolveSettledCookieAsync(
            CoreWebView2 core,
            string initialCookieValue,
            ISet<string> ignoredCookieValues)
        {
            string cookieValue = initialCookieValue;
            try
            {
                string[] checkUrls =
                {
                    "https://www.roblox.com",
                    "https://users.roblox.com",
                    "https://auth.roblox.com",
                    "https://roblox.com"
                };

                foreach (string checkUrl in checkUrls)
                {
                    var settledCookies = await core.CookieManager.GetCookiesAsync(checkUrl);
                    foreach (var c in settledCookies)
                    {
                        if (c.Name.Equals(".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(c.Value) &&
                            c.Value.StartsWith("_|", StringComparison.Ordinal))
                        {
                            if (!Account.IsShortLivedCookie(c.Value) || Account.IsShortLivedCookie(cookieValue))
                            {
                                cookieValue = c.Value;
                            }
                            ignoredCookieValues.Add(FastLoginCoordinator.FingerprintCookie(c.Value));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"Reading settled cookies threw: {ex.Message}");
            }

            return cookieValue;
        }

        public static async Task<string?> TryExtractDomPasswordAsync(CoreWebView2 core)
        {
            try
            {
                string passJson = await core.ExecuteScriptAsync(@"
                    (() => {
                        try {
                            const p = document.querySelector('#login-password') || document.querySelector('input[type=""password""]');
                            return p && p.value ? p.value : '';
                        } catch (_) { return ''; }
                    })()
                ");
                if (!string.IsNullOrWhiteSpace(passJson) && passJson != "null" && passJson != "\"\"")
                {
                    string? unescaped = null;
                    try { unescaped = JsonSerializer.Deserialize(passJson, ShiroAppJson.Default.String); }
                    catch (Exception ex) { AppServices.Logger.Debug($"Deserialize password JSON: {ex.Message}"); }
                    if (string.IsNullOrEmpty(unescaped))
                        unescaped = passJson.Trim('"');
                    if (!string.IsNullOrEmpty(unescaped))
                        return unescaped;
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"Capturing password from DOM threw: {ex.Message}");
            }

            return null;
        }
    }
}
