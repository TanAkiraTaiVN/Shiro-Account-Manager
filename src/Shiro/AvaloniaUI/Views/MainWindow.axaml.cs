#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Shiro.Services;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views
{
    public partial class MainWindow : Window
    {
        public static MainWindow? Instance { get; private set; }

        public MainViewModel? ViewModel => ViewDashboard?.ViewModel;
        public DashboardView? DashboardView => ViewDashboard;

        public MainWindow()
        {
            Instance = this;
            InitializeComponent();
            InitializeNavigationChrome();
            InitializeChrome2026();
            UpdateMaximizeIcon();
            WindowBackdropService.ApplyDarkAndBackdrop(this, cloakUntilFirstFrame: true);

            if (AppServices.HostUiBridge is HostUiBridgeService bridge)
            {
                bridge.RefreshAccountsOnUiHandler = RefreshAccounts;
                bridge.RefreshSingleAccountOnUiHandler = acc => ViewDashboard?.ViewModel?.AccountsVm.RefreshAccount(acc);
                bridge.NotifyAccountValidityOnUiHandler = UpdateAccountValidity;
                bridge.RefreshLiveProcessStatusOnUiHandler = RefreshLiveProcessStatus;
                bridge.UpdateWindowStatusOnUiHandler = SetStatus;
                bridge.NotifyRobloxProcessesClosedOnUiHandler = OnRobloxProcessesClosed;
                bridge.GetDashboardSelectedAccountsHandler = () => ViewDashboard?.ViewModel?.AccountsVm.GetSelectedAccounts();
                bridge.GetDashboardPlaceIdHandler = () => ViewDashboard?.ViewModel?.LaunchBar.PlaceId;
                bridge.GetDashboardJobIdHandler = () => ViewDashboard?.ViewModel?.LaunchBar.JobId;
            }

            if (ViewDashboard?.ViewModel != null)
            {
                if (ViewServerList != null && ViewDashboard.ViewModel.ServerListViewModel != null)
                {
                    ViewServerList.DataContext = ViewDashboard.ViewModel.ServerListViewModel;
                }

                DataContext = ViewDashboard.ViewModel;
                ViewDashboard.ViewModel.CommandPalette.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(CommandPaletteViewModel.IsCommandPaletteOpen) &&
                        ViewDashboard.ViewModel.CommandPalette.IsCommandPaletteOpen)
                    {
                        FocusCommandPaletteInput();
                    }
                };
                ViewDashboard.ViewModel.OnPageNavigated = ApplyNavigationPage;
                ApplyNavigationPage(ViewDashboard.ViewModel.CurrentPage);
            }

            if (ViewCommandPalette?.SearchTextBox is { } cmdSearchBox)
            {
                cmdSearchBox.AddHandler(KeyDownEvent, OnCommandPaletteKeyDown, RoutingStrategies.Tunnel);
            }

            bool boundsApplied = TryApplyInitialWindowBounds();
            Loaded += (_, _) =>
            {
                if (!boundsApplied)
                    boundsApplied = TryApplyInitialWindowBounds();
            };

            PropertyChanged += (s, e) =>
            {
                if (e.Property == WindowStateProperty)
                {
                    UpdateMaximizeIcon();
                }
            };

            RefreshAccounts(AppServices.AccountRepository.GetAll());

            Opened += OnMainWindowOpened;
        }

        internal const int MaxMasterPasswordAttempts = 3;

        private void OnMainWindowOpened(object? sender, EventArgs e)
            => _ = RunObservedStartupPromptsAsync();

        private async Task RunObservedStartupPromptsAsync()
        {
            try
            {
                await RunStartupPromptsSequentiallyAsync(
                    RunRiskDisclaimerPromptAsync,
                    RunVaultUnlockPromptAsync,
                    RunPendingBackupRestorePromptAsync);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Error($"[MainWindow] Startup prompts error: {ex.Message}", ex);
            }
        }

        private async Task RunRiskDisclaimerPromptAsync()
        {
            try
            {
                if (AppServices.GeneralSettings != null && !AppServices.GeneralSettings.Get<bool>(AppSettingsKeys.General.HasAcceptedRiskDisclaimer))
                {
                    string title = LocalizationService.Get("tos_disclaimer_title");
                    string msg = LocalizationService.Get("tos_disclaimer_message");

                    await MessageBoxDialog.ShowAsync(msg, title, MessageBoxButtons.OK, MessageBoxIcon.Information, this);
                    AppServices.GeneralSettings.Set(AppSettingsKeys.General.HasAcceptedRiskDisclaimer, "true");
                    AppServices.SaveSettings();
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Disclaimer dialog error: {ex.Message}");
            }
        }

        private async Task RunVaultUnlockPromptAsync()
        {
            if (!AppServices.AccountRepository.IsPasswordEncrypted()) return;
            try
            {
                bool vaultKeyNewerThanData = false;
                try
                {
                    if (System.IO.File.Exists(StoragePaths.VaultKeyFile) && System.IO.File.Exists(StoragePaths.AccountDataFile))
                        vaultKeyNewerThanData = System.IO.File.GetLastWriteTimeUtc(StoragePaths.VaultKeyFile) > System.IO.File.GetLastWriteTimeUtc(StoragePaths.AccountDataFile).AddSeconds(2);
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"VaultKey vs AccountData file time check threw: {ex.Message}");
                }

                if (vaultKeyNewerThanData)
                {
                    var recoverRes = await MessageBoxDialog.ShowAsync(
                        LocalizationService.Get("MainWindow.VaultKeyChangedPrompt"),
                        LocalizationService.Get("MainWindow.RecoverShiroDataTitle"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question,
                        this);

                    if (recoverRes == MessageBoxResult.Yes && AppServices.AccountRepository.TryRecoverOrRestore(out string statusMsg))
                    {
                        RefreshAccounts(AppServices.AccountRepository.GetAll());
                        ToastService.ShowSuccess(LocalizationService.Get("MainWindow.RecoverySuccessTitle"), statusMsg);
                        return;
                    }
                }

                bool unlocked = await TryPromptAndUnlockMasterPasswordAsync(
                    () => InputDialog.PromptAsync(
                        LocalizationService.Get("MainWindow.EnterMasterPasswordPrompt"),
                        LocalizationService.Get("MainWindow.UnlockShiroTitle"),
                        "",
                        false,
                        this,
                        isSecret: true),
                    hash => AppServices.AccountRepository.LoadFromStore(hash),
                    (title, message) => ToastService.ShowWarning(title, message));

                if (unlocked)
                {
                    RefreshAccounts(AppServices.AccountRepository.GetAll());
                    ToastService.ShowSuccess(
                        LocalizationService.Get("MainWindow.UnlockedTitle"),
                        LocalizationService.Format("MainWindow.AccountsLoadedCount", AppServices.AccountRepository.Count));
                    return;
                }

                var fallbackRes = await MessageBoxDialog.ShowAsync(
                    LocalizationService.Get("MainWindow.CannotDecryptAccountDataPrompt"),
                    LocalizationService.Get("MainWindow.RecoverDataTitle"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    this);

                if (fallbackRes == MessageBoxResult.Yes && AppServices.AccountRepository.TryRecoverOrRestore(out string fallbackMsg))
                {
                    RefreshAccounts(AppServices.AccountRepository.GetAll());
                    ToastService.ShowSuccess(LocalizationService.Get("MainWindow.RecoverySuccessTitle"), fallbackMsg);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Error($"[MainWindow] Unlock flow error: {ex.Message}");
            }
        }

        private async Task RunPendingBackupRestorePromptAsync()
        {
            try
            {
                if (await AppServices.AccountRepository.ResolvePendingBackupRestoreAsync())
                {
                    RefreshAccounts(AppServices.AccountRepository.GetAll());
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Pending backup restore error: {ex.Message}");
            }
        }

        internal static async Task<bool> TryPromptAndUnlockMasterPasswordAsync(
            Func<Task<string>> promptPasswordAsync,
            Func<byte[], bool> loadFromStore,
            Action<string, string> showWrongPasswordToast,
            Action<byte[], byte[]>? onAttemptBuffersZeroed = null)
        {
            for (int attempt = 1; attempt <= MaxMasterPasswordAttempts; attempt++)
            {
                string pass = await promptPasswordAsync();
                if (string.IsNullOrWhiteSpace(pass) || pass == "/UC")
                {
                    return false;
                }

                if (TryUnlockWithPasswordCore(pass, loadFromStore, onAttemptBuffersZeroed))
                {
                    return true;
                }

                showWrongPasswordToast(
                    LocalizationService.Get("MainWindow.WrongPasswordTitle"),
                    LocalizationService.Get("MainWindow.IncorrectMasterPassword"));
            }

            return false;
        }

        internal static bool TryUnlockWithPasswordCore(
            string password,
            Func<byte[], bool> loadFromStore,
            Action<byte[], byte[]>? onBuffersZeroed = null)
        {
            byte[]? passBytes = null;
            byte[]? hash = null;
            try
            {
                passBytes = Encoding.UTF8.GetBytes(password);
                hash = SHA512.HashData(passBytes);
                return loadFromStore(hash);
            }
            finally
            {
                if (passBytes != null)
                    CryptographicOperations.ZeroMemory(passBytes);
                if (hash != null)
                    CryptographicOperations.ZeroMemory(hash);
                if (onBuffersZeroed != null && passBytes != null && hash != null)
                    onBuffersZeroed(passBytes, hash);
            }
        }

        internal static async System.Threading.Tasks.Task RunStartupPromptsSequentiallyAsync(
            Func<System.Threading.Tasks.Task> runDisclaimer,
            Func<System.Threading.Tasks.Task> runUnlock,
            Func<System.Threading.Tasks.Task>? runPendingBackupRestore = null)
        {
            if (runDisclaimer != null)
                await runDisclaimer();
            if (runPendingBackupRestore != null)
                await runPendingBackupRestore();
            if (runUnlock != null)
                await runUnlock();
        }

        private volatile bool _isClosed;

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            if (ViewDashboard?.ViewModel != null)
            {
                ViewDashboard.ViewModel.OnPageNavigated = null;
                ViewDashboard.ViewModel.Dispose();
            }

            if (AppServices.HostUiBridge is HostUiBridgeService bridge)
            {
                bridge.RefreshAccountsOnUiHandler = null;
                bridge.RefreshSingleAccountOnUiHandler = null;
                bridge.NotifyAccountValidityOnUiHandler = null;
                bridge.RefreshLiveProcessStatusOnUiHandler = null;
                bridge.UpdateWindowStatusOnUiHandler = null;
                bridge.NotifyRobloxProcessesClosedOnUiHandler = null;
                bridge.GetDashboardSelectedAccountsHandler = null;
                bridge.GetDashboardPlaceIdHandler = null;
                bridge.GetDashboardJobIdHandler = null;
            }

            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }

            CleanupNavigationTransitions();
            CleanupChrome2026();
            ViewOverlays?.DetachStaticBindings();
            base.OnClosed(e);
        }

        public void SetStatus(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            AppDispatcher.Post(() =>
            {
                if (ViewDashboard?.ViewModel != null)
                {
                    ViewDashboard.ViewModel.ToolsStatus.StatusMessage = message;
                }
            });
        }

        public void RefreshAccounts(IReadOnlyList<Account> accounts)
        {
            AppDispatcher.Post(() =>
            {
                ViewDashboard?.LoadDashboardData(accounts);
            });
        }

        public void UpdateAccountValidity(Account account, bool isValid)
        {
            AppDispatcher.Post(() =>
            {
                ViewDashboard?.ViewModel?.AccountsVm.UpdateAccountValidity(account, isValid);
            });
        }

        public void RefreshLiveProcessStatus()
        {
            AppDispatcher.Post(() =>
            {
                ViewDashboard?.ViewModel?.ToolsStatus.RefreshLiveProcessStatus();
            });
        }

        private void OnRobloxProcessesClosed(int totalKilled, bool notify)
        {
            AppDispatcher.Post(() =>
            {
                if (ViewDashboard?.ViewModel is var mainVm && mainVm != null)
                {
                    if (notify)
                    {
                        mainVm.ToolsStatus.StatusMessage = totalKilled > 0
                            ? Shiro.Services.LocalizationService.Format("AccountManager.KilledRobloxProcessesExclamation", totalKilled)
                            : Shiro.Services.LocalizationService.Get("AccountManager.NoRobloxProcessesRunning");
                    }
                    foreach (var acc in mainVm.AccountsVm.Accounts) acc.IsInGame = false;
                    mainVm.AccountsVm.NotifyCounts();
                }
            });
        }

        public void OpenFastLoginDialog(Account? account = null)
            => AppServices.Dialogs.OpenFastLoginDialog(account);

        public void OpenBulkImportDialog()
            => AppServices.Dialogs.OpenBulkImportDialog();

        public void OpenProxyManagerDialog()
            => AppServices.Dialogs.OpenProxyManagerDialog();

        private bool TryApplyInitialWindowBounds()
        {
            try
            {
                var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
                if (screen == null)
                    return false;

                double scaling = screen.Scaling;
                if (scaling <= 0) scaling = 1.0;

                var workArea = screen.WorkingArea;
                double workAreaDipWidth = workArea.Width / scaling;
                double workAreaDipHeight = workArea.Height / scaling;

                double maxDipWidth = workAreaDipWidth * 0.94;
                double maxDipHeight = workAreaDipHeight * 0.88;

                double currentWidth = (!double.IsNaN(Width) && Width > 0) ? Width : 1260;
                double currentHeight = (!double.IsNaN(Height) && Height > 0) ? Height : 680;

                if (currentWidth > maxDipWidth) currentWidth = Math.Max(MinWidth, maxDipWidth);
                if (currentHeight > maxDipHeight) currentHeight = Math.Max(MinHeight, maxDipHeight);

                Width = currentWidth;
                Height = currentHeight;

                int pixelWidth = (int)Math.Round(currentWidth * scaling);
                int pixelHeight = (int)Math.Round(currentHeight * scaling);

                int centerX = workArea.X + (workArea.Width - pixelWidth) / 2;
                int centerY = workArea.Y + (workArea.Height - pixelHeight) / 2;

                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new Avalonia.PixelPoint(
                    Math.Max(workArea.X, centerX),
                    Math.Max(workArea.Y, centerY)
                );
                return true;
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Center window position calculation threw: {ex.Message}");
                return false;
            }
        }

        private void OnMinimizeClicked(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void OnMaximizeClicked(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            UpdateMaximizeIcon();
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e)
            => Close();

        private void UpdateMaximizeIcon()
        {
            bool isMaximized = WindowState == WindowState.Maximized;
            if (IconMaximize != null)
                IconMaximize.IsVisible = !isMaximized;
            if (IconRestore != null)
                IconRestore.IsVisible = isMaximized;
        }

        private void OnTitleBarPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount == 2)
                {
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    UpdateMaximizeIcon();
                }
                else
                {
                    BeginMoveDrag(e);
                }
            }
        }
    }
}
