#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager
    {
        private static readonly Timer _deferredSettingsSaveTimer = new Timer(_ =>
        {
            try { SaveSettings(); }
            catch (Exception ex) { AppServices.Logger.Error($"[DeferredSettingsSave] Error saving settings: {ex.Message}"); }
        }, null, Timeout.Infinite, Timeout.Infinite);

        internal static void QueueDeferredSaveSettings(int delayMs = 500)
        {
            try
            {
                _deferredSettingsSaveTimer.Change(Math.Max(50, delayMs), Timeout.Infinite);
            }
            catch (ObjectDisposedException) { /* timer disposed during shutdown */ }
            catch (Exception ex)
            {
                AppServices.Logger.Warn($"Failed to schedule deferred settings save: {ex.Message}");
            }
        }

        internal static void CancelDeferredSaveSettings()
        {
            SafeExecuteHelper.Try(() => _deferredSettingsSaveTimer.Change(Timeout.Infinite, Timeout.Infinite));
        }

        internal static void SaveSettings()
        {
            AccountManager? instance = Instance;
            if (instance?.settingsService != null)
            {
                instance.settingsService.Save();
                return;
            }

            AppSettings.ActiveIniFile.Save(StoragePaths.SettingsFile);
        }

        private Task LoadRecentGames() =>
            GameInfoService.LoadRecentGamesAsync(rg => AppDispatcher.Post(() => RecentGameAdded?.Invoke(this, new GameArgs(rg))));

        private Task AddRecentGame(Game RG, bool Loading = false) =>
            GameInfoService.AddRecentGameAsync(RG, Loading, rg => AppDispatcher.Post(() => RecentGameAdded?.Invoke(this, new GameArgs(rg))));

        private void WireStateAndHostBridgeServices()
        {
            // AppSettings.ActiveIniFile already configured
            if (AppServices.ServerBrowserState is ServerBrowserStateService sbState)
                sbState.LoadClientVersionDelegate = LoadClientVersionAsync;

            if (AppServices.HostUiBridge is HostUiBridgeService bridge)
            {
                bridge.RefreshAccountViewHandler = account =>
                {
                    if (account != null)
                        UpdateAccountView(account);
                    else
                        RefreshView();
                };
                bridge.UpdateAccountValidityOnUiHandler = (account, valid) =>
                {
                    AppDispatcher.Post(() =>
                    {
                        UpdateAccountValidity(account, valid);
                        RefreshView(account);
                    });
                };
                bridge.SchedulePresenceRefreshHandler = account =>
                {
                    if (account != null)
                        SchedulePresenceRefresh(account);
                };
                bridge.SignalNextLaunchAccountHandler = () => NextAccount();
                bridge.CancelLaunchingHandler = () => CancelLaunching();
                bridge.UpdateMultiRobloxHandler = () => UpdateMultiRoblox();
                bridge.CloseAllRobloxFromUiHandler = () => AppDispatcher.Post(() => ModernCloseRoblox());
                bridge.LaunchAppHomeHandler = ModernLaunchAccountAppHome;
                bridge.JoinServerHandler = (accounts, placeId, jobId) => ModernJoinServerDirect(new List<Account>(accounts), placeId, jobId);
                bridge.MultiLaunchHomeHandler = () => ModernMultiLaunch();
                bridge.GetCurrentSelectedAccountsHandler = GetCurrentSelectedAccounts;
                bridge.SaveSettingsHandler = SaveSettings;
                bridge.QueueDeferredSaveSettingsHandler = () => QueueDeferredSaveSettings();
                bridge.SetDashboardStatusHandler = SetDashboardStatus;
                bridge.EnsureBackendInitializedHandler = EnsureBackendInitialized;
                bridge.ShutdownBackendHandler = ShutdownBackend;
                bridge.InitializePostStartupHandler = InitializePostStartup;
                bridge.RestartWebServerHandler = RestartWebServerAsync;
                bridge.StopWebServerHandler = StopWebServerAsync;
                bridge.SetAutoCookieRefreshEnabledHandler = SetAutoCookieRefreshEnabled;
                bridge.ResetEncryptionHandler = Persistence.ResetEncryptionAsync;
                bridge.ExecuteApiLaunchHandler = ExecuteApiLaunchViaCoordinatorAsync;
            }
        }

        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void RegisterModuleHooks()
        {
            AppServices.OnInitialized = WireStaticBridgeDelegates;
        }

        static AccountManager() => WireStaticBridgeDelegates();

        private static void WireStaticBridgeDelegates()
        {
            if (AppServices.HostUiBridge is HostUiBridgeService hostBridge) hostBridge.EnsureBackendInitializedHandler = EnsureBackendInitialized;
            if (AppServices.ServerBrowserState is ServerBrowserStateService sbState) sbState.LoadClientVersionDelegate = LoadClientVersionAsync;
        }

        public static Task LoadClientVersionAsync(CancellationToken cancellationToken = default) =>
            GameInfoService.LoadClientVersionAsync(cancellationToken);
    }
}
