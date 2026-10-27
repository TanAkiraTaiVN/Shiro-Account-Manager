using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;
using RBX_Alt_Manager.ViewModels;

namespace RBX_Alt_Manager.AvaloniaUI.Views
{
    public partial class MainWindow : Window
    {
        public static MainWindow Instance { get; private set; }

        public MainViewModel ViewModel => ViewDashboard?.ViewModel;
        public DashboardView DashboardView => ViewDashboard;

        public MainWindow()
        {
            Instance = this;
            InitializeComponent();
            WindowBackdropService.ApplyDarkAndBackdrop(this);

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

                        // Calculate physical pixel position to center accurately within the monitor's usable work area
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

            RefreshAccounts(AccountManager.GetAccountsSnapshot());

            if (AccountManager.IsDatabasePasswordEncrypted())
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
                                "Phát hiện tệp khóa bảo mật (vault.key) vừa bị thay đổi sau khi lưu dữ liệu tài khoản.\n\nBạn có muốn Shiro tự động quét khôi phục khóa vault.key gốc (hoặc bản sao lưu hợp lệ) để lấy lại danh sách tài khoản không?\n(Chọn 'Không' nếu bạn đã đặt mật khẩu Master Password thủ công)",
                                "Khôi phục dữ liệu Shiro",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question,
                                this);

                            if (recoverRes == MessageBoxResult.Yes)
                            {
                                if (AccountManager.TryRecoverOrRestoreAccounts(out string statusMsg))
                                {
                                    RefreshAccounts(AccountManager.GetAccountsSnapshot());
                                    ToastService.ShowSuccess("Khôi phục thành công", statusMsg);
                                    return;
                                }
                            }
                        }

                        string pass = await InputDialog.PromptAsync(
                            "Nhập mật khẩu Master Password để mở khóa dữ liệu tài khoản (hoặc bấm Hủy để khôi phục tự động):",
                            "Mở khóa Shiro",
                            "",
                            false,
                            this);

                        if (!string.IsNullOrWhiteSpace(pass) && pass != "/UC")
                        {
                            byte[] hash = System.Security.Cryptography.SHA512.HashData(System.Text.Encoding.UTF8.GetBytes(pass));
                            if (AccountManager.LoadAccountsFromStore(hash))
                            {
                                RefreshAccounts(AccountManager.GetAccountsSnapshot());
                                ToastService.ShowSuccess("Đã mở khóa", $"Đã tải {AccountManager.AccountCount} tài khoản.");
                                return;
                            }

                            ToastService.ShowWarning("Sai mật khẩu", "Mật khẩu Master Password không chính xác.");
                        }

                        var fallbackRes = await MessageBoxDialog.ShowAsync(
                            "Không thể giải mã AccountData.json bằng mật khẩu vừa nhập.\n\nBạn có muốn tự động khôi phục khóa vault.key gốc hoặc tải bản sao lưu hợp lệ gần nhất không?",
                            "Khôi phục dữ liệu",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question,
                            this);

                        if (fallbackRes == MessageBoxResult.Yes && AccountManager.TryRecoverOrRestoreAccounts(out string fallbackMsg))
                        {
                            RefreshAccounts(AccountManager.GetAccountsSnapshot());
                            ToastService.ShowSuccess("Khôi phục thành công", fallbackMsg);
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Error($"[MainWindow] Unlock flow error: {ex.Message}");
                    }
                };
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.K)
            {
                if (ViewModel != null)
                {
                    ViewModel.IsCommandPaletteOpen = !ViewModel.IsCommandPaletteOpen;
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                if (ViewModel != null && ViewModel.IsCommandPaletteOpen)
                {
                    ViewModel.IsCommandPaletteOpen = false;
                    e.Handled = true;
                }
            }
        }

        public void SetStatus(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            ToastService.ShowInfo("Thông báo", message);
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

        public void OpenFastLoginDialog(Account account = null)
        {
            AppServices.Dialogs.OpenFastLoginDialog(account);
        }

        public void OpenBulkImportDialog()
        {
            AppServices.Dialogs.OpenBulkImportDialog();
        }

        public void OpenProxyManagerDialog()
        {
            AppServices.Dialogs.OpenProxyManagerDialog();
        }

        private void SetActiveView(Control activeView, Button activeButton)
        {
            if (ViewDashboard != null) ViewDashboard.IsVisible = (activeView == ViewDashboard);
            if (ViewServerList != null) ViewServerList.IsVisible = (activeView == ViewServerList);
            if (ViewSettings != null) ViewSettings.IsVisible = (activeView == ViewSettings);
            if (ViewAbout != null) ViewAbout.IsVisible = (activeView == ViewAbout);

            ResetNavButtons();
            activeButton?.Classes.Add("active");
        }

        private void ResetNavButtons()
        {
            BtnNavDashboard?.Classes.Remove("active");
            BtnNavServerList?.Classes.Remove("active");
            BtnNavRecentGames?.Classes.Remove("active");
            BtnNavTools?.Classes.Remove("active");
            BtnNavSettings?.Classes.Remove("active");
            BtnNavAbout?.Classes.Remove("active");
        }

        private void OnNavDashboardClicked(object sender, RoutedEventArgs e)
            => SetActiveView(ViewDashboard, BtnNavDashboard);

        private void OnNavServerListClicked(object sender, RoutedEventArgs e)
        {
            if (ViewServerList != null && ViewDashboard?.ViewModel?.ServerListViewModel != null)
            {
                ViewServerList.DataContext = ViewDashboard.ViewModel.ServerListViewModel;
                if (ViewServerList.ViewModel != null)
                    ViewServerList.ViewModel.SelectedSubTabIndex = 0;
            }
            SetActiveView(ViewServerList, BtnNavServerList);
        }

        private void OnNavRecentGamesClicked(object sender, RoutedEventArgs e)
        {
            if (ViewServerList != null && ViewDashboard?.ViewModel?.ServerListViewModel != null)
            {
                ViewServerList.DataContext = ViewDashboard.ViewModel.ServerListViewModel;
                if (ViewServerList.ViewModel != null)
                    ViewServerList.ViewModel.SelectedSubTabIndex = 2; // Favorites / Recent
            }
            SetActiveView(ViewServerList, BtnNavRecentGames);
        }

        private void OnNavSettingsClicked(object sender, RoutedEventArgs e)
            => SetActiveView(ViewSettings, BtnNavSettings);

        private void OnNavAboutClicked(object sender, RoutedEventArgs e)
            => SetActiveView(ViewAbout, BtnNavAbout);

        private void OnSidebarCloseRobloxClicked(object sender, RoutedEventArgs e)
        {
            var selected = ViewDashboard?.GetSelectedAccounts();
            if (selected != null && selected.Count > 0)
            {
                if (selected.Count > 1)
                {
                    bool confirm = AppServices.Dialogs.ShowConfirmation(
                        $"Bạn có chắc chắn muốn đóng tiến trình Roblox của {selected.Count} tài khoản đang chọn không?",
                        "Xác nhận đóng Roblox hàng loạt");
                    if (!confirm) return;
                }

                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    int closed = RobloxProcessService.CloseAccountRobloxProcesses(selected);
                    AppDispatcher.Post(() =>
                    {
                        if (closed > 0)
                        {
                            ToastService.ShowSuccess($"Đã đóng Roblox cho {closed} tài khoản đã chọn.");
                        }
                        else
                        {
                            ToastService.ShowInfo("Không tìm thấy tiến trình Roblox đang chạy của các tài khoản đã chọn.");
                        }
                        ViewModel?.RefreshLiveProcessStatus();
                    });
                });
            }
            else
            {
                bool confirm = AppServices.Dialogs.ShowConfirmation(
                    "Bạn có chắc chắn muốn đóng toàn bộ tất cả tiến trình Roblox đang chạy trên máy không?",
                    "Xác nhận đóng tất cả Roblox");
                if (confirm)
                {
                    ViewModel?.KillAllClientsCommand.Execute(null);
                }
            }
        }

        private void OnToolAntiAfkClicked(object sender, RoutedEventArgs e)
        {
            ViewModel?.ToggleAntiAfkCommand.Execute(null);
        }

        private void OnToolPotatoClicked(object sender, RoutedEventArgs e)
        {
            ViewModel?.TogglePotatoModeCommand.Execute(null);
        }

        private void OnToolZeroGpuClicked(object sender, RoutedEventArgs e)
        {
            ViewModel?.ToggleWindowsCommand.Execute(null);
        }

        private void OnToolOptimizeRamClicked(object sender, RoutedEventArgs e)
        {
            ViewModel?.AntiLagBoosterCommand.Execute(null);
        }

        private void OnToolMuteAllClicked(object sender, RoutedEventArgs e)
        {
            bool wasUnmuted = RobloxAudioService.ToggleMuteAll();
            string msg = wasUnmuted ? "🔇 Đã tắt tiếng toàn bộ các client Roblox đang chạy." : "🔊 Đã bật lại tiếng cho toàn bộ client Roblox.";
            ToastService.ShowSuccess(msg);
        }

        private void OnToolSmartMuteClicked(object sender, RoutedEventArgs e)
        {
            bool nextState = !RobloxAudioService.IsSmartMuteEnabled;
            RobloxAudioService.ConfigureSmartMute(nextState);
            AccountManager.General?.Set(AppSettingsKeys.General.AutoMuteBackgroundClients, nextState ? "true" : "false");
            string msg = nextState 
                ? "🔇 Đã kích hoạt Auto-Mute: Tự động tắt tiếng client chạy ngầm (chỉ bật tiếng cửa sổ đang chọn)."
                : "🔊 Đã tắt Auto-Mute nền.";
            ToastService.ShowSuccess(msg);
        }

        private void OnToolNexusClicked(object sender, RoutedEventArgs e)
        {
            AppServices.Dialogs.OpenNexusWindow();
        }

        private void OnToolProxyClicked(object sender, RoutedEventArgs e)
        {
            AppServices.Dialogs.OpenProxyManagerDialog();
        }

        private void OnToolCleanTrayClicked(object sender, RoutedEventArgs e)
        {
            ViewModel?.CleanSystemTrayCommand.Execute(null);
        }

        private void OnMinimizeClicked(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnMaximizeClicked(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            UpdateMaximizeIcon();
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void UpdateMaximizeIcon()
        {
            if (IconMaximize != null)
            {
                IconMaximize.Icon = WindowState == WindowState.Maximized ? LucideAvalonia.Enum.LucideIconNames.Copy : LucideAvalonia.Enum.LucideIconNames.Square;
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

        private void OnCommandPaletteBarPressed(object sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            if (ViewModel != null)
                ViewModel.IsCommandPaletteOpen = true;
        }

        private void OnCloseCommandPaletteClicked(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                ViewModel.IsCommandPaletteOpen = false;
        }

        private void OnCommandPaletteBackdropPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.Source is Border border && border.Background != null)
            {
                if (ViewModel != null)
                    ViewModel.IsCommandPaletteOpen = false;
            }
        }
    }
}
