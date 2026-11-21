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
        internal static void QueueDeferredSaveSettings(int delayMs = 500)
        {
            if (Instance?.shutdownRequested == true)
                return;

            AppServices.EnsureInitialized();
            AppServices.CurrentBootstrap?.QueueDeferredSettingsSave(SaveSettings, delayMs);
        }

        internal static void CancelDeferredSaveSettings()
        {
            AppServices.CurrentBootstrap?.CancelDeferredSettingsSave();
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

        internal void WireStateAndHostBridgeServices()
        {
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

        private void ClearHostUiBridgeHandlersIfOwned()
        {
            if (AppServices.HostUiBridge is not HostUiBridgeService bridge || !ReferenceEquals(bridge.ShutdownBackendHandler?.Target, this)) return;
            bridge.SchedulePresenceRefreshHandler = null; bridge.ExecuteApiLaunchHandler = null; bridge.LaunchAppHomeHandler = null;
            bridge.JoinServerHandler = null; bridge.MultiLaunchHomeHandler = null; bridge.GetCurrentSelectedAccountsHandler = null;
            bridge.InitializePostStartupHandler = null; bridge.ShutdownBackendHandler = null;
        }

        internal static void WireHostServices(IServiceProvider? services = null)
        {
            var bridge = services?.GetService(typeof(IHostUiBridgeService)) as IHostUiBridgeService ?? AppServices.HostUiBridge;
            if (bridge is HostUiBridgeService hostBridge)
            {
                hostBridge.EnsureBackendInitializedHandler = EnsureBackendInitialized;
                hostBridge.SaveSettingsHandler = SaveSettings;
                hostBridge.QueueDeferredSaveSettingsHandler = () => QueueDeferredSaveSettings();
            }

            var sbState = services?.GetService(typeof(IServerBrowserStateService)) as IServerBrowserStateService ?? AppServices.ServerBrowserState;
            if (sbState is ServerBrowserStateService serverBrowserState)
            {
                serverBrowserState.LoadClientVersionDelegate = LoadClientVersionAsync;
            }
        }

        public static Task LoadClientVersionAsync(CancellationToken cancellationToken = default) =>
            GameInfoService.LoadClientVersionAsync(cancellationToken);
    }
}
