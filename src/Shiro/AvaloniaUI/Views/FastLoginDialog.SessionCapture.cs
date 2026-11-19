#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Microsoft.Web.WebView2.Core;
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
        private string? _pendingCapturedFp;

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
            AppServices.HostUiBridge.NotifyWindowStatus(Shiro.Services.LocalizationService.Format("FastLogin.CookieExpiredTransitioning", accName));

            try
            {
                if (!string.IsNullOrEmpty(_restoredHbaScriptId))
                {
                    try { core.RemoveScriptToExecuteOnDocumentCreated(_restoredHbaScriptId); }
                    catch (Exception ex) { AppServices.Logger?.Debug($"RemoveScriptToExecuteOnDocumentCreated threw: {ex.Message}"); }
                    _restoredHbaScriptId = null;
                }

                _restoredModerationAccount = null;
                _restoredModerationCookieFp = null;
                _hasSeenModerationPage = false;
                _pendingCapturedFp = null;
                _pendingCapturedPassword = null;

                ResetHbaSnapshot();
                await ClearAuthCookiesAsync();
                if (_isClosed) return;

                _captured = false;
                _pollTimer?.Start();

                core.Navigate("https://www.roblox.com/login");
                await AsyncWaitHelper.WaitUntilAsync(
                    () => _isClosed || (core.Source ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromMilliseconds(1200),
                    TimeSpan.FromMilliseconds(50));
                if (_isClosed) return;
                await TryFillCredentialsAsync(forceClick: true);
            }
            catch (Exception ex) { AppServices.Logger?.Debug($"RedirectUnauthenticatedModerationToLoginAsync threw: {ex.Message}"); }
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
            AppServices.HostUiBridge.NotifyWindowStatus($"{accName}{progressTag}: {issueBadge} ({statusReason})");

            if (_queue.Count > 1)
            {
                _captured = true;
                _pollTimer?.Stop();
                await Task.Delay(500);
                if (_isClosed) return;
                await AdvanceToNextAccountOrCloseAsync();
            }
        }

        private async Task ResetToFreshLoginProfileAsync(bool waitForLoginUrl)
        {
            if (_isClosed) return;
            ResetMismatchState();
            var freshCore = WvBrowser != null ? await WvBrowser.ResetToFreshProfileAsync() : null;
            if (_isClosed) return;
            if (freshCore != null)
            {
                await ConfigureCoreWebViewAsync(freshCore);
                if (_isClosed) return;
                freshCore.Navigate("https://www.roblox.com/login");
            }

            if (waitForLoginUrl)
            {
                await AsyncWaitHelper.WaitUntilAsync(
                    () => _isClosed || (WvBrowser?.CoreWebView2?.Source ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromMilliseconds(350),
                    TimeSpan.FromMilliseconds(50));
            }
            else
            {
                await Task.Delay(350);
            }

            if (_isClosed) return;
            _captured = false;
            _pollTimer?.Start();
            await TryFillCredentialsAsync(forceClick: true);
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
                        oldCore.WebMessageReceived -= OnWebMessageReceived;
                    }

                    LoadAccountAtIndex(_queue.Index + 1);
                    _captured = true;
                    TxtInAppStatus.Text = Shiro.Services.LocalizationService.Format("FastLogin.TransitioningToNext", _username, _queue.Index + 1, _queue.Count);
                    TxtInAppStatus.Foreground = Brushes.DeepSkyBlue;

                    await ResetToFreshLoginProfileAsync(waitForLoginUrl: true);
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
                AppServices.Logger?.Debug($"[FastLoginDialog.AdvanceToNextAccountOrCloseAsync] {ex.Message}");
            }
        }

        private async Task HandleCapturedModerationPageAsync(CoreWebView2 core, string cookieValue, string fp, int delayMs)
        {
            var qAcc = GetCurrentQueueAccount();
            if (qAcc != null)
            {
                qAcc.SecurityToken = cookieValue;
                var modHba = await CollectFinalHbaAndFingerprintAsync(core);
                if (_isClosed) return;
                qAcc.ApplyHbaCapture(modHba);
                if (_queue.Count == 1)
                {
                    _ignoredCookieValues.Add(fp);
                    _restoredModerationCookieFp = fp;
                    _restoredModerationAccount = qAcc;
                    _hasSeenModerationPage = true;
                    _captured = false;
                    _pollTimer?.Start();
                }
            }
            _pendingCapturedFp = null;
            if (delayMs > 0) await Task.Delay(delayMs);
            if (_isClosed) return;
            var (b, r) = await DetectDomAccountIssueAsync(core);
            if (_isClosed) return;
            if (b == "UNAUTHENTICATED_MODERATION") { await RedirectUnauthenticatedModerationToLoginAsync(core); return; }
            await MarkCurrentAccountIssueAndSkipAsync(b ?? Shiro.Services.LocalizationService.Get("FastLogin.LockedOrBannedBadge"), r ?? Shiro.Services.LocalizationService.Get("FastLogin.AccountLockedOrModerated"));
        }

        private async Task CheckAndCaptureSessionAsync()
        {
            if (_isClosed || _captured || _isCheckingSession) return;

            var core = WvBrowser?.CoreWebView2;
            if (core == null) return;

            _isCheckingSession = true;
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
                                if (FastLoginScripts.ShouldDeleteIgnoredCookie(ignoredAction, fp, _pendingCapturedFp))
                                    SafeExecuteHelper.Try(() => core.CookieManager.DeleteCookie(secCookie), "FastLoginDialog.DeleteIgnoredCookie");
                                return;
                        }
                    }

                    _captured = true;
                    _pollTimer?.Stop();
                    _pendingCapturedFp = fp;
                    AppServices.Logger.Info($"[FastLogin] .ROBLOSECURITY detected for '{_username}' (len={secCookie.Value.Length}, url={core.Source}). Waiting for settle...");

                    TxtInAppStatus.Text = Shiro.Services.LocalizationService.Format("FastLogin.SessionCapturedRotating", _username ?? Shiro.Services.LocalizationService.Get("Dash_AccountsUnit"));
                    TxtInAppStatus.Foreground = Brushes.LightGreen;

                    for (int w = 0; w < 24; w++)
                    {
                        string navUrl = core.Source ?? string.Empty;
                        if (_queue.Count > 0 && IsModerationOrLockedUrl(navUrl))
                        {
                            await HandleCapturedModerationPageAsync(core, secCookie.Value, fp, delayMs: 700);
                            return;
                        }
                        if (!string.IsNullOrEmpty(navUrl) && !navUrl.Contains("/login", StringComparison.OrdinalIgnoreCase)) break;
                        await Task.Delay(250);
                    }

                    // Wait for post-login navigation to reach /home (or other target) AND for Roblox to upgrade
                    // the intermediate/short-lived cookie to a full long-lived session cookie.
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
                            catch (Exception ex) { AppServices.Logger?.Debug($"Cookie probe during settle wait threw: {ex.Message}"); }

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
                        await HandleCapturedModerationPageAsync(core, secCookie.Value, fp, delayMs: 0);
                        return;
                    }

                    string? inPageAccountJson = await FastLoginScripts.FetchAuthenticatedUserJsonAsync(core);
                    if (_isClosed) return;

                    var hbaSnapshot = await CollectFinalHbaAndFingerprintAsync(core);
                    if (_isClosed) return;
                    string cookieValue = await CaptureSettledCookieAndDomPasswordAsync(core, secCookie.Value);
                    if (_isClosed) return;

                    var captureOutcome = await FastLoginCoordinator.ResolveAndCommitCapturedSessionAsync(
                        cookieValue,
                        _username,
                        GetEffectivePasswordForCommit(),
                        CookieViewModel.Alias?.Trim(),
                        CookieViewModel.Group?.Trim(),
                        inPageAccountJson,
                        hbaSnapshot,
                        _queue.Count,
                        GetCurrentQueueAccount(),
                        () => DetectDomAccountIssueAsync(core));
                    if (_isClosed) return;
                    FastLoginScripts.ApplySettlementOutcome(
                        captureOutcome.Status,
                        _ignoredCookieValues,
                        ref _pendingCapturedFp,
                        fp,
                        Fingerprint(cookieValue));
                    Account? acc = captureOutcome.Account;
                    AppServices.Logger?.Info($"[FastLogin] Captured session for '{_username}': status={captureOutcome.Status}, payloadLen={Account.GetSessionTokenPayloadLength(cookieValue)}, hba={hbaSnapshot.HasPrivateKey}, registeredValid={acc?.Valid}, resolvedUser={acc?.Username}.");

                    switch (captureOutcome.Status)
                    {
                        case FastLoginCaptureStatus.Success:
                            _pendingCapturedPassword = null;
                            CreatedAccount = captureOutcome.Account;
                            if (_queue.Count > 0 && captureOutcome.Account != null)
                                MainWindow.Instance?.ViewModel?.UpdateAccountValidity(captureOutcome.Account, true);
                            else
                                MainWindow.Instance?.DashboardView?.LoadDashboardData();

                            string progressTag = _queue.Count > 1 ? $" ({_queue.Index + 1}/{_queue.Count})" : "";
                            if (captureOutcome.Account != null && !Account.IsShortLivedCookie(captureOutcome.Account.SecurityToken))
                                AppServices.HostUiBridge.NotifyWindowStatus(Shiro.Services.LocalizationService.Format("FastLogin.InAppLoginSuccess", progressTag, captureOutcome.Account.Username));

                            await AdvanceToNextAccountOrCloseAsync();
                            return;

                        case FastLoginCaptureStatus.UsernameMismatch:
                            core.NavigationStarting -= OnNavigationStarting;
                            core.NavigationCompleted -= OnNavigationCompleted;
                            core.SourceChanged -= OnSourceChanged;
                            core.WebResourceRequested -= OnWebResourceRequested;
                            core.WebMessageReceived -= OnWebMessageReceived;
                            string wrongUser = captureOutcome.Account?.Username ?? "Unknown";
                            string mismatchMsg = Shiro.Services.LocalizationService.Format("FastLogin.UsernameMismatchReset", wrongUser, _username ?? string.Empty);
                            TxtInAppStatus.Text = mismatchMsg;
                            TxtInAppStatus.Foreground = Brushes.Orange;
                            AppServices.HostUiBridge.NotifyWindowStatus(mismatchMsg);
                            await ResetToFreshLoginProfileAsync(waitForLoginUrl: false);
                            return;

                        case FastLoginCaptureStatus.UnauthenticatedModeration:
                            _pendingCapturedPassword = null;
                            await RedirectUnauthenticatedModerationToLoginAsync(core);
                            return;

                        case FastLoginCaptureStatus.AccountIssue:
                            _pendingCapturedPassword = null;
                            await MarkCurrentAccountIssueAndSkipAsync(
                                captureOutcome.IssueBadge ?? Shiro.Services.LocalizationService.Get("FastLogin.LockedOrBannedBadge"),
                                captureOutcome.Reason ?? Shiro.Services.LocalizationService.Get("FastLogin.SessionLockedOrModerated"));
                            return;

                        default:
                            if (_isClosed) return;
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
                AppServices.Logger.Warn($"[FastLogin] Capture error for '{_username}': {captureEx}");
                if (!_isClosed && _pendingCapturedFp != null)
                {
                    _ignoredCookieValues.Remove(_pendingCapturedFp);
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
