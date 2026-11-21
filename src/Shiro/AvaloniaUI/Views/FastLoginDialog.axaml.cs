#nullable enable
using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using Shiro.AvaloniaUI.Controls;
using Shiro.Services;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;

namespace Shiro.AvaloniaUI.Views
{
    public partial class FastLoginDialog : Window
    {
        public const double InAppModeHeight = 580;
        public const double InAppModeMinHeight = 480;
        public const double CookieModeHeight = 360;
        public const double CookieModeMinHeight = 330;

        private DispatcherTimer? _pollTimer;
        private FastLoginQueue _queue = new(Array.Empty<Account>());
        private readonly HashSet<string> _ignoredCookieValues = new HashSet<string>(StringComparer.Ordinal);
        private string _username = string.Empty;
        private string _password = string.Empty;
        private string? _pendingCapturedPassword;
        private bool _captured;
        private bool _isCheckingSession;

        public CookieImportViewModel CookieViewModel { get; } = new();

        public Account? CreatedAccount { get; private set; }

        public bool IsCookieModeActive => TabCookie?.IsChecked == true;

        // Only SHA-256 fingerprints of seen cookies are kept in memory, never the raw .ROBLOSECURITY values.
        private static string Fingerprint(string value) =>
            FastLoginCoordinator.FingerprintCookie(value);

        public FastLoginDialog()
        {
            DataContext = CookieViewModel;
            CookieViewModel.OnAccountCreated = () =>
            {
                CreatedAccount = CookieViewModel.CreatedAccount;
                MainWindow.Instance?.DashboardView?.LoadDashboardData();
            };
            CookieViewModel.CloseRequested += (_, r) => Close(r);
            InitializeComponent();
            WindowBackdropService.ApplyDarkAndBackdrop(this);
            StreamerModeService.StateChanged += OnStreamerModeStateChanged;
            if (BtnSkipNext != null)
            {
                BtnSkipNext.Command = new AsyncRelayCommand(AdvanceToNextAccountOrCloseAsync);
            }
        }

        public FastLoginDialog(Account? account = null) : this(account, startInCookieMode: false)
        {
        }

        public FastLoginDialog(Account? account, bool startInCookieMode) : this()
        {
            if (account != null)
            {
                _queue = new FastLoginQueue(new[] { account });
                LoadAccountAtIndex(0);
            }
            if (startInCookieMode)
            {
                SelectLoginMode(isCookieMode: true);
            }
        }

        public FastLoginDialog(IReadOnlyList<Account> accounts) : this()
        {
            _queue = new FastLoginQueue(accounts);
            if (_queue.Count > 0)
            {
                LoadAccountAtIndex(0);
            }
        }

        private void LoadAccountAtIndex(int index)
        {
            if (!_queue.MoveTo(index) || _queue.Current == null)
                return;

            var account = _queue.Current;
            _username = account.Username;
            _password = account.Password;
            _pendingCapturedPassword = null;
            _captured = false;
            _redirectedFromUnauthenticatedModeration = false;
            _restoredHbaScriptId = null;
            _restoredModerationCookieFp = null;
            _restoredModerationAccount = null;
            _hasSeenModerationPage = false;
            _pendingCapturedFp = null;

            if (!string.IsNullOrEmpty(_username))
            {
                BtnRefill.IsVisible = true;
                if (TxtDialogTitle != null) TxtDialogTitle.Text = _queue.Title;
                if (TxtDialogSubtitle != null) TxtDialogSubtitle.Text = _queue.Subtitle;
            }

            if (BtnSkipNext != null) BtnSkipNext.IsVisible = _queue.HasNext;
            CookieViewModel.LoadFromAccount(account);
        }

        public void SelectLoginMode(bool isCookieMode)
        {
            if (TabInApp != null) TabInApp.IsChecked = !isCookieMode;
            if (TabCookie != null) TabCookie.IsChecked = isCookieMode;
            ApplyModeLayout(isCookieMode);
        }

        private void OnModeCheckedChanged(object sender, RoutedEventArgs e)
            => ApplyModeLayout(IsCookieModeActive);

        private void ApplyModeLayout(bool isCookieMode)
        {
            bool isInApp = !isCookieMode;
            if (PnlInApp != null) PnlInApp.IsVisible = isInApp;
            if (PnlCookie != null) PnlCookie.IsVisible = isCookieMode;
            if (BtnVerify != null) BtnVerify.IsVisible = isCookieMode;
            if (BtnAddCookie != null) BtnAddCookie.IsVisible = isCookieMode;
            if (TxtTip != null) TxtTip.IsVisible = isInApp;

            MinHeight = isCookieMode ? CookieModeMinHeight : InAppModeMinHeight;
            Height = isCookieMode ? CookieModeHeight : InAppModeHeight;

            if (isInApp && WvBrowser != null)
            {
                Dispatcher.UIThread.Post(() => WvBrowser.UpdateBounds(), DispatcherPriority.Render);
            }
        }

        private void OnWebViewInitialized(object sender, WebView2InitializedEventArgs e) => _ = HandleWebViewInitializedAsync(e);

        private void StartPollTimer()
        {
            if (_isClosed) return;
            if (_pollTimer != null)
            {
                _pollTimer.Stop();
                _pollTimer.Tick -= OnPollTimerTick;
            }
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _pollTimer.Tick += OnPollTimerTick;
            _pollTimer.Start();
        }

        private async Task HandleWebViewInitializedAsync(WebView2InitializedEventArgs e)
        {
            if (_isClosed) return;
            if (!e.IsSuccess)
            {
                TxtInAppStatus.Text = Shiro.Services.LocalizationService.Format("FastLogin.WebViewInitFailed", e.Error?.Message);
                TxtInAppStatus.Foreground = Brushes.OrangeRed;
                return;
            }

            TxtInAppStatus.Text = Shiro.Services.LocalizationService.Get("FastLogin.LoadingSecureLoginPage");
            TxtInAppStatus.Foreground = Brushes.DeepSkyBlue;

            var core = WvBrowser?.CoreWebView2;
            if (core == null) return;

            try
            {
                await InitializeCoreSessionAsync(
                    () => ConfigureCoreWebViewAsync(core),
                    acc => RobloxHbaService.RestoreWebViewModerationSessionAsync(core, acc, _currentHbaSnapshot),
                    url => core.Navigate(url));
            }
            catch (Exception ex)
            {
                if (!_isClosed)
                {
                    TxtInAppStatus.Text = Shiro.Services.LocalizationService.Format("Common.ErrorFormat", ex.Message);
                    TxtInAppStatus.Foreground = Brushes.OrangeRed;
                }
            }
        }

        internal async Task InitializeCoreSessionAsync(
            Func<Task> configureCoreAsync,
            Func<Account, Task<string?>> restoreModerationSessionAsync,
            Action<string> navigate)
        {
            if (_isClosed) return;
            await configureCoreAsync();
            if (_isClosed) return;

            var singleAcc = _queue.Count == 1 ? _queue.Current : null;
            if (singleAcc != null && singleAcc.IsBannedOrLocked && !string.IsNullOrWhiteSpace(singleAcc.SecurityToken))
            {
                _restoredModerationAccount = singleAcc;
                _restoredModerationCookieFp = Fingerprint(singleAcc.SecurityToken);
                _hasSeenModerationPage = false;
                _restoredHbaScriptId = await restoreModerationSessionAsync(singleAcc);
                if (_isClosed) return;
                _ignoredCookieValues.Add(_restoredModerationCookieFp);
                TxtInAppStatus.Text = Shiro.Services.LocalizationService.Format("FastLogin.OpeningModerationPage", StreamerModeService.GetDisplayUsername(singleAcc.Username), singleAcc.AccountStatusIssue);
                navigate("https://www.roblox.com/home");
            }
            else
            {
                navigate("https://www.roblox.com/login");
            }

            StartPollTimer();
        }

        private int _isPollTickBusy;

        private async Task ShowLoginFillStatusAndFillAsync()
        {
            string shownUser = StreamerModeService.GetDisplayUsername(_username);
            TxtInAppStatus.Text = !string.IsNullOrEmpty(_password)
                ? Shiro.Services.LocalizationService.Format("FastLogin.AutoFillingAndLoggingIn", shownUser)
                : Shiro.Services.LocalizationService.Format("FastLogin.AutoFillingUsername", shownUser);
            TxtInAppStatus.Foreground = Brushes.LightGreen;
            await TryFillCredentialsAsync(forceClick: false);
        }

        private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e) => _ = HandleSourceChangedAsync();

        private async Task HandleSourceChangedAsync()
        {
            if (_isClosed) return;
            try
            {
                string currentUrl = WvBrowser?.CoreWebView2?.Source ?? string.Empty;
                if (!string.IsNullOrEmpty(_username) && currentUrl.Contains("/login", StringComparison.OrdinalIgnoreCase))
                    await ShowLoginFillStatusAndFillAsync();

                if (!_isClosed)
                    await CheckAndCaptureSessionAsync();
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[FastLoginDialog.OnSourceChanged] {ex.Message}");
            }
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => _ = HandleNavigationCompletedAsync(e);

        private async Task HandleNavigationCompletedAsync(CoreWebView2NavigationCompletedEventArgs e)
        {
            _isDocumentNavigating = false;
            if (_isClosed) return;
            try
            {
                if (!e.IsSuccess)
                {
                    if (e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted ||
                        e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
                    {
                        return;
                    }

                    var core = WvBrowser?.CoreWebView2;
                    AppServices.Logger?.Warn($"[FastLoginDialog] Navigation failed: url={WebAuthPolicy.RedactSensitiveQuery(core?.Source ?? string.Empty)}, http={e.HttpStatusCode}, webError={e.WebErrorStatus}");
                    var action = await FastLoginCoordinator.ClassifyNavigationFailureAsync(core, e.HttpStatusCode, _restoredModerationAccount != null, _redirectedFromUnauthenticatedModeration);
                    if (_isClosed) return;
                    if (action == WebViewNavigationFailureAction.FallbackModerationToLogin && core != null)
                    {
                        await RedirectUnauthenticatedModerationToLoginAsync(core);
                        return;
                    }

                    TxtInAppStatus.Text = action == WebViewNavigationFailureAction.ShowHttpError
                        ? Shiro.Services.LocalizationService.Format("FastLogin.HttpErrorLoadFailed", e.HttpStatusCode)
                        : Shiro.Services.LocalizationService.Get("FastLogin.CannotLoadLoginPage");
                    TxtInAppStatus.Foreground = Brushes.OrangeRed;
                    return;
                }

                string currentUrl = WvBrowser?.CoreWebView2?.Source ?? string.Empty;
                bool isLoginPage = currentUrl.Contains("/login", StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrEmpty(_username) && isLoginPage)
                {
                    await ShowLoginFillStatusAndFillAsync();
                }
                else if (isLoginPage)
                {
                    TxtInAppStatus.Text = Shiro.Services.LocalizationService.Get("FastLogin.LoginPageReady");
                    TxtInAppStatus.Foreground = Brushes.LightGreen;
                }

                if (!_isClosed)
                    await CheckAndCaptureSessionAsync();
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[FastLoginDialog.OnNavigationCompleted] {ex.Message}");
            }
        }

        private void OnPollTimerTick(object? sender, EventArgs e) => _ = HandlePollTimerTickAsync();

        private async Task HandlePollTimerTickAsync()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _isPollTickBusy, 1, 0) != 0)
                return;

            try
            {
                await CheckAndCaptureSessionAsync();
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[FastLoginDialog.OnPollTimerTick] {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _isPollTickBusy, 0);
            }
        }

        private void OnRefillClicked(object sender, RoutedEventArgs e) => _ = HandleRefillClickedAsync();

        private async Task HandleRefillClickedAsync()
        {
            try
            {
                await TryFillCredentialsAsync(forceClick: true);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[FastLoginDialog.OnRefillClicked] {ex.Message}");
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
                AppServices.Logger?.Debug($"Reload WebView2 threw: {ex.Message}");
            }
        }

        private void OnCancelClicked(object sender, RoutedEventArgs e)
        {
            SafeExecuteHelper.Try(() => _pollTimer?.Stop());
            Close(false);
        }

        private bool _isClosed;

        private void OnStreamerModeStateChanged(bool _) => AppDispatcher.Post(() =>
        {
            if (!_isClosed && !string.IsNullOrEmpty(_username) && TxtDialogTitle != null)
                TxtDialogTitle.Text = _queue.Title;
        });

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            _isClosed = true;
            StreamerModeService.StateChanged -= OnStreamerModeStateChanged;
            CookieViewModel.OnClosing();
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
                    core.NavigationStarting -= OnNavigationStarting;
                    core.NavigationCompleted -= OnNavigationCompleted;
                    core.SourceChanged -= OnSourceChanged;
                    core.WebResourceRequested -= OnWebResourceRequested;
                    core.WebMessageReceived -= OnWebMessageReceived;
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"OnClosing cleanup threw: {ex.Message}");
            }
            base.OnClosing(e);
        }
    }
}
