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
                            title = "Shiro - Điều khoản & Trách nhiệm sử dụng";
                        if (string.IsNullOrWhiteSpace(msg) || msg == "tos_disclaimer_message")
                            msg = "Shiro là tiện ích quản lý tài khoản và hỗ trợ đa phiên chạy cục bộ trên máy tính của bạn.\n\nLưu ý quan trọng:\n1. Mọi kết nối được thực hiện trực tiếp giữa thiết bị của bạn và Roblox, không qua máy chủ trung gian.\n2. Bạn hoàn toàn chịu trách nhiệm về bảo mật thông tin tài khoản và tuân thủ Điều khoản dịch vụ (ToS) của Roblox.\n3. Các tính năng tự động (như Anti-AFK, Multi-Instance) được cung cấp theo nguyên tắc tự nguyện và mặc định tắt.\n\nNhấn Đồng ý để tiếp tục sử dụng.";

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
                                "Phát hiện tệp khóa bảo mật (vault.key) vừa bị thay đổi sau khi lưu dữ liệu tài khoản.\n\nBạn có muốn Shiro tự động quét khôi phục khóa vault.key gốc (hoặc bản sao lưu hợp lệ) để lấy lại danh sách tài khoản không?\n(Chọn 'Không' nếu bạn đã đặt mật khẩu Master Password thủ công)",
                                "Khôi phục dữ liệu Shiro",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question,
                                this);

                            if (recoverRes == MessageBoxResult.Yes)
                            {
                                if (AppServices.AccountRepository.TryRecoverOrRestore(out string statusMsg))
                                {
                                    RefreshAccounts(AppServices.AccountRepository.GetAll());
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
                            if (AppServices.AccountRepository.LoadFromStore(hash))
                            {
                                RefreshAccounts(AppServices.AccountRepository.GetAll());
                                ToastService.ShowSuccess("Đã mở khóa", $"Đã tải {AppServices.AccountRepository.Count} tài khoản.");
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

                        if (fallbackRes == MessageBoxResult.Yes && AppServices.AccountRepository.TryRecoverOrRestore(out string fallbackMsg))
                        {
                            RefreshAccounts(AppServices.AccountRepository.GetAll());
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
