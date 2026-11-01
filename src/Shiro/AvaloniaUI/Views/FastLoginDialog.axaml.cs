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
using Shiro.AvaloniaUI.Controls;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
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

        private async void OnWebViewInitialized(object sender, WebView2InitializedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                TxtInAppStatus.Text = $"Không thể khởi tạo WebView2: {e.Error?.Message}";
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
                TxtInAppStatus.Text = $"Lỗi: {ex.Message}";
                TxtInAppStatus.Foreground = Brushes.OrangeRed;
            }
        }

        private int _isPollTickBusy;

        private async void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
        {
            try
            {
                await CheckAndCaptureSessionAsync();
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[FastLoginDialog.OnSourceChanged] {ex.Message}");
            }
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                if (!e.IsSuccess)
                {
                    if (e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted ||
                        e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
                    {
                        return;
                    }

                    TxtInAppStatus.Text = "Không thể tải trang đăng nhập. Hãy kiểm tra kết nối Proton VPN và bấm 'Tải lại'.";
                    TxtInAppStatus.Foreground = Brushes.OrangeRed;
                    return;
                }

                string currentUrl = WvBrowser?.CoreWebView2?.Source ?? string.Empty;
                bool isLoginPage = currentUrl.Contains("/login", StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrEmpty(_username) && isLoginPage)
                {
                    TxtInAppStatus.Text = !string.IsNullOrEmpty(_password)
                        ? $"Đang tự động điền và đăng nhập {_username}..."
                        : $"Đang tự động điền {_username}...";
                    TxtInAppStatus.Foreground = Brushes.LightGreen;
                    await TryFillCredentialsAsync(forceClick: false);
                }
                else if (isLoginPage)
                {
                    TxtInAppStatus.Text = "Trang đăng nhập đã sẵn sàng. Hãy đăng nhập tài khoản của bạn.";
                    TxtInAppStatus.Foreground = Brushes.LightGreen;
                }

                await CheckAndCaptureSessionAsync();
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[FastLoginDialog.OnNavigationCompleted] {ex.Message}");
            }
        }

        private async void OnPollTimerTick(object? sender, EventArgs e)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _isPollTickBusy, 1, 0) != 0)
                return;

            try
            {
                await CheckAndCaptureSessionAsync();
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[FastLoginDialog.OnPollTimerTick] {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _isPollTickBusy, 0);
            }
        }

        private async void OnRefillClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                await TryFillCredentialsAsync(forceClick: true);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[FastLoginDialog.OnRefillClicked] {ex.Message}");
            }
        }

        private async void OnSkipNextClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                await AdvanceToNextAccountOrCloseAsync();
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[FastLoginDialog.OnSkipNextClicked] {ex.Message}");
            }
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
                    TxtPreviewStatus.Text = "Cookie hợp lệ và đã được xác thực!";
                    TxtPreviewStatus.Foreground = Brushes.LightGreen;
                }
                else
                {
                    _lastVerifiedCookie = string.Empty;
                    _lastVerifiedAccount = null;
                    TxtPreviewUsername.Text = "Tài khoản không hợp lệ";
                    TxtPreviewStatus.Text = "Cookie đã hết hạn hoặc không đúng.";
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
                    AccountManager.Instance?.SetDashboardStatus($"Đã thêm tài khoản {newAcc.Username} thành công!");
                    Close(true);
                }
                else
                {
                    TxtPreviewStatus.Text = "Thất bại: Cookie không hợp lệ hoặc đã hết hạn.";
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
            DisposeInteractiveLoginScope();
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
