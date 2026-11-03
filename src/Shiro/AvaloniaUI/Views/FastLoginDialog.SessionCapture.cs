#nullable enable
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media;
using Microsoft.Web.WebView2.Core;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public partial class FastLoginDialog
    {
        private Account? GetCurrentQueueAccount() =>
            (_currentQueueIndex >= 0 && _currentQueueIndex < _accountQueue.Count) ? _accountQueue[_currentQueueIndex] : null;

        private bool _redirectedFromUnauthenticatedModeration;
        private string? _restoredHbaScriptId;

        private async Task RedirectUnauthenticatedModerationToLoginAsync(CoreWebView2 core)
        {
            if (_redirectedFromUnauthenticatedModeration) return;
            _redirectedFromUnauthenticatedModeration = true;

            var currentAcc = GetCurrentQueueAccount();
            string accName = currentAcc?.Username ?? _username ?? Shiro.Services.LocalizationService.Get("Nav_Accounts");
            if (currentAcc != null)
            {
                currentAcc.Valid = false;
                currentAcc.AccountStatusIssue = string.Empty;
                AppServices.AccountRepository.SaveAsync(true).Forget();
                MainWindow.Instance?.ViewModel?.UpdateAccountValidity(currentAcc, false);
            }

            TxtInAppStatus.Text = Shiro.Services.LocalizationService.Format("Auto_FastLoginDialogS_074", accName);
            TxtInAppStatus.Foreground = Brushes.DeepSkyBlue;
            AppSettings.SetDashboardStatus(Shiro.Services.LocalizationService.Format("Auto_FastLoginDialogS_075", accName));

            try
            {
                if (!string.IsNullOrEmpty(_restoredHbaScriptId))
                {
                    try { core.RemoveScriptToExecuteOnDocumentCreated(_restoredHbaScriptId); }
                    catch (Exception ex) { Program.Logger?.Debug($"RemoveScriptToExecuteOnDocumentCreated threw: {ex.Message}"); }
                    _restoredHbaScriptId = null;
                }

                ResetHbaSnapshot();
                await ClearAuthCookiesAsync();

                _captured = false;
                _pollTimer?.Start();

                core.Navigate("https://www.roblox.com/login");
                await Task.Delay(350);
                await TryFillCredentialsAsync(forceClick: false);
            }
            catch (Exception ex) { Program.Logger?.Debug($"RedirectUnauthenticatedModerationToLoginAsync threw: {ex.Message}"); }
        }

        private async Task MarkCurrentAccountIssueAndSkipAsync(string issueBadge, string statusReason)
        {
            var currentAcc = GetCurrentQueueAccount();
            string accName = currentAcc?.Username ?? _username ?? Shiro.Services.LocalizationService.Get("Nav_Accounts");
            if (currentAcc != null)
            {
                currentAcc.Valid = false;
                currentAcc.AccountStatusIssue = issueBadge;
                AppServices.AccountRepository.SaveAsync(true).Forget();
                MainWindow.Instance?.ViewModel?.UpdateAccountValidity(currentAcc, false);
            }

            string progressTag = _accountQueue.Count > 1 ? $" ({_currentQueueIndex + 1}/{_accountQueue.Count})" : "";
            TxtInAppStatus.Text = $"{accName}{progressTag}: {statusReason}";
            TxtInAppStatus.Foreground = Brushes.OrangeRed;
            AppSettings.SetDashboardStatus($"{accName}{progressTag}: {issueBadge} ({statusReason})");

            if (_accountQueue.Count > 1)
            {
                _captured = true;
                _pollTimer?.Stop();
                await Task.Delay(500);
                await AdvanceToNextAccountOrCloseAsync();
            }
        }

        private async Task AdvanceToNextAccountOrCloseAsync()
        {
            if (_currentQueueIndex + 1 < _accountQueue.Count)
            {
                _captured = true;
                _pollTimer?.Stop();

                var oldCore = WvBrowser?.CoreWebView2;
                if (oldCore != null)
                {
                    oldCore.NavigationCompleted -= OnNavigationCompleted;
                    oldCore.SourceChanged -= OnSourceChanged;
                }

                LoadAccountAtIndex(_currentQueueIndex + 1);
                _captured = true;
                TxtInAppStatus.Text = Shiro.Services.LocalizationService.Format("Auto_FastLoginDialogS_076", _username, _currentQueueIndex + 1, _accountQueue.Count);
                TxtInAppStatus.Foreground = Brushes.DeepSkyBlue;

                if (WvBrowser != null)
                {
                    ResetHbaSnapshot();
                    var freshCore = await WvBrowser.ResetToFreshProfileAsync();
                    if (freshCore != null)
                    {
                        await ConfigureCoreWebViewAsync(freshCore);
                        freshCore.Navigate("https://www.roblox.com/login");
                    }
                }

                await Task.Delay(350);
                _captured = false;
                _pollTimer?.Start();
                await TryFillCredentialsAsync(forceClick: false);
            }
            else
            {
                ReloginQueueStore.Clear();
                await Task.Delay(300);
                Close(true);
            }
        }

        private async Task CheckAndCaptureSessionAsync()
        {
            if (_captured || _isCheckingSession) return;

            var core = WvBrowser?.CoreWebView2;
            if (core == null) return;

            _isCheckingSession = true;
            try
            {
                string currentUrl = core.Source ?? string.Empty;
                if (_accountQueue.Count > 1 && IsModerationOrLockedUrl(currentUrl))
                {
                    var (b, r) = await DetectDomAccountIssueAsync(core);
                    if (b == "UNAUTHENTICATED_MODERATION") { await RedirectUnauthenticatedModerationToLoginAsync(core); return; }
                    await MarkCurrentAccountIssueAndSkipAsync(b ?? Shiro.Services.LocalizationService.Get("Auto_FastLoginDialogS_078"), r ?? Shiro.Services.LocalizationService.Get("Auto_FastLoginDialogS_077"));
                    return;
                }

                var cookies = await core.CookieManager.GetCookiesAsync("https://www.roblox.com");
                CoreWebView2Cookie? secCookie = null;
                foreach (var c in cookies)
                {
                    if (c.Name.Equals(".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase)) { secCookie = c; break; }
                }

                if (secCookie != null && !string.IsNullOrWhiteSpace(secCookie.Value) && secCookie.Value.StartsWith("_|"))
                {
                    string fp = Fingerprint(secCookie.Value);
                    if (_ignoredCookieValues.Contains(fp))
                    {
                        if (IsModerationOrLockedUrl(currentUrl))
                        {
                            var (domBadge, domReason) = await DetectDomAccountIssueAsync(core);
                            if (domBadge == "UNAUTHENTICATED_MODERATION") { await RedirectUnauthenticatedModerationToLoginAsync(core); return; }
                            if (!string.IsNullOrEmpty(domBadge)) await MarkCurrentAccountIssueAndSkipAsync(domBadge, domReason ?? domBadge);
                            return;
                        }
                        if (_accountQueue.Count == 1 && _accountQueue[0].IsBannedOrLocked && currentUrl.Contains("/home", StringComparison.OrdinalIgnoreCase))
                        {
                            _ignoredCookieValues.Remove(fp);
                        }
                        else
                        {
                            SafeExecuteHelper.Try(() => core.CookieManager.DeleteCookie(secCookie), "FastLoginDialog.DeleteIgnoredCookie");
                            return;
                        }
                    }

                    _captured = true;
                    _pollTimer?.Stop();
                    _ignoredCookieValues.Add(fp);
                    Program.Logger.Info($"[FastLogin] .ROBLOSECURITY detected for '{_username}' (len={secCookie.Value.Length}, url={core.Source}). Waiting for settle...");

                    TxtInAppStatus.Text = Shiro.Services.LocalizationService.Format("Auto_FastLoginDialogS_079", _username ?? Shiro.Services.LocalizationService.Get("Dash_AccountsUnit"));
                    TxtInAppStatus.Foreground = Brushes.LightGreen;

                    for (int w = 0; w < 24; w++)
                    {
                        string navUrl = core.Source ?? string.Empty;
                        if (_accountQueue.Count > 0 && IsModerationOrLockedUrl(navUrl))
                        {
                            var qAcc = GetCurrentQueueAccount();
                            if (qAcc != null) qAcc.SecurityToken = secCookie.Value;
                            await Task.Delay(700);
                            var (b, r) = await DetectDomAccountIssueAsync(core);
                            if (b == "UNAUTHENTICATED_MODERATION") { await RedirectUnauthenticatedModerationToLoginAsync(core); return; }
                            await MarkCurrentAccountIssueAndSkipAsync(b ?? Shiro.Services.LocalizationService.Get("Auto_FastLoginDialogS_078"), r ?? Shiro.Services.LocalizationService.Get("Auto_FastLoginDialogS_077"));
                            return;
                        }
                        if (!string.IsNullOrEmpty(navUrl) && !navUrl.Contains("/login", StringComparison.OrdinalIgnoreCase)) break;
                        await Task.Delay(250);
                    }

                    await Task.Delay(1500);

                    string postRedirectUrl = core.Source ?? string.Empty;
                    if (_accountQueue.Count > 0 && IsModerationOrLockedUrl(postRedirectUrl))
                    {
                        var qAcc = GetCurrentQueueAccount();
                        if (qAcc != null) qAcc.SecurityToken = secCookie.Value;
                        var (b, r) = await DetectDomAccountIssueAsync(core);
                        if (b == "UNAUTHENTICATED_MODERATION") { await RedirectUnauthenticatedModerationToLoginAsync(core); return; }
                        await MarkCurrentAccountIssueAndSkipAsync(b ?? Shiro.Services.LocalizationService.Get("Auto_FastLoginDialogS_078"), r ?? Shiro.Services.LocalizationService.Get("Auto_FastLoginDialogS_077"));
                        return;
                    }

                    string? inPageAccountJson = null;
                    try
                    {
                        string rawAuthJson = await core.ExecuteScriptAsync(@"
                            (async () => {
                                try {
                                    const res = await fetch('https://users.roblox.com/v1/users/authenticated', { method: 'GET', credentials: 'include', headers: { 'Accept': 'application/json' } });
                                    if (res.ok) { const d = await res.json(); if (d && d.id) return JSON.stringify({ UserId: d.id, Name: d.name, DisplayName: d.displayName }); }
                                } catch (_) { /* ignore fetch error */ }
                                return '';
                            })()");
                        if (!string.IsNullOrWhiteSpace(rawAuthJson) && rawAuthJson != "null" && rawAuthJson != "\"\"")
                        {
                            try { inPageAccountJson = JsonSerializer.Deserialize(rawAuthJson, ShiroAppJson.Default.String); }
                            catch (Exception) { inPageAccountJson = rawAuthJson.Trim('"'); }
                            if (string.IsNullOrWhiteSpace(inPageAccountJson) || !inPageAccountJson.StartsWith('{')) inPageAccountJson = null;
                        }
                        await Task.Delay(300);
                    }
                    catch (Exception ex) { Program.Logger?.Debug($"Trigger in-page auth request threw: {ex.Message}"); }

                    string cookieValue = secCookie.Value;
                    try
                    {
                        string[] checkUrls = { "https://www.roblox.com", "https://auth.roblox.com", "https://roblox.com" };
                        foreach (string checkUrl in checkUrls)
                        {
                            var settledCookies = await core.CookieManager.GetCookiesAsync(checkUrl);
                            foreach (var c in settledCookies)
                            {
                                if (c.Name.Equals(".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Value) && c.Value.StartsWith("_|"))
                                {
                                    cookieValue = c.Value;
                                    _ignoredCookieValues.Add(Fingerprint(cookieValue));
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Program.Logger?.Debug($"Reading settled cookies threw: {ex.Message}"); }

                    if (string.IsNullOrEmpty(_password))
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
                                try { unescaped = JsonSerializer.Deserialize(passJson, ShiroAppJson.Default.String); } catch (Exception ex) { Program.Logger?.Debug($"Deserialize password JSON: {ex.Message}"); }
                                if (string.IsNullOrEmpty(unescaped)) unescaped = passJson.Trim('"');
                                if (!string.IsNullOrEmpty(unescaped))
                                {
                                    _password = unescaped;
                                    var currAcc = GetCurrentQueueAccount();
                                    if (currAcc != null) currAcc.Password = unescaped;
                                }
                            }
                        }
                        catch (Exception ex) { Program.Logger?.Debug($"Capturing password from DOM threw: {ex.Message}"); }
                    }

                    var hbaSnapshot = await CollectFinalHbaAndFingerprintAsync(core);
                    try { core.Stop(); }
                    catch (Exception ex) { Program.Logger?.Debug($"core.Stop() threw: {ex.Message}"); }

                    Account? acc = await AppServices.AccountRepository.AddOrUpdateWithHbaAsync(cookieValue, _password ?? "", accountJson: inPageAccountJson, hbaCapture: hbaSnapshot);
                    Program.Logger?.Info($"[FastLogin] Captured session for '{_username}': payloadLen={Account.GetSessionTokenPayloadLength(cookieValue)}, hba={hbaSnapshot.HasPrivateKey}, registeredValid={acc?.Valid}, resolvedUser={acc?.Username}.");

                    var captureOutcome = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                        acc,
                        cookieValue,
                        _username,
                        _password,
                        TxtCookieAlias?.Text?.Trim(),
                        TxtCookieGroup?.Text?.Trim(),
                        _accountQueue.Count,
                        GetCurrentQueueAccount(),
                        () => DetectDomAccountIssueAsync(core));

                    switch (captureOutcome.Status)
                    {
                        case FastLoginCaptureStatus.Success:
                            CreatedAccount = captureOutcome.Account;
                            if (_accountQueue.Count > 0 && captureOutcome.Account != null)
                                MainWindow.Instance?.ViewModel?.UpdateAccountValidity(captureOutcome.Account, true);
                            else
                                MainWindow.Instance?.DashboardView?.LoadDashboardData();

                            string progressTag = _accountQueue.Count > 1 ? $" ({_currentQueueIndex + 1}/{_accountQueue.Count})" : "";
                            if (captureOutcome.Account != null && !Account.IsShortLivedCookie(captureOutcome.Account.SecurityToken))
                                AppSettings.SetDashboardStatus(Shiro.Services.LocalizationService.Format("Auto_FastLoginDialogS_080", progressTag, captureOutcome.Account.Username));

                            await AdvanceToNextAccountOrCloseAsync();
                            return;

                        case FastLoginCaptureStatus.UsernameMismatch:
                            core.NavigationCompleted -= OnNavigationCompleted;
                            core.SourceChanged -= OnSourceChanged;
                            ResetHbaSnapshot();
                            var freshCore = WvBrowser != null ? await WvBrowser.ResetToFreshProfileAsync() : null;
                            if (freshCore != null)
                            {
                                await ConfigureCoreWebViewAsync(freshCore);
                                freshCore.Navigate("https://www.roblox.com/login");
                            }
                            await Task.Delay(350);
                            _captured = false;
                            _pollTimer?.Start();
                            await TryFillCredentialsAsync(forceClick: true);
                            return;

                        case FastLoginCaptureStatus.UnauthenticatedModeration:
                            await RedirectUnauthenticatedModerationToLoginAsync(core);
                            return;

                        case FastLoginCaptureStatus.AccountIssue:
                            await MarkCurrentAccountIssueAndSkipAsync(
                                captureOutcome.IssueBadge ?? Shiro.Services.LocalizationService.Get("Auto_FastLoginDialogS_078"),
                                captureOutcome.Reason ?? Shiro.Services.LocalizationService.Get("Auto_FastLoginDialogS_081"));
                            return;

                        default:
                            _captured = false;
                            _pollTimer?.Start();
                            return;
                    }
                }
                else if (_accountQueue.Count > 0)
                {
                    var (issueBadge, reason) = await DetectDomAccountIssueAsync(core);
                    if (issueBadge == "UNAUTHENTICATED_MODERATION")
                        await RedirectUnauthenticatedModerationToLoginAsync(core);
                    else if (!string.IsNullOrEmpty(issueBadge))
                        await MarkCurrentAccountIssueAndSkipAsync(issueBadge, reason ?? issueBadge);
                }
            }
            catch (Exception captureEx)
            {
                Program.Logger.Warn($"[FastLogin] Capture error for '{_username}': {captureEx}");
            }
            finally
            {
                _isCheckingSession = false;
            }
        }
    }
}
