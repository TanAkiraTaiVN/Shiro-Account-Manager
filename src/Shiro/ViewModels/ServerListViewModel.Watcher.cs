#nullable enable
using System;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class ServerListViewModel
    {
        private bool _watcherEnabled;
        private bool _exitOnBeta;
        private bool _exitIfNoConnection;
        private bool _saveWindowPositions;
        private bool _verifyDataModel = true;
        private bool _ignoreExistingProcesses = true;
        private bool _closeRbxMemory;
        private decimal _memoryThresholdMb = RobloxWatcher.DefaultMemoryThresholdMb;
        private decimal _connectionTimeoutSec = 20;
        private string _expectedWindowTitle = "Roblox";
        private ICommand? _fallbackArrangeCommand;

        public ICommand ArrangeRobloxWindowsCommand =>
            _mainViewModel?.ToolsStatus.ArrangeRobloxWindowsCommand
            ?? (_fallbackArrangeCommand ??= new RelayCommand(ExecuteFallbackArrange));

        private void ExecuteFallbackArrange(object? param)
        {
            try
            {
                RobloxWindowLayoutMode mode = RobloxWindowArrangerService.ParseLayoutMode(param);
                var (arranged, desc) = RobloxWindowArrangerService.ArrangeWindows(mode);
                if (arranged > 0)
                {
                    string okMsg = LocalizationService.Format("Main.WindowsArrangedWithLayout", arranged, desc);
                    LoadingStatus = okMsg;
                    ToastService.ShowSuccess(LocalizationService.Get("Main.ArrangeWindowsTitle"), okMsg);
                }
                else
                {
                    LoadingStatus = desc;
                    ToastService.ShowWarning(LocalizationService.Get("Main.ArrangeWindowsTitle"), desc);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[ExecuteFallbackArrange] {ex.Message}");
            }
        }

        public bool WatcherEnabled
        {
            get => _watcherEnabled;
            set
            {
                if (SetProperty(ref _watcherEnabled, value))
                {
                    _watcherConfigService.SaveBool(AppSettingsKeys.Watcher.Enabled, value);
                    if (value) RobloxWatcher.Start();
                    else RobloxWatcher.Stop();
                }
            }
        }

        public bool ExitOnBeta
        {
            get => _exitOnBeta;
            set { if (SetProperty(ref _exitOnBeta, value)) _watcherConfigService.SaveBool(AppSettingsKeys.Watcher.ExitOnBeta, value); }
        }

        public bool ExitIfNoConnection
        {
            get => _exitIfNoConnection;
            set { if (SetProperty(ref _exitIfNoConnection, value)) _watcherConfigService.SaveBool(AppSettingsKeys.Watcher.ExitIfNoConnection, value); }
        }

        public bool SaveWindowPositions
        {
            get => _saveWindowPositions;
            set { if (SetProperty(ref _saveWindowPositions, value)) { _watcherConfigService.SaveBool(AppSettingsKeys.Watcher.SaveWindowPositions, value); RobloxWatcher.RememberWindowPositions = value; } }
        }

        public bool VerifyDataModel
        {
            get => _verifyDataModel;
            set { if (SetProperty(ref _verifyDataModel, value)) { _watcherConfigService.SaveBool(AppSettingsKeys.Watcher.VerifyDataModel, value); RobloxWatcher.VerifyDataModel = value; } }
        }

        public bool IgnoreExistingProcesses
        {
            get => _ignoreExistingProcesses;
            set { if (SetProperty(ref _ignoreExistingProcesses, value)) { _watcherConfigService.SaveBool(AppSettingsKeys.Watcher.IgnoreExistingProcesses, value); RobloxWatcher.IgnoreExistingProcesses = value; } }
        }

        public bool CloseRbxMemory
        {
            get => _closeRbxMemory;
            set { if (SetProperty(ref _closeRbxMemory, value)) { _watcherConfigService.SaveBool(AppSettingsKeys.Watcher.CloseRbxMemory, value); RobloxWatcher.CloseIfMemoryLow = value; } }
        }

        public decimal MemoryThresholdMb
        {
            get => _memoryThresholdMb;
            set { if (SetProperty(ref _memoryThresholdMb, value)) { _watcherConfigService.SaveDecimal("MemoryLowValue", value); RobloxWatcher.MemoryLowValue = (int)value; } }
        }

        public decimal ConnectionTimeoutSec
        {
            get => _connectionTimeoutSec;
            set { if (SetProperty(ref _connectionTimeoutSec, value)) _watcherConfigService.SaveDecimal(AppSettingsKeys.Watcher.NoConnectionTimeout, value); }
        }

        public string ExpectedWindowTitle
        {
            get => _expectedWindowTitle;
            set { if (SetProperty(ref _expectedWindowTitle, value)) { _watcherConfigService.SaveSetting(AppSettingsKeys.Watcher.ExpectedWindowTitle, value); RobloxWatcher.ExpectedWindowTitle = value; } }
        }

        private void LoadWatcherSettings()
        {
            ServerWatcherConfig cfg = _watcherConfigService.Load(syncRuntime: true);
            ApplyWatcherConfigFields(cfg);

            ServerWatcherConfigService cfgSvc = _watcherConfigService;
            _watcherConfigChangedHandler = WeakEventSubscription.CreateAction<ServerListViewModel, ServerWatcherConfig>(
                this,
                static (vm, next) => vm.ApplyWatcherConfigFields(next),
                h => cfgSvc.ConfigChanged -= h);
            cfgSvc.ConfigChanged += _watcherConfigChangedHandler;
        }

        private void ApplyWatcherConfigFields(ServerWatcherConfig cfg)
        {
            SetProperty(ref _watcherEnabled, cfg.WatcherEnabled, nameof(WatcherEnabled));
            SetProperty(ref _exitOnBeta, cfg.ExitOnBeta, nameof(ExitOnBeta));
            SetProperty(ref _exitIfNoConnection, cfg.ExitIfNoConnection, nameof(ExitIfNoConnection));
            SetProperty(ref _saveWindowPositions, cfg.SaveWindowPositions, nameof(SaveWindowPositions));
            SetProperty(ref _verifyDataModel, cfg.VerifyDataModel, nameof(VerifyDataModel));
            SetProperty(ref _ignoreExistingProcesses, cfg.IgnoreExistingProcesses, nameof(IgnoreExistingProcesses));
            SetProperty(ref _closeRbxMemory, cfg.CloseRbxMemory, nameof(CloseRbxMemory));
            SetProperty(ref _memoryThresholdMb, cfg.MemoryThresholdMb, nameof(MemoryThresholdMb));
            SetProperty(ref _connectionTimeoutSec, cfg.ConnectionTimeoutSec, nameof(ConnectionTimeoutSec));
            SetProperty(ref _expectedWindowTitle, cfg.ExpectedWindowTitle, nameof(ExpectedWindowTitle));
        }
    }
}
