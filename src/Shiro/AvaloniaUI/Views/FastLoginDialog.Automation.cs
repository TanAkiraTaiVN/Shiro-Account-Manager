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
            if (e != null && !e.IsRedirected)
                _isDocumentNavigating = true;
            else
                _isDocumentNavigating = true;
        }

        private async Task ConfigureCoreWebViewAsync(CoreWebView2 core)
        {
            if (core == null) return;

            _isDocumentNavigating = false;
            await ClearAuthCookiesAsync();
            await InstallHbaCaptureHookAsync(core);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(FastLoginCoordinator.GetLoginPageEnhancementScript());

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

                if (e.Request.Headers.Contains(RobloxHbaService.BoundAuthHeaderName))
                    return;

                string uri = e.Request.Uri ?? string.Empty;
                if (!WebAuthPolicy.IsTrustedRobloxOrigin(uri))
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
                AppServices.Logger?.Debug($"OnWebMessageReceived threw: {ex.Message}");
            }
        }

        private Task ClearAuthCookiesAsync() =>
            FastLoginCoordinator.ClearWebViewAuthCookiesAsync(WvBrowser?.CoreWebView2, _ignoredCookieValues);

        private async Task<string> CaptureSettledCookieAndDomPasswordAsync(CoreWebView2 core, string initialCookieValue)
        {
            string cookieValue = await FastLoginScripts.ResolveSettledCookieAsync(core, initialCookieValue, _ignoredCookieValues);
            if (string.IsNullOrEmpty(_password))
            {
                string? extracted = await FastLoginScripts.TryExtractDomPasswordAsync(core);
                if (!string.IsNullOrEmpty(extracted))
                {
                    _password = extracted;
                    var currAcc = GetCurrentQueueAccount();
                    if (currAcc != null) currAcc.Password = extracted;
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
