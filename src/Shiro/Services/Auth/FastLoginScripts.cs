#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Shiro.Services
{
    /// <summary>
    /// JavaScript payloads and WebView2 DOM/cookie helpers for FastLogin credential auto-fill and session capture.
    /// </summary>
    internal static class FastLoginScripts
    {
        public static string GetLoginPageEnhancementScript() => @"
            (() => {
                try {
                    const h = (location.hostname || '').toLowerCase();
                    if (location.protocol !== 'https:' || (h !== 'roblox.com' && !h.endsWith('.roblox.com'))) return;
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
                            let hooked = false;
                            const hookPass = () => {
                                const p = document.querySelector('#login-password') || document.querySelector('input[type=""password""]');
                                if (p) {
                                    hooked = true;
                                    if (!p.__shiroHooked) {
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
                                }
                            };
                            hookPass();
                            if (!hooked) {
                                let attempts = 0;
                                const timerId = setInterval(() => { hookPass(); if (hooked || ++attempts >= 50) clearInterval(timerId); }, 400);
                                window.addEventListener('pagehide', () => clearInterval(timerId), { once: true });
                            }
                        } catch(_) { /* ignore */ }
                    });
                } catch(_) { /* ignore */ }
            })();
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

        internal sealed record ResolveSettledCookieResult(
            string CookieValue,
            IReadOnlyList<string> CandidateFingerprints,
            string? PendingCapturedFp);

        public static async Task<string> ResolveSettledCookieAsync(
            CoreWebView2 core,
            string initialCookieValue,
            ISet<string> ignoredCookieValues)
        {
            var observed = new List<string>();
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
                            observed.Add(c.Value);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"Reading settled cookies threw: {ex.Message}");
            }

            return ResolveSettledCookieCandidates(initialCookieValue, observed, ignoredCookieValues).CookieValue;
        }

        internal static ResolveSettledCookieResult ResolveSettledCookieCandidates(
            string initialCookieValue,
            IEnumerable<string> settledCookies,
            ISet<string>? ignoredCookieValues = null)
        {
            string cookieValue = initialCookieValue;
            string initialFp = !string.IsNullOrWhiteSpace(initialCookieValue)
                ? FastLoginCoordinator.FingerprintCookie(initialCookieValue)
                : string.Empty;
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(initialFp))
                candidates.Add(initialFp);

            foreach (string raw in settledCookies)
            {
                if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith("_|", StringComparison.Ordinal))
                    continue;

                string fp = FastLoginCoordinator.FingerprintCookie(raw);
                if (!candidates.Contains(fp))
                    candidates.Add(fp);

                if (ignoredCookieValues != null &&
                    !string.Equals(fp, initialFp, StringComparison.Ordinal) &&
                    ignoredCookieValues.Contains(fp))
                {
                    continue;
                }

                if (ShouldReplaceSettledCookie(cookieValue, raw))
                {
                    cookieValue = raw;
                }
            }

            return new ResolveSettledCookieResult(
                cookieValue,
                candidates,
                string.IsNullOrEmpty(initialFp) ? null : initialFp);
        }

        internal static bool ApplySettlementOutcome(
            FastLoginCaptureStatus status,
            ISet<string> ignoredCookieValues,
            ref string? pendingCapturedFp,
            params string?[] candidateFingerprints)
        {
            if (status == FastLoginCaptureStatus.RetryPolling)
            {
                foreach (string? fp in candidateFingerprints)
                {
                    if (!string.IsNullOrEmpty(fp))
                    {
                        ignoredCookieValues.Remove(fp);
                        pendingCapturedFp ??= fp;
                    }
                }
                if (!string.IsNullOrEmpty(pendingCapturedFp))
                    ignoredCookieValues.Remove(pendingCapturedFp);
                return true;
            }

            if (!string.IsNullOrEmpty(pendingCapturedFp))
                ignoredCookieValues.Add(pendingCapturedFp);
            foreach (string? fp in candidateFingerprints)
            {
                if (!string.IsNullOrEmpty(fp))
                    ignoredCookieValues.Add(fp);
            }
            pendingCapturedFp = null;
            return false;
        }

        internal static bool ShouldDeleteIgnoredCookie(
            IgnoredCookieAction action,
            string cookieFingerprint,
            string? pendingCapturedFp) =>
            action == IgnoredCookieAction.DeleteCookie &&
            (string.IsNullOrEmpty(pendingCapturedFp) ||
             !string.Equals(cookieFingerprint, pendingCapturedFp, StringComparison.Ordinal));

        internal static bool ShouldReplaceSettledCookie(string currentCookie, string candidateCookie)
        {
            if (string.IsNullOrWhiteSpace(candidateCookie))
                return false;
            if (string.IsNullOrWhiteSpace(currentCookie))
                return true;

            int candidateLen = Account.GetSessionTokenPayloadLength(candidateCookie);
            int currentLen = Account.GetSessionTokenPayloadLength(currentCookie);
            return candidateLen >= 850 || (currentLen < 850 && candidateLen > currentLen);
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

        public static async Task<string?> EvaluateAsyncJsStringAsync(CoreWebView2? core, string asyncJsExpression)
        {
            if (core == null || string.IsNullOrWhiteSpace(asyncJsExpression))
                return null;

            try
            {
                string escapedExpr = JsonSerializer.Serialize(asyncJsExpression, ShiroAppJson.Default.String);
                string cdpParams = $"{{\"expression\":{escapedExpr},\"awaitPromise\":true,\"returnByValue\":true}}";
                string cdpResponse = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", cdpParams);
                if (!string.IsNullOrWhiteSpace(cdpResponse))
                {
                    using var doc = JsonDocument.Parse(cdpResponse);
                    if (doc.RootElement.TryGetProperty("result", out var resEl) &&
                        resEl.TryGetProperty("value", out var valEl) &&
                        valEl.ValueKind == JsonValueKind.String)
                    {
                        return valEl.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[FastLogin] CDP Runtime.evaluate awaitPromise threw: {ex.Message}");
            }

            return null;
        }

        public static async Task<string?> FetchAuthenticatedUserJsonAsync(CoreWebView2 core)
        {
            try
            {
                string? resolvedAuthJson = await EvaluateAsyncJsStringAsync(core, @"
                    (async () => {
                        try {
                            const res = await fetch('https://users.roblox.com/v1/users/authenticated', { method: 'GET', credentials: 'include', headers: { 'Accept': 'application/json' } });
                            if (res.ok) { const d = await res.json(); if (d && d.id) return JSON.stringify({ UserId: d.id, Name: d.name, DisplayName: d.displayName }); }
                        } catch (_) { /* ignore fetch error */ }
                        return '';
                    })()");
                if (!string.IsNullOrWhiteSpace(resolvedAuthJson) && resolvedAuthJson.StartsWith('{'))
                    return resolvedAuthJson;
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Trigger in-page auth request threw: {ex.Message}");
            }

            return null;
        }

        public static async Task<RobloxHbaService.CaptureSnapshot> CollectHbaAndTrackerSnapshotAsync(
            CoreWebView2? core,
            RobloxHbaService.CaptureSnapshot snapshot,
            string username)
        {
            if (core != null)
            {
                try
                {
                    string? syncedJson = await EvaluateAsyncJsStringAsync(core, @"
                        (async () => {
                            try {
                                if (typeof window.__shiroSyncHbaFromIndexedDb === 'function') {
                                    return await window.__shiroSyncHbaFromIndexedDb();
                                }
                                const s = window.__shiroHbaState || {};
                                return JSON.stringify({
                                    publicKeySpki: s.publicKeySpki || '',
                                    tokenTemplate: s.tokenTemplate || '',
                                    userAgent: s.userAgent || navigator.userAgent || '',
                                    authoritativeKey: !!s.authoritativeKey,
                                    saiBound: !!s.saiBound
                                });
                            } catch (_) { return ''; }
                        })()");

                    if (!string.IsNullOrWhiteSpace(syncedJson) && syncedJson.StartsWith('{'))
                    {
                        using JsonDocument doc = JsonDocument.Parse(syncedJson);
                        RobloxHbaService.MergeCapturePayloadIntoSnapshot(snapshot, doc.RootElement);
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"[FastLogin] Reading final HBA state from IndexedDB/DOM threw: {ex.Message}");
                }

                try
                {
                    string[] trackerOrigins = { "https://www.roblox.com", "https://roblox.com" };
                    foreach (string origin in trackerOrigins)
                    {
                        var cookies = await core.CookieManager.GetCookiesAsync(origin);
                        foreach (var c in cookies)
                        {
                            if (c.Name.Equals("RBXEventTrackerV2", StringComparison.OrdinalIgnoreCase))
                            {
                                string? btid = RobloxHbaService.ExtractBrowserTrackerIdFromCookie(c.Value);
                                if (!string.IsNullOrWhiteSpace(btid))
                                {
                                    snapshot.BrowserTrackerId = btid;
                                    break;
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(snapshot.BrowserTrackerId))
                            break;
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"[FastLogin] Reading RBXEventTrackerV2 cookie threw: {ex.Message}");
                }
            }

            if (snapshot.HasPrivateKey)
            {
                snapshot.TokenTemplate = RobloxHbaService.NormalizeTemplate(snapshot.TokenTemplate);
                AppServices.Logger?.Info($"[FastLogin] Captured HBA ECDSA device-binding key for '{username}' (template='{snapshot.TokenTemplate}', trackerId={!string.IsNullOrEmpty(snapshot.BrowserTrackerId)}).");
            }

            return snapshot;
        }
    }
}
