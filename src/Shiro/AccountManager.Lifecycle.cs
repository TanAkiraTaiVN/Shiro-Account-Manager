#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PuppeteerSharp;
using PuppeteerSharp.BrowserData;
using Shiro.Classes;
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
                string AFN = Path.Combine(Directory.GetCurrentDirectory(), "Auto Update.exe");
                string AU2FN = Path.Combine(Directory.GetCurrentDirectory(), "AU.exe");

                if (File.Exists(AFN)) File.Delete(AFN);
                if (File.Exists(AU2FN)) File.Delete(AU2FN);

                DirectoryInfo UpdateDir = new DirectoryInfo(Path.Combine(Environment.CurrentDirectory, "Update"));

                if (UpdateDir.Exists)
                    UpdateDir.RecursiveDelete();
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Old updater cleanup threw: {ex.Message}");
            }

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
                LoadClientVersionAsync(lifetimeCancellation.Token),
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
                enabled: AppSettings.General.Get<bool>("ShowPresence"));

            if (AppSettings.General.Get<bool>("ShowPresence"))
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

            StreamerModeService.Initialize();
            DiscordRpcService.Default.Start();
            DiscordRpcService.Default.UpdateCurrentPresence();

            try { ClientSettingsPatcher.PatchSettings(); }
            catch (Exception ex) { Program.Logger?.Warn($"PatchSettings failed: {ex.Message}"); }

            // Use installed Chrome/Chromium through CDP by default. The
            // compatibility flag is retained for existing settings files and
            // now selects the maintained WebView2 runtime.
            PuppeteerSupported = !AppSettings.General.Get<bool>("UseEmbeddedBrowser");

            if (PuppeteerSupported && AccountBrowser.FindSystemBrowserExecutable() == null && AccountBrowser.FindCachedChromiumExecutable() == null)
            {
                Program.Logger?.Warn("No Chrome/Edge installation was found; browser-based sign-in will be unavailable until one is installed.");
                SetDashboardStatus("" + AccountBrowser.BrowserNotFoundMessage);
            }

            if (AppSettings.AccountControl.Get<bool>("StartOnLaunch"))
                ModernOpenAccountControl();

            OfferResumeReloginQueue();
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
                    await Task.Delay(OfferResumeDelayMilliseconds, lifetimeCancellation.Token).ConfigureAwait(false);
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

                    AppDispatcher.Post(() =>
                    {
                        try
                        {
                            bool resume = AppServices.Dialogs.ShowConfirmation(
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
                    });
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
            if (shutdownFinalized)
                return;

            shutdownRequested = true;
            Persistence.IsShutdownRequested = true;

            SafeExecuteHelper.Try(CancelLaunching);
            SafeExecuteHelper.Try(() => launchCoordinator?.Dispose());
            SafeExecuteHelper.Try(() => lifetimeCancellation?.Cancel());
            SafeExecuteHelper.Try(() => AutoCookieRefresh?.Stop());
            SafeExecuteHelper.Try(() => PresenceTimer?.Stop());
            SafeExecuteHelper.Try(() => AutoCookieRefresh?.Dispose());
            SafeExecuteHelper.Try(() => PresenceTimer?.Dispose());
            SafeExecuteHelper.Try(RobloxSingletonService.Stop);
            SafeExecuteHelper.Try(AntiAfkService.Stop);
            SafeExecuteHelper.Try(() => RobloxAudioService.ConfigureSmartMute(false));
            SafeExecuteHelper.Try(RobloxWatcher.Stop);
            SafeExecuteHelper.Try(() => MemoryOptimizer.ConfigureAutoTrim(false));
            SafeExecuteHelper.Try(RobloxJobObjectService.Close);
            SafeExecuteHelper.Try(RobloxWindowService.ShowAllWindows);
            SafeExecuteHelper.Try(() => { if (AltManagerWS != null) _ = AltManagerWS.StopAsync(); });
            SafeExecuteHelper.Try(() => RobloxDirectLauncher.RestoreOriginalCookies());
            SafeExecuteHelper.Try(() => AccountRuntimeStateStore.Flush());
            SafeExecuteHelper.Try(DiscordRpcService.Default.Dispose);

            try
            {
                Persistence.SaveAccounts(bypassRateLimit: true);
            }
            catch (Exception ex)
            {
                Program.Logger.Error($"Error saving accounts on close: {ex}");
            }

            try
            {
                if (!string.IsNullOrEmpty(AppServices.ServerBrowserState.CurrentPlaceId))
                {
                    AppSettings.General.Set("SavedPlaceId", AppServices.ServerBrowserState.CurrentPlaceId);
                }
                settingsService?.Save();
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"Settings save on shutdown threw: {ex.Message}");
            }

            SafeExecuteHelper.Try(StoragePaths.ClearCachedVaultKey);
            SafeExecuteHelper.Try(RobloxHbaService.ClearKeyCache);

            shutdownFinalized = true;

            try
            {
                if (!SuppressExitOnShutdown && Avalonia.Application.Current == null)
                {
                    Environment.Exit(0);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Environment.Exit threw: {ex.Message}");
            }
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
            if (AltManagerWS != null)
            {
                try
                {
                    await AltManagerWS.StopAsync().ConfigureAwait(false);
                    await AltManagerWS.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[AccountManager] StopWebServerAsync error: {ex.Message}");
                }
                finally
                {
                    AltManagerWS = null;
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
