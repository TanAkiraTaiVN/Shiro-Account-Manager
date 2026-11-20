#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Shiro.Services;

namespace Shiro.ViewModels
{
    /// <summary>
    /// Manages tool toggles (Anti-AFK, Potato, Zero-GPU, Smart Mute, Streamer Mode),
    /// live Roblox process counts, window arrangement, and dashboard status bar state.
    /// </summary>
    public sealed class ToolsStatusViewModel : ObservableObject, IDisposable
    {
        private readonly IAppLogger _logger;
        private readonly ISettingsService _settings;
        private readonly Func<int> _getSelectedCount;
        private readonly Func<List<Account>?> _getAccountModelsSnapshot;
        private readonly Action<Dictionary<Account, int>?> _applyLiveProcessStatuses;
        private readonly Action _refreshDisplays;

        private string _statusMessage = string.Empty;
        private string _lastDefaultReadyStatus = string.Empty;
        private bool _isPotatoMode;
        private bool _isAntiAfk;
        private bool _areWindowsHidden;
        private int _runningRobloxProcessCount;
        private int _lastRobloxProcessCount = -1;
        private int _isRefreshingLiveProcessStatus;
        private volatile bool _hasPendingRefreshRequest;
        private Action? _audioStatusChangedHandler;
        private Action<bool>? _streamerModeChangedHandler;
        private int _disposed;

        public ToolsStatusViewModel(
            IAppLogger? logger = null,
            ISettingsService? settings = null,
            Func<int>? getSelectedCount = null,
            Func<List<Account>?>? getAccountModelsSnapshot = null,
            Action<Dictionary<Account, int>?>? applyLiveProcessStatuses = null,
            Action? refreshDisplays = null)
        {
            _logger = logger ?? AppServices.Logger;
            _settings = settings ?? AppServices.GeneralSettings;
            _getSelectedCount = getSelectedCount ?? (() => 0);
            _getAccountModelsSnapshot = getAccountModelsSnapshot ?? (() => null);
            _applyLiveProcessStatuses = applyLiveProcessStatuses ?? (_ => { });
            _refreshDisplays = refreshDisplays ?? (() => { });

            _lastDefaultReadyStatus = LocalizationService.Get("Status_ReadyDefault");
            _statusMessage = _lastDefaultReadyStatus;

            InitializeCommands();
            SubscribeWeakEvents();
        }

        public ICommand AntiLagBoosterCommand { get; private set; } = null!;
        public ICommand ToggleWindowsCommand { get; private set; } = null!;
        public ICommand ToggleAntiAfkCommand { get; private set; } = null!;
        public ICommand TogglePotatoModeCommand { get; private set; } = null!;
        public ICommand ToggleStreamerModeCommand { get; private set; } = null!;
        public ICommand ToggleSmartMuteCommand { get; private set; } = null!;
        public ICommand FocusAccountWindowCommand { get; private set; } = null!;
        public ICommand ArrangeRobloxWindowsCommand { get; private set; } = null!;
        public ICommand OpenProxyManagerCommand { get; private set; } = null!;
        public ICommand CleanSystemTrayCommand { get; private set; } = null!;
        public ICommand KillAllClientsCommand { get; private set; } = null!;
        public ICommand RefreshPresenceCommand { get; private set; } = null!;

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public string FooterReadyStatusText => LocalizationService.Get("Main.ShiroReadyStatus");

        public int RunningRobloxProcessCount
        {
            get => _runningRobloxProcessCount;
            set
            {
                if (Volatile.Read(ref _disposed) == 0 && SetProperty(ref _runningRobloxProcessCount, value))
                {
                    OnPropertyChanged(nameof(CloseRobloxButtonText));
                    OnPropertyChanged(nameof(HasRunningRobloxProcesses));
                }
            }
        }

        public bool HasRunningRobloxProcesses => _runningRobloxProcessCount > 0;

        public string CloseRobloxButtonText
        {
            get
            {
                int selectedCount = _getSelectedCount();
                if (selectedCount > 0)
                {
                    string template = LocalizationService.Get("Nav_CloseRobloxSelected");
                    if (!string.IsNullOrEmpty(template) && template.Contains("{0}"))
                        return string.Format(template, selectedCount);
                    return $"{LocalizationService.Get("Nav_CloseRoblox")} ({selectedCount})";
                }
                return _runningRobloxProcessCount > 0
                    ? $"{LocalizationService.Get("Nav_CloseRoblox")} ({_runningRobloxProcessCount})"
                    : LocalizationService.Get("Nav_CloseRoblox");
            }
        }

        public bool IsPotatoMode
        {
            get => _isPotatoMode;
            set
            {
                if (SetProperty(ref _isPotatoMode, value))
                {
                    _settings?.Set("EnablePotatoMode", value ? "true" : "false");
                    ClientSettingsPatcher.PatchSettings();
                    AppServices.HostUiBridge.QueueDeferredSaveSettings();
                    StatusMessage = value
                        ? LocalizationService.Get("Main.PotatoModeActivated")
                        : LocalizationService.Get("Main.PotatoModeDeactivated");
                    NotifyToolStatesChanged();
                }
            }
        }

        public bool IsAntiAfk
        {
            get => _isAntiAfk;
            set
            {
                if (SetProperty(ref _isAntiAfk, value))
                {
                    AntiAfkService.SetConfig(value);
                    _settings?.Set("EnableAntiAfk", value ? "true" : "false");
                    AppServices.HostUiBridge.QueueDeferredSaveSettings();
                    StatusMessage = value
                        ? LocalizationService.Get("Main.AntiAfkActivated")
                        : LocalizationService.Get("Main.AntiAfkDeactivated");
                    NotifyToolStatesChanged();
                }
            }
        }

        public bool AreWindowsHidden
        {
            get => _areWindowsHidden;
            set
            {
                if (SetProperty(ref _areWindowsHidden, value))
                {
                    if (value)
                    {
                        var (hidden, _) = RobloxWindowService.HideAllWindows();
                        StatusMessage = LocalizationService.Format("Main.ZeroGpuActivatedCount", hidden);
                    }
                    else
                    {
                        var (restored, _) = RobloxWindowService.ShowAllWindows();
                        StatusMessage = LocalizationService.Format("Main.RestoredRobloxWindows", restored);
                    }
                    NotifyToolStatesChanged();
                }
            }
        }

        public bool IsSmartMuteActive => RobloxAudioService.IsSmartMuteEnabled;
        public bool IsStreamerModeActive => StreamerModeService.IsActive;
        public char JobIdMaskChar => IsStreamerModeActive ? '*' : '\0';

        public bool IsStreamerModeEnabled
        {
            get => StreamerModeService.IsEnabled;
            set
            {
                if (StreamerModeService.IsEnabled != value)
                {
                    StreamerModeService.IsEnabled = value;
                    _settings?.Set(AppSettingsKeys.General.HideUsernames, value ? "true" : "false");
                    NotifyToolStatesChanged();
                    StatusMessage = value
                        ? LocalizationService.Get("Main.StreamerModeActivated")
                        : LocalizationService.Get("MainWindow.StreamerModeDisabled");
                    _refreshDisplays();
                }
            }
        }

        public int ActiveToolsCount
        {
            get
            {
                int count = 0;
                if (IsAntiAfk) count++;
                if (IsPotatoMode) count++;
                if (AreWindowsHidden) count++;
                if (IsSmartMuteActive) count++;
                if (IsStreamerModeActive) count++;
                return count;
            }
        }

        public bool HasActiveTools => ActiveToolsCount > 0;

        public void NotifyToolStatesChanged()
        {
            OnPropertyChanged(nameof(IsAntiAfk));
            OnPropertyChanged(nameof(IsPotatoMode));
            OnPropertyChanged(nameof(AreWindowsHidden));
            OnPropertyChanged(nameof(IsSmartMuteActive));
            OnPropertyChanged(nameof(IsStreamerModeEnabled));
            OnPropertyChanged(nameof(IsStreamerModeActive));
            OnPropertyChanged(nameof(JobIdMaskChar));
            OnPropertyChanged(nameof(ActiveToolsCount));
            OnPropertyChanged(nameof(HasActiveTools));
        }

        public void RefreshStateFromSettings()
        {
            SetProperty(ref _isAntiAfk, AntiAfkService.IsEnabled, nameof(IsAntiAfk));
            SetProperty(ref _areWindowsHidden, RobloxWindowService.AreWindowsHidden, nameof(AreWindowsHidden));
            bool potatoEnabled = _settings != null &&
                                 _settings.Exists("EnablePotatoMode") &&
                                 _settings.Get<bool>("EnablePotatoMode");
            SetProperty(ref _isPotatoMode, potatoEnabled, nameof(IsPotatoMode));
            NotifyToolStatesChanged();
        }

        internal void NotifySelectionChanged() => OnPropertyChanged(nameof(CloseRobloxButtonText));

        internal void NotifyLanguageChanged()
        {
            OnPropertyChanged(nameof(CloseRobloxButtonText));
            OnPropertyChanged(nameof(FooterReadyStatusText));
            string localizedReady = LocalizationService.Get("Status_ReadyDefault");
            if (string.IsNullOrWhiteSpace(_statusMessage)
                || string.Equals(_statusMessage, _lastDefaultReadyStatus, StringComparison.Ordinal)
                || string.Equals(_statusMessage, localizedReady, StringComparison.Ordinal))
            {
                _lastDefaultReadyStatus = localizedReady;
                StatusMessage = localizedReady;
            }
        }

        public void RefreshLiveProcessStatus()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (Avalonia.Application.Current != null && Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(RefreshLiveProcessStatus);
                return;
            }

            if (Avalonia.Application.Current == null)
            {
                int total = RobloxProcessService.GetTotalRunningRobloxCount();
                List<Account>? inlineModels = total > 0 ? _getAccountModelsSnapshot() : null;
                Dictionary<Account, int>? map = inlineModels != null && inlineModels.Count > 0
                    ? RobloxWindowArrangerService.ResolveRunningAccountPids(inlineModels)
                    : null;
                ApplyLiveProcessStatusResults(total, map);
                return;
            }

            if (Interlocked.CompareExchange(ref _isRefreshingLiveProcessStatus, 1, 0) != 0)
            {
                _hasPendingRefreshRequest = true;
                return;
            }

            List<Account>? models = _getAccountModelsSnapshot();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    do
                    {
                        _hasPendingRefreshRequest = false;
                        int totalRobloxProcs = RobloxProcessService.GetTotalRunningRobloxCount();
                        Dictionary<Account, int>? runningMap = null;
                        if (totalRobloxProcs > 0 && models != null && models.Count > 0)
                            runningMap = RobloxWindowArrangerService.ResolveRunningAccountPids(models);

                        AppDispatcher.Post(() => ApplyLiveProcessStatusResults(totalRobloxProcs, runningMap));
                    }
                    while (_hasPendingRefreshRequest);
                }
                catch (Exception ex) { _logger.Debug($"[RefreshLiveProcessStatus] Process check error: {ex.Message}"); }
                finally
                {
                    Interlocked.Exchange(ref _isRefreshingLiveProcessStatus, 0);
                    if (_hasPendingRefreshRequest && Volatile.Read(ref _disposed) == 0)
                        AppDispatcher.Post(RefreshLiveProcessStatus);
                }
            });
        }

        private void ApplyLiveProcessStatusResults(int totalRobloxProcs, Dictionary<Account, int>? runningMap)
        {
            try
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                RunningRobloxProcessCount = totalRobloxProcs;
                if (totalRobloxProcs == 0 && _areWindowsHidden)
                {
                    _areWindowsHidden = false;
                    OnPropertyChanged(nameof(AreWindowsHidden));
                    NotifyToolStatesChanged();
                }

                if (_lastRobloxProcessCount > 0 && totalRobloxProcs < _lastRobloxProcessCount)
                    Utilities.QueueRefreshNotificationArea();
                _lastRobloxProcessCount = totalRobloxProcs;
                _applyLiveProcessStatuses(runningMap);
            }
            catch (Exception ex) { _logger.Debug($"[ApplyLiveProcessStatusResults] UI update error: {ex.Message}"); }
        }

        private void SubscribeWeakEvents()
        {
            _audioStatusChangedHandler = WeakEventSubscription.CreateAction<ToolsStatusViewModel>(
                this,
                static vm => AppDispatcher.Post(() => { if (Volatile.Read(ref vm._disposed) == 0) vm.NotifyToolStatesChanged(); }),
                static h => RobloxAudioService.StatusChanged -= h);
            RobloxAudioService.StatusChanged += _audioStatusChangedHandler;

            _streamerModeChangedHandler = WeakEventSubscription.CreateAction<ToolsStatusViewModel, bool>(
                this,
                static (vm, _) => AppDispatcher.Post(() =>
                {
                    if (Volatile.Read(ref vm._disposed) != 0) return;
                    vm.NotifyToolStatesChanged();
                    vm._refreshDisplays();
                }),
                static h => StreamerModeService.StateChanged -= h);
            StreamerModeService.StateChanged += _streamerModeChangedHandler;
        }

        private void InitializeCommands()
        {
            AntiLagBoosterCommand = new RelayCommand(() =>
            {
                var (bytesSaved, count, prioCount) = MemoryOptimizer.OptimizeRobloxClients(true);
                double mb = bytesSaved / (1024.0 * 1024.0);
                StatusMessage = (count > 0 || prioCount > 0)
                    ? LocalizationService.Format("Main.SuperOptimizationSuccess", mb, prioCount)
                    : LocalizationService.Get("Main.MemoryOptimizedStatus");
            });

            ToggleWindowsCommand = new RelayCommand(() =>
            {
                bool wasHidden = RobloxWindowService.AreWindowsHidden || _areWindowsHidden;
                if (wasHidden)
                {
                    var (shown, _) = RobloxWindowService.ShowAllWindows();
                    _areWindowsHidden = false;
                    OnPropertyChanged(nameof(AreWindowsHidden));
                    NotifyToolStatesChanged();
                    StatusMessage = LocalizationService.Format("Main.RestoredRobloxWindows", shown);
                }
                else
                {
                    var (hidden, _) = RobloxWindowService.HideAllWindows();
                    bool active = hidden > 0 && RobloxWindowService.AreWindowsHidden;
                    _areWindowsHidden = active;
                    OnPropertyChanged(nameof(AreWindowsHidden));
                    NotifyToolStatesChanged();
                    if (active)
                    {
                        string msg = LocalizationService.Format("Main.ZeroGpuActivated", hidden);
                        StatusMessage = msg;
                        ToastService.ShowSuccess("Zero-GPU Mode", msg);
                    }
                    else
                    {
                        string warn = LocalizationService.Get("Main.ZeroGpuNoWindowsOpen");
                        StatusMessage = warn;
                        ToastService.ShowInfo("Zero-GPU Mode", warn);
                    }
                }
            });

            ToggleAntiAfkCommand = new RelayCommand(() =>
            {
                bool next = !AntiAfkService.IsEnabled;
                if (IsAntiAfk != next) IsAntiAfk = next;
                else AntiAfkService.SetConfig(next);
            });
            TogglePotatoModeCommand = new RelayCommand(() => IsPotatoMode = !IsPotatoMode);
            ToggleStreamerModeCommand = new RelayCommand(() => IsStreamerModeEnabled = !IsStreamerModeEnabled);
            OpenProxyManagerCommand = new RelayCommand(() => AppServices.Dialogs.OpenProxyManagerDialog());
            RefreshPresenceCommand = new RelayCommand(() =>
            {
                RefreshLiveProcessStatus();
                StatusMessage = LocalizationService.Get("Main.PresenceStatusUpdated");
            });
            KillAllClientsCommand = new RelayCommand(() =>
            {
                _ = Task.Run(() =>
                {
                    int killed = RobloxProcessService.KillAllRobloxProcesses();
                    AppDispatcher.Post(() =>
                    {
                        StatusMessage = LocalizationService.Format("Main.KilledRobloxCount", killed);
                        RefreshLiveProcessStatus();
                    });
                });
            });
            CleanSystemTrayCommand = new RelayCommand(() =>
            {
                Task.Run(() => Utilities.ThoroughCleanNotificationArea());
                StatusMessage = LocalizationService.Get("Main.TrayCleanedSuccess");
                ToastService.ShowSuccess(LocalizationService.Get("Main.SystemTrayTitle"), LocalizationService.Get("Main.TrayCleanedGhostIcons"));
            });
            FocusAccountWindowCommand = new RelayCommand(param =>
            {
                if (param is not AccountViewModel avm || avm.Model == null) return;
                if (RobloxWindowArrangerService.FocusAccountWindow(avm.Model))
                    StatusMessage = LocalizationService.Format("Main.BroughtWindowToFront", avm.Username);
                else
                {
                    RefreshLiveProcessStatus();
                    ToastService.ShowInfo(avm.Username, LocalizationService.Get("Main.WindowNotFoundForAccount"));
                }
            });
            ArrangeRobloxWindowsCommand = new RelayCommand(param =>
            {
                RobloxWindowLayoutMode mode = RobloxWindowLayoutMode.AutoGrid;
                if (param is RobloxWindowLayoutMode explicitMode) mode = explicitMode;
                else if (param is string raw)
                {
                    mode = raw.Trim().ToLowerInvariant() switch
                    {
                        "2x2" or "grid2x2" => RobloxWindowLayoutMode.Grid2x2,
                        "3x2" or "grid3x2" => RobloxWindowLayoutMode.Grid3x2,
                        "4x3" or "grid4x3" => RobloxWindowLayoutMode.Grid4x3,
                        "cascade" or "cascadecorner" => RobloxWindowLayoutMode.CascadeCorner,
                        _ => RobloxWindowLayoutMode.AutoGrid
                    };
                }

                var (arranged, desc) = RobloxWindowArrangerService.ArrangeWindows(mode);
                if (arranged > 0)
                {
                    string okMsg = LocalizationService.Format("Main.WindowsArrangedWithLayout", arranged, desc);
                    StatusMessage = okMsg;
                    ToastService.ShowSuccess(LocalizationService.Get("Main.ArrangeWindowsTitle"), okMsg);
                }
                else
                {
                    StatusMessage = desc;
                    ToastService.ShowWarning(LocalizationService.Get("Main.ArrangeWindowsTitle"), desc);
                }
            });
            ToggleSmartMuteCommand = new RelayCommand(() =>
            {
                bool next = !RobloxAudioService.IsSmartMuteEnabled;
                RobloxAudioService.ConfigureSmartMute(next);
                _settings?.Set("AutoMuteBackgroundClients", next ? "true" : "false");
                AppServices.HostUiBridge.QueueDeferredSaveSettings();
                NotifyToolStatesChanged();
                string msg = next ? LocalizationService.Get("Main.SmartAutoMuteActivated") : LocalizationService.Get("Main.SmartAutoMuteDeactivated");
                StatusMessage = msg;
                ToastService.ShowInfo("Smart Auto-Mute", msg);
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_audioStatusChangedHandler != null) RobloxAudioService.StatusChanged -= _audioStatusChangedHandler;
            if (_streamerModeChangedHandler != null) StreamerModeService.StateChanged -= _streamerModeChangedHandler;
            GC.SuppressFinalize(this);
        }
    }
}
