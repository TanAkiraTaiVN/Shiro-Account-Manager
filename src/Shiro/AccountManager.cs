using Microsoft.Win32;
using PuppeteerSharp;
using PuppeteerSharp.BrowserData;
using Shiro.Classes;
using Shiro.Http;
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
using Shiro.Services;
using Shiro.AvaloniaUI.Views;



namespace Shiro
{
    public partial class AccountManager : IDisposable
    {
        public static AccountManager? Instance;
        internal static List<Account> AccountsList = new();
        public static List<Account> SelectedAccounts
        {
            get => SelectionStateService.Default.SelectedAccounts;
            set => SelectionStateService.Default.SelectedAccounts = value;
        }
        public static List<Game> RecentGames
        {
            get => ServerBrowserStateService.Default.RecentGames;
            set => ServerBrowserStateService.Default.RecentGames = value;
        }
        public static Account? SelectedAccount
        {
            get => SelectionStateService.Default.SelectedAccount;
            set => SelectionStateService.Default.SelectedAccount = value;
        }
        public static Account? LastValidAccount
        {
            get => SelectionStateService.Default.LastValidAccount;
            set => SelectionStateService.Default.LastValidAccount = value;
        }
        public static RestClient MainClient = null!;
        public static RestClient AvatarClient = null!;
        public static RestClient FriendsClient = null!;
        public static RestClient UsersClient = null!;
        public static RestClient AuthClient = null!;
        public static RestClient EconClient = null!;
        public static RestClient AccountClient = null!;
        public static RestClient GameJoinClient = null!;
        public static RestClient ThumbnailsClient = null!;
        public static RestClient GamesClient = null!;
        public static RestClient DevelopClient = null!;
        public static RestClient PresenceClient = null!;
        public static RestClient ApiClient = null!;

        public static string CurrentPlaceId
        {
            get => ServerBrowserStateService.Default.CurrentPlaceId;
            set => ServerBrowserStateService.Default.CurrentPlaceId = value;
        }

        public static string CurrentJobId
        {
            get => ServerBrowserStateService.Default.CurrentJobId;
            set => ServerBrowserStateService.Default.CurrentJobId = value;
        }

        public static List<ServerData> CachedServers => ServerBrowserStateService.Default.CachedServers;

        private readonly static DateTime startTime = DateTime.Now;
        public static bool ShuffleJobID
        {
            get => ServerBrowserStateService.Default.ShuffleJobId;
            set => ServerBrowserStateService.Default.ShuffleJobId = value;
        }
        private static bool PuppeteerSupported;
        public static string CurrentVersion
        {
            get => ServerBrowserStateService.Default.CurrentClientVersion ?? string.Empty;
            set => ServerBrowserStateService.Default.CurrentClientVersion = value;
        }
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
        private KestrelWebServer? AltManagerWS;
        internal static string WSPassword
        {
            get => ServerBrowserStateService.Default.WebSocketPassword ?? string.Empty;
            set => ServerBrowserStateService.Default.WebSocketPassword = value;
        }
        public Shiro.Services.ModernPeriodicTask? AutoCookieRefresh { get; private set; }
        public Shiro.Services.ModernPeriodicTask? PresenceTimer { get; private set; }
        private readonly System.Threading.Lock presenceTaskLock = new();
        private Task? presenceRefreshTask;
        private readonly CancellationTokenSource lifetimeCancellation = new CancellationTokenSource();
        private readonly SemaphoreSlim cookieRefreshGate = new SemaphoreSlim(1, 1);
        private Task? vcredistInstallTask;
        private Task? clientVersionTask;
        private Task? recentGamesTask;
        private bool shutdownRequested;
        private bool shutdownFinalized;

        private static IniFile _iniSettings = null!;
        public static IniFile IniSettings
        {
            get => _iniSettings;
            set { _iniSettings = value; AppSettings.ActiveIniFile = value; }
        }
        private static IniSection _general = null!;
        public static IniSection General
        {
            get => _general;
            set { _general = value; AppSettings.General = value; }
        }
        public static IniSection Developer = null!;
        public static IniSection WebServer = null!;
        public static IniSection AccountControl = null!;
        public static IniSection Watcher = null!;
        public static IniSection Prompts = null!;
        private readonly SettingsService settingsService;
        internal static AccountStore accountStore = null!;
        internal static readonly System.Threading.Lock AccountListLock = new();
        private readonly PresenceService presenceService;
        private readonly BrowserService browserService;
        private RobloxApiClient? apiClient;
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
                string? configured = General?.Get("AccountJoinDelay");
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

        public static void AddAccountThreadSafe(Account account)
        {
            if (account == null) return;
            lock (AccountListLock)
            {
                AccountsList ??= new List<Account>();
                AccountsList.Add(account);
                _cachedAccountsSnapshot = Array.Empty<Account>();
            }
        }

        internal static void ReplaceAccountsList(List<Account> accounts)
        {
            if (accounts != null)
            {
                foreach (var acc in accounts)
                {
                    if (acc != null)
                    {
                        if (string.IsNullOrWhiteSpace(acc.SecurityToken))
                        {
                            acc.Valid = false;
                        }
                        else if (acc.Valid && acc.HasExpiredShortLivedCookie())
                        {
                            acc.Valid = false;
                            acc.IsDirty = true;
                        }
                    }
                }
            }

            IReadOnlyList<Account> snapshot;
            lock (AccountListLock)
            {
                AccountsList = accounts ?? new List<Account>();
                _cachedAccountsSnapshot = Array.Empty<Account>();
                snapshot = GetAccountsSnapshot();
            }

            MainWindow.Instance?.RefreshAccounts(snapshot);
        }

        private static readonly System.Threading.Lock saveLock = new();
        private static DateTime lastSaveTime = DateTime.MinValue;
        private static readonly System.Threading.Lock rgSaveLock = new();
        public event EventHandler<GameArgs>? RecentGameAdded;

        internal ILaunchCoordinator launchCoordinator;
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
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Migrating legacy settings fields threw: {ex.Message}");
                    }
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
            WireStateAndHostBridgeServices();

            if (General.Exists("SavedPlaceId") && !string.IsNullOrWhiteSpace(General.Get("SavedPlaceId")))
            {
                CurrentPlaceId = General.Get("SavedPlaceId")?.Trim() ?? string.Empty;
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
                    if (!Utilities.YesNoPrompt("Shiro", "Shiro has detected you have a monitor larger than average", $"Would you like to keep the WindowScale setting of {Program.Scale:F2}?", false))
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
            if (!WebServer.Exists("Password")) WebServer.Set("Password", ""); else WSPassword = CryptoHelper.UnprotectSecretString(WebServer.Get("Password") ?? string.Empty);
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

        public void RefreshView(object? obj = null)
        {
            RefreshModernDashboard();
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
            Shiro.AvaloniaUI.Views.MainWindow.Instance?.RefreshAccounts(snapshot);
        }

        // Modern dashboard and Avalonia action entry points extracted to AccountManager.ModernActions.cs

        public static string ShowDialog(string text, string caption, string defaultText = "", bool big = false)
        {
            return Shiro.AvaloniaUI.Views.InputDialog.Prompt(text, caption, defaultText, big);
        }

        internal async Task ObserveBackgroundTaskAsync(Task? task, string operation)
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
                Program.Logger?.Warn($"Background operation '{operation}' failed: {exception.Message}");
            }
        }

        // Presence, MultiRoblox, Launch, Persistence, Accounts, WebServer, and Lifecycle extracted to partial classes
    }
}
