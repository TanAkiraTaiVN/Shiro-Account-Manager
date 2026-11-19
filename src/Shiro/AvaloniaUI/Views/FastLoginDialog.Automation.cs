#nullable enable
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public partial class FastLoginDialog
    {
        private static bool IsModerationOrLockedUrl(string url) =>
            FastLoginCoordinator.IsModerationOrLockedUrl(url);

        private Task<(string? IssueBadge, string? Reason)> DetectDomAccountIssueAsync(CoreWebView2? core) =>
            FastLoginCoordinator.DetectDomAccountIssueAsync(core, _queue.Count > 0);

        private volatile bool _isDocumentNavigating;

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            _isDocumentNavigating = true;
        }

        private Task ConfigureCoreWebViewAsync(CoreWebView2 core)
        {
            if (_isClosed || core == null) return Task.CompletedTask;

            return ConfigureCoreWebViewCoreAsync(
                ClearAuthCookiesAsync,
                () => InstallHbaCaptureHookAsync(core),
                () => core.AddScriptToExecuteOnDocumentCreatedAsync(FastLoginCoordinator.GetLoginPageEnhancementScript()),
                () => AttachCoreWebViewHandlers(core));
        }

        internal async Task ConfigureCoreWebViewCoreAsync(
            Func<Task> clearCookiesAsync,
            Func<Task> installHbaHookAsync,
            Func<Task> addEnhancementScriptAsync,
            Action attachHandlers)
        {
            if (_isClosed) return;
            _isDocumentNavigating = false;

            await clearCookiesAsync();
            if (_isClosed) return;

            await installHbaHookAsync();
            if (_isClosed) return;

            await addEnhancementScriptAsync();
            if (_isClosed) return;

            attachHandlers();
        }

        private void AttachCoreWebViewHandlers(CoreWebView2 core)
        {
            try
            {
                core.AddWebResourceRequestedFilter("https://*.roblox.com/*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested -= OnWebResourceRequested;
                core.WebResourceRequested += OnWebResourceRequested;
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[FastLoginDialog] AddWebResourceRequestedFilter threw: {ex.Message}");
            }

            core.WebMessageReceived -= OnWebMessageReceived;
            core.WebMessageReceived += OnWebMessageReceived;

            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.SourceChanged -= OnSourceChanged;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.SourceChanged += OnSourceChanged;
        }

        private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                var acc = _restoredModerationAccount;
                if (acc == null || !acc.HasHbaBinding || e?.Request == null)
                    return;

                string uri = e.Request.Uri ?? string.Empty;
                if (!WebAuthPolicy.IsTrustedRobloxOrigin(uri))
                    return;

                if (!FastLoginCoordinator.ShouldSignWebViewResourceContext(e.ResourceContext))
                {
                    if (e.Request.Headers.Contains(RobloxHbaService.BoundAuthHeaderName))
                        e.Request.Headers.RemoveHeader(RobloxHbaService.BoundAuthHeaderName);
                    return;
                }

                if (e.Request.Headers.Contains(RobloxHbaService.BoundAuthHeaderName))
                    return;

                string method = string.IsNullOrWhiteSpace(e.Request.Method) ? "GET" : e.Request.Method;
                string? bodyContent = null;
                if (e.Request.Content != null && e.Request.Content.CanSeek)
                {
                    long pos = e.Request.Content.Position;
                    using var reader = new StreamReader(e.Request.Content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
                    bodyContent = reader.ReadToEnd();
                    e.Request.Content.Position = pos;
                }

                if (RobloxHbaService.TryBuildBoundAuthToken(
                    acc.HbaPrivateKeyPkcs8,
                    uri,
                    method,
                    bodyContent,
                    acc.HbaTemplate,
                    unixTimestampOverride: null,
                    out string batToken) && !string.IsNullOrEmpty(batToken))
                {
                    e.Request.Headers.SetHeader(RobloxHbaService.BoundAuthHeaderName, batToken);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[FastLoginDialog.OnWebResourceRequested] {ex.Message}");
            }
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                if (e == null || !WebAuthPolicy.IsTrustedRobloxOrigin(e.Source))
                    return;

                string? raw = null;
                try { raw = e.TryGetWebMessageAsString(); } catch (Exception ex) { AppServices.Logger?.Debug($"TryGetWebMessageAsString: {ex.Message}"); }
                if (string.IsNullOrEmpty(raw))
                {
                    try { raw = e.WebMessageAsJson; } catch (Exception ex) { AppServices.Logger?.Debug($"WebMessageAsJson: {ex.Message}"); }
                }

                if (!string.IsNullOrEmpty(raw))
                {
                    HandleWebMessageJson(raw);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"OnWebMessageReceived threw: {ex.Message}");
            }
        }

        internal void HandleWebMessageJson(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
                return;

            using var doc = JsonDocument.Parse(raw);
            if (TryHandleHbaWebMessage(doc.RootElement))
                return;

            if (doc.RootElement.TryGetProperty("type", out var typeEl) &&
                typeEl.ValueKind == JsonValueKind.String &&
                typeEl.GetString() == "shiro_password_captured" &&
                doc.RootElement.TryGetProperty("password", out var passEl) &&
                passEl.ValueKind == JsonValueKind.String)
            {
                string? pass = passEl.GetString();
                if (!string.IsNullOrEmpty(pass))
                {
                    // Hold DOM-captured password separately until username verification passes.
                    _pendingCapturedPassword = pass;
                }
            }
        }

        internal string GetEffectivePasswordForCommit() =>
            !string.IsNullOrEmpty(_pendingCapturedPassword) ? _pendingCapturedPassword : _password;

        internal void ResetMismatchState()
        {
            _pendingCapturedPassword = null;
            _pendingCapturedFp = null;
            ResetHbaSnapshot();
        }

        private Task ClearAuthCookiesAsync() =>
            FastLoginCoordinator.ClearWebViewAuthCookiesAsync(WvBrowser?.CoreWebView2, _ignoredCookieValues);

        private async Task<string> CaptureSettledCookieAndDomPasswordAsync(CoreWebView2 core, string initialCookieValue)
        {
            string cookieValue = await FastLoginScripts.ResolveSettledCookieAsync(core, initialCookieValue, _ignoredCookieValues);
            if (string.IsNullOrEmpty(_pendingCapturedPassword))
            {
                string? extracted = await FastLoginScripts.TryExtractDomPasswordAsync(core);
                if (!string.IsNullOrEmpty(extracted))
                {
                    _pendingCapturedPassword = extracted;
                }
            }
            return cookieValue;
        }

        private async Task TryFillCredentialsAsync(bool forceClick = false)
        {
            var core = WvBrowser?.CoreWebView2;
            if (core == null || string.IsNullOrEmpty(_username)) return;

            try
            {
                string js = FastLoginCoordinator.BuildCredentialFillScript(_username, _password, forceClick);
                await core.ExecuteScriptAsync(js);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Script execution in TryFillCredentialsAsync threw: {ex.Message}");
            }
        }
    }
}
