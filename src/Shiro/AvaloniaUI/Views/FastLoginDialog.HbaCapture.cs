#nullable enable
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public partial class FastLoginDialog
    {
        private readonly IDisposable _interactiveLoginScope = RobloxHbaService.BeginInteractiveLoginScope();
        private RobloxHbaService.CaptureSnapshot _currentHbaSnapshot = new();

        private void ResetHbaSnapshot()
        {
            _currentHbaSnapshot = new RobloxHbaService.CaptureSnapshot();
        }

        private void DisposeInteractiveLoginScope()
        {
            try { _interactiveLoginScope.Dispose(); }
            catch (Exception ex) { AppServices.Logger?.Debug($"[FastLoginDialog.DisposeInteractiveLoginScope] {ex.Message}"); }
        }

        private static async Task InstallHbaCaptureHookAsync(CoreWebView2 core)
        {
            if (core == null)
                return;

            await core.AddScriptToExecuteOnDocumentCreatedAsync(RobloxHbaService.GetBrowserCaptureScript());
        }

        private static async Task<string?> EvaluateAsyncJsStringAsync(CoreWebView2 core, string asyncJsExpression)
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

        private bool TryHandleHbaWebMessage(JsonElement root)
        {
            if (!root.TryGetProperty("type", out JsonElement typeEl) ||
                !string.Equals(typeEl.GetString(), "shiro_hba_captured", StringComparison.Ordinal))
            {
                return false;
            }

            MergeHbaJsonIntoSnapshot(root);
            return true;
        }

        private void MergeHbaJsonIntoSnapshot(JsonElement root) =>
            RobloxHbaService.MergeCapturePayloadIntoSnapshot(_currentHbaSnapshot, root);

        private async Task<RobloxHbaService.CaptureSnapshot> CollectFinalHbaAndFingerprintAsync(CoreWebView2 core)
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
                                    privateKeyPkcs8: s.privateKeyPkcs8 || '',
                                    publicKeySpki: s.publicKeySpki || '',
                                    tokenTemplate: s.tokenTemplate || '',
                                    userAgent: s.userAgent || navigator.userAgent || '',
                                    authoritativeKey: !!s.authoritativeKey,
                                    saiBound: !!s.saiBound
                                });
                            } catch (_) { return ''; }
                        })()
                    ");

                    if (!string.IsNullOrWhiteSpace(syncedJson) && syncedJson.StartsWith('{'))
                    {
                        using JsonDocument doc = JsonDocument.Parse(syncedJson);
                        MergeHbaJsonIntoSnapshot(doc.RootElement);
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
                                    _currentHbaSnapshot.BrowserTrackerId = btid;
                                    break;
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(_currentHbaSnapshot.BrowserTrackerId))
                            break;
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"[FastLogin] Reading RBXEventTrackerV2 cookie threw: {ex.Message}");
                }
            }

            if (_currentHbaSnapshot.HasPrivateKey)
            {
                _currentHbaSnapshot.TokenTemplate = RobloxHbaService.NormalizeTemplate(_currentHbaSnapshot.TokenTemplate);

                AppServices.Logger?.Info($"[FastLogin] Captured HBA ECDSA device-binding key for '{_username}' (template='{_currentHbaSnapshot.TokenTemplate}', trackerId={!string.IsNullOrEmpty(_currentHbaSnapshot.BrowserTrackerId)}).");
            }

            return _currentHbaSnapshot;
        }
    }
}
