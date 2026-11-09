#nullable enable
using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Shiro.Classes;
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
            WindowBackdropService.ApplyDarkAndBackdrop(this);

            if (AppServices.HostUiBridge is HostUiBridgeService bridge)
            {
                bridge.RefreshAccountsOnUiHandler = RefreshAccounts;
                bridge.NotifyAccountValidityOnUiHandler = UpdateAccountValidity;
                bridge.RefreshLiveProcessStatusOnUiHandler = RefreshLiveProcessStatus;
                bridge.UpdateWindowStatusOnUiHandler = SetStatus;
                bridge.NotifyRobloxProcessesClosedOnUiHandler = OnRobloxProcessesClosed;
                bridge.GetDashboardSelectedAccountsHandler = () => ViewDashboard?.ViewModel?.GetSelectedAccounts();
                bridge.GetDashboardPlaceIdHandler = () => ViewDashboard?.ViewModel?.PlaceId;
                bridge.GetDashboardJobIdHandler = () => ViewDashboard?.ViewModel?.JobId;
            }

            if (ViewDashboard?.ViewModel != null)
            {
                DataContext = ViewDashboard.ViewModel;
                ViewDashboard.ViewModel.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.IsCommandPaletteOpen) &&
                        ViewDashboard.ViewModel.IsCommandPaletteOpen)
                    {
                        FocusCommandPaletteInput();
                    }
                };
            }

            if (TxtCommandPalette != null)
            {
                TxtCommandPalette.AddHandler(KeyDownEvent, OnCommandPaletteKeyDown, RoutingStrategies.Tunnel);
            }

            Loaded += (s, e) =>
            {
                try
                {
                    var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
                    if (screen != null)
                    {
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

                        Position = new Avalonia.PixelPoint(
                            Math.Max(workArea.X, centerX),
                            Math.Max(workArea.Y, centerY)
                        );
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Center window position calculation threw: {ex.Message}");
                }
            };

            PropertyChanged += (s, e) =>
            {
                if (e.Property == WindowStateProperty)
                {
                    UpdateMaximizeIcon();
                }
            };

            if (ViewServerList != null && ViewDashboard?.ViewModel?.ServerListViewModel != null)
            {
                ViewServerList.DataContext = ViewDashboard.ViewModel.ServerListViewModel;
            }

            RefreshAccounts(AppServices.AccountRepository.GetAll());

            Opened += async (s, e) =>
            {
                try
                {
                    if (AppSettings.General != null && !AppSettings.General.Get<bool>(AppSettingsKeys.General.HasAcceptedRiskDisclaimer))
                    {
                        string title = Loc.Get("tos_disclaimer_title");
                        string msg = Loc.Get("tos_disclaimer_message");
                        if (string.IsNullOrWhiteSpace(title) || title == "tos_disclaimer_title")
                            title = Shiro.Services.LocalizationService.Get("tos_disclaimer_title");
                        if (string.IsNullOrWhiteSpace(msg) || msg == "tos_disclaimer_message")
                            msg = Shiro.Services.LocalizationService.Get("tos_disclaimer_message");

                        await MessageBoxDialog.ShowAsync(msg, title, MessageBoxButtons.OK, MessageBoxIcon.Information, this);
                        AppSettings.General.Set(AppSettingsKeys.General.HasAcceptedRiskDisclaimer, "true");
                        AppSettings.Save();
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Disclaimer dialog error: {ex.Message}");
                }
            };

            if (AppServices.AccountRepository.IsPasswordEncrypted())
            {
                Opened += async (s, e) =>
                {
                    try
                    {
                        bool vaultKeyNewerThanData = false;
                        try
                        {
                            if (System.IO.File.Exists(StoragePaths.VaultKeyFile) && System.IO.File.Exists(StoragePaths.AccountDataFile))
                            {
                                vaultKeyNewerThanData = System.IO.File.GetLastWriteTimeUtc(StoragePaths.VaultKeyFile) >
                                                        System.IO.File.GetLastWriteTimeUtc(StoragePaths.AccountDataFile).AddSeconds(2);
                            }
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"VaultKey vs AccountData file time check threw: {ex.Message}");
                        }

                        if (vaultKeyNewerThanData)
                        {
                            var recoverRes = await MessageBoxDialog.ShowAsync(
                                Shiro.Services.LocalizationService.Get("MainWindow.VaultKeyChangedPrompt"),
                                Shiro.Services.LocalizationService.Get("MainWindow.RecoverShiroDataTitle"),
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question,
                                this);

                            if (recoverRes == MessageBoxResult.Yes)
                            {
                                if (AppServices.AccountRepository.TryRecoverOrRestore(out string statusMsg))
                                {
                                    RefreshAccounts(AppServices.AccountRepository.GetAll());
                                    ToastService.ShowSuccess(Shiro.Services.LocalizationService.Get("MainWindow.RecoverySuccessTitle"), statusMsg);
                                    return;
                                }
                            }
                        }

                        string pass = await InputDialog.PromptAsync(
                            Shiro.Services.LocalizationService.Get("MainWindow.EnterMasterPasswordPrompt"),
                            Shiro.Services.LocalizationService.Get("MainWindow.UnlockShiroTitle"),
                            "",
                            false,
                            this);

                        if (!string.IsNullOrWhiteSpace(pass) && pass != "/UC")
                        {
                            byte[] hash = System.Security.Cryptography.SHA512.HashData(System.Text.Encoding.UTF8.GetBytes(pass));
                            if (AppServices.AccountRepository.LoadFromStore(hash))
                            {
                                RefreshAccounts(AppServices.AccountRepository.GetAll());
                                ToastService.ShowSuccess(Shiro.Services.LocalizationService.Get("MainWindow.UnlockedTitle"), Shiro.Services.LocalizationService.Format("MainWindow.AccountsLoadedCount", AppServices.AccountRepository.Count));
                                return;
                            }

                            ToastService.ShowWarning(Shiro.Services.LocalizationService.Get("MainWindow.WrongPasswordTitle"), Shiro.Services.LocalizationService.Get("MainWindow.IncorrectMasterPassword"));
                        }

                        var fallbackRes = await MessageBoxDialog.ShowAsync(
                            Shiro.Services.LocalizationService.Get("MainWindow.CannotDecryptAccountDataPrompt"),
                            Shiro.Services.LocalizationService.Get("MainWindow.RecoverDataTitle"),
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question,
                            this);

                        if (fallbackRes == MessageBoxResult.Yes && AppServices.AccountRepository.TryRecoverOrRestore(out string fallbackMsg))
                        {
                            RefreshAccounts(AppServices.AccountRepository.GetAll());
                            ToastService.ShowSuccess(Shiro.Services.LocalizationService.Get("MainWindow.RecoverySuccessTitle"), fallbackMsg);
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Error($"[MainWindow] Unlock flow error: {ex.Message}");
                    }
                };
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            ViewDashboard?.ViewModel?.Dispose();

            if (AppServices.HostUiBridge is HostUiBridgeService bridge)
            {
                bridge.RefreshAccountsOnUiHandler = null;
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

            base.OnClosed(e);
        }

        public void SetStatus(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            AppDispatcher.Post(() =>
            {
                if (ViewDashboard?.ViewModel != null)
                {
                    ViewDashboard.ViewModel.StatusMessage = message;
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
                ViewDashboard?.ViewModel?.UpdateAccountValidity(account, isValid);
            });
        }

        public void RefreshLiveProcessStatus()
        {
            AppDispatcher.Post(() =>
            {
                ViewDashboard?.ViewModel?.RefreshLiveProcessStatus();
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
                        mainVm.StatusMessage = totalKilled > 0
                            ? Shiro.Services.LocalizationService.Format("AccountManager.KilledRobloxProcessesExclamation", totalKilled)
                            : Shiro.Services.LocalizationService.Get("AccountManager.NoRobloxProcessesRunning");
                    }
                    foreach (var acc in mainVm.Accounts) acc.IsInGame = false;
                    mainVm.NotifyCounts();
                }
            });
        }

        public void OpenFastLoginDialog(Account? account = null)
            => AppServices.Dialogs.OpenFastLoginDialog(account);

        public void OpenBulkImportDialog()
            => AppServices.Dialogs.OpenBulkImportDialog();

        public void OpenProxyManagerDialog()
            => AppServices.Dialogs.OpenProxyManagerDialog();

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
            if (IconMaximize != null)
            {
                IconMaximize.Icon = WindowState == WindowState.Maximized
                    ? LucideAvalonia.Enum.LucideIconNames.Copy
                    : LucideAvalonia.Enum.LucideIconNames.Square;
            }
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
