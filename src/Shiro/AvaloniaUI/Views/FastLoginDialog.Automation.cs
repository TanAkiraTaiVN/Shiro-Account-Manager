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
        private static bool IsModerationOrLockedUrl(string url) =>
            FastLoginCoordinator.IsModerationOrLockedUrl(url);

        private Task<(string? IssueBadge, string? Reason)> DetectDomAccountIssueAsync(CoreWebView2? core) =>
            FastLoginCoordinator.DetectDomAccountIssueAsync(core, _queue.Count > 0);

        private async Task ConfigureCoreWebViewAsync(CoreWebView2 core)
        {
            if (core == null) return;

            await ClearAuthCookiesAsync();
            await InstallHbaCaptureHookAsync(core);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(FastLoginCoordinator.GetLoginPageEnhancementScript());

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
                try { raw = e.TryGetWebMessageAsString(); } catch (Exception ex) { Program.Logger?.Debug($"TryGetWebMessageAsString: {ex.Message}"); }
                if (string.IsNullOrEmpty(raw))
                {
                    try { raw = e.WebMessageAsJson; } catch (Exception ex) { Program.Logger?.Debug($"WebMessageAsJson: {ex.Message}"); }
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

        private Task ClearAuthCookiesAsync() =>
            FastLoginCoordinator.ClearWebViewAuthCookiesAsync(WvBrowser?.CoreWebView2, _ignoredCookieValues);

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
                Program.Logger?.Debug($"Script execution in TryFillCredentialsAsync threw: {ex.Message}");
            }
        }
    }
}
