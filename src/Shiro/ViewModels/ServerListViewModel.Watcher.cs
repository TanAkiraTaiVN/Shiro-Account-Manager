#nullable enable
using System;
using System.Globalization;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class ServerListViewModel
    {
        private bool _watcherEnabled;
        private bool _exitOnBeta;
        private bool _exitIfNoConnection;
        private bool _saveWindowPositions;
        private bool _verifyDataModel;
        private bool _ignoreExistingProcesses;
        private bool _closeRbxMemory;
        private decimal _memoryThresholdMb = 200;
        private decimal _connectionTimeoutSec = 20;
        private string _expectedWindowTitle = "Roblox";

        public bool WatcherEnabled
        {
            get => _watcherEnabled;
            set
            {
                if (SetProperty(ref _watcherEnabled, value))
                {
                    SaveWatcherSetting("Enabled", value ? "true" : "false");
                    if (value) RobloxWatcher.Start();
                    else RobloxWatcher.Stop();
                }
            }
        }

        public bool ExitOnBeta
        {
            get => _exitOnBeta;
            set { if (SetProperty(ref _exitOnBeta, value)) SaveWatcherSetting("ExitOnBeta", value ? "true" : "false"); }
        }

        public bool ExitIfNoConnection
        {
            get => _exitIfNoConnection;
            set { if (SetProperty(ref _exitIfNoConnection, value)) SaveWatcherSetting("ExitIfNoConnection", value ? "true" : "false"); }
        }

        public bool SaveWindowPositions
        {
            get => _saveWindowPositions;
            set { if (SetProperty(ref _saveWindowPositions, value)) { SaveWatcherSetting("SaveWindowPositions", value ? "true" : "false"); RobloxWatcher.RememberWindowPositions = value; } }
        }

        public bool VerifyDataModel
        {
            get => _verifyDataModel;
            set { if (SetProperty(ref _verifyDataModel, value)) { SaveWatcherSetting("VerifyDataModel", value ? "true" : "false"); RobloxWatcher.VerifyDataModel = value; } }
        }

        public bool IgnoreExistingProcesses
        {
            get => _ignoreExistingProcesses;
            set { if (SetProperty(ref _ignoreExistingProcesses, value)) { SaveWatcherSetting("IgnoreExistingProcesses", value ? "true" : "false"); RobloxWatcher.IgnoreExistingProcesses = value; } }
        }

        public bool CloseRbxMemory
        {
            get => _closeRbxMemory;
            set { if (SetProperty(ref _closeRbxMemory, value)) { SaveWatcherSetting("CloseRbxMemory", value ? "true" : "false"); RobloxWatcher.CloseIfMemoryLow = value; } }
        }

        public decimal MemoryThresholdMb
        {
            get => _memoryThresholdMb;
            set { if (SetProperty(ref _memoryThresholdMb, value)) { SaveWatcherSetting("MemoryLowValue", value.ToString(CultureInfo.InvariantCulture)); RobloxWatcher.MemoryLowValue = (int)value; } }
        }

        public decimal ConnectionTimeoutSec
        {
            get => _connectionTimeoutSec;
            set { if (SetProperty(ref _connectionTimeoutSec, value)) SaveWatcherSetting("NoConnectionTimeout", value.ToString(CultureInfo.InvariantCulture)); }
        }

        public string ExpectedWindowTitle
        {
            get => _expectedWindowTitle;
            set { if (SetProperty(ref _expectedWindowTitle, value)) { SaveWatcherSetting("ExpectedWindowTitle", value); RobloxWatcher.ExpectedWindowTitle = value; } }
        }

        private void LoadWatcherSettings()
        {
            try
            {
                var watcher = AppServices.WatcherSettings;
                if (watcher != null)
                {
                    _watcherEnabled = watcher.Get<bool>("Enabled");
                    _exitOnBeta = watcher.Get<bool>("ExitOnBeta");
                    _exitIfNoConnection = watcher.Get<bool>("ExitIfNoConnection");
                    _saveWindowPositions = watcher.Get<bool>("SaveWindowPositions");
                    _verifyDataModel = watcher.Exists("VerifyDataModel") ? watcher.Get<bool>("VerifyDataModel") : true;
                    _ignoreExistingProcesses = watcher.Exists("IgnoreExistingProcesses") ? watcher.Get<bool>("IgnoreExistingProcesses") : true;
                    _closeRbxMemory = watcher.Get<bool>("CloseRbxMemory");
                    _memoryThresholdMb = watcher.Exists("MemoryLowValue") ? watcher.Get<decimal>("MemoryLowValue") : 200;
                    _connectionTimeoutSec = watcher.Exists("NoConnectionTimeout") ? watcher.Get<decimal>("NoConnectionTimeout") : 20;
                    _expectedWindowTitle = (watcher.Exists("ExpectedWindowTitle") ? watcher.Get<string>("ExpectedWindowTitle") : "Roblox") ?? "Roblox";

                    RobloxWatcher.VerifyDataModel = _verifyDataModel;
                    RobloxWatcher.IgnoreExistingProcesses = _ignoreExistingProcesses;
                    RobloxWatcher.CloseIfMemoryLow = _closeRbxMemory;
                    RobloxWatcher.MemoryLowValue = (int)_memoryThresholdMb;
                    RobloxWatcher.CloseIfWindowTitle = watcher.Exists("CloseIfWindowTitle") && watcher.Get<bool>("CloseIfWindowTitle");
                    RobloxWatcher.ExpectedWindowTitle = _expectedWindowTitle;
                    RobloxWatcher.RememberWindowPositions = _saveWindowPositions;

                    if (_watcherEnabled)
                    {
                        RobloxWatcher.Start();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[LoadWatcherSettings] {ex.Message}");
            }
        }

        private void SaveWatcherSetting(string key, string value)
        {
            try
            {
                AppServices.WatcherSettings?.Set(key, value);
                AppServices.SaveSettings();
            }
            catch (Exception ex)
            {
                _logger.Debug($"[SaveWatcherSetting] {key}: {ex.Message}");
            }
        }
    }
}
