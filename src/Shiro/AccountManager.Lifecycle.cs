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
        private bool _backendServicesInitialized = false;

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

            // Build the shared API boundary before child forms are created.
            // Previously ServerList constructed its own RestClients in its
            // constructor, which made timeout/proxy configuration diverge
            // from the rest of the application.
            apiClient = new RobloxApiClient();
            MainClient = apiClient.Main;
            AvatarClient = apiClient.Avatar;
            AuthClient = apiClient.Auth;
            EconClient = apiClient.Economy;
            AccountClient = apiClient.Account;
            GameJoinClient = apiClient.GameJoin;
            UsersClient = apiClient.Users;
            FriendsClient = apiClient.Friends;
            ThumbnailsClient = apiClient.Thumbnails;
            GamesClient = apiClient.Games;
            DevelopClient = apiClient.Develop;
            PresenceClient = apiClient.Presence;
            ApiClient = apiClient.Api;

            CurrentPlaceId = General.Exists("SavedPlaceId") ? (General.Get("SavedPlaceId") ?? "5315046213") : "5315046213";

            try
            {
                if (Developer.Get<bool>("EnableWebServer"))
                {
                    string PortStr = WebServer.Exists("WebServerPort") ? (WebServer.Get("WebServerPort") ?? "7963") : "7963";
                    int Port = int.TryParse(PortStr, out int p) ? p : 7963;
                    bool AllowExternal = WebServer.Get<bool>("AllowExternalConnections");

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

            if (General.Get<bool>("ShuffleJobId"))
                ShuffleJobID = true;

            if (General.Get<bool>("AutoCookieRefresh"))
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
                enabled: General.Get<bool>("ShowPresence"));

            if (General.Get<bool>("ShowPresence"))
                _ = ObserveBackgroundTaskAsync(UpdatePresence(), "initial presence refresh");
        }

        public void InitializePostStartup()
        {
            bool autoClose = General.Exists("AutoCloseRobloxOnLaunch") ? General.Get<bool>("AutoCloseRobloxOnLaunch") : false;
            if (autoClose)
            {
                ModernCloseRoblox(notify: false);
            }

            if (General.Get<bool>("EnableMultiRbx"))
                TryEnableMultiRoblox(promptIfRobloxRunning: false, notify: false);

            if (General.Exists("EnableAntiAfk") && General.Get<bool>("EnableAntiAfk"))
                AntiAfkService.SetConfig(true, General.Exists("AntiAfkIntervalMinutes") ? General.Get<int>("AntiAfkIntervalMinutes") : 8);

            if ((General.Exists("AutoTrimMemory") && General.Get<bool>("AutoTrimMemory")) ||
                General.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming, false))
                MemoryOptimizer.ConfigureAutoTrim(true, General.Exists("AutoTrimIntervalMinutes") ? General.Get<int>("AutoTrimIntervalMinutes") : 1);

            if (General.Exists(AppSettingsKeys.General.AutoMuteBackgroundClients) && General.Get<bool>(AppSettingsKeys.General.AutoMuteBackgroundClients))
                RobloxAudioService.ConfigureSmartMute(true);

            if (General.Exists(AppSettingsKeys.General.MaxTotalRobloxCpuPercent))
            {
                int cpuCap = General.Get<int>(AppSettingsKeys.General.MaxTotalRobloxCpuPercent, 0);
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
            PuppeteerSupported = !General.Get<bool>("UseEmbeddedBrowser");

            if (PuppeteerSupported && AccountBrowser.FindSystemBrowserExecutable() == null && AccountBrowser.FindCachedChromiumExecutable() == null)
            {
                Program.Logger?.Warn("No Chrome/Edge installation was found; browser-based sign-in will be unavailable until one is installed.");
                SetDashboardStatus("" + AccountBrowser.BrowserNotFoundMessage);
            }

            if (AccountControl.Get<bool>("StartOnLaunch"))
                ModernOpenAccountControl();

            OfferResumeReloginQueue();
        }

        // Offers to continue a batch re-login that was interrupted (dialog or app closed mid-way).
        private void OfferResumeReloginQueue()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // Wait for startup validation so already-recovered accounts are skipped.
                    await Task.Delay(12000, lifetimeCancellation.Token).ConfigureAwait(false);
                    var pendingIds = ReloginQueueStore.Load();
                    if (pendingIds.Count == 0) return;

                    var pending = GetAccountsSnapshot()
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
                                Shiro.Services.LocalizationService.Format("Auto_AccountManagerLi_009", pending.Count),
                                Shiro.Services.LocalizationService.Get("Auto_AccountManagerLi_010"));
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
                SaveAccounts(true);
            }
            catch (Exception ex)
            {
                Program.Logger.Error($"Error saving accounts on close: {ex}");
            }

            try
            {
                if (!string.IsNullOrEmpty(CurrentPlaceId))
                {
                    General?.Set("SavedPlaceId", CurrentPlaceId);
                }
                settingsService?.Save();
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"Settings save on shutdown threw: {ex.Message}");
            }

            SafeExecuteHelper.Try(StoragePaths.ClearCachedVaultKey);

            shutdownFinalized = true;

            try
            {
                if (Avalonia.Application.Current == null)
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
