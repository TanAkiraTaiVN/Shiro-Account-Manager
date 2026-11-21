#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager
    {
        private static readonly object _initLock = new();
        private static bool _accountsInitialLoaded = false;
        private bool _backendServicesInitialized = false;

        public static void EnsureBackendInitialized()
        {
            lock (_initLock)
            {
                if (Instance == null)
                {
                    _ = new AccountManager();
                }
                if (Instance != null)
                {
                    WireHostServices();
                    Instance.WireStateAndHostBridgeServices();
                    Instance.InitializeBackendServices();
                    if (!_accountsInitialLoaded)
                    {
                        _accountsInitialLoaded = true;
                        Persistence.LoadAccounts();
                    }
                }
            }
        }

        public void InitializeBackendServices()
        {
            if (_backendServicesInitialized) return;
            _backendServicesInitialized = true;

            try
            {
                // Ensure any previous un-restored cookies backup is restored on startup
                RobloxDirectLauncher.RestoreOriginalCookies();
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Cookie restore on startup threw: {ex.Message}");
            }

            // Shared API boundary is owned by AppServices.RobloxApi.
            // Keeping RobloxApiClient as the single owner prevents independent client construction
            // and ensures consistent timeout/proxy configuration across the application.

            AppServices.ServerBrowserState.CurrentPlaceId = AppSettings.General.Exists("SavedPlaceId") ? (AppSettings.General.Get("SavedPlaceId") ?? "5315046213") : "5315046213";

            try
            {
                if (AppSettings.Developer.Get<bool>("EnableWebServer"))
                {
                    string PortStr = AppSettings.WebServer.Exists("WebServerPort") ? (AppSettings.WebServer.Get("WebServerPort") ?? "7963") : "7963";
                    int Port = int.TryParse(PortStr, out int p) ? p : 7963;
                    bool AllowExternal = AppSettings.WebServer.Get<bool>("AllowExternalConnections");

                    AltManagerWS = new KestrelWebServer(Port, AllowExternal, SendKestrelResponse, ValidateWebSocketAuth);
                    _ = ObserveBackgroundTaskAsync(AltManagerWS.StartAsync(), "Kestrel WebServer startup");
                }
            }
            catch (Exception x) { Program.Logger?.Error($"Failed to start webserver: {x}"); }

            clientVersionTask = ObserveBackgroundTaskAsync(
                LoadClientVersionAsync(SafeLifetimeToken),
                "Roblox client version probe");

            settingsService.Save();

            recentGamesTask = ObserveBackgroundTaskAsync(LoadRecentGames(), "recent games load");

            if (AppSettings.General.Get<bool>("ShuffleJobId"))
                AppServices.ServerBrowserState.ShuffleJobId = true;

            if (AppSettings.General.Get<bool>("AutoCookieRefresh"))
            {
                AutoCookieRefresh = new Shiro.Services.ModernPeriodicTask(
                    TimeSpan.FromMinutes(5),
                    async token => await RefreshStaleCookiesAsync(token).ConfigureAwait(false),
                    enabled: true);
            }

            PresenceTimer = new Shiro.Services.ModernPeriodicTask(
                TimeSpan.FromMinutes(GetPresenceRefreshMinutes()),
                async token =>
                {
                    if (shutdownRequested || token.IsCancellationRequested || Shiro.Services.RobloxHbaService.IsInteractiveLoginActive) return;
                    await UpdatePresence().ConfigureAwait(false);
                },
                enabled: AppSettings.General.Get<bool>("ShowPresence", true));

            if (AppSettings.General.Get<bool>("ShowPresence", true))
                _ = ObserveBackgroundTaskAsync(UpdatePresence(), "initial presence refresh");
        }

        public void InitializePostStartup()
        {
            bool autoClose = AppSettings.General.Exists("AutoCloseRobloxOnLaunch") ? AppSettings.General.Get<bool>("AutoCloseRobloxOnLaunch") : false;
            if (autoClose)
            {
                ModernCloseRoblox(notify: false);
            }

            if (AppSettings.General.Get<bool>("EnableMultiRbx"))
                TryEnableMultiRoblox(promptIfRobloxRunning: false, notify: false);
            else
                UpdateMultiRoblox();

            if (AppSettings.General.Exists("EnableAntiAfk") && AppSettings.General.Get<bool>("EnableAntiAfk"))
                AntiAfkService.SetConfig(true, AppSettings.General.Exists("AntiAfkIntervalMinutes") ? AppSettings.General.Get<int>("AntiAfkIntervalMinutes") : 8);

            if ((AppSettings.General.Exists("AutoTrimMemory") && AppSettings.General.Get<bool>("AutoTrimMemory")) ||
                AppSettings.General.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming, false))
                MemoryOptimizer.ConfigureAutoTrim(true, AppSettings.General.Exists("AutoTrimIntervalMinutes") ? AppSettings.General.Get<int>("AutoTrimIntervalMinutes") : 1);

            if (AppSettings.General.Exists(AppSettingsKeys.General.AutoMuteBackgroundClients) && AppSettings.General.Get<bool>(AppSettingsKeys.General.AutoMuteBackgroundClients))
                RobloxAudioService.ConfigureSmartMute(true);

            if (AppSettings.General.Exists(AppSettingsKeys.General.MaxTotalRobloxCpuPercent))
            {
                int cpuCap = AppSettings.General.Get<int>(AppSettingsKeys.General.MaxTotalRobloxCpuPercent, 0);
                if (cpuCap > 0)
                    RobloxJobObjectService.SetCpuRateLimit(cpuCap);
            }

            AppServices.SettingsSideEffects.ConfigureBackgroundThrottling(
                AppSettings.General.Get<bool>(AppSettingsKeys.General.EnableBackgroundThrottling, false),
                AppSettings.General.Get<int>(AppSettingsKeys.General.BackgroundTargetFps, 10));

            StreamerModeService.Initialize();
            DiscordRpcService.Default.Start();
            DiscordRpcService.Default.UpdateCurrentPresence();

            try { ClientSettingsPatcher.PatchSettings(); }
            catch (Exception ex) { Program.Logger?.Warn($"PatchSettings failed: {ex.Message}"); }

            OfferResumeReloginQueue();

            if (Avalonia.Application.Current != null && !AppSettings.Prompts.Exists("VCPrompted"))
                vcredistInstallTask = ObserveBackgroundTaskAsync(PrerequisiteService.PromptAndInstallVCRedistAsync(AppSettings.Prompts, (msg, title) => AppDispatcher.InvokeTaskAsync(() => AppServices.Dialogs.ShowConfirmationAsync(msg, title)), cancellationToken: SafeLifetimeToken), "VCRedist prerequisite prompt");
        }

        // Offers to continue a batch re-login that was interrupted (dialog or app closed mid-way).
        internal static int OfferResumeDelayMilliseconds = 12000;
        internal static bool SuppressExitOnShutdown;
        internal Task OfferResumeReloginQueue()
        {
            return Task.Run(async () =>
            {
                try
                {
                    // Wait for startup validation so already-recovered accounts are skipped.
                    await Task.Delay(OfferResumeDelayMilliseconds, SafeLifetimeToken).ConfigureAwait(false);
                    var pendingIds = ReloginQueueStore.Load();
                    if (pendingIds.Count == 0) return;

                    var pending = AppServices.AccountRepository.GetAll()
                        .Where(a => a != null && !a.Valid && !a.IsBannedOrLocked && pendingIds.Contains(a.UserID))
                        .ToList();
                    if (pending.Count == 0)
                    {
                        ReloginQueueStore.Clear();
                        return;
                    }

                    await AppDispatcher.InvokeTaskAsync(async () =>
                    {
                        try
                        {
                            bool resume = await AppServices.Dialogs.ShowConfirmationAsync(
                                Shiro.Services.LocalizationService.Format("AccountManager.ResumeBulkLoginPrompt", pending.Count),
                                Shiro.Services.LocalizationService.Get("AccountManager.ResumeBulkLoginTitle"));
                            if (!resume)
                            {
                                ReloginQueueStore.Clear();
                                return;
                            }

                            if (pending.Count == 1) AppServices.Dialogs.OpenFastLoginDialog(pending[0]);
                            else AppServices.Dialogs.OpenFastLoginDialogBatch(pending);
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"[OfferResumeReloginQueue] {ex.Message}");
                        }
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* canceled */ }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[OfferResumeReloginQueue] {ex.Message}");
                }
            });
        }

        public void ShutdownBackend()
        {
            lock (_shutdownLock)
            {
                if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
                    return;

                shutdownRequested = true;
                Persistence.IsShutdownRequested = true;

                try { BuildShutdownCoordinator().ExecuteAll(); }
                finally
                {
                    shutdownFinalized = true;
                    ClearHostUiBridgeHandlersIfOwned();
                }
            }

            try { if (!SuppressExitOnShutdown && !Program.Closed && Avalonia.Application.Current == null) Environment.Exit(0); }
            catch (Exception ex) { Program.Logger?.Debug($"Environment.Exit threw: {ex.Message}"); }
        }

        private ShutdownCoordinator BuildShutdownCoordinator()
        {
            var coordinator = new ShutdownCoordinator();
            coordinator.Register(ShutdownPhase.StopMonitors, "CancelLaunching", CancelLaunching);
            coordinator.Register(ShutdownPhase.StopMonitors, "LaunchCoordinator.Dispose", () => launchCoordinator?.Dispose());
            coordinator.Register(ShutdownPhase.StopMonitors, "LifetimeCancellation.Cancel", () => lifetimeCancellation?.Cancel());
            coordinator.Register(ShutdownPhase.StopMonitors, "AutoCookieRefresh.Stop", () => { SafeExecuteHelper.Try(() => AutoCookieRefresh?.Stop()); SafeExecuteHelper.Try(() => AutoCookieRefresh?.Dispose()); });
            coordinator.Register(ShutdownPhase.StopMonitors, "PresenceTimer.Stop", () => { SafeExecuteHelper.Try(() => PresenceTimer?.Stop()); SafeExecuteHelper.Try(() => PresenceTimer?.Dispose()); });
            coordinator.Register(ShutdownPhase.StopMonitors, "AutoReconnectService.StopForShutdown", AutoReconnectService.Instance.StopForShutdown);
            coordinator.Register(ShutdownPhase.StopMonitors, "StreamerModeService.Shutdown", StreamerModeService.Shutdown);
            coordinator.Register(ShutdownPhase.StopMonitors, "RobloxSingletonService.Stop", RobloxSingletonService.Stop);
            coordinator.Register(ShutdownPhase.StopMonitors, "AntiAfkService.Stop", AntiAfkService.Stop);
            coordinator.Register(ShutdownPhase.StopMonitors, "RobloxAudioService.Shutdown", RobloxAudioService.Shutdown);
            coordinator.Register(ShutdownPhase.StopMonitors, "RobloxWatcher.Stop", RobloxWatcher.Stop);
            coordinator.Register(ShutdownPhase.StopMonitors, "RobloxBackgroundThrottler.Disable", () => RobloxBackgroundThrottlerService.Instance.IsEnabled = false);
            coordinator.Register(ShutdownPhase.StopMonitors, "MemoryOptimizer.DisableAutoTrim", () => MemoryOptimizer.ConfigureAutoTrim(false));
            coordinator.Register(ShutdownPhase.StopMonitors, "Win32TrayHelper.CancelPendingRefresh", Win32TrayHelper.CancelPendingRefresh);
            coordinator.Register(ShutdownPhase.StopMonitors, "RobloxJobObjectService.Close", RobloxJobObjectService.Close);
            coordinator.Register(ShutdownPhase.StopMonitors, "RobloxWindowService.ShowAllWindows", () => RobloxWindowService.ShowAllWindows());
            coordinator.Register(ShutdownPhase.StopServersAndIntegrations, "KestrelWebServer.Dispose", () =>
            {
                if (Interlocked.Exchange(ref AltManagerWS, null) is { } ws)
                    Task.Run(async () => await ws.DisposeAsync().ConfigureAwait(false)).Wait(TimeSpan.FromSeconds(2));
            });
            coordinator.Register(ShutdownPhase.StopServersAndIntegrations, "WebhookNotificationService.Shutdown", () => WebhookNotificationService.Shutdown(1000));
            coordinator.Register(ShutdownPhase.StopServersAndIntegrations, "PresenceService.Dispose", () => presenceService?.Dispose());
            coordinator.Register(ShutdownPhase.StopServersAndIntegrations, "DiscordRpcService.Dispose", DiscordRpcService.Default.Dispose);
            coordinator.Register(ShutdownPhase.FlushPersistence, "RobloxDirectLauncher.RestoreOriginalCookies", () => RobloxDirectLauncher.RestoreOriginalCookies());
            coordinator.Register(ShutdownPhase.FlushPersistence, "AccountRuntimeStateStore.Shutdown", () => AccountRuntimeStateStore.Shutdown());
            coordinator.Register(ShutdownPhase.FlushPersistence, "Persistence.SaveAccounts", () =>
            {
                try { Persistence.SaveAccounts(bypassRateLimit: true); }
                catch (Exception ex) { Program.Logger.Error($"Error saving accounts on close: {ex}"); }
            });
            coordinator.Register(ShutdownPhase.FlushPersistence, "Persistence.StopDeferredSave", Persistence.StopDeferredSave);
            coordinator.Register(ShutdownPhase.FlushPersistence, "SettingsService.Save", () =>
            {
                try
                {
                    CancelDeferredSaveSettings();
                    if (!string.IsNullOrEmpty(AppServices.ServerBrowserState.CurrentPlaceId))
                        AppSettings.General.Set("SavedPlaceId", AppServices.ServerBrowserState.CurrentPlaceId);
                    settingsService?.Save();
                }
                catch (Exception ex) { Program.Logger?.Warn($"Settings save on shutdown threw: {ex.Message}"); }
            });
            coordinator.Register(ShutdownPhase.ClearSecretsAndPools, "StoragePaths.ClearCachedVaultKey", StoragePaths.ClearCachedVaultKey);
            coordinator.Register(ShutdownPhase.ClearSecretsAndPools, "RobloxHbaService.ClearKeyCache", RobloxHbaService.ClearKeyCache);
            coordinator.Register(ShutdownPhase.ClearSecretsAndPools, "ProxyBindingService.ClearClients", ProxyBindingService.ClearClients);
            coordinator.Register(ShutdownPhase.ClearSecretsAndPools, "SharedHttpClient.DisposeAll", SharedHttpClient.DisposeAll);
            coordinator.Register(ShutdownPhase.ClearSecretsAndPools, "LifetimeSync.Dispose", () => { SafeExecuteHelper.Try(() => lifetimeCancellation?.Dispose()); SafeExecuteHelper.Try(() => cookieRefreshGate?.Dispose()); });
            return coordinator;
        }

        public void SetAutoCookieRefreshEnabled(bool enabled)
        {
            if (enabled)
            {
                if (AutoCookieRefresh == null)
                {
                    AutoCookieRefresh = new Shiro.Services.ModernPeriodicTask(
                        TimeSpan.FromMinutes(5),
                        async token => await RefreshStaleCookiesAsync(token).ConfigureAwait(false),
                        enabled: true);
                }
            }
            else
            {
                if (AutoCookieRefresh != null)
                {
                    SafeExecuteHelper.Try(AutoCookieRefresh.Dispose);
                    AutoCookieRefresh = null;
                }
            }
        }

        public async Task RestartWebServerAsync(int port, bool allowExternal)
        {
            await StopWebServerAsync().ConfigureAwait(false);
            try
            {
                AltManagerWS = new KestrelWebServer(port, allowExternal, SendKestrelResponse, ValidateWebSocketAuth);
                await AltManagerWS.StartAsync().ConfigureAwait(false);
                Program.Logger?.Info($"[AccountManager] Kestrel WebServer started dynamically on port {port}.");
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[AccountManager] Dynamic WebServer startup failed: {ex.Message}");
                AltManagerWS = null;
            }
        }

        public async Task StopWebServerAsync()
        {
            KestrelWebServer? ws = Interlocked.Exchange(ref AltManagerWS, null);
            if (ws != null)
            {
                try
                {
                    await ws.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[AccountManager] StopWebServerAsync error: {ex.Message}");
                }
            }
        }

        public void Close() => ShutdownBackend();

        public void Dispose()
        {
            ShutdownBackend();
            GC.SuppressFinalize(this);
        }
    }
}
