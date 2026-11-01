#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
            Loaded += (s, e) => LoadSettingsValues();
        }

        private void OnCategoryTabClicked(object sender, RoutedEventArgs e)
        {
            if (PanelGeneral == null) return;

            PanelGeneral.IsVisible = (TabRadioGeneral?.IsChecked == true);
            PanelPerformance.IsVisible = (TabRadioPerformance?.IsChecked == true);
            PanelLaunching.IsVisible = (TabRadioLaunching?.IsChecked == true);
            PanelSecurity.IsVisible = (TabRadioSecurity?.IsChecked == true);

        }

        private bool _isLoadingSettings;

        private void LoadSettingsValues()
        {
            _isLoadingSettings = true;
            try
            {
                var settings = AppServices.Settings;
                if (settings != null)
                {
                    // General
                    if (CmbLanguage != null)
                        CmbLanguage.SelectedIndex = LocalizationService.CurrentLanguage == AppLanguage.Vietnamese ? 0 : 1;

                    if (SwHideUsernames != null)
                    {
                        bool hide = AccountManager.General != null &&
                                    AccountManager.General.Exists("HideUsernames") &&
                                    AccountManager.General.Get<bool>("HideUsernames");
                        SwHideUsernames.IsChecked = hide;
                    }

                    if (SwCheckUpdates != null)
                        SwCheckUpdates.IsChecked = settings.CheckForUpdates;

                    // Performance
                    if (SwUnlockFps != null) SwUnlockFps.IsChecked = settings.UnlockFPS;
                    if (NumTargetFps != null) NumTargetFps.Value = settings.MaxFPSValue > 0 ? settings.MaxFPSValue : 144;
                    if (SwPotatoMode != null) SwPotatoMode.IsChecked = settings.EnablePotatoMode;
                    if (SwMultiRbx != null) SwMultiRbx.IsChecked = settings.EnableMultiRbx;
                    if (SwAntiAfk != null) SwAntiAfk.IsChecked = AntiAfkService.IsEnabled;
                    if (SwAutoMute != null)
                        SwAutoMute.IsChecked = AccountManager.General != null && AccountManager.General.Exists(AppSettingsKeys.General.AutoMuteBackgroundClients)
                            ? AccountManager.General.Get<bool>(AppSettingsKeys.General.AutoMuteBackgroundClients)
                            : false;
                    if (SwAggressiveRamTrim != null)
                        SwAggressiveRamTrim.IsChecked = AccountManager.General != null && AccountManager.General.Exists(AppSettingsKeys.General.AggressiveRamTrimming)
                            ? AccountManager.General.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming)
                            : true;
                    if (NumJobCpuCap != null)
                        NumJobCpuCap.Value = AccountManager.General != null && AccountManager.General.Exists(AppSettingsKeys.General.MaxTotalRobloxCpuPercent)
                            ? Math.Clamp(AccountManager.General.Get<int>(AppSettingsKeys.General.MaxTotalRobloxCpuPercent), 0, 100)
                            : 0;

                    // Launching
                    if (SwSmartFastLaunch != null)
                        SwSmartFastLaunch.IsChecked = AccountManager.General != null && AccountManager.General.Exists(AppSettingsKeys.General.SmartFastLaunch)
                            ? AccountManager.General.Get<bool>(AppSettingsKeys.General.SmartFastLaunch)
                            : true;
                    if (NumJoinDelay != null) NumJoinDelay.Value = settings.AccountJoinDelay;
                    if (SwUseProxies != null) SwUseProxies.IsChecked = settings.UseProxies;
                    if (SwEnableWebServer != null) SwEnableWebServer.IsChecked = settings.EnableWebServer;
                    if (NumWebServerPort != null) NumWebServerPort.Value = settings.WebServerPort;
                    if (NumNexusPort != null) NumNexusPort.Value = settings.NexusPort;

                    // Security
                    if (SwAutoCookie != null) SwAutoCookie.IsChecked = settings.AutoCookieRefresh;
                    if (SwSavePasswords != null) SwSavePasswords.IsChecked = settings.SavePasswords;

                    RefreshThemeSelection();
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[SettingsView.LoadSettingsValues] {ex}");
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }

        private AccentTheme _selectedTheme = AccentTheme.Monochrome;
        private CancellationTokenSource? _feedbackCts;

        private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingSettings || CmbLanguage == null) return;
            var newLang = CmbLanguage.SelectedIndex == 0 ? AppLanguage.Vietnamese : AppLanguage.English;
            if (newLang == LocalizationService.CurrentLanguage) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => LocalizationService.SetLanguage(newLang),
                Avalonia.Threading.DispatcherPriority.Background);
        }

        private void RefreshThemeSelection()
        {
            _selectedTheme = AccentThemeService.CurrentTheme;
            ApplyButtonBorder(BtnThemeMonochrome, _selectedTheme == AccentTheme.Monochrome);
            ApplyButtonBorder(BtnThemeViolet, _selectedTheme == AccentTheme.Violet);
            ApplyButtonBorder(BtnThemeCyan, _selectedTheme == AccentTheme.Cyan);
            ApplyButtonBorder(BtnThemeEmerald, _selectedTheme == AccentTheme.Emerald);
            ApplyButtonBorder(BtnThemePink, _selectedTheme == AccentTheme.Pink);
        }

        private static void ApplyButtonBorder(Button btn, bool active)
        {
            if (btn == null) return;
            btn.BorderBrush = active ? Brushes.White : Brushes.Transparent;
            btn.BorderThickness = active ? new Thickness(2) : new Thickness(1);
        }

        private void ApplySelectedTheme(AccentTheme theme)
        {
            _selectedTheme = theme;
            AccentThemeService.ApplyTheme(theme, save: true);
            RefreshThemeSelection();
        }

        private void OnThemeMonochromeClicked(object sender, RoutedEventArgs e) => ApplySelectedTheme(AccentTheme.Monochrome);
        private void OnThemeVioletClicked(object sender, RoutedEventArgs e) => ApplySelectedTheme(AccentTheme.Violet);
        private void OnThemeCyanClicked(object sender, RoutedEventArgs e) => ApplySelectedTheme(AccentTheme.Cyan);
        private void OnThemeEmeraldClicked(object sender, RoutedEventArgs e) => ApplySelectedTheme(AccentTheme.Emerald);
        private void OnThemePinkClicked(object sender, RoutedEventArgs e) => ApplySelectedTheme(AccentTheme.Pink);

        private void OnSaveSettingsClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                var settings = AppServices.Settings;
                if (settings != null)
                {
                    // Accent Theme & Language Persistence
                    AccentThemeService.ApplyTheme(_selectedTheme, save: false);
                    AccountManager.General?.Set(AppSettingsKeys.General.AccentTheme, _selectedTheme.ToString());
                    try { AppSettings.Current.AccentTheme = _selectedTheme.ToString(); }
                    catch (Exception ex) { Program.Logger?.Debug($"AppSettings.Current.AccentTheme threw: {ex.Message}"); }

                    if (CmbLanguage != null && CmbLanguage.SelectedIndex >= 0)
                    {
                        var targetLang = CmbLanguage.SelectedIndex == 0 ? AppLanguage.Vietnamese : AppLanguage.English;
                        if (targetLang != LocalizationService.CurrentLanguage)
                        {
                            LocalizationService.SetLanguage(targetLang, saveSetting: false);
                        }
                        AccountManager.General?.Set(AppSettingsKeys.General.Language, targetLang == AppLanguage.English ? "en" : "vi");
                    }

                    // General
                    if (SwHideUsernames?.IsChecked != null)
                    {
                        AccountManager.General?.Set("HideUsernames", SwHideUsernames.IsChecked.Value ? "true" : "false");
                        var accountVms = MainWindow.Instance?.ViewModel?.Accounts;
                        if (accountVms != null)
                        {
                            foreach (var vm in accountVms)
                                vm?.NotifyDisplayNameChanged();
                        }
                    }

                    if (SwCheckUpdates?.IsChecked != null)
                        settings.CheckForUpdates = SwCheckUpdates.IsChecked.Value;

                    // Performance
                    bool patchSettingsNeeded = false;
                    if (SwUnlockFps?.IsChecked != null)
                    {
                        if (settings.UnlockFPS != SwUnlockFps.IsChecked.Value) patchSettingsNeeded = true;
                        settings.UnlockFPS = SwUnlockFps.IsChecked.Value;
                    }
                    if (NumTargetFps?.Value != null)
                    {
                        if (settings.MaxFPSValue != (int)NumTargetFps.Value) patchSettingsNeeded = true;
                        settings.MaxFPSValue = (int)NumTargetFps.Value;
                    }
                    if (SwPotatoMode?.IsChecked != null)
                    {
                        settings.EnablePotatoMode = SwPotatoMode.IsChecked.Value;
                        AccountManager.General?.Set("EnablePotatoMode", SwPotatoMode.IsChecked.Value ? "true" : "false");
                        patchSettingsNeeded = true;
                    }
                    if (patchSettingsNeeded)
                    {
                        _ = Task.Run(ClientSettingsPatcher.PatchSettings);
                    }
                    if (SwMultiRbx?.IsChecked != null) settings.EnableMultiRbx = SwMultiRbx.IsChecked.Value;
                    if (SwAntiAfk?.IsChecked != null) AntiAfkService.SetConfig(SwAntiAfk.IsChecked.Value);
                    if (SwAutoMute?.IsChecked != null)
                    {
                        bool autoMute = SwAutoMute.IsChecked.Value;
                        AccountManager.General?.Set(AppSettingsKeys.General.AutoMuteBackgroundClients, autoMute ? "true" : "false");
                        RobloxAudioService.ConfigureSmartMute(autoMute);
                    }
                    if (SwAggressiveRamTrim?.IsChecked != null)
                    {
                        bool aggressiveTrim = SwAggressiveRamTrim.IsChecked.Value;
                        AccountManager.General?.Set(AppSettingsKeys.General.AggressiveRamTrimming, aggressiveTrim ? "true" : "false");
                        MemoryOptimizer.ConfigureAutoTrim(aggressiveTrim, 1);
                    }
                    if (NumJobCpuCap?.Value != null)
                    {
                        int cpuCap = Math.Clamp((int)NumJobCpuCap.Value.Value, 0, 100);
                        AccountManager.General?.Set(AppSettingsKeys.General.MaxTotalRobloxCpuPercent, cpuCap.ToString());
                        RobloxJobObjectService.SetCpuRateLimit(cpuCap);
                    }

                    // Launching
                    if (SwSmartFastLaunch?.IsChecked != null)
                    {
                        AccountManager.General?.Set(AppSettingsKeys.General.SmartFastLaunch, SwSmartFastLaunch.IsChecked.Value ? "true" : "false");
                    }
                    if (NumJoinDelay?.Value != null) settings.AccountJoinDelay = (int)NumJoinDelay.Value;
                    if (SwUseProxies?.IsChecked != null) settings.UseProxies = SwUseProxies.IsChecked.Value;
                    if (SwEnableWebServer?.IsChecked != null)
                    {
                        bool prevServer = settings.EnableWebServer;
                        int prevPort = settings.WebServerPort;
                        int targetPort = NumWebServerPort?.Value != null ? (int)NumWebServerPort.Value : prevPort;
                        bool newServer = SwEnableWebServer.IsChecked.Value;

                        settings.EnableWebServer = newServer;
                        if (NumWebServerPort?.Value != null) settings.WebServerPort = targetPort;

                        if (prevServer != newServer || (newServer && prevPort != targetPort))
                        {
                            if (newServer && AccountManager.Instance != null)
                            {
                                bool allowExternal = AccountManager.WebServer != null && AccountManager.WebServer.Get<bool>("AllowExternalConnections");
                                _ = AccountManager.Instance.RestartWebServerAsync(targetPort, allowExternal);
                            }
                            else if (!newServer && AccountManager.Instance != null)
                            {
                                _ = AccountManager.Instance.StopWebServerAsync();
                            }
                        }
                    }
                    if (NumWebServerPort?.Value != null) settings.WebServerPort = (int)NumWebServerPort.Value;
                    if (NumNexusPort?.Value != null) settings.NexusPort = (int)NumNexusPort.Value;

                    // Security
                    if (SwAutoCookie?.IsChecked != null)
                    {
                        bool prevAutoCookie = settings.AutoCookieRefresh;
                        bool newAutoCookie = SwAutoCookie.IsChecked.Value;
                        settings.AutoCookieRefresh = newAutoCookie;
                        if (prevAutoCookie != newAutoCookie && AccountManager.Instance != null)
                        {
                            AccountManager.Instance.SetAutoCookieRefreshEnabled(newAutoCookie);
                        }
                    }
                    if (SwSavePasswords?.IsChecked != null) settings.SavePasswords = SwSavePasswords.IsChecked.Value;

                    AccountManager.QueueDeferredSaveSettings();
                }

                _ = ShowSaveFeedbackAsync("Đã lưu toàn bộ cài đặt thành công!");
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[SettingsView.OnSaveSettingsClicked] {ex}");
                if (TxtSaveFeedback != null)
                    TxtSaveFeedback.Text = $"Lỗi khi lưu cài đặt: {ex.Message}";
            }
        }

        private async Task ShowSaveFeedbackAsync(string message)
        {
            if (TxtSaveFeedback == null) return;

            _feedbackCts?.Cancel();
            _feedbackCts?.Dispose();
            var cts = new CancellationTokenSource();
            _feedbackCts = cts;

            TxtSaveFeedback.Text = message;
            try
            {
                await Task.Delay(3000, cts.Token);
                if (!cts.IsCancellationRequested && TxtSaveFeedback != null)
                    TxtSaveFeedback.Text = string.Empty;
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer save click
            }
        }

        private void OnCleanTrayClicked(object sender, RoutedEventArgs e)
        {
            Task.Run(() => Utilities.ThoroughCleanNotificationArea());
            AppServices.Dialogs.ShowInfo("Đã gửi yêu cầu làm sạch khay hệ thống Windows (Taskbar Tray) thành công!", "Dọn dẹp");
        }

        private void OnClearAvatarCacheClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                string avatarDir = Path.Combine(StoragePaths.CacheDirectory, "Avatars");
                int deletedCount = 0;
                if (Directory.Exists(avatarDir))
                {
                    foreach (string f in Directory.EnumerateFiles(avatarDir))
                    {
                        try
                        {
                            File.Delete(f);
                            deletedCount++;
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"Delete avatar cache file failed: {ex.Message}");
                        }
                    }
                }

                ViewModels.AccountViewModel.ClearAvatarMemoryCache();
                var accountVms = MainWindow.Instance?.ViewModel?.Accounts;
                if (accountVms != null)
                {
                    foreach (var accVm in accountVms)
                    {
                        accVm?.InvalidateAvatar();
                    }
                }

                if (deletedCount > 0)
                {
                    AppServices.Dialogs.ShowInfo($"Đã xóa {deletedCount} file ảnh đại diện avatar và làm mới bộ nhớ đệm.", "Dọn dẹp");
                }
                else
                {
                    AppServices.Dialogs.ShowInfo("Đã làm mới bộ nhớ đệm avatar (ổ đĩa hiện đang trống).", "Dọn dẹp");
                }
            }
            catch (Exception ex)
            {
                AppServices.Dialogs.ShowError($"Lỗi khi xóa cache avatar: {ex.Message}", "Lỗi");
            }
        }

        private async void OnChangeMasterPasswordClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AccountManager.Instance != null)
                {
                    await AccountManager.Instance.ResetEncryptionAsync(true);
                }
            }
            catch (Exception ex)
            {
                AppServices.Dialogs.ShowError($"Lỗi khi đổi mật khẩu chủ: {ex.Message}", "Lỗi");
            }
        }
    }
}
