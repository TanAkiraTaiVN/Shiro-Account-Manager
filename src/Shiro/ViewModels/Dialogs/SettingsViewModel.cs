#nullable enable
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs
{
    public sealed class SettingsViewModel : DialogViewModelBase
    {
        private readonly AppSettings _settings;
        private readonly ISettingsService _general;
        private readonly ISettingsSideEffects _effects;
        private readonly IDialogService _dialogs;
        private readonly TimeSpan _feedbackDuration;
        private readonly IAppLogger _logger;

        private bool _isLoading;
        private CancellationTokenSource? _feedbackCts;

        private int _selectedLanguageIndex;
        private AccentTheme _selectedTheme = AccentTheme.Monochrome;
        private bool _hideUsernames;
        private bool _checkForUpdates = true;

        private bool _unlockFps;
        private decimal? _targetFps = 144m;
        private bool _potatoMode;
        private bool _multiRbx = true;
        private bool _antiAfk = true;
        private bool _autoMute;
        private bool _aggressiveRamTrim = true;
        private decimal? _jobCpuCap = 0m;

        private bool _smartFastLaunch = true;
        private decimal? _joinDelay = 8m;
        private bool _useProxies;
        private bool _enableWebServer;
        private decimal? _webServerPort = 7963m;
        private decimal? _nexusPort = 5242m;

        private bool _autoCookieRefresh = true;
        private bool _savePasswords = true;
        private string _saveFeedback = string.Empty;

        public SettingsViewModel(
            AppSettings? settings = null,
            ISettingsService? general = null,
            ISettingsSideEffects? effects = null,
            IDialogService? dialogs = null,
            TimeSpan? feedbackDuration = null,
            IAppLogger? logger = null)
        {
            _settings = settings ?? AppServices.Settings;
            _general = general ?? AppServices.GeneralSettings;
            _effects = effects ?? SettingsSideEffectsService.Default;
            _dialogs = dialogs ?? AppServices.Dialogs;
            _feedbackDuration = feedbackDuration ?? TimeSpan.FromMilliseconds(3000);
            _logger = logger ?? AppServices.Logger;

            LoadCommand = new RelayCommand(Load);
            SaveCommand = new RelayCommand(Save);
            SelectThemeCommand = new RelayCommand<AccentTheme>(SelectTheme);
            CleanTrayCommand = new RelayCommand(CleanTray);
            ClearAvatarCacheCommand = new RelayCommand(ClearAvatarCache);
            ChangeMasterPasswordCommand = TrackCommand(new AsyncRelayCommand(ChangeMasterPasswordAsync, () => !IsBusy));
        }

        public int SelectedLanguageIndex
        {
            get => _selectedLanguageIndex;
            set
            {
                if (SetProperty(ref _selectedLanguageIndex, value) && !_isLoading && value >= 0)
                {
                    var newLang = value == 0 ? AppLanguage.Vietnamese : AppLanguage.English;
                    _effects.SetLanguage(newLang);
                }
            }
        }

        public AccentTheme SelectedTheme
        {
            get => _selectedTheme;
            private set
            {
                if (SetProperty(ref _selectedTheme, value))
                {
                    OnPropertyChanged(nameof(IsThemeMonochrome));
                    OnPropertyChanged(nameof(IsThemeViolet));
                    OnPropertyChanged(nameof(IsThemeCyan));
                    OnPropertyChanged(nameof(IsThemeEmerald));
                    OnPropertyChanged(nameof(IsThemePink));
                }
            }
        }

        public bool IsThemeMonochrome => SelectedTheme == AccentTheme.Monochrome;
        public bool IsThemeViolet => SelectedTheme == AccentTheme.Violet;
        public bool IsThemeCyan => SelectedTheme == AccentTheme.Cyan;
        public bool IsThemeEmerald => SelectedTheme == AccentTheme.Emerald;
        public bool IsThemePink => SelectedTheme == AccentTheme.Pink;

        public bool HideUsernames
        {
            get => _hideUsernames;
            set => SetProperty(ref _hideUsernames, value);
        }

        public bool CheckForUpdates
        {
            get => _checkForUpdates;
            set => SetProperty(ref _checkForUpdates, value);
        }

        public bool UnlockFps
        {
            get => _unlockFps;
            set => SetProperty(ref _unlockFps, value);
        }

        public decimal? TargetFps
        {
            get => _targetFps;
            set => SetProperty(ref _targetFps, value);
        }

        public bool PotatoMode
        {
            get => _potatoMode;
            set => SetProperty(ref _potatoMode, value);
        }

        public bool MultiRbx
        {
            get => _multiRbx;
            set => SetProperty(ref _multiRbx, value);
        }

        public bool AntiAfk
        {
            get => _antiAfk;
            set => SetProperty(ref _antiAfk, value);
        }

        public bool AutoMute
        {
            get => _autoMute;
            set => SetProperty(ref _autoMute, value);
        }

        public bool AggressiveRamTrim
        {
            get => _aggressiveRamTrim;
            set => SetProperty(ref _aggressiveRamTrim, value);
        }

        public decimal? JobCpuCap
        {
            get => _jobCpuCap;
            set => SetProperty(ref _jobCpuCap, value);
        }

        public bool SmartFastLaunch
        {
            get => _smartFastLaunch;
            set => SetProperty(ref _smartFastLaunch, value);
        }

        public decimal? JoinDelay
        {
            get => _joinDelay;
            set => SetProperty(ref _joinDelay, value);
        }

        public bool UseProxies
        {
            get => _useProxies;
            set => SetProperty(ref _useProxies, value);
        }

        public bool EnableWebServer
        {
            get => _enableWebServer;
            set => SetProperty(ref _enableWebServer, value);
        }

        public decimal? WebServerPort
        {
            get => _webServerPort;
            set => SetProperty(ref _webServerPort, value);
        }

        public decimal? NexusPort
        {
            get => _nexusPort;
            set => SetProperty(ref _nexusPort, value);
        }

        public bool AutoCookieRefresh
        {
            get => _autoCookieRefresh;
            set => SetProperty(ref _autoCookieRefresh, value);
        }

        public bool SavePasswords
        {
            get => _savePasswords;
            set => SetProperty(ref _savePasswords, value);
        }

        public string SaveFeedback
        {
            get => _saveFeedback;
            private set => SetProperty(ref _saveFeedback, value);
        }

        public ICommand LoadCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand SelectThemeCommand { get; }
        public ICommand CleanTrayCommand { get; }
        public ICommand ClearAvatarCacheCommand { get; }
        public IAsyncRelayCommand ChangeMasterPasswordCommand { get; }

        public static WebServerAction ComputeWebServerAction(bool wasOn, int oldPort, bool isOn, int newPort)
        {
            if (!wasOn && isOn) return WebServerAction.Start;
            if (wasOn && !isOn) return WebServerAction.Stop;
            if (wasOn && isOn && oldPort != newPort) return WebServerAction.Restart;
            return WebServerAction.None;
        }

        public void Load()
        {
            _isLoading = true;
            try
            {
                SelectedLanguageIndex = LocalizationService.CurrentLanguage == AppLanguage.Vietnamese ? 0 : 1;
                SelectedTheme = AccentThemeService.CurrentTheme;
                HideUsernames = _general.Exists("HideUsernames") && _general.Get<bool>("HideUsernames");
                CheckForUpdates = _settings.CheckForUpdates;

                UnlockFps = _settings.UnlockFPS;
                TargetFps = _settings.MaxFPSValue > 0 ? _settings.MaxFPSValue : 144m;
                PotatoMode = _settings.EnablePotatoMode;
                MultiRbx = _settings.EnableMultiRbx;
                AntiAfk = AntiAfkService.IsEnabled;
                AutoMute = _general.Exists(AppSettingsKeys.General.AutoMuteBackgroundClients)
                    && _general.Get<bool>(AppSettingsKeys.General.AutoMuteBackgroundClients);
                AggressiveRamTrim = !_general.Exists(AppSettingsKeys.General.AggressiveRamTrimming)
                    || _general.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming);

                int rawCpu = _general.Exists(AppSettingsKeys.General.MaxTotalRobloxCpuPercent)
                    ? _general.Get<int>(AppSettingsKeys.General.MaxTotalRobloxCpuPercent)
                    : 0;
                JobCpuCap = Math.Clamp(rawCpu, 0, 100);

                SmartFastLaunch = !_general.Exists(AppSettingsKeys.General.SmartFastLaunch)
                    || _general.Get<bool>(AppSettingsKeys.General.SmartFastLaunch);
                JoinDelay = _settings.AccountJoinDelay;
                UseProxies = _settings.UseProxies;
                EnableWebServer = _settings.EnableWebServer;
                WebServerPort = _settings.WebServerPort;
                NexusPort = _settings.NexusPort;

                AutoCookieRefresh = _settings.AutoCookieRefresh;
                SavePasswords = _settings.SavePasswords;
            }
            catch (Exception ex)
            {
                _logger.Error($"[SettingsViewModel.Load] {ex}");
            }
            finally
            {
                _isLoading = false;
            }
        }

        public void SelectTheme(AccentTheme theme)
        {
            SelectedTheme = theme;
            _effects.ApplyTheme(theme, save: true);
        }

        public void Save()
        {
            try
            {
                _effects.ApplyTheme(SelectedTheme, save: false);
                _general.Set(AppSettingsKeys.General.AccentTheme, SelectedTheme.ToString());

                if (SelectedLanguageIndex >= 0)
                {
                    var targetLang = SelectedLanguageIndex == 0 ? AppLanguage.Vietnamese : AppLanguage.English;
                    _effects.SetLanguage(targetLang);
                    _general.Set(AppSettingsKeys.General.Language, targetLang == AppLanguage.English ? "en" : "vi");
                }

                _general.Set("HideUsernames", HideUsernames ? "true" : "false");
                _effects.NotifyDisplayNamesChanged();

                _settings.CheckForUpdates = CheckForUpdates;

                bool patchSettingsNeeded = false;
                if (_settings.UnlockFPS != UnlockFps)
                {
                    patchSettingsNeeded = true;
                    _settings.UnlockFPS = UnlockFps;
                }

                if (TargetFps.HasValue)
                {
                    int targetFpsInt = (int)TargetFps.Value;
                    if (_settings.MaxFPSValue != targetFpsInt)
                    {
                        patchSettingsNeeded = true;
                        _settings.MaxFPSValue = targetFpsInt;
                    }
                }

                if (_settings.EnablePotatoMode != PotatoMode)
                {
                    patchSettingsNeeded = true;
                    _settings.EnablePotatoMode = PotatoMode;
                    _general.Set("EnablePotatoMode", PotatoMode ? "true" : "false");
                }

                if (patchSettingsNeeded)
                {
                    _effects.PatchClientSettings();
                }

                _settings.EnableMultiRbx = MultiRbx;
                _effects.SetAntiAfk(AntiAfk);

                _general.Set(AppSettingsKeys.General.AutoMuteBackgroundClients, AutoMute ? "true" : "false");
                _effects.ConfigureSmartMute(AutoMute);

                _general.Set(AppSettingsKeys.General.AggressiveRamTrimming, AggressiveRamTrim ? "true" : "false");
                _effects.ConfigureAutoTrim(AggressiveRamTrim);

                if (JobCpuCap.HasValue)
                {
                    int cpuCap = Math.Clamp((int)JobCpuCap.Value, 0, 100);
                    _general.Set(AppSettingsKeys.General.MaxTotalRobloxCpuPercent, cpuCap.ToString(CultureInfo.InvariantCulture));
                    _effects.SetCpuRateLimit(cpuCap);
                }

                _general.Set(AppSettingsKeys.General.SmartFastLaunch, SmartFastLaunch ? "true" : "false");
                if (JoinDelay.HasValue)
                    _settings.AccountJoinDelay = (int)JoinDelay.Value;
                _settings.UseProxies = UseProxies;

                bool prevServer = _settings.EnableWebServer;
                int prevPort = _settings.WebServerPort;
                int targetPort = WebServerPort.HasValue ? (int)WebServerPort.Value : prevPort;
                bool newServer = EnableWebServer;

                _settings.EnableWebServer = newServer;
                if (WebServerPort.HasValue)
                    _settings.WebServerPort = targetPort;

                var wsAction = ComputeWebServerAction(prevServer, prevPort, newServer, targetPort);
                if (wsAction is WebServerAction.Start or WebServerAction.Restart)
                {
                    bool allowExternal = _settings.GetSection("WebServer")?.Get<bool>("AllowExternalConnections") ?? false;
                    _ = _effects.RestartWebServerAsync(targetPort, allowExternal);
                }
                else if (wsAction == WebServerAction.Stop)
                {
                    _ = _effects.StopWebServerAsync();
                }

                if (NexusPort.HasValue)
                    _settings.NexusPort = (int)NexusPort.Value;

                bool prevAutoCookie = _settings.AutoCookieRefresh;
                _settings.AutoCookieRefresh = AutoCookieRefresh;
                if (prevAutoCookie != AutoCookieRefresh)
                {
                    _effects.SetAutoCookieRefresh(AutoCookieRefresh);
                }

                _settings.SavePasswords = SavePasswords;
                _effects.QueueDeferredSave();

                _ = ShowSaveFeedbackAsync(LocalizationService.Get("Settings.SaveSuccess"));
            }
            catch (Exception ex)
            {
                _logger.Error($"[SettingsViewModel.Save] {ex}");
                _feedbackCts?.Cancel();
                SaveFeedback = LocalizationService.Format("Settings.SaveError", ex.Message);
            }
        }

        private async Task ShowSaveFeedbackAsync(string message)
        {
            _feedbackCts?.Cancel();
            _feedbackCts?.Dispose();
            var cts = new CancellationTokenSource();
            _feedbackCts = cts;

            SaveFeedback = message;
            try
            {
                await Task.Delay(_feedbackDuration, cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    SaveFeedback = string.Empty;
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer save click or disposal
            }
        }

        private void CleanTray()
        {
            _effects.CleanNotificationArea();
            _dialogs.ShowInfo(
                LocalizationService.Get("Settings.CleanTrayRequestSent"),
                LocalizationService.Get("Settings.CleanActionTitle"));
        }

        private void ClearAvatarCache()
        {
            try
            {
                int deletedCount = _effects.ClearAvatarCache();
                if (deletedCount > 0)
                {
                    _dialogs.ShowInfo(
                        LocalizationService.Format("Settings.AvatarCacheClearedFiles", deletedCount),
                        LocalizationService.Get("Settings.CleanActionTitle"));
                }
                else
                {
                    _dialogs.ShowInfo(
                        LocalizationService.Get("Settings.AvatarCacheRefreshedEmpty"),
                        LocalizationService.Get("Settings.CleanActionTitle"));
                }
            }
            catch (Exception ex)
            {
                _dialogs.ShowError(
                    LocalizationService.Format("Settings.ClearAvatarCacheError", ex.Message),
                    LocalizationService.Get("Status_Error"));
            }
        }

        private async Task ChangeMasterPasswordAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                await _effects.ResetEncryptionAsync();
            }
            catch (Exception ex)
            {
                _dialogs.ShowError(
                    LocalizationService.Format("Settings.ChangeMasterPasswordError", ex.Message),
                    LocalizationService.Get("Status_Error"));
            }
            finally
            {
                IsBusy = false;
            }
        }

        public override void OnClosing()
        {
            _feedbackCts?.Cancel();
            _feedbackCts?.Dispose();
            _feedbackCts = null;
            base.OnClosing();
        }
    }
}
