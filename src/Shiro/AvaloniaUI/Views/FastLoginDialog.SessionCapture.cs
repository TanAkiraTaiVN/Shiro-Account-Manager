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
        private Account? GetCurrentQueueAccount()
        {
            return (_currentQueueIndex >= 0 && _currentQueueIndex < _accountQueue.Count)
                ? _accountQueue[_currentQueueIndex]
                : null;
        }

        private async Task MarkCurrentAccountIssueAndSkipAsync(string issueBadge, string statusReason)
        {
            var currentAcc = GetCurrentQueueAccount();
            string accName = currentAcc?.Username ?? _username ?? "Tài khoản";

            if (currentAcc != null)
            {
                currentAcc.Valid = false;
                currentAcc.AccountStatusIssue = issueBadge;
                AccountManager.SaveAccountsAsync(true).Forget();
                MainWindow.Instance?.ViewModel?.UpdateAccountValidity(currentAcc, false);
            }

            string progressTag = _accountQueue.Count > 1 ? $" ({_currentQueueIndex + 1}/{_accountQueue.Count})" : "";
            TxtInAppStatus.Text = $"{accName}{progressTag}: {statusReason}";
            TxtInAppStatus.Foreground = Brushes.OrangeRed;
            AccountManager.Instance?.SetDashboardStatus($"{accName}{progressTag}: {issueBadge} ({statusReason})");

            // For automated batch login queues, advance to the next account
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

                TxtInAppStatus.Text = $"Chuyển sang {_username} ({_currentQueueIndex + 1}/{_accountQueue.Count})...";
                TxtInAppStatus.Foreground = Brushes.DeepSkyBlue;

                CoreWebView2? freshCore = null;
                if (WvBrowser != null)
                {
                    ResetHbaSnapshot();
                    freshCore = await WvBrowser.ResetToFreshProfileAsync();
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
                    await MarkCurrentAccountIssueAndSkipAsync("Bị khóa / Ban", "Tài khoản bị khóa hoặc kiểm duyệt");
                    return;
                }

                var cookies = await core.CookieManager.GetCookiesAsync("https://www.roblox.com");
                CoreWebView2Cookie? secCookie = null;
                foreach (var c in cookies)
                {
                    if (c.Name.Equals(".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase))
                    {
                        secCookie = c;
                        break;
                    }
                }

                if (secCookie != null && !string.IsNullOrWhiteSpace(secCookie.Value) && secCookie.Value.StartsWith("_|"))
                {
                    string fp = Fingerprint(secCookie.Value);
                    if (_ignoredCookieValues.Contains(fp))
                    {
                        if (IsModerationOrLockedUrl(currentUrl))
                        {
                            var (domBadge, domReason) = await DetectDomAccountIssueAsync(core);
                            if (!string.IsNullOrEmpty(domBadge))
                                await MarkCurrentAccountIssueAndSkipAsync(domBadge, domReason ?? domBadge);
                            return;
                        }
                        if (_accountQueue.Count == 1 && _accountQueue[0].IsBannedOrLocked &&
                            currentUrl.Contains("/home", StringComparison.OrdinalIgnoreCase))
                        {
                            _ignoredCookieValues.Remove(fp);
                        }
                        else
                        {
                            try { core.CookieManager.DeleteCookie(secCookie); } catch (Exception) { }
                            return;
                        }
                    }

                    _captured = true;
                    _pollTimer?.Stop();
                    _ignoredCookieValues.Add(fp);
                    Program.Logger.Info($"[FastLogin] .ROBLOSECURITY detected for '{_username}' (len={secCookie.Value.Length}, url={core.Source}). Waiting for settle...");

                    TxtInAppStatus.Text = $"Đã bắt phiên {_username ?? "tài khoản"}! Đang chờ xoay Cookie & lưu...";
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
                            await MarkCurrentAccountIssueAndSkipAsync(b ?? "Bị khóa / Ban", r ?? "Tài khoản bị khóa hoặc kiểm duyệt");
                            return;
                        }
                        if (!string.IsNullOrEmpty(navUrl) && !navUrl.Contains("/login", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                        await Task.Delay(250);
                    }

                    await Task.Delay(1500);

                    string postRedirectUrl = core.Source ?? string.Empty;
                    if (_accountQueue.Count > 0 && IsModerationOrLockedUrl(postRedirectUrl))
                    {
                        var qAcc = GetCurrentQueueAccount();
                        if (qAcc != null) qAcc.SecurityToken = secCookie.Value;
                        var (b, r) = await DetectDomAccountIssueAsync(core);
                        await MarkCurrentAccountIssueAndSkipAsync(b ?? "Bị khóa / Ban", r ?? "Tài khoản bị khóa hoặc kiểm duyệt");
                        return;
                    }

                    try
                    {
                        await core.ExecuteScriptAsync(@"
                            (async () => {
                                try {
                                    await fetch('https://users.roblox.com/v1/users/authenticated', {
                                        method: 'GET',
                                        credentials: 'include',
                                        headers: { 'Accept': 'application/json' }
                                    });
                                } catch (_) {}
                            })()
                        ");
                        await Task.Delay(300);
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Trigger in-page auth request threw: {ex.Message}");
                    }

                    string cookieValue = secCookie.Value;
                    try
                    {
                        string[] checkUrls = { "https://www.roblox.com", "https://auth.roblox.com", "https://roblox.com" };
                        foreach (string checkUrl in checkUrls)
                        {
                            var settledCookies = await core.CookieManager.GetCookiesAsync(checkUrl);
                            foreach (var c in settledCookies)
                            {
                                if (c.Name.Equals(".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase) &&
                                    !string.IsNullOrWhiteSpace(c.Value) &&
                                    c.Value.StartsWith("_|"))
                                {
                                    cookieValue = c.Value;
                                    _ignoredCookieValues.Add(Fingerprint(cookieValue));
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Reading settled cookies threw: {ex.Message}");
                    }

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
                                try { unescaped = JsonSerializer.Deserialize(passJson, ShiroAppJson.Default.String); } catch (Exception) { }
                                if (string.IsNullOrEmpty(unescaped)) unescaped = passJson.Trim('"');
                                if (!string.IsNullOrEmpty(unescaped))
                                {
                                    _password = unescaped;
                                    var currAcc = GetCurrentQueueAccount();
                                    if (currAcc != null)
                                    {
                                        currAcc.Password = unescaped;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"Capturing password from DOM threw: {ex.Message}");
                        }
                    }

                    var hbaSnapshot = await CollectFinalHbaAndFingerprintAsync(core);

                    try
                    {
                        core.Stop();
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"core.Stop() threw: {ex.Message}");
                    }

                    Account? acc = await AccountManager.AddAccountAsync(cookieValue, _password ?? "", hbaCapture: hbaSnapshot);
                    Program.Logger?.Info($"[FastLogin] Captured session for '{_username}': payloadLen={Account.GetSessionTokenPayloadLength(cookieValue)}, hba={hbaSnapshot.HasPrivateKey}, registeredValid={acc?.Valid}, resolvedUser={acc?.Username}.");

                    if (acc != null && acc.Valid)
                    {
                        if (!string.IsNullOrEmpty(_password))
                        {
                            acc.Password = _password;
                        }
                        if (!string.IsNullOrEmpty(_username) &&
                            !string.Equals(acc.Username, _username, StringComparison.OrdinalIgnoreCase))
                        {
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
                        }

                        acc.AccountStatusIssue = string.Empty;
                        string? alias = TxtCookieAlias?.Text?.Trim();
                        string? group = TxtCookieGroup?.Text?.Trim();
                        if (!string.IsNullOrEmpty(alias)) acc.Alias = alias;
                        if (!string.IsNullOrEmpty(group)) acc.Group = group;

                        try { await AccountManager.SaveAccountsAsync(true); }
                        catch (Exception ex) { Program.Logger?.Warn($"SaveAccountsAsync after fast login failed: {ex.Message}"); }

                        CreatedAccount = acc;
                        if (_accountQueue.Count > 0)
                            MainWindow.Instance?.ViewModel?.UpdateAccountValidity(acc, true);
                        else
                            MainWindow.Instance?.DashboardView?.LoadDashboardData();

                        string progressTag = _accountQueue.Count > 1 ? $" ({_currentQueueIndex + 1}/{_accountQueue.Count})" : "";
                        if (!Account.IsShortLivedCookie(acc.SecurityToken))
                            AccountManager.Instance?.SetDashboardStatus($"Đăng nhập In-App thành công{progressTag}: {acc.Username}!");

                        await AdvanceToNextAccountOrCloseAsync();
                    }
                    else
                    {
                        if (_accountQueue.Count > 0)
                        {
                            var qAcc = GetCurrentQueueAccount();
                            string badge = acc?.IsBannedOrLocked == true ? acc.AccountStatusIssue : "Bị khóa / Ban";
                            if (qAcc != null && string.IsNullOrEmpty(qAcc.SecurityToken))
                                qAcc.SecurityToken = cookieValue;
                            await MarkCurrentAccountIssueAndSkipAsync(badge, "Phiên bị khóa hoặc kiểm duyệt");
                            return;
                        }

                        _captured = false;
                        _pollTimer?.Start();
                    }
                }
                else if (_accountQueue.Count > 0)
                {
                    var (issueBadge, reason) = await DetectDomAccountIssueAsync(core);
                    if (!string.IsNullOrEmpty(issueBadge))
                    {
                        await MarkCurrentAccountIssueAndSkipAsync(issueBadge, reason ?? issueBadge);
                    }
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
