#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs
{
    public sealed partial class SettingsViewModel
    {
        private bool _allowExternalConnections;
        private bool _everyRequestRequiresPassword;
        private bool _allowGetAccounts = true;
        private bool _allowGetCookie;
        private bool _allowAccountEditing;
        private bool _allowLaunchAccount = true;
        private string _discordWebhookUrl = string.Empty;
        private string _telegramBotToken = string.Empty;
        private string _telegramChatId = string.Empty;
        private bool _notifyOnDisconnect;
        private bool _notifyOnModeration = true;
        private bool _discordRpcEnabled = true;
        private string _discordRpcClientId = string.Empty;
        private bool _discordRpcShowGame = true;
        private bool _discordRpcJoinCurrentServer = true;
        private bool _enableAutoReconnect;
        private bool _enableTokenDeElevation;
        private bool _streamerModeAutoDetect;
        private bool _antiAfkForegroundFallback;
        private bool _closeIfWindowTitle;
        private string _expectedWindowTitle = "Roblox";
        private decimal? _noConnectionTimeout = 20m;
        private bool _enableBackgroundThrottling;
        private decimal? _backgroundTargetFps = 10m;

        public bool AllowExternalConnections { get => _allowExternalConnections; set => SetProperty(ref _allowExternalConnections, value); }
        public bool EveryRequestRequiresPassword { get => _everyRequestRequiresPassword; set => SetProperty(ref _everyRequestRequiresPassword, value); }
        public bool AllowGetAccounts { get => _allowGetAccounts; set => SetProperty(ref _allowGetAccounts, value); }
        public bool AllowGetCookie { get => _allowGetCookie; set => SetProperty(ref _allowGetCookie, value); }
        public bool AllowAccountEditing { get => _allowAccountEditing; set => SetProperty(ref _allowAccountEditing, value); }
        public bool AllowLaunchAccount { get => _allowLaunchAccount; set => SetProperty(ref _allowLaunchAccount, value); }
        public string DiscordWebhookUrl { get => _discordWebhookUrl; set => SetProperty(ref _discordWebhookUrl, value ?? string.Empty); }
        public string TelegramBotToken { get => _telegramBotToken; set => SetProperty(ref _telegramBotToken, value ?? string.Empty); }
        public string TelegramChatId { get => _telegramChatId; set => SetProperty(ref _telegramChatId, value ?? string.Empty); }
        public bool NotifyOnDisconnect { get => _notifyOnDisconnect; set => SetProperty(ref _notifyOnDisconnect, value); }
        public bool NotifyOnModeration { get => _notifyOnModeration; set => SetProperty(ref _notifyOnModeration, value); }
        public bool DiscordRpcEnabled { get => _discordRpcEnabled; set => SetProperty(ref _discordRpcEnabled, value); }
        public string DiscordRpcClientId { get => _discordRpcClientId; set => SetProperty(ref _discordRpcClientId, value ?? string.Empty); }
        public bool DiscordRpcShowGame { get => _discordRpcShowGame; set => SetProperty(ref _discordRpcShowGame, value); }
        public bool DiscordRpcJoinCurrentServer { get => _discordRpcJoinCurrentServer; set => SetProperty(ref _discordRpcJoinCurrentServer, value); }
        public bool EnableAutoReconnect { get => _enableAutoReconnect; set => SetProperty(ref _enableAutoReconnect, value); }
        public bool EnableTokenDeElevation { get => _enableTokenDeElevation; set => SetProperty(ref _enableTokenDeElevation, value); }
        public bool StreamerModeAutoDetect { get => _streamerModeAutoDetect; set => SetProperty(ref _streamerModeAutoDetect, value); }
        public bool AntiAfkForegroundFallback { get => _antiAfkForegroundFallback; set => SetProperty(ref _antiAfkForegroundFallback, value); }
        public bool CloseIfWindowTitle { get => _closeIfWindowTitle; set => SetProperty(ref _closeIfWindowTitle, value); }
        public string ExpectedWindowTitle { get => _expectedWindowTitle; set => SetProperty(ref _expectedWindowTitle, value ?? string.Empty); }
        public decimal? NoConnectionTimeout { get => _noConnectionTimeout; set => SetProperty(ref _noConnectionTimeout, value); }
        public bool EnableBackgroundThrottling { get => _enableBackgroundThrottling; set => SetProperty(ref _enableBackgroundThrottling, value); }
        public decimal? BackgroundTargetFps { get => _backgroundTargetFps; set => SetProperty(ref _backgroundTargetFps, value); }

        private readonly ServerWatcherConfigService _watcherConfig = AppServices.ServerWatcherConfig;
        private Action<bool>? _streamerStateChangedHandler;

        public IAsyncRelayCommand ExportVaultCommand { get; private set; } = null!;
        public IAsyncRelayCommand ImportVaultCommand { get; private set; } = null!;

        private void InitializeAdvancedCommands()
        {
            ExportVaultCommand = TrackCommand(new AsyncRelayCommand(ExportVaultAsync, () => !IsBusy));
            ImportVaultCommand = TrackCommand(new AsyncRelayCommand(ImportVaultAsync, () => !IsBusy));
        }

        public static WebServerAction ComputeWebServerAction(
            bool wasOn,
            int oldPort,
            bool oldAllowExternal,
            bool isOn,
            int newPort,
            bool newAllowExternal)
        {
            if (!wasOn && isOn) return WebServerAction.Start;
            if (wasOn && !isOn) return WebServerAction.Stop;
            if (wasOn && isOn && (oldPort != newPort || oldAllowExternal != newAllowExternal)) return WebServerAction.Restart;
            return WebServerAction.None;
        }

        private void LoadAdvanced()
        {
            if (_streamerStateChangedHandler == null)
            {
                _streamerStateChangedHandler = WeakEventSubscription.CreateAction<SettingsViewModel, bool>(
                    this,
                    static (vm, enabled) => vm.OnStreamerModeStateChanged(enabled),
                    static h => StreamerModeService.StateChanged -= h);
                StreamerModeService.StateChanged += _streamerStateChangedHandler;
            }

            IniSection ws = _settings.GetSection(AppSettingsKeys.Sections.WebServer) ?? _settings.IniFile.Section(AppSettingsKeys.Sections.WebServer);
            AllowExternalConnections = ws.Get<bool>(AppSettingsKeys.WebServer.AllowExternalConnections);
            EveryRequestRequiresPassword = ws.Get<bool>(AppSettingsKeys.WebServer.EveryRequestRequiresPassword);
            AllowGetAccounts = !ws.Exists(AppSettingsKeys.WebServer.AllowGetAccounts) || ws.Get<bool>(AppSettingsKeys.WebServer.AllowGetAccounts);
            AllowGetCookie = ws.Get<bool>(AppSettingsKeys.WebServer.AllowGetCookie);
            AllowAccountEditing = ws.Get<bool>(AppSettingsKeys.WebServer.AllowAccountEditing);
            AllowLaunchAccount = !ws.Exists(AppSettingsKeys.WebServer.AllowLaunchAccount) || ws.Get<bool>(AppSettingsKeys.WebServer.AllowLaunchAccount);

            DiscordWebhookUrl = CryptoHelper.UnprotectSecretString(_general.Get<string>(AppSettingsKeys.General.DiscordWebhookUrl, string.Empty) ?? string.Empty);
            TelegramBotToken = CryptoHelper.UnprotectSecretString(_general.Get<string>(AppSettingsKeys.General.TelegramBotToken, string.Empty) ?? string.Empty);
            TelegramChatId = _general.Get<string>(AppSettingsKeys.General.TelegramChatId, string.Empty) ?? string.Empty;
            NotifyOnDisconnect = _general.Get<bool>(AppSettingsKeys.General.NotifyOnDisconnect, false);
            NotifyOnModeration = !_general.Exists(AppSettingsKeys.General.NotifyOnModeration) || _general.Get<bool>(AppSettingsKeys.General.NotifyOnModeration, true);

            DiscordRpcEnabled = !_general.Exists(AppSettingsKeys.General.DiscordRpcEnabled) || _general.Get<bool>(AppSettingsKeys.General.DiscordRpcEnabled, true);
            DiscordRpcClientId = _general.Get<string>(AppSettingsKeys.General.DiscordRpcClientId, string.Empty) ?? string.Empty;
            DiscordRpcShowGame = !_general.Exists(AppSettingsKeys.General.DiscordRpcShowGame) || _general.Get<bool>(AppSettingsKeys.General.DiscordRpcShowGame, true);
            DiscordRpcJoinCurrentServer = !_general.Exists(AppSettingsKeys.General.DiscordRpcJoinCurrentServer) || _general.Get<bool>(AppSettingsKeys.General.DiscordRpcJoinCurrentServer, true);

            EnableAutoReconnect = _general.Exists(AppSettingsKeys.General.AutoReconnect)
                ? _general.Get<bool>(AppSettingsKeys.General.AutoReconnect, false)
                : _general.Get<bool>("EnableAutoReconnect", false);
            EnableTokenDeElevation = _general.Get<bool>(AppSettingsKeys.General.EnableTokenDeElevation, false);
            StreamerModeAutoDetect = _general.Get<bool>(AppSettingsKeys.General.StreamerModeAutoDetect, false);
            AntiAfkForegroundFallback = _general.Get<bool>(AppSettingsKeys.General.AntiAfkForegroundFallback, false);

            ISettingsService? watcher = _settings.GetSection(AppSettingsKeys.Sections.Watcher) ?? _settings.IniFile.Section(AppSettingsKeys.Sections.Watcher);
            ServerWatcherConfig watcherCfg = _watcherConfig.Load(watcher, syncRuntime: false);
            CloseIfWindowTitle = watcherCfg.CloseIfWindowTitle;
            ExpectedWindowTitle = watcherCfg.ExpectedWindowTitle;
            NoConnectionTimeout = Math.Clamp((int)watcherCfg.ConnectionTimeoutSec, 0, 600);

            EnableBackgroundThrottling = _general.Get<bool>(AppSettingsKeys.General.EnableBackgroundThrottling, false);
            int bgFps = _general.Exists(AppSettingsKeys.General.BackgroundTargetFps)
                ? _general.Get<int>(AppSettingsKeys.General.BackgroundTargetFps, 10)
                : 10;
            BackgroundTargetFps = Math.Clamp(bgFps, 1, 60);
        }

        private void SaveAdvanced()
        {
            IniSection ws = _settings.GetSection(AppSettingsKeys.Sections.WebServer) ?? _settings.IniFile.Section(AppSettingsKeys.Sections.WebServer);
            ws.Set(AppSettingsKeys.WebServer.AllowExternalConnections, AllowExternalConnections ? "true" : "false");
            ws.Set(AppSettingsKeys.WebServer.EveryRequestRequiresPassword, EveryRequestRequiresPassword ? "true" : "false");
            ws.Set(AppSettingsKeys.WebServer.AllowGetAccounts, AllowGetAccounts ? "true" : "false");
            ws.Set(AppSettingsKeys.WebServer.AllowGetCookie, AllowGetCookie ? "true" : "false");
            ws.Set(AppSettingsKeys.WebServer.AllowAccountEditing, AllowAccountEditing ? "true" : "false");
            ws.Set(AppSettingsKeys.WebServer.AllowLaunchAccount, AllowLaunchAccount ? "true" : "false");

            string trimmedWebhook = DiscordWebhookUrl.Trim();
            string trimmedTelegramToken = TelegramBotToken.Trim();
            _general.Set(
                AppSettingsKeys.General.DiscordWebhookUrl,
                string.IsNullOrEmpty(trimmedWebhook) ? string.Empty : CryptoHelper.ProtectSecretString(trimmedWebhook));
            _general.Set(
                AppSettingsKeys.General.TelegramBotToken,
                string.IsNullOrEmpty(trimmedTelegramToken) ? string.Empty : CryptoHelper.ProtectSecretString(trimmedTelegramToken));
            _general.Set(AppSettingsKeys.General.TelegramChatId, TelegramChatId.Trim());
            _general.Set(AppSettingsKeys.General.NotifyOnDisconnect, NotifyOnDisconnect ? "true" : "false");
            _general.Set(AppSettingsKeys.General.NotifyOnModeration, NotifyOnModeration ? "true" : "false");

            string prevClientId = (_general.Get<string>(AppSettingsKeys.General.DiscordRpcClientId, string.Empty) ?? string.Empty).Trim();
            string nextClientId = DiscordRpcClientId.Trim();
            _general.Set(AppSettingsKeys.General.DiscordRpcEnabled, DiscordRpcEnabled ? "true" : "false");
            _general.Set(AppSettingsKeys.General.DiscordRpcClientId, nextClientId);
            _general.Set(AppSettingsKeys.General.DiscordRpcShowGame, DiscordRpcShowGame ? "true" : "false");
            _general.Set(AppSettingsKeys.General.DiscordRpcJoinCurrentServer, DiscordRpcJoinCurrentServer ? "true" : "false");
            _effects.ApplyDiscordRpcSettings(DiscordRpcEnabled, !string.Equals(prevClientId, nextClientId, StringComparison.Ordinal));

            _general.Set(AppSettingsKeys.General.AutoReconnect, EnableAutoReconnect ? "true" : "false");
            _general.Set(AppSettingsKeys.General.EnableTokenDeElevation, EnableTokenDeElevation ? "true" : "false");
            _general.Set(AppSettingsKeys.General.StreamerModeAutoDetect, StreamerModeAutoDetect ? "true" : "false");
            _general.Set(AppSettingsKeys.General.AntiAfkForegroundFallback, AntiAfkForegroundFallback ? "true" : "false");

            AutoReconnectService.Instance.IsEnabled = EnableAutoReconnect;
            StreamerModeService.AutoDetectEnabled = StreamerModeAutoDetect;

            ISettingsService? watcher = _settings.GetSection(AppSettingsKeys.Sections.Watcher) ?? _settings.IniFile.Section(AppSettingsKeys.Sections.Watcher);
            _watcherConfig.SaveAdvancedSettings(CloseIfWindowTitle, ExpectedWindowTitle, NoConnectionTimeout, watcher);

            _general.Set("EnableAntiAfk", AntiAfk ? "true" : "false");
            int clampedBgFps = BackgroundTargetFps.HasValue ? Math.Clamp((int)BackgroundTargetFps.Value, 1, 60) : 10;
            _general.Set(AppSettingsKeys.General.EnableBackgroundThrottling, EnableBackgroundThrottling ? "true" : "false");
            _general.Set(AppSettingsKeys.General.BackgroundTargetFps, clampedBgFps.ToString(CultureInfo.InvariantCulture));
            _effects.ConfigureBackgroundThrottling(EnableBackgroundThrottling, clampedBgFps);
        }

        private async Task ExportVaultAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            try { await _effects.ExportVaultAsync(); }
            catch (Exception ex) { _dialogs.ShowError(ex.Message, LocalizationService.Get("Status_Error")); }
            finally { IsBusy = false; }
        }

        private async Task ImportVaultAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            try { await _effects.ImportVaultAsync(); }
            catch (CryptographicException) { _dialogs.ShowError(LocalizationService.Get("Settings.ImportVaultWrongPassword"), LocalizationService.Get("Status_Error")); }
            catch (InvalidDataException) { _dialogs.ShowError(LocalizationService.Get("Settings.ImportVaultInvalidFile"), LocalizationService.Get("Status_Error")); }
            catch (Exception ex) { _dialogs.ShowError(ex.Message, LocalizationService.Get("Status_Error")); }
            finally { IsBusy = false; }
        }

        private void OnStreamerModeStateChanged(bool _)
        {
            bool enabled = StreamerModeService.IsEnabled;
            if (_hideUsernames != enabled)
                SetProperty(ref _hideUsernames, enabled, nameof(HideUsernames));
        }

        private void CleanupAdvanced()
        {
            if (_streamerStateChangedHandler != null)
            {
                StreamerModeService.StateChanged -= _streamerStateChangedHandler;
                _streamerStateChangedHandler = null;
            }
            _feedbackCts?.Cancel();
            _feedbackCts?.Dispose();
            _feedbackCts = null;
        }
    }
}
