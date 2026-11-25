#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        void UpdateMultiRoblox();
        void ConfigureBackgroundThrottling(bool enabled, int targetFps);
        void ConfigureSmartMute(bool on);
        void ConfigureAutoTrim(bool on);
        void SetCpuRateLimit(int percent);
        Task RestartWebServerAsync(int port, bool allowExternal);
        Task StopWebServerAsync();
        void SetAutoCookieRefresh(bool on);
        void SetStreamerMode(bool enabled) { }
        event EventHandler? DisplayNamesChanged;
        event EventHandler? AvatarCacheCleared;
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

        private static readonly AsyncLocal<Func<string, Task<(bool Confirmed, string? Path)>>?> _testPickExportPathOverride = new();

        public event EventHandler? DisplayNamesChanged;
        public event EventHandler? AvatarCacheCleared;

        // Test seam for headless verification of Save vs Cancel export picker outcomes.
        internal static Func<string, Task<(bool Confirmed, string? Path)>>? TestPickExportPathOverride
        {
            get => _testPickExportPathOverride.Value;
            set => _testPickExportPathOverride.Value = value;
        }

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

        public void SetAntiAfk(bool on)
        {
            AntiAfkService.SetConfig(on);
            AppServices.GeneralSettings.Set("EnableAntiAfk", on ? "true" : "false");
        }

        public void UpdateMultiRoblox()
        {
            if (AppServices.HostUiBridge is HostUiBridgeService bridge && bridge.UpdateMultiRobloxHandler == null)
            {
                bool enabled = AppServices.GeneralSettings.Get<bool>(AppSettingsKeys.General.EnableMultiRbx, true);
                RobloxSingletonService.Apply(enabled);
                return;
            }
            AppServices.HostUiBridge.UpdateMultiRoblox();
        }

        public void ConfigureBackgroundThrottling(bool enabled, int targetFps)
        {
            var throttler = RobloxBackgroundThrottlerService.Instance;
            throttler.TargetBackgroundFps = targetFps;
            throttler.IsEnabled = enabled;
        }

        public void ConfigureSmartMute(bool on) => RobloxAudioService.ConfigureSmartMute(on);

        public void ConfigureAutoTrim(bool on) => MemoryOptimizer.ConfigureAutoTrim(on, 1);

        public void SetCpuRateLimit(int percent) => RobloxJobObjectService.SetCpuRateLimit(percent);

        public Task RestartWebServerAsync(int port, bool allowExternal) =>
            AppServices.HostUiBridge.RestartWebServerAsync(port, allowExternal);

        public Task StopWebServerAsync() =>
            AppServices.HostUiBridge.StopWebServerAsync();

        public void SetAutoCookieRefresh(bool on) =>
            AppServices.HostUiBridge.SetAutoCookieRefreshEnabled(on);

        public void SetStreamerMode(bool enabled) =>
            StreamerModeService.IsEnabled = enabled;

        public void NotifyDisplayNamesChanged()
        {
            DisplayNamesChanged?.Invoke(this, EventArgs.Empty);
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

            AvatarCacheCleared?.Invoke(this, EventArgs.Empty);

            return deletedCount;
        }

        public Task ResetEncryptionAsync() => AppServices.HostUiBridge.ResetEncryptionAsync(true);

        public async Task<bool> ExportVaultAsync()
        {
            string prompt = LocalizationService.Get("Settings.VaultPassphrasePrompt");
            string title = LocalizationService.Get("Settings.ExportVaultBtn");
            string pass = AppServices.Dialogs is AvaloniaDialogService avaloniaDialogs
                ? await avaloniaDialogs.ShowConfirmedSecretPromptAsync(prompt, title)
                : await AppServices.Dialogs.ShowSecretPromptAsync(prompt, title);
            if (string.IsNullOrWhiteSpace(pass) || pass == "/UC") return false;

            string? path = await ResolveExportDestinationAsync();
            if (string.IsNullOrWhiteSpace(path)) return false;

            var accounts = AppServices.AccountRepository.GetAll();
            await Task.Run(() =>
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                using var fs = File.Create(path);
                VaultExportService.ExportVault(accounts, pass, fs);
            });
            ToastService.ShowSuccess(LocalizationService.Get("Settings.ExportVaultBtn"), path);
            return true;
        }

        internal static IReadOnlyList<Avalonia.Platform.Storage.FilePickerFileType> CreateVaultFileTypes()
        {
            return
            [
                new Avalonia.Platform.Storage.FilePickerFileType(LocalizationService.Get("Settings.VaultFileTypeName"))
                {
                    Patterns = ["*.shirovault"]
                },
                Avalonia.Platform.Storage.FilePickerFileTypes.All
            ];
        }

        internal static IReadOnlyList<Avalonia.Platform.Storage.FilePickerFileType> CreateVaultImportFileTypes()
        {
            return
            [
                new Avalonia.Platform.Storage.FilePickerFileType(LocalizationService.Get("Settings.VaultAndBackupFileTypeName"))
                {
                    Patterns = ["*.shirovault", "*.bak", "*.backup"]
                },
                new Avalonia.Platform.Storage.FilePickerFileType(LocalizationService.Get("Settings.VaultFileTypeName"))
                {
                    Patterns = ["*.shirovault"]
                },
                new Avalonia.Platform.Storage.FilePickerFileType(LocalizationService.Get("Settings.BackupFileTypeName"))
                {
                    Patterns = ["*.bak", "*.backup"]
                },
                Avalonia.Platform.Storage.FilePickerFileTypes.All
            ];
        }

        internal static Avalonia.Platform.Storage.FilePickerSaveOptions CreateVaultSaveOptions(
            string suggestedFileName,
            Avalonia.Platform.Storage.IStorageFolder? startLocation)
        {
            return new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = LocalizationService.Get("Settings.ExportVaultBtn"),
                SuggestedFileName = suggestedFileName,
                DefaultExtension = "shirovault",
                ShowOverwritePrompt = true,
                FileTypeChoices = CreateVaultFileTypes(),
                SuggestedStartLocation = startLocation
            };
        }

        internal static Avalonia.Platform.Storage.FilePickerOpenOptions CreateVaultOpenOptions(
            Avalonia.Platform.Storage.IStorageFolder? startLocation)
        {
            return new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = LocalizationService.Get("Settings.ImportVaultBtn"),
                AllowMultiple = false,
                FileTypeFilter = CreateVaultImportFileTypes(),
                SuggestedStartLocation = startLocation
            };
        }

        private static async Task<Avalonia.Platform.Storage.IStorageFolder?> TryGetSuggestedVaultStartLocationAsync(
            Avalonia.Platform.Storage.IStorageProvider storageProvider)
        {
            try
            {
                var docsFolder = await storageProvider.TryGetWellKnownFolderAsync(Avalonia.Platform.Storage.WellKnownFolder.Documents);
                if (docsFolder != null)
                    return docsFolder;
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"Documents folder lookup failed: {ex.Message}");
            }

            try
            {
                return await storageProvider.TryGetWellKnownFolderAsync(Avalonia.Platform.Storage.WellKnownFolder.Desktop);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"Desktop folder lookup failed: {ex.Message}");
                return null;
            }
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
                var startLocation = await TryGetSuggestedVaultStartLocationAsync(storageProvider);
                var file = await storageProvider.SaveFilePickerAsync(CreateVaultSaveOptions(suggestedFileName, startLocation));
                string? localPath = file?.Path.LocalPath;
                return string.IsNullOrWhiteSpace(localPath) ? null : localPath;
            }

            return Path.Combine(StoragePaths.BackupsDirectory, $"shiro-vault-{DateTime.UtcNow:yyyyMMddHHmmss}.shirovault");
        }

        public async Task<bool> ImportVaultAsync()
        {
            string? path = null;
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.StorageProvider is { CanOpen: true } storageProvider)
            {
                var startLocation = await TryGetSuggestedVaultStartLocationAsync(storageProvider);
                var files = await storageProvider.OpenFilePickerAsync(CreateVaultOpenOptions(startLocation));
                path = files.Count > 0 ? files[0].Path.LocalPath : null;
            }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            return await ImportVaultFromPathAsync(path);
        }

        internal async Task<bool> ImportVaultFromPathAsync(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

            IReadOnlyList<Account> imported;
            if (IsPortableVaultFile(path))
            {
                string pass = await AppServices.Dialogs.ShowSecretPromptAsync(
                    LocalizationService.Get("Settings.ImportVaultPassphrasePrompt"),
                    LocalizationService.Get("Settings.ImportVaultBtn"));
                if (string.IsNullOrWhiteSpace(pass) || pass == "/UC") return false;

                imported = await Task.Run(() =>
                {
                    using var fs = File.OpenRead(path);
                    return VaultExportService.ImportVault(fs, pass);
                });
            }
            else
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.Length > VaultExportService.MaxEncryptedVaultBytes)
                    throw new InvalidDataException($"Backup file exceeds maximum allowed size of {VaultExportService.MaxEncryptedVaultBytes} bytes.");

                byte[] rawBytes = await Task.Run(() => File.ReadAllBytes(path));
                var decodeKeys = AccountPersistenceService.CreateBackupImportDecodeKeys();
                var decodeResult = await Task.Run(() => AccountFileDecoder.Decode(rawBytes, masterPassword: null, decodeKeys));

                switch (decodeResult.Status)
                {
                    case AccountDecodeStatus.Loaded:
                        imported = decodeResult.Accounts;
                        break;
                    case AccountDecodeStatus.Empty:
                        imported = Array.Empty<Account>();
                        break;
                    case AccountDecodeStatus.NeedsMasterPassword:
                    {
                        string pass = await AppServices.Dialogs.ShowSecretPromptAsync(
                            LocalizationService.Get("Settings.ImportBackupMasterPasswordPrompt"),
                            LocalizationService.Get("Settings.ImportVaultBtn"));
                        if (string.IsNullOrWhiteSpace(pass) || pass == "/UC") return false;

                        var retryResult = await Task.Run(() =>
                        {
                            byte[] passBytes = Encoding.UTF8.GetBytes(pass);
                            byte[] hash = SHA512.HashData(passBytes);
                            try
                            {
                                return AccountFileDecoder.Decode(rawBytes, hash, decodeKeys);
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(passBytes);
                                CryptographicOperations.ZeroMemory(hash);
                            }
                        });

                        if (retryResult.Status == AccountDecodeStatus.Loaded)
                            imported = retryResult.Accounts;
                        else if (retryResult.Status == AccountDecodeStatus.NeedsMasterPassword)
                            throw new CryptographicException("Incorrect Master Password for backup file.");
                        else
                            throw new InvalidDataException("Invalid or corrupted backup file.");
                        break;
                    }
                    default:
                        throw new InvalidDataException("Invalid or unsupported vault or backup file.");
                }
            }

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

        private static bool IsPortableVaultFile(string path)
        {
            Span<byte> header = stackalloc byte[14];
            using var fs = File.OpenRead(path);
            int read = fs.Read(header);
            return VaultExportService.HasVaultHeader(header[..read]);
        }
    }
}
