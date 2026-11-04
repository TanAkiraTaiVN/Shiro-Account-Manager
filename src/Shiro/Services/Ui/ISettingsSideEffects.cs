#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shiro.Classes;
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
    }

    public sealed class SettingsSideEffectsService : ISettingsSideEffects
    {
        public static SettingsSideEffectsService Default { get; } = new();

        public static Func<IEnumerable<AccountViewModel>?>? AccountViewModelsProvider { get; set; }

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
    }
}
