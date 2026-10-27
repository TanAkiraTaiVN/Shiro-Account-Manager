#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using RBX_Alt_Manager.AvaloniaUI.Controls;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.AvaloniaUI.Views
{
    public partial class FastLoginDialog : Window
    {
        private DispatcherTimer? _pollTimer;
        private readonly List<Account> _accountQueue = new List<Account>();
        private readonly HashSet<string> _ignoredCookieValues = new HashSet<string>(StringComparer.Ordinal);
        private int _currentQueueIndex;
        private string _username = string.Empty;
        private string _password = string.Empty;
        private bool _captured;
        private bool _isCheckingSession;
        private string _lastVerifiedCookie = string.Empty;
        private Account? _lastVerifiedAccount;

        public Account? CreatedAccount { get; private set; }

        // Only SHA-256 fingerprints of seen cookies are kept in memory, never the raw .ROBLOSECURITY values.
        private static string Fingerprint(string value) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty)));

        public FastLoginDialog()
        {
            InitializeComponent();
        }

        public FastLoginDialog(Account? account = null) : this()
        {
            if (account != null)
            {
                _accountQueue.Add(account);
                LoadAccountAtIndex(0);
            }
        }

        public FastLoginDialog(IReadOnlyList<Account> accounts) : this()
        {
            if (accounts != null)
            {
                foreach (var acc in accounts)
                {
                    if (acc != null)
                        _accountQueue.Add(acc);
                }
            }

            if (_accountQueue.Count > 0)
            {
                LoadAccountAtIndex(0);
            }
        }

        private void LoadAccountAtIndex(int index)
        {
            if (index < 0 || index >= _accountQueue.Count)
                return;

            _currentQueueIndex = index;
            var account = _accountQueue[index];
            _username = account.Username;
            _password = account.Password;
            _captured = false;

            if (_accountQueue.Count > 1)
                ReloginQueueStore.Save(_accountQueue.Skip(index).Select(a => a.UserID));

            if (!string.IsNullOrEmpty(_username))
            {
                BtnRefill.IsVisible = true;
                if (TxtDialogTitle != null)
                {
                    TxtDialogTitle.Text = _accountQueue.Count > 1
                        ? $"Đăng nhập: {_username} ({index + 1}/{_accountQueue.Count})"
                        : $"Đăng nhập: {_username}";
                }
                if (TxtDialogSubtitle != null)
                {
                    TxtDialogSubtitle.Text = !string.IsNullOrEmpty(_password)
                        ? "Tự động điền Tài khoản + Mật khẩu và bấm Log In"
                        : "Tự động điền Tài khoản và bắt phiên đăng nhập";
                }
            }

            if (BtnSkipNext != null)
            {
                BtnSkipNext.IsVisible = _accountQueue.Count > 1 && index + 1 < _accountQueue.Count;
            }

            if (TxtCookieAlias != null)
                TxtCookieAlias.Text = account.Alias ?? string.Empty;
            if (TxtCookieGroup != null)
                TxtCookieGroup.Text = account.Group ?? string.Empty;
            if (TxtCookie != null && !string.IsNullOrEmpty(account.SecurityToken))
                TxtCookie.Text = account.SecurityToken;
        }

        private void OnModeCheckedChanged(object sender, RoutedEventArgs e)
        {
            bool isInApp = TabInApp?.IsChecked == true;
            if (PnlInApp != null) PnlInApp.IsVisible = isInApp;
            if (PnlCookie != null) PnlCookie.IsVisible = !isInApp;
            if (BtnVerify != null) BtnVerify.IsVisible = !isInApp;
            if (BtnAddCookie != null) BtnAddCookie.IsVisible = !isInApp;
            if (TxtTip != null) TxtTip.IsVisible = isInApp;

            if (isInApp && WvBrowser != null)
            {
                Dispatcher.UIThread.Post(() => WvBrowser.UpdateBounds(), DispatcherPriority.Render);
            }
        }

        private async Task ConfigureCoreWebViewAsync(CoreWebView2 core)
        {
            if (core == null) return;

            await ClearAuthCookiesAsync();

            // Inject dark background & GDPR banner suppression only.
            // IMPORTANT: Do NOT strip secureAuthenticationIntent or block IndexedDB('roblox-security')!
            // Stripping SAI causes POST /v2/login to issue a downgraded 592-byte (790-char) fallback cookie
            // that expires after ~30 minutes on auth.roblox.com, instead of a full 704-byte (939-char) 7+ day session.
            // Cross-account isolation is guaranteed by giving every account its own virgin WebView2 ProfileName.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(@"
                try {
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
                        try { if (window.OneTrust && window.OneTrust.AllowAll) window.OneTrust.AllowAll(); } catch(_) {}
                        try {
                            const hookPass = () => {
                                const p = document.querySelector('#login-password') || document.querySelector('input[type=""password""]');
                                if (p && !p.__shiroHooked) {
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
                            };
                            hookPass();
                            setInterval(hookPass, 400);
                        } catch(_) {}
                    });
                } catch(_) {}
            ");

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
                try { raw = e.TryGetWebMessageAsString(); } catch (Exception) { }
                if (string.IsNullOrEmpty(raw))
                {
                    try { raw = e.WebMessageAsJson; } catch (Exception) { }
                }

                if (!string.IsNullOrEmpty(raw))
                {
                    using var doc = JsonDocument.Parse(raw);
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

        private async void OnWebViewInitialized(object sender, WebView2InitializedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                TxtInAppStatus.Text = $"❌ Không thể khởi tạo WebView2: {e.Error?.Message}";
                TxtInAppStatus.Foreground = Brushes.OrangeRed;
                return;
            }

            TxtInAppStatus.Text = "Đang tải trang đăng nhập Roblox an toàn...";
            TxtInAppStatus.Foreground = Brushes.DeepSkyBlue;

            var core = WvBrowser?.CoreWebView2;
            if (core == null) return;

            try
            {
                await ConfigureCoreWebViewAsync(core);

                // Navigate to login
                core.Navigate("https://www.roblox.com/login");

                // Start fast poll timer to capture .ROBLOSECURITY session
                if (_pollTimer != null)
                {
                    _pollTimer.Stop();
                    _pollTimer.Tick -= OnPollTimerTick;
                }
                _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _pollTimer.Tick += OnPollTimerTick;
                _pollTimer.Start();
            }
            catch (Exception ex)
            {
                TxtInAppStatus.Text = $"❌ Lỗi: {ex.Message}";
                TxtInAppStatus.Foreground = Brushes.OrangeRed;
            }
        }

        private static bool IsModerationOrLockedUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return false;

            return url.Contains("/not-approved", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/account-status", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/banned", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/moderation", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/account-locked", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/security-notification", StringComparison.OrdinalIgnoreCase);
        }

        private Account? GetCurrentQueueAccount()
        {
            return (_currentQueueIndex >= 0 && _currentQueueIndex < _accountQueue.Count)
                ? _accountQueue[_currentQueueIndex]
                : null;
        }

        private async Task MarkCurrentAccountIssueAndSkipAsync(string issueBadge, string statusReason)
        {
            _captured = true;
            _pollTimer?.Stop();

            var currentAcc = GetCurrentQueueAccount();
            string accName = currentAcc?.Username ?? _username ?? "Tài khoản";

            if (currentAcc != null)
            {
                currentAcc.Valid = false;
                currentAcc.AccountStatusIssue = issueBadge;
                _ = AccountManager.SaveAccountsAsync(true);
                MainWindow.Instance?.ViewModel?.UpdateAccountValidity(currentAcc, false);
            }

            string progressTag = _accountQueue.Count > 1 ? $" ({_currentQueueIndex + 1}/{_accountQueue.Count})" : "";
            TxtInAppStatus.Text = $"⛔ {accName}{progressTag}: {statusReason} — Đang bỏ qua...";
            TxtInAppStatus.Foreground = Brushes.OrangeRed;
            AccountManager.Instance?.SetDashboardStatus($"⛔ Đã đánh dấu {accName}{progressTag}: {issueBadge} ({statusReason})");

            await Task.Delay(350);
            await AdvanceToNextAccountOrCloseAsync();
        }

        private async Task<(string? IssueBadge, string? Reason)> DetectDomAccountIssueAsync(CoreWebView2? core)
        {
            if (core == null || _accountQueue.Count == 0)
                return (null, null);

            try
            {
                string rawJson = await core.ExecuteScriptAsync(@"
                    (() => {
                        try {
                            const url = (window.location.href || '').toLowerCase();
                            if (url.includes('/not-approved') || url.includes('/account-status') || url.includes('/moderation') || url.includes('/security-notification') || url.includes('/account-locked')) {
                                return 'LOCKED_OR_BANNED';
                            }
                            const errSelectors = '#login-form-error, .form-error, .alert-warning, .text-error, [role=""alert""], .modal-body, .not-approved-container';
                            const nodes = document.querySelectorAll(errSelectors);
                            let combined = '';
                            for (const n of nodes) {
                                if (n && n.offsetParent !== null && n.innerText) {
                                    combined += ' ' + n.innerText.toLowerCase();
                                }
                            }
                            if (!combined) return '';
                            if (combined.includes('locked') || combined.includes('bị khóa') || combined.includes('reset your password') || combined.includes('security notification')) {
                                return 'ACCOUNT_LOCKED';
                            }
                            if (combined.includes('not approved') || combined.includes('banned') || combined.includes('terminated') || combined.includes('moderated') || combined.includes('deleted') || combined.includes('suspended')) {
                                return 'ACCOUNT_BANNED';
                            }
                            if (combined.includes('incorrect username or password') || combined.includes('invalid username or password') || combined.includes('sai mật khẩu') || combined.includes('tên người dùng hoặc mật khẩu không đúng')) {
                                return 'WRONG_PASSWORD';
                            }
                            return '';
                        } catch (_) {
                            return '';
                        }
                    })()
                ");

                if (string.IsNullOrWhiteSpace(rawJson))
                    return (null, null);

                string code = rawJson.Trim().Trim('"');
                return code switch
                {
                    "LOCKED_OR_BANNED" => ("⛔ Bị khóa / Ban", "Tài khoản bị khóa hoặc kiểm duyệt"),
                    "ACCOUNT_LOCKED" => ("⛔ Bị khóa / Ban", "Tài khoản bị khóa bảo mật"),
                    "ACCOUNT_BANNED" => ("⛔ Bị khóa / Ban", "Tài khoản bị Ban / Đình chỉ"),
                    "WRONG_PASSWORD" => ("⛔ Sai mật khẩu", "Sai tài khoản hoặc mật khẩu"),
                    _ => (null, null)
                };
            }
            catch
            {
                return (null, null);
            }
        }

        private async void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
        {
            string currentUrl = WvBrowser?.CoreWebView2?.Source ?? string.Empty;
            if (!_captured && _accountQueue.Count > 0 && IsModerationOrLockedUrl(currentUrl))
            {
                await MarkCurrentAccountIssueAndSkipAsync("⛔ Bị khóa / Ban", "Tài khoản bị khóa hoặc Ban");
                return;
            }

            await CheckAndCaptureSessionAsync();
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            string currentUrl = WvBrowser?.CoreWebView2?.Source ?? string.Empty;
            if (!_captured && _accountQueue.Count > 0 && IsModerationOrLockedUrl(currentUrl))
            {
                await MarkCurrentAccountIssueAndSkipAsync("⛔ Bị khóa / Ban", "Tài khoản bị khóa hoặc Ban");
                return;
            }

            if (!e.IsSuccess)
            {
                if (e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted ||
                    e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
                {
                    return;
                }

                TxtInAppStatus.Text = "⚠️ Không thể tải trang đăng nhập. Hãy kiểm tra kết nối Proton VPN và bấm 'Tải lại'.";
                TxtInAppStatus.Foreground = Brushes.OrangeRed;
                return;
            }

            bool isLoginPage = currentUrl.Contains("/login", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(_username) && isLoginPage)
            {
                TxtInAppStatus.Text = !string.IsNullOrEmpty(_password)
                    ? $"⚡ Đang tự động điền và đăng nhập {_username}..."
                    : $"⚡ Đang tự động điền {_username}...";
                TxtInAppStatus.Foreground = Brushes.LightGreen;
                await TryFillCredentialsAsync(forceClick: false);
            }
            else if (isLoginPage)
            {
                TxtInAppStatus.Text = "✅ Trang đăng nhập đã sẵn sàng. Hãy đăng nhập tài khoản của bạn.";
                TxtInAppStatus.Foreground = Brushes.LightGreen;
            }

            await CheckAndCaptureSessionAsync();
        }

        private async void OnPollTimerTick(object? sender, EventArgs e)
        {
            await CheckAndCaptureSessionAsync();
        }

        private async Task ClearAuthCookiesAsync()
        {
            var core = WvBrowser?.CoreWebView2;
            if (core == null) return;
            try
            {
                try { core.Stop(); } catch (Exception) { }

                // Unload any active roblox.com page BEFORE deleting cookies so the open page
                // cannot detect cookie removal or send a background sign-out / session-revoke beacon.
                try
                {
                    string currentSrc = core.Source ?? string.Empty;
                    if (!string.IsNullOrEmpty(currentSrc) &&
                        !currentSrc.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                    {
                        core.Navigate("about:blank");
                        await Task.Delay(80);
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Navigate to about:blank failed: {ex.Message}");
                }

                var cookieManager = core.CookieManager;
                string[] urls =
                {
                    "https://www.roblox.com",
                    "https://roblox.com",
                    "https://auth.roblox.com",
                    "https://apis.roblox.com",
                    "https://users.roblox.com"
                };

                foreach (string url in urls)
                {
                    try
                    {
                        var existingCookies = await cookieManager.GetCookiesAsync(url);
                        foreach (var c in existingCookies)
                        {
                            if (c.Name.Equals(".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(c.Value))
                            {
                                _ignoredCookieValues.Add(Fingerprint(c.Value));
                            }
                            cookieManager.DeleteCookie(c);
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Clear cookies for {url} threw: {ex.Message}");
                    }
                }

                try { cookieManager.DeleteCookiesWithDomainAndPath(".ROBLOSECURITY", ".roblox.com", "/"); } catch (Exception) { }
                try { cookieManager.DeleteCookiesWithDomainAndPath(".RBXIDCHECK", ".roblox.com", "/"); } catch (Exception) { }
                try { cookieManager.DeleteCookiesWithDomainAndPath(".RBXSESSIONTRACKER", ".roblox.com", "/"); } catch (Exception) { }
                try { cookieManager.DeleteCookiesWithDomainAndPath("RBXEventTrackerV2", ".roblox.com", "/"); } catch (Exception) { }

                // Purge all cookies, localStorage, sessionStorage, and HBA IndexedDB keys via DevTools Protocol & Profile API
                try { await core.Profile.ClearBrowsingDataAsync(); } catch (Exception) { }
                try { await core.CallDevToolsProtocolMethodAsync("Network.clearBrowserCookies", "{}"); } catch (Exception) { }
                try
                {
                    await core.CallDevToolsProtocolMethodAsync(
                        "Storage.clearDataForOrigin",
                        "{\"origin\":\"https://www.roblox.com\",\"storageTypes\":\"cookies,local_storage,session_storage,indexeddb\"}");
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"DevTools clearDataForOrigin roblox.com threw: {ex.Message}");
                }
                try
                {
                    await core.CallDevToolsProtocolMethodAsync(
                        "Storage.clearDataForOrigin",
                        "{\"origin\":\"https://auth.roblox.com\",\"storageTypes\":\"cookies,local_storage,session_storage,indexeddb\"}");
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"DevTools clearDataForOrigin auth.roblox.com threw: {ex.Message}");
                }

                // Re-apply GDPR Consent cookies so annoying banner never covers login
                try
                {
                    var consentCookie1 = cookieManager.CreateCookie("OptanonAlertBoxClosed", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), ".roblox.com", "/");
                    cookieManager.AddOrUpdateCookie(consentCookie1);

                    var consentCookie2 = cookieManager.CreateCookie("OptanonConsent", "isGpcEnabled=0&datestamp=" + Uri.EscapeDataString(DateTime.UtcNow.ToString()) + "&version=202401.1.0&browserGpcFlag=0&isIABGlobal=false&hosts=&consentId=shiro-fast-login&interactionCount=1&isAnonUser=1&landingPath=NotLandingPage&groups=C0001%3A1%2CC0002%3A1%2CC0003%3A1%2CC0004%3A1", ".roblox.com", "/");
                    cookieManager.AddOrUpdateCookie(consentCookie2);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"GDPR cookie create threw: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"ClearSessionAsync threw: {ex.Message}");
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

                TxtInAppStatus.Text = $"➡️ Chuyển sang {_username} ({_currentQueueIndex + 1}/{_accountQueue.Count})...";
                TxtInAppStatus.Foreground = Brushes.DeepSkyBlue;

                // Create a 100% isolated virgin WebView2 profile for the next account so no IndexedDB HBA keys,
                // LocalStorage CachedUserId, or RBXEventTrackerV2 cookies are ever shared across accounts!
                CoreWebView2? freshCore = null;
                if (WvBrowser != null)
                {
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
                if (_accountQueue.Count > 0 && IsModerationOrLockedUrl(currentUrl))
                {
                    await MarkCurrentAccountIssueAndSkipAsync("⛔ Bị khóa / Ban", "Tài khoản bị khóa hoặc kiểm duyệt");
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
                    if (_ignoredCookieValues.Contains(Fingerprint(secCookie.Value)))
                    {
                        try { core.CookieManager.DeleteCookie(secCookie); } catch (Exception) { }
                        return;
                    }

                    _captured = true;
                    _pollTimer?.Stop();
                    _ignoredCookieValues.Add(Fingerprint(secCookie.Value));
                    Program.Logger.Info($"[FastLogin] .ROBLOSECURITY detected for '{_username}' (len={secCookie.Value.Length}, url={core.Source}). Waiting for settle...");

                    TxtInAppStatus.Text = $"🎉 Đã bắt phiên {_username ?? "tài khoản"}! Đang chờ xoay Cookie & lưu...";
                    TxtInAppStatus.Foreground = Brushes.LightGreen;

                    // 1. Wait for WebView2 to redirect away from /login (e.g. to /home or /discover)
                    for (int w = 0; w < 24; w++)
                    {
                        string navUrl = core.Source ?? string.Empty;
                        if (_accountQueue.Count > 0 && IsModerationOrLockedUrl(navUrl))
                        {
                            await MarkCurrentAccountIssueAndSkipAsync("⛔ Bị khóa / Ban", "Tài khoản bị khóa hoặc kiểm duyệt");
                            return;
                        }
                        if (!string.IsNullOrEmpty(navUrl) && !navUrl.Contains("/login", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                        await Task.Delay(250);
                    }

                    // 2. Allow post-redirect /home requests and server-side cookie rotation to settle
                    await Task.Delay(1500);

                    string postRedirectUrl = core.Source ?? string.Empty;
                    if (_accountQueue.Count > 0 && IsModerationOrLockedUrl(postRedirectUrl))
                    {
                        await MarkCurrentAccountIssueAndSkipAsync("⛔ Bị khóa / Ban", "Tài khoản bị khóa hoặc kiểm duyệt");
                        return;
                    }

                    // 3. Trigger an in-page authenticated request so any edge Set-Cookie rotation completes inside WebView2
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

                    // 4. Read the final settled/rotated .ROBLOSECURITY cookie across Roblox origins
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

                    // 5. If password was manually entered or autofilled, capture it before stopping WebView2
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
                                try { unescaped = JsonSerializer.Deserialize<string>(passJson); } catch (Exception) { }
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

                    // 6. Stop WebView2 before registering in C#
                    try
                    {
                        core.Stop();
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"core.Stop() threw: {ex.Message}");
                    }

                    Account? acc = await AccountManager.AddAccountAsync(cookieValue, _password ?? "");
                    Program.Logger?.Info($"[FastLogin] Captured session for '{_username}': payloadLen={Account.GetSessionTokenPayloadLength(cookieValue)}, registeredValid={acc?.Valid}, resolvedUser={acc?.Username}.");

                    if (acc != null && acc.Valid)
                    {
                        if (!string.IsNullOrEmpty(_password))
                        {
                            acc.Password = _password;
                        }
                        if (!string.IsNullOrEmpty(_username) &&
                            !string.Equals(acc.Username, _username, StringComparison.OrdinalIgnoreCase))
                        {
                            // Stale session from previous account; reset to a fresh isolated WebView2 profile and re-login target account
                            core.NavigationCompleted -= OnNavigationCompleted;
                            core.SourceChanged -= OnSourceChanged;
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
                            AccountManager.Instance?.SetDashboardStatus($"✅ Đăng nhập In-App thành công{progressTag}: {acc.Username}!");

                        await AdvanceToNextAccountOrCloseAsync();
                    }
                    else
                    {
                        if (_accountQueue.Count > 0)
                        {
                            await MarkCurrentAccountIssueAndSkipAsync("⛔ Bị khóa / Ban", "Phiên bị khóa hoặc kiểm duyệt");
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

        private async Task TryFillCredentialsAsync(bool forceClick = false)
        {
            var core = WvBrowser?.CoreWebView2;
            if (core == null || string.IsNullOrEmpty(_username)) return;

            try
            {
                string userJson = JsonSerializer.Serialize(_username);
                string passJson = JsonSerializer.Serialize(_password ?? "");
                string hasPassJs = !string.IsNullOrEmpty(_password) ? "true" : "false";
                string forceClickJs = forceClick ? "true" : "false";

                string js = $@"
                    (() => {{
                        try {{
                            const targetUser = {userJson};
                            const targetPass = {passJson};
                            const hasPass = {hasPassJs};
                            const forceClick = {forceClickJs};

                            if (forceClick) {{
                                window.__shiroAutoSubmittedForUser = '';
                            }}

                            const setReactValue = (el, val) => {{
                                if (!el || val === undefined || val === null) return false;
                                if (el.value === val) return true;
                                try {{
                                    el.focus();
                                    if (el._valueTracker) {{
                                        el._valueTracker.setValue('');
                                    }}
                                    const proto = window.HTMLInputElement ? window.HTMLInputElement.prototype : Object.getPrototypeOf(el);
                                    const desc = Object.getOwnPropertyDescriptor(proto, 'value');
                                    if (desc && desc.set) {{
                                        desc.set.call(el, val);
                                    }} else {{
                                        el.value = val;
                                    }}
                                    try {{
                                        el.dispatchEvent(new InputEvent('input', {{ bubbles: true, cancelable: true, inputType: 'insertText', data: val }}));
                                    }} catch (_) {{
                                        el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                                    }}
                                    el.dispatchEvent(new Event('change', {{ bubbles: true }}));
                                    el.blur();
                                    return el.value === val;
                                }} catch (_) {{
                                    return false;
                                }}
                            }};

                            const clickLoginButton = () => {{
                                const btn = document.querySelector('#login-button') ||
                                    Array.from(document.querySelectorAll('button, input[type=""submit""], [role=""button""]'))
                                        .find(b => {{
                                            const t = (b.innerText || b.value || '').trim().toLowerCase();
                                            return t === 'log in' || t === 'login' || t === 'đăng nhập';
                                        }});
                                if (btn && !btn.disabled) {{
                                    btn.click();
                                    return true;
                                }}
                                return false;
                            }};

                            let attempts = 0;
                            if (window.__shiroFillInterval) {{
                                clearInterval(window.__shiroFillInterval);
                                window.__shiroFillInterval = null;
                            }}

                            const tick = () => {{
                                attempts++;
                                const u = document.querySelector('#login-username') || document.querySelector('input[name=""username""]');
                                const p = document.querySelector('#login-password') || document.querySelector('input[type=""password""]');

                                if (!u || (hasPass && !p)) {{
                                    if (attempts > 40 && window.__shiroFillInterval) {{
                                        clearInterval(window.__shiroFillInterval);
                                        window.__shiroFillInterval = null;
                                    }}
                                    return;
                                }}

                                const uOk = setReactValue(u, targetUser);
                                const pOk = !hasPass || setReactValue(p, targetPass);

                                if (uOk && pOk && hasPass) {{
                                    if (window.__shiroFillInterval) {{
                                        clearInterval(window.__shiroFillInterval);
                                        window.__shiroFillInterval = null;
                                    }}
                                    if (window.__shiroAutoSubmittedForUser !== targetUser) {{
                                        window.__shiroAutoSubmittedForUser = targetUser;
                                        setTimeout(() => {{
                                            setReactValue(u, targetUser);
                                            setReactValue(p, targetPass);
                                            clickLoginButton();
                                        }}, 280);
                                    }}
                                    return;
                                }}

                                if (attempts > 40 && window.__shiroFillInterval) {{
                                    clearInterval(window.__shiroFillInterval);
                                    window.__shiroFillInterval = null;
                                }}
                            }};

                            tick();
                            window.__shiroFillInterval = setInterval(tick, 220);
                        }} catch(_) {{}}
                    }})();
                ";
                await core.ExecuteScriptAsync(js);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Script execution in TryFillCredentialsAsync threw: {ex.Message}");
            }
        }

        private async void OnRefillClicked(object sender, RoutedEventArgs e)
        {
            await TryFillCredentialsAsync(forceClick: true);
        }

        private async void OnSkipNextClicked(object sender, RoutedEventArgs e)
        {
            await AdvanceToNextAccountOrCloseAsync();
        }

        private void OnReloadClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                WvBrowser?.CoreWebView2?.Reload();
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Reload WebView2 threw: {ex.Message}");
            }
        }

        private async void OnVerifyClicked(object sender, RoutedEventArgs e)
        {
            string cookie = Account.SanitizeCookie(TxtCookie?.Text ?? string.Empty);
            if (string.IsNullOrEmpty(cookie))
            {
                TxtPreviewStatus.Text = "Vui lòng dán Cookie trước khi kiểm tra.";
                TxtPreviewStatus.Foreground = Brushes.OrangeRed;
                return;
            }

            TxtPreviewStatus.Text = "Đang kiểm tra với Roblox API...";
            TxtPreviewStatus.Foreground = Brushes.DeepSkyBlue;
            BtnVerify.IsEnabled = false;

            try
            {
                var account = await Account.CreateAsync(cookie);
                if (account.Valid)
                {
                    _lastVerifiedCookie = cookie;
                    _lastVerifiedAccount = account;
                    TxtPreviewUsername.Text = $"{account.Username} (ID: {account.UserID})";
                    TxtPreviewStatus.Text = "✅ Cookie hợp lệ và đã được xác thực!";
                    TxtPreviewStatus.Foreground = Brushes.LightGreen;
                }
                else
                {
                    _lastVerifiedCookie = string.Empty;
                    _lastVerifiedAccount = null;
                    TxtPreviewUsername.Text = "Tài khoản không hợp lệ";
                    TxtPreviewStatus.Text = "❌ Cookie đã hết hạn hoặc không đúng.";
                    TxtPreviewStatus.Foreground = Brushes.OrangeRed;
                }
            }
            catch (Exception ex)
            {
                _lastVerifiedCookie = string.Empty;
                _lastVerifiedAccount = null;
                TxtPreviewStatus.Text = $"Lỗi: {ex.Message}";
                TxtPreviewStatus.Foreground = Brushes.OrangeRed;
            }
            finally
            {
                BtnVerify.IsEnabled = true;
            }
        }

        private async void OnAddClicked(object sender, RoutedEventArgs e)
        {
            string cookie = Account.SanitizeCookie(TxtCookie?.Text ?? string.Empty);
            if (string.IsNullOrEmpty(cookie))
            {
                TxtPreviewStatus.Text = "Vui lòng nhập Cookie hợp lệ.";
                TxtPreviewStatus.Foreground = Brushes.OrangeRed;
                return;
            }

            BtnAddCookie.IsEnabled = false;
            try
            {
                Account? newAcc;
                if (_lastVerifiedAccount != null &&
                    _lastVerifiedAccount.Valid &&
                    string.Equals(_lastVerifiedCookie, cookie, StringComparison.Ordinal))
                {
                    newAcc = AccountManager.RegisterValidatedAccount(_lastVerifiedAccount, _lastVerifiedAccount.SecurityToken, _password ?? "", saveImmediate: true);
                }
                else
                {
                    newAcc = await AccountManager.AddAccountAsync(cookie, _password ?? "");
                }

                if (newAcc != null && newAcc.Valid)
                {
                    string? alias = TxtCookieAlias?.Text?.Trim();
                    string? group = TxtCookieGroup?.Text?.Trim();
                    bool metadataUpdated = false;

                    if (!string.IsNullOrEmpty(alias))
                    {
                        newAcc.Alias = alias;
                        metadataUpdated = true;
                    }
                    if (!string.IsNullOrEmpty(group))
                    {
                        newAcc.Group = group;
                        metadataUpdated = true;
                    }

                    if (metadataUpdated)
                    {
                        try { await AccountManager.SaveAccountsAsync(true); }
                        catch (Exception ex) { Program.Logger?.Warn($"SaveAccountsAsync after fast login failed: {ex.Message}"); }
                    }

                    CreatedAccount = newAcc;
                    MainWindow.Instance?.DashboardView?.LoadDashboardData();
                    AccountManager.Instance?.SetDashboardStatus($"✅ Đã thêm tài khoản {newAcc.Username} thành công!");
                    Close(true);
                }
                else
                {
                    TxtPreviewStatus.Text = "❌ Thất bại: Cookie không hợp lệ hoặc đã hết hạn.";
                    TxtPreviewStatus.Foreground = Brushes.OrangeRed;
                }
            }
            catch (Exception ex)
            {
                TxtPreviewStatus.Text = $"Lỗi: {ex.Message}";
                TxtPreviewStatus.Foreground = Brushes.OrangeRed;
            }
            finally
            {
                BtnAddCookie.IsEnabled = true;
            }
        }

        private void OnCancelClicked(object sender, RoutedEventArgs e)
        {
            try { _pollTimer?.Stop(); } catch (Exception) { }
            Close(false);
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            try
            {
                if (_pollTimer != null)
                {
                    _pollTimer.Stop();
                    _pollTimer.Tick -= OnPollTimerTick;
                    _pollTimer = null;
                }

                var core = WvBrowser?.CoreWebView2;
                if (core != null)
                {
                    core.NavigationCompleted -= OnNavigationCompleted;
                    core.SourceChanged -= OnSourceChanged;
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"OnClosing cleanup threw: {ex.Message}");
            }
            base.OnClosing(e);
        }
    }
}
