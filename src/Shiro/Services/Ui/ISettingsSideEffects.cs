#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shiro.ViewModels;

namespace Shiro.Services
{
    public enum WebServerAction
    {
        None,
        Start,
        Restart,
        Stop
    }

    public interface ISettingsSideEffects
    {
        void ApplyTheme(AccentTheme theme, bool save);
        void SetLanguage(AppLanguage lang);
        void PatchClientSettings();
        void SetAntiAfk(bool on);
        void ConfigureSmartMute(bool on);
        void ConfigureAutoTrim(bool on);
        void SetCpuRateLimit(int percent);
        Task RestartWebServerAsync(int port, bool allowExternal);
        Task StopWebServerAsync();
        void SetAutoCookieRefresh(bool on);
        void NotifyDisplayNamesChanged();
        void QueueDeferredSave();
        void CleanNotificationArea();
        int ClearAvatarCache();
        Task ResetEncryptionAsync();
        Task<bool> ExportVaultAsync();
        Task<bool> ImportVaultAsync();
        void ApplyDiscordRpcSettings(bool enabled, bool clientIdChanged) { }
    }

    public sealed class SettingsSideEffectsService : ISettingsSideEffects
    {
        public static SettingsSideEffectsService Default { get; } = new();

        public static Func<IEnumerable<AccountViewModel>?>? AccountViewModelsProvider { get; set; }

        // Test seam for headless verification of Save vs Cancel export picker outcomes.
        internal static Func<string, Task<(bool Confirmed, string? Path)>>? TestPickExportPathOverride { get; set; }

        public void ApplyTheme(AccentTheme theme, bool save)
        {
            AccentThemeService.ApplyTheme(theme, save: save);
            if (!save)
            {
                try
                {
                    AppServices.Settings.AccentTheme = theme.ToString();
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Debug($"Settings.AccentTheme threw: {ex.Message}");
                }
            }
        }

        public void SetLanguage(AppLanguage lang)
        {
            if (lang != LocalizationService.CurrentLanguage)
            {
                LocalizationService.SetLanguage(lang, saveSetting: false);
            }
        }

        public void PatchClientSettings()
        {
            _ = Task.Run(ClientSettingsPatcher.PatchSettings);
        }

        public void SetAntiAfk(bool on) => AntiAfkService.SetConfig(on);

        public void ConfigureSmartMute(bool on) => RobloxAudioService.ConfigureSmartMute(on);

        public void ConfigureAutoTrim(bool on) => MemoryOptimizer.ConfigureAutoTrim(on, 1);

        public void SetCpuRateLimit(int percent) => RobloxJobObjectService.SetCpuRateLimit(percent);

        public Task RestartWebServerAsync(int port, bool allowExternal) =>
            AppServices.HostUiBridge.RestartWebServerAsync(port, allowExternal);

        public Task StopWebServerAsync() =>
            AppServices.HostUiBridge.StopWebServerAsync();

        public void SetAutoCookieRefresh(bool on) =>
            AppServices.HostUiBridge.SetAutoCookieRefreshEnabled(on);

        public void NotifyDisplayNamesChanged()
        {
            var accountVms = AccountViewModelsProvider?.Invoke();
            if (accountVms == null) return;
            foreach (var vm in accountVms)
            {
                vm?.NotifyDisplayNameChanged();
            }
        }

        public void QueueDeferredSave() => AppServices.HostUiBridge.QueueDeferredSaveSettings();

        public void ApplyDiscordRpcSettings(bool enabled, bool clientIdChanged) =>
            DiscordRpcService.Default.ApplyConfigurationChanged(enabled, reconnect: clientIdChanged);

        public void CleanNotificationArea()
        {
            _ = Task.Run(() => Utilities.ThoroughCleanNotificationArea());
        }

        public int ClearAvatarCache()
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
                        AppServices.Logger.Debug($"Delete avatar cache file failed: {ex.Message}");
                    }
                }
            }

            AccountViewModel.ClearAvatarMemoryCache();
            var accountVms = AccountViewModelsProvider?.Invoke();
            if (accountVms != null)
            {
                foreach (var accVm in accountVms)
                {
                    accVm?.InvalidateAvatar();
                }
            }

            return deletedCount;
        }

        public Task ResetEncryptionAsync() => AppServices.HostUiBridge.ResetEncryptionAsync(true);

        public async Task<bool> ExportVaultAsync()
        {
            string pass = await AppServices.Dialogs.ShowSecretPromptAsync(
                LocalizationService.Get("Settings.VaultPassphrasePrompt"),
                LocalizationService.Get("Settings.ExportVaultBtn"));
            if (string.IsNullOrWhiteSpace(pass) || pass == "/UC") return false;

            string? path = await ResolveExportDestinationAsync();
            if (string.IsNullOrWhiteSpace(path)) return false;

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await using var fs = File.Create(path);
            VaultExportService.ExportVault(AppServices.AccountRepository.GetAll(), pass, fs);
            ToastService.ShowSuccess(LocalizationService.Get("Settings.ExportVaultBtn"), path);
            return true;
        }

        internal static async Task<string?> ResolveExportDestinationAsync()
        {
            string suggestedFileName = $"shiro-vault-{DateTime.UtcNow:yyyyMMdd}.shirovault";
            if (TestPickExportPathOverride != null)
            {
                var (confirmed, pickedPath) = await TestPickExportPathOverride(suggestedFileName);
                return confirmed && !string.IsNullOrWhiteSpace(pickedPath) ? pickedPath : null;
            }

            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.StorageProvider is { CanSave: true } storageProvider)
            {
                var file = await storageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = LocalizationService.Get("Settings.ExportVaultBtn"),
                    SuggestedFileName = suggestedFileName
                });
                string? localPath = file?.Path.LocalPath;
                return string.IsNullOrWhiteSpace(localPath) ? null : localPath;
            }

            return Path.Combine(StoragePaths.BackupsDirectory, $"shiro-vault-{DateTime.UtcNow:yyyyMMddHHmmss}.shirovault");
        }

        public async Task<bool> ImportVaultAsync()
        {
            string? path = null;
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.StorageProvider != null)
            {
                var files = await desktop.MainWindow.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = LocalizationService.Get("Settings.ImportVaultBtn"),
                    AllowMultiple = false
                });
                path = files.Count > 0 ? files[0].Path.LocalPath : null;
            }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            return await ImportVaultFromPathAsync(path);
        }

        internal async Task<bool> ImportVaultFromPathAsync(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

            string pass = await AppServices.Dialogs.ShowSecretPromptAsync(
                LocalizationService.Get("Settings.VaultPassphrasePrompt"),
                LocalizationService.Get("Settings.ImportVaultBtn"));
            if (string.IsNullOrWhiteSpace(pass) || pass == "/UC") return false;

            await using var fs = File.OpenRead(path);
            var imported = VaultExportService.ImportVault(fs, pass);
            int added = 0;
            foreach (var acc in imported)
            {
                if (!AppServices.AccountRepository.GetAll().Any(e => string.Equals(e.Username, acc.Username, StringComparison.OrdinalIgnoreCase)))
                {
                    AppServices.AccountRepository.AddThreadSafe(acc);
                    added++;
                }
            }
            await AppServices.AccountRepository.SaveAsync(true);
            if (added > 0)
                AppServices.HostUiBridge.NotifyAccountsReplaced(AppServices.AccountRepository.GetAll());
            ToastService.ShowSuccess(LocalizationService.Get("Settings.ImportVaultBtn"), LocalizationService.Format("Settings.ImportVaultSuccess", added));
            return added > 0;
        }
    }
}
