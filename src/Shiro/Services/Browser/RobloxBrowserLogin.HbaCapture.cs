#nullable enable
using System;
using System.Text.Json;
using System.Threading.Tasks;
using PuppeteerSharp;
using Shiro.Services;

namespace Shiro.Services
{
    internal static partial class RobloxBrowserLogin
    {
        private static async Task InstallHbaCaptureOnPageAsync(IPage? page)
        {
            if (page == null || page.IsClosed)
                return;

            string script = RobloxHbaService.GetBrowserCaptureScript();
            try
            {
                await page.EvaluateExpressionOnNewDocumentAsync(script).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[RobloxBrowserLogin] EvaluateOnNewDocumentAsync HBA script threw: {ex.Message}");
            }

            try
            {
                await page.EvaluateFunctionAsync(@"(hookCode) => {
                    try {
                        const alreadyHooked = !!window.__shiroHbaHookInstalled;
                        if (!alreadyHooked) {
                            (0, eval)(hookCode);
                            if (window.location && window.location.pathname.toLowerCase().includes('/login')) {
                                for (const dbName of ['hbaDB', 'hbaStore']) {
                                    try { indexedDB.deleteDatabase(dbName); } catch (_) { /* ignore */ }
                                }
                            }
                        }
                    } catch (_) { /* ignore */ }
                }", script).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[RobloxBrowserLogin] Live HBA hook injection threw: {ex.Message}");
            }
        }

        private static async Task<RobloxHbaService.CaptureSnapshot> ExtractHbaCaptureFromPageAsync(IPage? page)
        {
            var snapshot = new RobloxHbaService.CaptureSnapshot();
            if (page == null || page.IsClosed)
                return snapshot;

            try
            {
                string? rawJson = await page.EvaluateFunctionAsync<string>(@"async () => {
                    try {
                        try {
                            await fetch('https://users.roblox.com/v1/users/authenticated', {
                                method: 'GET',
                                credentials: 'include',
                                headers: { 'Accept': 'application/json' }
                            });
                        } catch (_) { /* ignore */ }
                        if (typeof window.__shiroSyncHbaFromIndexedDb === 'function') {
                            return await window.__shiroSyncHbaFromIndexedDb();
                        }
                        const s = window.__shiroHbaState || {};
                        return JSON.stringify({
                            privateKeyPkcs8: s.privateKeyPkcs8 || '',
                            publicKeySpki: s.publicKeySpki || '',
                            tokenTemplate: s.tokenTemplate || '',
                            userAgent: s.userAgent || navigator.userAgent || ''
                        });
                    } catch (_) {
                        return '';
                    }
                }").ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(rawJson) && rawJson.StartsWith('{'))
                {
                    using JsonDocument doc = JsonDocument.Parse(rawJson);
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("privateKeyPkcs8", out var privEl))
                        snapshot.PrivateKeyPkcs8Base64 = privEl.GetString();
                    if (root.TryGetProperty("publicKeySpki", out var pubEl))
                        snapshot.PublicKeySpkiBase64 = pubEl.GetString();
                    if (root.TryGetProperty("tokenTemplate", out var tplEl))
                        snapshot.TokenTemplate = tplEl.GetString();
                    if (root.TryGetProperty("userAgent", out var uaEl))
                        snapshot.UserAgent = uaEl.GetString();
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[RobloxBrowserLogin] ExtractHbaCaptureFromPageAsync DOM read threw: {ex.Message}");
            }

            try
            {
                CookieParam[] cookies = await page.GetCookiesAsync("https://www.roblox.com", "https://roblox.com").ConfigureAwait(false);
                foreach (CookieParam cookie in cookies)
                {
                    if (string.Equals(cookie.Name, "RBXEventTrackerV2", StringComparison.OrdinalIgnoreCase))
                    {
                        string? btid = RobloxHbaService.ExtractBrowserTrackerIdFromCookie(cookie.Value);
                        if (!string.IsNullOrWhiteSpace(btid))
                        {
                            snapshot.BrowserTrackerId = btid;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[RobloxBrowserLogin] Reading RBXEventTrackerV2 cookie threw: {ex.Message}");
            }

            return snapshot;
        }
    }
}
