#nullable enable
using System;
using System.Globalization;

namespace Shiro.Services
{
    /// <summary>
    /// Immutable snapshot of Roblox Watcher configuration values shared between
    /// Server List Watcher tab and Settings Advanced panel.
    /// </summary>
    public readonly record struct ServerWatcherConfig(
        bool WatcherEnabled,
        bool ExitOnBeta,
        bool ExitIfNoConnection,
        bool SaveWindowPositions,
        bool VerifyDataModel,
        bool IgnoreExistingProcesses,
        bool CloseIfWindowTitle,
        bool CloseRbxMemory,
        decimal MemoryThresholdMb,
        decimal ConnectionTimeoutSec,
        string ExpectedWindowTitle);

    /// <summary>
    /// Single source of truth for reading, persisting, and applying Roblox Watcher
    /// configuration to <see cref="RobloxWatcher"/>.
    /// </summary>
    public sealed class ServerWatcherConfigService
    {
        private readonly Func<ISettingsService?> _watcherProvider;
        private readonly IAppLogger? _logger;
        private readonly Action _saveSettings;

        public ServerWatcherConfigService()
            : this(null, null, null)
        {
        }

        public ServerWatcherConfigService(
            Func<ISettingsService?>? watcherProvider,
            IAppLogger? logger = null,
            Action? saveSettings = null)
        {
            _watcherProvider = watcherProvider ?? (() => AppServices.WatcherSettings);
            _logger = logger;
            _saveSettings = saveSettings ?? AppServices.SaveSettings;
        }

        public event Action<ServerWatcherConfig>? ConfigChanged;

        private IAppLogger Logger => _logger ?? AppServices.Logger;

        public ServerWatcherConfig Load(ISettingsService? watcherOverride = null, bool syncRuntime = true)
        {
            var defaults = new ServerWatcherConfig(
                WatcherEnabled: false,
                ExitOnBeta: false,
                ExitIfNoConnection: false,
                SaveWindowPositions: false,
                VerifyDataModel: true,
                IgnoreExistingProcesses: true,
                CloseIfWindowTitle: false,
                CloseRbxMemory: false,
                MemoryThresholdMb: (decimal)RobloxWatcher.DefaultMemoryThresholdMb,
                ConnectionTimeoutSec: 20m,
                ExpectedWindowTitle: "Roblox");

            try
            {
                ISettingsService? watcher = watcherOverride ?? _watcherProvider();
                if (watcher == null)
                    return defaults;

                bool watcherEnabled = watcher.Get<bool>(AppSettingsKeys.Watcher.Enabled);
                bool exitOnBeta = watcher.Get<bool>(AppSettingsKeys.Watcher.ExitOnBeta);
                bool exitIfNoConnection = watcher.Get<bool>(AppSettingsKeys.Watcher.ExitIfNoConnection);
                bool saveWindowPositions = watcher.Get<bool>(AppSettingsKeys.Watcher.SaveWindowPositions);
                bool verifyDataModel = !watcher.Exists(AppSettingsKeys.Watcher.VerifyDataModel)
                    || watcher.Get<bool>(AppSettingsKeys.Watcher.VerifyDataModel);
                bool ignoreExisting = !watcher.Exists(AppSettingsKeys.Watcher.IgnoreExistingProcesses)
                    || watcher.Get<bool>(AppSettingsKeys.Watcher.IgnoreExistingProcesses);
                bool closeIfWindowTitle = watcher.Exists(AppSettingsKeys.Watcher.CloseIfWindowTitle)
                    && watcher.Get<bool>(AppSettingsKeys.Watcher.CloseIfWindowTitle);
                bool closeRbxMemory = watcher.Get<bool>(AppSettingsKeys.Watcher.CloseRbxMemory);
                decimal memoryThresholdMb = watcher.Exists("MemoryLowValue")
                    ? watcher.Get<decimal>("MemoryLowValue")
                    : (decimal)RobloxWatcher.DefaultMemoryThresholdMb;
                decimal connectionTimeoutSec = watcher.Exists(AppSettingsKeys.Watcher.NoConnectionTimeout)
                    ? watcher.Get<decimal>(AppSettingsKeys.Watcher.NoConnectionTimeout)
                    : 20m;
                string expectedTitle = (watcher.Exists(AppSettingsKeys.Watcher.ExpectedWindowTitle)
                    ? watcher.Get<string>(AppSettingsKeys.Watcher.ExpectedWindowTitle)
                    : "Roblox") ?? "Roblox";

                var config = new ServerWatcherConfig(
                    WatcherEnabled: watcherEnabled,
                    ExitOnBeta: exitOnBeta,
                    ExitIfNoConnection: exitIfNoConnection,
                    SaveWindowPositions: saveWindowPositions,
                    VerifyDataModel: verifyDataModel,
                    IgnoreExistingProcesses: ignoreExisting,
                    CloseIfWindowTitle: closeIfWindowTitle,
                    CloseRbxMemory: closeRbxMemory,
                    MemoryThresholdMb: memoryThresholdMb,
                    ConnectionTimeoutSec: connectionTimeoutSec,
                    ExpectedWindowTitle: expectedTitle);

                if (syncRuntime)
                    ApplyRuntime(config, controlWatcherLifecycle: true);

                return config;
            }
            catch (Exception ex)
            {
                Logger.Debug($"[ServerWatcherConfigService.Load] {ex.Message}");
                return defaults;
            }
        }

        public void ApplyRuntime(ServerWatcherConfig config, bool controlWatcherLifecycle)
        {
            RobloxWatcher.VerifyDataModel = config.VerifyDataModel;
            RobloxWatcher.IgnoreExistingProcesses = config.IgnoreExistingProcesses;
            RobloxWatcher.CloseIfMemoryLow = config.CloseRbxMemory;
            RobloxWatcher.MemoryLowValue = (int)config.MemoryThresholdMb;
            RobloxWatcher.CloseIfWindowTitle = config.CloseIfWindowTitle;
            RobloxWatcher.ExpectedWindowTitle = config.ExpectedWindowTitle;
            RobloxWatcher.RememberWindowPositions = config.SaveWindowPositions;

            if (controlWatcherLifecycle)
            {
                if (config.WatcherEnabled)
                    RobloxWatcher.Start();
                else
                    RobloxWatcher.Stop();
            }
        }

        public void SaveSetting(string key, string value, ISettingsService? watcherOverride = null)
        {
            try
            {
                ISettingsService? watcher = watcherOverride ?? _watcherProvider();
                watcher?.Set(key, value);
                _saveSettings();
                ConfigChanged?.Invoke(Load(watcher, syncRuntime: false));
            }
            catch (Exception ex)
            {
                Logger.Debug($"[ServerWatcherConfigService.SaveSetting] {key}: {ex.Message}");
            }
        }

        public void SaveBool(string key, bool value, ISettingsService? watcherOverride = null)
            => SaveSetting(key, value ? "true" : "false", watcherOverride);

        public void SaveDecimal(string key, decimal value, ISettingsService? watcherOverride = null)
            => SaveSetting(key, value.ToString(CultureInfo.InvariantCulture), watcherOverride);

        public void SaveAdvancedSettings(
            bool closeIfWindowTitle,
            string expectedWindowTitle,
            decimal? noConnectionTimeout,
            ISettingsService? watcherOverride = null)
        {
            ISettingsService? watcher = watcherOverride ?? _watcherProvider();
            string trimmedTitle = (expectedWindowTitle ?? string.Empty).Trim();
            if (watcher != null)
            {
                watcher.Set(AppSettingsKeys.Watcher.CloseIfWindowTitle, closeIfWindowTitle ? "true" : "false");
                watcher.Set(AppSettingsKeys.Watcher.ExpectedWindowTitle, trimmedTitle);
                if (noConnectionTimeout.HasValue)
                {
                    watcher.Set(
                        AppSettingsKeys.Watcher.NoConnectionTimeout,
                        ((int)noConnectionTimeout.Value).ToString(CultureInfo.InvariantCulture));
                }
            }

            RobloxWatcher.CloseIfWindowTitle = closeIfWindowTitle;
            RobloxWatcher.ExpectedWindowTitle = trimmedTitle;
            ConfigChanged?.Invoke(Load(watcher, syncRuntime: false));
        }
    }
}
