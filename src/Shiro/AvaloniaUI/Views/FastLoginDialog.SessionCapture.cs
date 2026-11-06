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
        private Account? GetCurrentQueueAccount() => _queue.Current;

        private bool _redirectedFromUnauthenticatedModeration;
        private string? _restoredHbaScriptId;
        private string? _restoredModerationCookieFp;
        private Account? _restoredModerationAccount;
        private bool _hasSeenModerationPage;

        private async Task RedirectUnauthenticatedModerationToLoginAsync(CoreWebView2 core)
        {
            if (_redirectedFromUnauthenticatedModeration) return;
            _redirectedFromUnauthenticatedModeration = true;

            var currentAcc = GetCurrentQueueAccount();
            string accName = currentAcc?.Username ?? _username ?? Shiro.Services.LocalizationService.Get("Nav_Accounts");
            if (currentAcc != null)
            {
                currentAcc.Valid = false;
                AppServices.AccountRepository.SaveAsync(true).Forget();
                MainWindow.Instance?.ViewModel?.UpdateAccountValidity(currentAcc, false);
            }

            TxtInAppStatus.Text = Shiro.Services.LocalizationService.Format("FastLogin.SessionExpiredCodeOpening", accName);
            TxtInAppStatus.Foreground = Brushes.DeepSkyBlue;
            AppSettings.SetDashboardStatus(Shiro.Services.LocalizationService.Format("FastLogin.CookieExpiredTransitioning", accName));

            try
            {
                if (!string.IsNullOrEmpty(_restoredHbaScriptId))
                {
                    try { core.RemoveScriptToExecuteOnDocumentCreated(_restoredHbaScriptId); }
                    catch (Exception ex) { Program.Logger?.Debug($"RemoveScriptToExecuteOnDocumentCreated threw: {ex.Message}"); }
                    _restoredHbaScriptId = null;
                }

                _restoredModerationAccount = null;
                _restoredModerationCookieFp = null;
                _hasSeenModerationPage = false;

                ResetHbaSnapshot();
                await ClearAuthCookiesAsync();

                _captured = false;
                _pollTimer?.Start();

                core.Navigate("https://www.roblox.com/login");
                await AsyncWaitHelper.WaitUntilAsync(
                    () => (core.Source ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromMilliseconds(1200),
                    TimeSpan.FromMilliseconds(50));
                await TryFillCredentialsAsync(forceClick: true);
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

            string progressTag = _queue.Count > 1 ? $" ({_queue.Index + 1}/{_queue.Count})" : "";
            TxtInAppStatus.Text = $"{accName}{progressTag}: {statusReason}";
            TxtInAppStatus.Foreground = Brushes.OrangeRed;
            AppSettings.SetDashboardStatus($"{accName}{progressTag}: {issueBadge} ({statusReason})");

            if (_queue.Count > 1)
            {
                _captured = true;
                _pollTimer?.Stop();
                await Task.Delay(500);
                await AdvanceToNextAccountOrCloseAsync();
            }
        }

        private async Task AdvanceToNextAccountOrCloseAsync()
        {
            if (_isClosed) return;
            try
            {
                if (_queue.HasNext)
                {
                    _captured = true;
                    _pollTimer?.Stop();

                    var oldCore = WvBrowser?.CoreWebView2;
                    if (oldCore != null)
                    {
                        oldCore.NavigationStarting -= OnNavigationStarting;
                        oldCore.NavigationCompleted -= OnNavigationCompleted;
                        oldCore.SourceChanged -= OnSourceChanged;
                        oldCore.WebResourceRequested -= OnWebResourceRequested;
                    }

                    LoadAccountAtIndex(_queue.Index + 1);
                    _captured = true;
                    TxtInAppStatus.Text = Shiro.Services.LocalizationService.Format("FastLogin.TransitioningToNext", _username, _queue.Index + 1, _queue.Count);
                    TxtInAppStatus.Foreground = Brushes.DeepSkyBlue;

                    if (WvBrowser != null && !_isClosed)
                    {
                        ResetHbaSnapshot();
                        var freshCore = await WvBrowser.ResetToFreshProfileAsync();
                        if (_isClosed) return;
                        if (freshCore != null)
                        {
                            await ConfigureCoreWebViewAsync(freshCore);
                            if (_isClosed) return;
                            freshCore.Navigate("https://www.roblox.com/login");
                        }
                    }

                    await AsyncWaitHelper.WaitUntilAsync(
                        () => _isClosed || (WvBrowser?.CoreWebView2?.Source ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase),
                        TimeSpan.FromMilliseconds(350),
                        TimeSpan.FromMilliseconds(50));
                    if (_isClosed) return;
                    _captured = false;
                    _pollTimer?.Start();
                    await TryFillCredentialsAsync(forceClick: true);
                }
                else
                {
                    ReloginQueueStore.Clear();
                    await Task.Delay(300);
                    if (!_isClosed)
                        Close(true);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[FastLoginDialog.AdvanceToNextAccountOrCloseAsync] {ex.Message}");
            }
        }

        private async Task CheckAndCaptureSessionAsync()
        {
            if (_isClosed || _captured || _isCheckingSession) return;

            var core = WvBrowser?.CoreWebView2;
            if (core == null) return;

            _isCheckingSession = true;
            string? pendingCapturedFp = null;
            try
            {
                string currentUrl = core.Source ?? string.Empty;
                if (_queue.Count > 1 && IsModerationOrLockedUrl(currentUrl))
                {
                    var (b, r) = await DetectDomAccountIssueAsync(core);
                    if (b == "UNAUTHENTICATED_MODERATION") { await RedirectUnauthenticatedModerationToLoginAsync(core); return; }
                    await MarkCurrentAccountIssueAndSkipAsync(b ?? Shiro.Services.LocalizationService.Get("FastLogin.LockedOrBannedBadge"), r ?? Shiro.Services.LocalizationService.Get("FastLogin.AccountLockedOrModerated"));
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
                        bool isRestoredModerationCookie =
                            !string.IsNullOrEmpty(_restoredModerationCookieFp) &&
                            string.Equals(fp, _restoredModerationCookieFp, StringComparison.Ordinal);

                        var ignoredAction = FastLoginCoordinator.EvaluateIgnoredCookieAction(
                            currentUrl,
                            isRestoredModerationCookie,
                            _hasSeenModerationPage);

                        switch (ignoredAction)
                        {
                            case IgnoredCookieAction.InspectModerationDom:
                            {
                                _hasSeenModerationPage = true;
                                var (domBadge, domReason) = await DetectDomAccountIssueAsync(core);
                                if (domBadge == "UNAUTHENTICATED_MODERATION") { await RedirectUnauthenticatedModerationToLoginAsync(core); return; }
                                if (!string.IsNullOrEmpty(domBadge)) await MarkCurrentAccountIssueAndSkipAsync(domBadge, domReason ?? domBadge);
                                return;
                            }

                            case IgnoredCookieAction.WaitForModerationRedirect:
                                return;

                            case IgnoredCookieAction.RedirectToLogin:
                                await RedirectUnauthenticatedModerationToLoginAsync(core);
                                return;

                            case IgnoredCookieAction.CaptureReactivatedSession:
                                _ignoredCookieValues.Remove(fp);
                                _restoredModerationCookieFp = null;
                                _restoredModerationAccount = null;
                                _hasSeenModerationPage = false;
                                break;

                            case IgnoredCookieAction.DeleteCookie:
                            default:
                                SafeExecuteHelper.Try(() => core.CookieManager.DeleteCookie(secCookie), "FastLoginDialog.DeleteIgnoredCookie");
                                return;
                        }
                    }

                    _captured = true;
                    _pollTimer?.Stop();
                    _ignoredCookieValues.Add(fp);
                    pendingCapturedFp = fp;
                    Program.Logger.Info($"[FastLogin] .ROBLOSECURITY detected for '{_username}' (len={secCookie.Value.Length}, url={core.Source}). Waiting for settle...");

                    TxtInAppStatus.Text = Shiro.Services.LocalizationService.Format("FastLogin.SessionCapturedRotating", _username ?? Shiro.Services.LocalizationService.Get("Dash_AccountsUnit"));
                    TxtInAppStatus.Foreground = Brushes.LightGreen;

                    for (int w = 0; w < 24; w++)
                    {
                        string navUrl = core.Source ?? string.Empty;
                        if (_queue.Count > 0 && IsModerationOrLockedUrl(navUrl))
                        {
                            var qAcc = GetCurrentQueueAccount();
                            if (qAcc != null)
                            {
                                qAcc.SecurityToken = secCookie.Value;
                                var modHba = await CollectFinalHbaAndFingerprintAsync(core);
                                qAcc.ApplyHbaCapture(modHba);
                                if (_queue.Count == 1)
                                {
                                    _restoredModerationCookieFp = fp;
                                    _restoredModerationAccount = qAcc;
                                    _hasSeenModerationPage = true;
                                    _captured = false;
                                    _pollTimer?.Start();
                                }
                            }
                            await Task.Delay(700);
                            var (b, r) = await DetectDomAccountIssueAsync(core);
                            if (b == "UNAUTHENTICATED_MODERATION") { await RedirectUnauthenticatedModerationToLoginAsync(core); return; }
                            await MarkCurrentAccountIssueAndSkipAsync(b ?? Shiro.Services.LocalizationService.Get("FastLogin.LockedOrBannedBadge"), r ?? Shiro.Services.LocalizationService.Get("FastLogin.AccountLockedOrModerated"));
                            return;
                        }
                        if (!string.IsNullOrEmpty(navUrl) && !navUrl.Contains("/login", StringComparison.OrdinalIgnoreCase)) break;
                        await Task.Delay(250);
                    }

                    // Wait for post-login navigation to reach /home (or other target) AND for Roblox to upgrade
                    // the intermediate/short-lived cookie to a full long-lived session cookie.
                    // This handles high-latency VPN connections without prematurely capturing a downgraded token.
                    DateTime settleStart = DateTime.UtcNow;
                    await AsyncWaitHelper.WaitUntilAsync(
                        async () =>
                        {
                            if (_isClosed) return true;
                            string u = core.Source ?? string.Empty;
                            if (IsModerationOrLockedUrl(u)) return true;

                            try
                            {
                                var currentCookies = await core.CookieManager.GetCookiesAsync("https://www.roblox.com");
                                var currentSec = currentCookies.FirstOrDefault(c =>
                                    c.Name.Equals(".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase) &&
                                    !string.IsNullOrWhiteSpace(c.Value) &&
                                    c.Value.StartsWith("_|"));

                                if (currentSec != null)
                                {
                                    if (!Account.IsShortLivedCookie(currentSec.Value))
                                        return true;

                                    if (!string.IsNullOrEmpty(u) &&
                                        !u.Contains("/login", StringComparison.OrdinalIgnoreCase) &&
                                        (DateTime.UtcNow - settleStart).TotalMilliseconds >= 3500)
                                    {
                                        return true;
                                    }
                                }
                            }
                            catch (Exception ex) { Program.Logger?.Debug($"Cookie probe during settle wait threw: {ex.Message}"); }

                            return false;
                        },
                        TimeSpan.FromSeconds(10),
                        TimeSpan.FromMilliseconds(250));

                    // Also wait for the post-login document navigation (/home) to commit its HTTP response headers
                    await AsyncWaitHelper.WaitUntilAsync(
                        () => _isClosed || !_isDocumentNavigating || IsModerationOrLockedUrl(core.Source ?? string.Empty),
                        TimeSpan.FromSeconds(4),
                        TimeSpan.FromMilliseconds(150));
                    if (!_isClosed)
                        await Task.Delay(300);

                    string postRedirectUrl = core.Source ?? string.Empty;
                    if (_queue.Count > 0 && IsModerationOrLockedUrl(postRedirectUrl))
                    {
                        var qAcc = GetCurrentQueueAccount();
                        if (qAcc != null)
                        {
                            qAcc.SecurityToken = secCookie.Value;
                            var modHba = await CollectFinalHbaAndFingerprintAsync(core);
                            qAcc.ApplyHbaCapture(modHba);
                            if (_queue.Count == 1)
                            {
                                _restoredModerationCookieFp = fp;
                                _restoredModerationAccount = qAcc;
                                _hasSeenModerationPage = true;
                                _captured = false;
                                _pollTimer?.Start();
                            }
                        }
                        var (b, r) = await DetectDomAccountIssueAsync(core);
                        if (b == "UNAUTHENTICATED_MODERATION") { await RedirectUnauthenticatedModerationToLoginAsync(core); return; }
                        await MarkCurrentAccountIssueAndSkipAsync(b ?? Shiro.Services.LocalizationService.Get("FastLogin.LockedOrBannedBadge"), r ?? Shiro.Services.LocalizationService.Get("FastLogin.AccountLockedOrModerated"));
                        return;
                    }

                    string? inPageAccountJson = null;
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
                            inPageAccountJson = resolvedAuthJson;
                    }
                    catch (Exception ex) { Program.Logger?.Debug($"Trigger in-page auth request threw: {ex.Message}"); }

                    var hbaSnapshot = await CollectFinalHbaAndFingerprintAsync(core);

                    string cookieValue = secCookie.Value;
                    try
                    {
                        string[] checkUrls = { "https://www.roblox.com", "https://users.roblox.com", "https://auth.roblox.com", "https://roblox.com" };
                        foreach (string checkUrl in checkUrls)
                        {
                            var settledCookies = await core.CookieManager.GetCookiesAsync(checkUrl);
                            foreach (var c in settledCookies)
                            {
                                if (c.Name.Equals(".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Value) && c.Value.StartsWith("_|"))
                                {
                                    // Prefer a full (long-lived) cookie over a temporary/short-lived cookie
                                    if (!Account.IsShortLivedCookie(c.Value) || Account.IsShortLivedCookie(cookieValue))
                                    {
                                        cookieValue = c.Value;
                                    }
                                    _ignoredCookieValues.Add(Fingerprint(c.Value));
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

                    Account? acc = await AppServices.AccountRepository.AddOrUpdateWithHbaAsync(cookieValue, _password ?? "", accountJson: inPageAccountJson, hbaCapture: hbaSnapshot);
                    pendingCapturedFp = null;
                    Program.Logger?.Info($"[FastLogin] Captured session for '{_username}': payloadLen={Account.GetSessionTokenPayloadLength(cookieValue)}, hba={hbaSnapshot.HasPrivateKey}, registeredValid={acc?.Valid}, resolvedUser={acc?.Username}.");

                    var captureOutcome = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                        acc,
                        cookieValue,
                        _username,
                        _password,
                        CookieViewModel.Alias?.Trim(),
                        CookieViewModel.Group?.Trim(),
                        _queue.Count,
                        GetCurrentQueueAccount(),
                        () => DetectDomAccountIssueAsync(core));

                    switch (captureOutcome.Status)
                    {
                        case FastLoginCaptureStatus.Success:
                            CreatedAccount = captureOutcome.Account;
                            if (_queue.Count > 0 && captureOutcome.Account != null)
                                MainWindow.Instance?.ViewModel?.UpdateAccountValidity(captureOutcome.Account, true);
                            else
                                MainWindow.Instance?.DashboardView?.LoadDashboardData();

                            string progressTag = _queue.Count > 1 ? $" ({_queue.Index + 1}/{_queue.Count})" : "";
                            if (captureOutcome.Account != null && !Account.IsShortLivedCookie(captureOutcome.Account.SecurityToken))
                                AppSettings.SetDashboardStatus(Shiro.Services.LocalizationService.Format("FastLogin.InAppLoginSuccess", progressTag, captureOutcome.Account.Username));

                            await AdvanceToNextAccountOrCloseAsync();
                            return;

                        case FastLoginCaptureStatus.UsernameMismatch:
                            core.NavigationStarting -= OnNavigationStarting;
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
                                captureOutcome.IssueBadge ?? Shiro.Services.LocalizationService.Get("FastLogin.LockedOrBannedBadge"),
                                captureOutcome.Reason ?? Shiro.Services.LocalizationService.Get("FastLogin.SessionLockedOrModerated"));
                            return;

                        default:
                            _captured = false;
                            _pollTimer?.Start();
                            return;
                    }
                }
                else if (_queue.Count > 0)
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
                if (!_isClosed && pendingCapturedFp != null)
                {
                    _ignoredCookieValues.Remove(pendingCapturedFp);
                    _captured = false;
                    _pollTimer?.Start();
                }
            }
            finally
            {
                _isCheckingSession = false;
            }
        }
    }
}
