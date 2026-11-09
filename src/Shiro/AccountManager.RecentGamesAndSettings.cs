#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager
    {
        private static string RecentGamesFilePath => StoragePaths.RecentGamesFile;

        private static readonly Timer _deferredSettingsSaveTimer = new Timer(_ =>
        {
            try { SaveSettings(); }
            catch (Exception ex) { Program.Logger?.Error($"[DeferredSettingsSave] Error saving settings: {ex.Message}"); }
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
                Program.Logger?.Warn($"Failed to schedule deferred settings save: {ex.Message}");
            }
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

        private async Task LoadRecentGames()
        {
            lock (rgSaveLock)
            {
                AppServices.ServerBrowserState.RecentGames = new List<Game>();
            }

            if (File.Exists(RecentGamesFilePath))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(RecentGamesFilePath).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        List<Game>? Games = Utilities.FastJson.TryDeserialize<List<Game>>(json, out var gList) ? gList : null;
                        if (Games != null && Games.Count > 0)
                        {
                            await Task.WhenAll(Games.Where(g => g != null).Select(g => g.WaitForDetails())).ConfigureAwait(false);
                            foreach (Game RG in Games)
                            {
                                if (RG != null)
                                    await AddRecentGame(RG, true).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Warn($"Failed to load recent games: {ex.Message}");
                }
            }
        }

        private async Task AddRecentGame(Game RG, bool Loading = false)
        {
            if (RG == null) return;
            await RG.WaitForDetails().ConfigureAwait(false);

            int maxRecentGames = AppSettings.General.Exists("MaxRecentGames") ? AppSettings.General.Get<int>("MaxRecentGames") : 10;
            string? serialized = null;

            lock (rgSaveLock)
            {
                AppServices.ServerBrowserState.RecentGames ??= new List<Game>();
                AppServices.ServerBrowserState.RecentGames.RemoveAll(g => g?.Details?.placeId == RG.Details?.placeId);

                while (AppServices.ServerBrowserState.RecentGames.Count > maxRecentGames && AppServices.ServerBrowserState.RecentGames.Count > 0)
                {
                    AppServices.ServerBrowserState.RecentGames.RemoveAt(0);
                }

                AppServices.ServerBrowserState.RecentGames.Add(RG);

                if (!Loading)
                {
                    serialized = Utilities.FastJson.Serialize(AppServices.ServerBrowserState.RecentGames, indented: true);
                    StoragePaths.AtomicWriteText(RecentGamesFilePath, serialized);
                }
            }

            if (!Loading)
            {
                this.InvokeIfRequired(() => RecentGameAdded?.Invoke(this, new GameArgs(RG)));
            }
        }

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
                    BeginInvoke(() =>
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
                bridge.CloseAllRobloxFromUiHandler = () => InvokeIfRequired(() => ModernCloseRoblox());
                bridge.LaunchAppHomeHandler = ModernLaunchAccountAppHome;
                bridge.JoinServerHandler = (accounts, placeId, jobId) => ModernJoinServerDirect(new List<Account>(accounts), placeId, jobId);
                bridge.MultiLaunchHomeHandler = () => ModernMultiLaunch();
                bridge.GetCurrentSelectedAccountsHandler = GetCurrentSelectedAccounts;
                bridge.SaveSettingsHandler = SaveSettings;
                bridge.QueueDeferredSaveSettingsHandler = () => QueueDeferredSaveSettings();
                bridge.SetDashboardStatusHandler = SetDashboardStatus;
                bridge.EnsureBackendInitializedHandler = EnsureBackendInitialized;
                bridge.InitializePostStartupHandler = InitializePostStartup;
                bridge.RestartWebServerHandler = RestartWebServerAsync;
                bridge.StopWebServerHandler = StopWebServerAsync;
                bridge.SetAutoCookieRefreshEnabledHandler = SetAutoCookieRefreshEnabled;
                bridge.ResetEncryptionHandler = Persistence.ResetEncryptionAsync;
            }
        }

        static AccountManager()
        {
            if (AppServices.HostUiBridge is HostUiBridgeService hostBridge) hostBridge.EnsureBackendInitializedHandler = EnsureBackendInitialized;
            if (AppServices.ServerBrowserState is ServerBrowserStateService sbState) sbState.LoadClientVersionDelegate = LoadClientVersionAsync;
        }

        public static async Task LoadClientVersionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                string versionJson = await SharedHttpClient.GetStringAsync(
                    "https://clientsettings.roblox.com/v1/client-version/WindowsPlayer",
                    retryTransient: true,
                    cancellationToken: cancellationToken);

                using var vDoc = System.Text.Json.JsonDocument.Parse(versionJson);
                if (vDoc.RootElement.TryGetProperty("clientVersionUpload", out var token))
                    AppServices.ServerBrowserState.CurrentClientVersion = token.GetString() ?? string.Empty;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown cancelled the optional version probe.
            }
            catch (Exception exception)
            {
                Program.Logger?.Debug($"Roblox client version probe failed: {exception.Message}");
            }
        }
    }
}
