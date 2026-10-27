using Microsoft.Win32;
using PuppeteerSharp;
using PuppeteerSharp.BrowserData;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Http;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Services;
using RBX_Alt_Manager.AvaloniaUI.Views;



namespace RBX_Alt_Manager
{
    public partial class AccountManager : IDisposable
    {
        public static AccountManager Instance;
        public static List<Account> AccountsList;
        public static List<Account> SelectedAccounts;
        public static List<Game> RecentGames;
        public static Account SelectedAccount;
        public static Account LastValidAccount; // this is used for the Batch class since getting place details requires authorization, auto updates whenever an account is used
        public static RestClient MainClient;
        public static RestClient AvatarClient;
        public static RestClient FriendsClient;
        public static RestClient UsersClient;
        public static RestClient AuthClient;
        public static RestClient EconClient;
        public static RestClient AccountClient;
        public static RestClient GameJoinClient;
        public static RestClient ThumbnailsClient;
        public static RestClient GamesClient;
        public static RestClient DevelopClient;
        public static RestClient PresenceClient;
        public static RestClient ApiClient;
        private static string _currentPlaceId = string.Empty;
        private static string _currentJobId = string.Empty;

        public static string CurrentPlaceId
        {
            get
            {
                if (!string.IsNullOrEmpty(_currentPlaceId)) return _currentPlaceId;
                return General?.Exists("SavedPlaceId") == true ? General.Get("SavedPlaceId") : "5315046213";
            }
            set
            {
                _currentPlaceId = value ?? string.Empty;
            }
        }

        public static string CurrentJobId
        {
            get => _currentJobId ?? string.Empty;
            set => _currentJobId = value ?? string.Empty;
        }

        public static List<ServerData> CachedServers { get; } = new List<ServerData>();

        private readonly static DateTime startTime = DateTime.Now;
        public static bool ShuffleJobID = false;
        private static bool PuppeteerSupported;
        public static string CurrentVersion;
        public bool IsDisposed => shutdownFinalized;
        public bool IsHandleCreated => true;

        [LibraryImport("user32.dll")]
        private static partial int GetSystemMetrics(int nIndex);

        public void BeginInvoke(Action action)
        {
            if (action == null) return;
            AppDispatcher.Post(action);
        }

        public void InvokeIfRequired(Action action)
        {
            if (action == null) return;
            AppDispatcher.Post(action);
        }
        private KestrelWebServer AltManagerWS;
        private string WSPassword { get; set; }
        public RBX_Alt_Manager.Services.ModernPeriodicTask AutoCookieRefresh { get; private set; }
        public RBX_Alt_Manager.Services.ModernPeriodicTask PresenceTimer { get; private set; }
        private readonly System.Threading.Lock presenceTaskLock = new();
        private Task presenceRefreshTask;
        private readonly CancellationTokenSource lifetimeCancellation = new CancellationTokenSource();
        private readonly SemaphoreSlim cookieRefreshGate = new SemaphoreSlim(1, 1);
        private Task vcredistInstallTask;
        private Task clientVersionTask;
        private Task recentGamesTask;
        private bool shutdownRequested;
        private bool shutdownFinalized;

        public static IniFile IniSettings;
        public static IniSection General;
        public static IniSection Developer;
        public static IniSection WebServer;
        public static IniSection AccountControl;
        public static IniSection Watcher;
        public static IniSection Prompts;
        private readonly SettingsService settingsService;
        private static AccountStore accountStore;
        internal static readonly System.Threading.Lock AccountListLock = new();
        private readonly PresenceService presenceService;
        private readonly BrowserService browserService;
        private RobloxApiClient apiClient;
        private bool accountDataNeedsMigration;

        // Keep the value used by the launch countdown and the native launch
        // gate in one place.  Older builds had a second hardcoded 30-second
        // gate in Account.cs, which made smaller values in Settings appear to
        // be ignored.
        internal const int DefaultAccountJoinDelaySeconds = 8;
        internal const int MinimumAccountJoinDelaySeconds = 1;
        internal const int MaximumAccountJoinDelaySeconds = 3600;

        internal static int GetConfiguredAccountJoinDelaySeconds()
        {
            int delay = DefaultAccountJoinDelaySeconds;

            try
            {
                string configured = General?.Get("AccountJoinDelay");
                if (!int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out delay))
                    delay = DefaultAccountJoinDelaySeconds;
            }
            catch
            {
                delay = DefaultAccountJoinDelaySeconds;
            }

            return Math.Max(
                MinimumAccountJoinDelaySeconds,
                Math.Min(MaximumAccountJoinDelaySeconds, delay));
        }

        private static Account[] _cachedAccountsSnapshot = Array.Empty<Account>();

        internal static IReadOnlyList<Account> GetAccountsSnapshot()
        {
            lock (AccountListLock)
            {
                if (AccountsList == null || AccountsList.Count == 0)
                    return Array.Empty<Account>();

                Account[] cached = _cachedAccountsSnapshot;
                if (cached != null && cached.Length == AccountsList.Count)
                {
                    bool same = true;
                    for (int i = 0; i < cached.Length; i++)
                    {
                        if (!ReferenceEquals(cached[i], AccountsList[i]))
                        {
                            same = false;
                            break;
                        }
                    }
                    if (same)
                        return cached;
                }

                int nonNullCount = 0;
                for (int i = 0; i < AccountsList.Count; i++)
                {
                    if (AccountsList[i] != null)
                        nonNullCount++;
                }

                if (nonNullCount == 0)
                {
                    _cachedAccountsSnapshot = Array.Empty<Account>();
                    return _cachedAccountsSnapshot;
                }

                var result = new Account[nonNullCount];
                int idx = 0;
                for (int i = 0; i < AccountsList.Count; i++)
                {
                    var acc = AccountsList[i];
                    if (acc != null)
                        result[idx++] = acc;
                }

                _cachedAccountsSnapshot = result;
                return result;
            }
        }

        public static IReadOnlyList<Account> Accounts => GetAccountsSnapshot();

        public static int AccountCount
        {
            get
            {
                lock (AccountListLock) return AccountsList?.Count ?? 0;
            }
        }

        private static void ReplaceAccountsList(List<Account> accounts)
        {
            if (accounts != null)
            {
                foreach (var acc in accounts)
                {
                    if (acc != null && !string.IsNullOrWhiteSpace(acc.SecurityToken))
                    {
                        // Stored sessions remain valid by default (matching original RAM behavior).
                        // Accounts are never falsely invalidated on startup or without user action.
                        acc.Valid = true;
                        acc.AccountStatusIssue = string.Empty;
                    }
                    else if (acc != null && acc.Valid && acc.HasExpiredShortLivedCookie())
                    {
                        acc.Valid = false;
                        acc.IsDirty = true;
                    }
                }
            }

            lock (AccountListLock)
                AccountsList = accounts ?? new List<Account>();

            MainWindow.Instance?.RefreshAccounts(AccountsList);
        }

        private static readonly System.Threading.Lock saveLock = new();
        private static DateTime lastSaveTime = DateTime.MinValue;
        private static readonly System.Threading.Lock rgSaveLock = new();
        public event EventHandler<GameArgs> RecentGameAdded;

        public bool IsDownloadingChromium { get; private set; }
        private readonly ILaunchCoordinator launchCoordinator;
        // Roblox accepts the app protocol asynchronously. A safe stagger keeps
        // multi-account Home launches reliable and prevents Roblox from invalidating sessions due to rapid requests.
        private const int HomeLaunchStaggerMilliseconds = 2500;

        private static readonly byte[] Entropy = new byte[] { 0x52, 0x4f, 0x42, 0x4c, 0x4f, 0x58, 0x20, 0x41, 0x43, 0x43, 0x4f, 0x55, 0x4e, 0x54, 0x20, 0x4d, 0x41, 0x4e, 0x41, 0x47, 0x45, 0x52, 0x20, 0x7c, 0x20, 0x3a, 0x29, 0x20, 0x7c, 0x20, 0x42, 0x52, 0x4f, 0x55, 0x47, 0x48, 0x54, 0x20, 0x54, 0x4f, 0x20, 0x59, 0x4f, 0x55, 0x20, 0x42, 0x55, 0x59, 0x20, 0x69, 0x63, 0x33, 0x77, 0x30, 0x6c, 0x66 };

        public AccountManager()
        {
            Instance = this;

            if (File.Exists(StoragePaths.SettingsFile))
            {
                IniSettings = new IniFile(StoragePaths.SettingsFile);
                if (File.Exists(StoragePaths.LegacySettingsFile))
                {
                    try
                    {
                        var legacy = new IniFile(StoragePaths.LegacySettingsFile);
                        var gen = IniSettings.Section("General");
                        var legGen = legacy.Section("General");
                        if (legGen != null)
                        {
                            if (!gen.Exists("SavedPlaceId") && legGen.Exists("SavedPlaceId"))
                            {
                                gen.Set("SavedPlaceId", legGen.Get("SavedPlaceId"));
                            }
                            if (!gen.Exists("AccentTheme"))
                            {
                                gen.Set("AccentTheme", "Monochrome");
                            }
                        }
                    }
                    catch { }
                }
            }
            else if (File.Exists(StoragePaths.LegacySettingsFile))
            {
                IniSettings = new IniFile(StoragePaths.LegacySettingsFile);
                IniSettings.Save(StoragePaths.SettingsFile);
            }
            else
            {
                IniSettings = new IniFile();
            }
            settingsService = new SettingsService(IniSettings);
            accountStore = new AccountStore();
            presenceService = new PresenceService();
            browserService = new BrowserService();

            General = IniSettings.Section("General");
            Developer = IniSettings.Section("Developer");
            WebServer = IniSettings.Section("WebServer");
            AccountControl = IniSettings.Section("AccountControl");
            Watcher = IniSettings.Section("Watcher");
            Prompts = IniSettings.Section("Prompts");

            if (General.Exists("SavedPlaceId") && !string.IsNullOrWhiteSpace(General.Get("SavedPlaceId")))
            {
                _currentPlaceId = General.Get("SavedPlaceId").Trim();
            }

            if (!General.Exists("AccentTheme")) General.Set("AccentTheme", "Monochrome");
            if (!General.Exists("CheckForUpdates")) General.Set("CheckForUpdates", "true");
            if (!General.Exists("AccountJoinDelay"))
                General.Set("AccountJoinDelay", DefaultAccountJoinDelaySeconds.ToString(CultureInfo.InvariantCulture));
            if (!General.Exists("AsyncJoin")) General.Set("AsyncJoin", "false");
            if (!General.Exists("DisableAgingAlert")) General.Set("DisableAgingAlert", "false");
            if (!General.Exists("SavePasswords")) General.Set("SavePasswords", "true");
            if (!General.Exists("ServerRegionFormat")) General.Set("ServerRegionFormat", "<city>, <countryCode>", "Visit http://ip-api.com/json/1.1.1.1 to see available format options");
            if (!General.Exists("MaxRecentGames")) General.Set("MaxRecentGames", "8");
            if (!General.Exists("ShuffleChoosesLowestServer")) General.Set("ShuffleChoosesLowestServer", "false");
            if (!General.Exists("ShufflePageCount")) General.Set("ShufflePageCount", "5");
            if (!General.Exists("IPApiLink")) General.Set("IPApiLink", "http://ip-api.com/json/<ip>");
            if (!General.Exists("WindowScale"))
            {
                double screenHeight = OperatingSystem.IsWindows() ? GetSystemMetrics(1) : 1080;
                double screenWidth = OperatingSystem.IsWindows() ? GetSystemMetrics(0) : 1920;
                General.Set("WindowScale", screenHeight <= screenWidth ? Math.Max(Math.Min((float)(screenHeight / 1080.0), 2f), 1f).ToString(".0#", CultureInfo.InvariantCulture) : "1.0");

                if (Program.Scale > 1)
                    if (!Utilities.YesNoPrompt("Shiro", "RAM has detected you have a monitor larger than average", $"Would you like to keep the WindowScale setting of {Program.Scale:F2}?", false))
                        General.Set("WindowScale", "1.0");
                    else
                        AppServices.Dialogs.ShowInfo("In case the font scaling is incorrect, open RAMSettings.ini and change \"ScaleFonts=true\" to \"ScaleFonts=false\" without the quotes.");
            }
            if (!General.Exists("ScaleFonts")) General.Set("ScaleFonts", "true");
            if (!General.Exists("AutoCookieRefresh")) General.Set("AutoCookieRefresh", "true");
            if (!General.Exists("AutoCloseLastProcess")) General.Set("AutoCloseLastProcess", "true");
            if (!General.Exists("AutoCloseRobloxOnLaunch")) General.Set("AutoCloseRobloxOnLaunch", "false");
            if (!General.Exists("ShowPresence")) General.Set("ShowPresence", "true");
            if (!General.Exists("PresenceUpdateRate")) General.Set("PresenceUpdateRate", "5");
            // Multi-client support is the default for fresh installations so
            // the Launch selected workflow can open more than one Roblox
            // client without requiring a first-run settings change. An
            // existing user preference is preserved when the key is present.
            if (!General.Exists("EnableMultiRbx")) General.Set("EnableMultiRbx", "true");
            if (!General.Exists("UnlockFPS")) General.Set("UnlockFPS", "false");
            if (!General.Exists("MaxFPSValue")) General.Set("MaxFPSValue", "120");
            if (!General.Exists("EnablePotatoMode")) General.Set("EnablePotatoMode", "false");
            // Keep the native Roblox Home protocol as the default. Chrome is
            // still available as an optional compatibility workaround when
            // native authentication is rate-limited.
            if (!General.Exists("UseChromeHome")) General.Set("UseChromeHome", "false");
            // Optional per-browser proxy routing. Keep this off by default so
            // normal installations retain the direct/system network route.
            // When enabled, RobloxBrowserLogin reads proxies.txt from the
            // application folder and validates entries before launching Chrome.
            if (!General.Exists("UseProxies")) General.Set("UseProxies", "false");
            if (!General.Exists("ProxyTimeout")) General.Set("ProxyTimeout", "5000");
            if (!General.Exists("ProxyTestLimit")) General.Set("ProxyTestLimit", "10");
            if (!General.Exists(AppSettingsKeys.General.PreferDirectLaunch)) General.Set(AppSettingsKeys.General.PreferDirectLaunch, "false");
            if (!General.Exists(AppSettingsKeys.General.SmartFastLaunch)) General.Set(AppSettingsKeys.General.SmartFastLaunch, "true");

            if (!Developer.Exists("DevMode")) Developer.Set("DevMode", "false");
            if (!Developer.Exists("EnableWebServer")) Developer.Set("EnableWebServer", "false");

            if (!WebServer.Exists("WebServerPort")) WebServer.Set("WebServerPort", "7963");
            if (!WebServer.Exists("AllowGetCookie")) WebServer.Set("AllowGetCookie", "false");
            if (!WebServer.Exists("AllowGetAccounts")) WebServer.Set("AllowGetAccounts", "false");
            if (!WebServer.Exists("AllowLaunchAccount")) WebServer.Set("AllowLaunchAccount", "false");
            if (!WebServer.Exists("AllowAccountEditing")) WebServer.Set("AllowAccountEditing", "false");
            if (!WebServer.Exists("Password")) WebServer.Set("Password", ""); else WSPassword = WebServer.Get("Password");
            if (!WebServer.Exists("EveryRequestRequiresPassword")) WebServer.Set("EveryRequestRequiresPassword", "false");
            if (!WebServer.Exists("AllowExternalConnections")) WebServer.Set("AllowExternalConnections", "false");

            if (!AccountControl.Exists("AllowExternalConnections")) AccountControl.Set("AllowExternalConnections", "false");
            if (!AccountControl.Exists("RelaunchDelay")) AccountControl.Set("RelaunchDelay", "60");
            if (!AccountControl.Exists("LauncherDelayNumber")) AccountControl.Set("LauncherDelayNumber", "9");
            if (!AccountControl.Exists("NexusPort")) AccountControl.Set("NexusPort", "5242");

            ReplaceAccountsList(new List<Account>());
            SelectedAccounts = new List<Account>();
            launchCoordinator = new LaunchCoordinator(ExecuteLaunchRequestAsync);
            browserService = BrowserService.Default;

            if (!Prompts.Exists("VCPrompted") && PrerequisiteService.NeedsVCRedist())
                vcredistInstallTask = PrerequisiteService.InstallVCRedistAsync(lifetimeCancellation.Token);
        }

        public void RefreshView(object obj = null)
        {
            RefreshModernDashboard();
        }

        public static void EnsureBackendInitialized()
        {
            if (Instance == null)
            {
                _ = new AccountManager();
            }
            if (Instance != null)
            {
                Instance.InitializeBackendServices();
                Instance.LoadAccounts();
            }
        }

        private void EnableModernDashboard()
        {
            RefreshModernDashboard();

            if (General != null && General.Get<bool>("ShowPresence"))
                _ = UpdatePresence();
        }

        private void RefreshModernDashboard()
        {
            var snapshot = GetAccountsSnapshot();
            RBX_Alt_Manager.AvaloniaUI.Views.MainWindow.Instance?.RefreshAccounts(snapshot);
        }

        // Modern dashboard and Avalonia action entry points extracted to AccountManager.ModernActions.cs

        public static string ShowDialog(string text, string caption, string defaultText = "", bool big = false)
        {
            return RBX_Alt_Manager.AvaloniaUI.Views.InputDialog.Prompt(text, caption, defaultText, big);
        }

        private async Task ObserveBackgroundTaskAsync(Task task, string operation)
        {
            if (task == null)
                return;

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
            {
                // Normal during application shutdown.
            }
            catch (Exception exception)
            {
                try { Program.Logger.Warn($"Background operation '{operation}' failed: {exception.Message}"); } catch { }
            }
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
                    CurrentVersion = token.GetString();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown cancelled the optional version probe.
            }
            catch (Exception exception)
            {
                try { Program.Logger.Debug($"Roblox client version probe failed: {exception.Message}"); } catch { }
            }
        }

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
            catch { }

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

            // Legacy child forms are lazily instantiated on demand instead of allocating GDI handles at startup

            CurrentPlaceId = General.Exists("SavedPlaceId") ? General.Get("SavedPlaceId") : "5315046213";

            try
            {
                if (Developer.Get<bool>("EnableWebServer"))
                {
                    string PortStr = WebServer.Exists("WebServerPort") ? WebServer.Get("WebServerPort") : "7963";
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
                AutoCookieRefresh = new RBX_Alt_Manager.Services.ModernPeriodicTask(
                    TimeSpan.FromMinutes(5),
                    async token => await RefreshStaleCookiesAsync(token).ConfigureAwait(false),
                    enabled: true);
            }

            PresenceTimer = new RBX_Alt_Manager.Services.ModernPeriodicTask(
                TimeSpan.FromMinutes(GetPresenceRefreshMinutes()),
                async token =>
                {
                    if (shutdownRequested || token.IsCancellationRequested) return;
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
                General.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming, true))
                MemoryOptimizer.ConfigureAutoTrim(true, General.Exists("AutoTrimIntervalMinutes") ? General.Get<int>("AutoTrimIntervalMinutes") : 1);

            if (General.Exists(AppSettingsKeys.General.AutoMuteBackgroundClients) && General.Get<bool>(AppSettingsKeys.General.AutoMuteBackgroundClients))
                RobloxAudioService.ConfigureSmartMute(true);

            try { ClientSettingsPatcher.PatchSettings(); } catch { }

            // Use installed Chrome/Chromium through CDP by default. The
            // compatibility flag is retained for existing settings files and
            // now selects the maintained WebView2 runtime.
            PuppeteerSupported = !General.Get<bool>("UseEmbeddedBrowser");

            if (PuppeteerSupported && AccountBrowser.FindSystemBrowserExecutable() == null && !AccountBrowser.Fetcher.GetInstalledBrowsers().Any())
            {
                _ = ObserveBackgroundTaskAsync(DownloadChromiumAsync(), "Chromium download");
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
                                $"Lần đăng nhập lại hàng loạt trước chưa hoàn tất. Tiếp tục với {pending.Count} tài khoản còn lại?",
                                "Tiếp tục đăng nhập lại");
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
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[OfferResumeReloginQueue] {ex.Message}");
                }
            });
        }

        private async Task DownloadChromiumAsync()
        {
            IsDownloadingChromium = true;
            try
            {
                await AccountBrowser.Fetcher.DownloadAsync(Chrome.DefaultBuildId).ConfigureAwait(false);
                Program.Logger.Info("Bundled Chromium download completed.");
            }
            catch (Exception exception)
            {
                Program.Logger.Warn($"Could not download the bundled Chromium browser: {exception.Message}");
                if (!IsDisposed)
                    SetDashboardStatus("⚠️ Tải Chromium thất bại. Vui lòng cài Google Chrome hoặc thử lại từ Thêm tài khoản.");
            }
            finally
            {
                IsDownloadingChromium = false;
                RefreshModernDashboard();
            }
        }

        public void ShutdownBackend()
        {
            if (shutdownFinalized)
                return;

            shutdownRequested = true;

            try { CancelLaunching(); } catch { }
            try { launchCoordinator?.Dispose(); } catch { }
            try { lifetimeCancellation?.Cancel(); } catch { }
            try { AutoCookieRefresh?.Stop(); } catch { }
            try { PresenceTimer?.Stop(); } catch { }
            try { AutoCookieRefresh?.Dispose(); } catch { }
            try { PresenceTimer?.Dispose(); } catch { }
            try { RobloxSingletonService.Stop(); } catch { }
            try { AntiAfkService.Stop(); } catch { }
            try { RobloxAudioService.ConfigureSmartMute(false); } catch { }
            try { RobloxWatcher.Stop(); } catch { }
            try { MemoryOptimizer.ConfigureAutoTrim(false); } catch { }
            try { RobloxWindowService.ShowAllWindows(); } catch { }
            try { AltManagerWS?.StopAsync().GetAwaiter().GetResult(); } catch { }

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
            catch { }

            shutdownFinalized = true;

            try
            {
                if (Avalonia.Application.Current == null)
                {
                    Environment.Exit(0);
                }
            }
            catch { }
        }

        public void Close() => ShutdownBackend();

        public void Dispose()
        {
            ShutdownBackend();
            GC.SuppressFinalize(this);
        }

        // Presence, MultiRoblox, Launch, Persistence, Accounts, and WebServer extracted to partial classes
    }
}
