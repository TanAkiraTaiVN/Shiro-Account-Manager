using Microsoft.Win32;
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

namespace Shiro
{
    public partial class AccountManager : IDisposable
    {
        public static AccountManager? Instance { get; internal set; }
        internal const string DefaultGeoIpLink = "https://ipwho.is/<ip>";
        internal const string LegacyGeoIpLink = "http://ip-api.com/json/<ip>";
        private static AccountPersistenceService Persistence => AppServices.GetRequiredService<AccountPersistenceService>();
        private static AccountRegistrationService Registration => AppServices.GetRequiredService<AccountRegistrationService>();

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

        private readonly SettingsService settingsService;
        internal static System.Threading.Lock AccountListLock => Persistence.AccountListLock;
        private readonly PresenceService presenceService;
        private readonly BrowserService browserService;

        // Keep the value used by the launch countdown and the native launch
        // gate in one place. Older builds had a second hardcoded 30-second
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
                string? configured = AppSettings.General.Get("AccountJoinDelay");
                if (!int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out delay))
                    delay = DefaultAccountJoinDelaySeconds;
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[AccountManager] GetConfiguredAccountJoinDelaySeconds failed", ex);
                delay = DefaultAccountJoinDelaySeconds;
            }

            return Math.Max(
                MinimumAccountJoinDelaySeconds,
                Math.Min(MaximumAccountJoinDelaySeconds, delay));
        }

        internal static void ReplaceAccountsList(List<Account> accounts) => Persistence.ReplaceAccountsList(accounts);

        internal static void ResetAccountsForTests(IEnumerable<Account> accounts)
        {
            ReplaceAccountsList(accounts.ToList());
        }

        private static readonly System.Threading.Lock rgSaveLock = new();
        public event EventHandler<GameArgs>? RecentGameAdded;

        internal ILaunchCoordinator launchCoordinator;
        // Roblox accepts the app protocol asynchronously. A safe stagger keeps
        // multi-account Home launches reliable and prevents Roblox from invalidating sessions due to rapid requests.
        private const int HomeLaunchStaggerMilliseconds = 2500;


        public AccountManager()
        {
            Instance = this;

            IniFile iniSettings;
            if (File.Exists(StoragePaths.SettingsFile))
            {
                iniSettings = new IniFile(StoragePaths.SettingsFile);
                if (File.Exists(StoragePaths.LegacySettingsFile))
                {
                    try
                    {
                        var legacy = new IniFile(StoragePaths.LegacySettingsFile);
                        var gen = iniSettings.Section("General");
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
                iniSettings = new IniFile(StoragePaths.LegacySettingsFile);
                iniSettings.Save(StoragePaths.SettingsFile);
            }
            else
            {
                iniSettings = new IniFile();
            }

            AppSettings.ActiveIniFile = iniSettings;
            settingsService = new SettingsService(iniSettings);
            Persistence.Store = new AccountStore();
            Persistence.IsShutdownRequested = false;
            presenceService = new PresenceService();
            browserService = new BrowserService();

            WireStateAndHostBridgeServices();

            if (AppSettings.General.Exists("SavedPlaceId") && !string.IsNullOrWhiteSpace(AppSettings.General.Get("SavedPlaceId")))
            {
                AppServices.ServerBrowserState.CurrentPlaceId = AppSettings.General.Get("SavedPlaceId")?.Trim() ?? string.Empty;
            }

            if (!AppSettings.General.Exists("AccentTheme")) AppSettings.General.Set("AccentTheme", "Monochrome");
            if (!AppSettings.General.Exists("CheckForUpdates")) AppSettings.General.Set("CheckForUpdates", "true");
            if (!AppSettings.General.Exists("AccountJoinDelay"))
                AppSettings.General.Set("AccountJoinDelay", DefaultAccountJoinDelaySeconds.ToString(CultureInfo.InvariantCulture));
            if (!AppSettings.General.Exists("AsyncJoin")) AppSettings.General.Set("AsyncJoin", "false");
            if (!AppSettings.General.Exists("DisableAgingAlert")) AppSettings.General.Set("DisableAgingAlert", "false");
            if (!AppSettings.General.Exists("SavePasswords")) AppSettings.General.Set("SavePasswords", "true");
            if (!AppSettings.General.Exists("ServerRegionFormat")) AppSettings.General.Set("ServerRegionFormat", "<city>, <countryCode>", "Visit https://ipwho.is/1.1.1.1 to see available format options");
            if (!AppSettings.General.Exists("MaxRecentGames")) AppSettings.General.Set("MaxRecentGames", "8");
            if (!AppSettings.General.Exists("ShuffleChoosesLowestServer")) AppSettings.General.Set("ShuffleChoosesLowestServer", "false");
            if (!AppSettings.General.Exists("ShufflePageCount")) AppSettings.General.Set("ShufflePageCount", "5");
            EnsureModernGeoIpLink(AppSettings.General);
            if (!AppSettings.General.Exists("WindowScale"))
            {
                double screenHeight = OperatingSystem.IsWindows() ? GetSystemMetrics(1) : 1080;
                double screenWidth = OperatingSystem.IsWindows() ? GetSystemMetrics(0) : 1920;
                AppSettings.General.Set("WindowScale", screenHeight <= screenWidth ? Math.Max(Math.Min((float)(screenHeight / 1080.0), 2f), 1f).ToString(".0#", CultureInfo.InvariantCulture) : "1.0");

                if (Program.Scale > 1)
                    if (!Utilities.YesNoPrompt("Shiro", "Shiro has detected you have a monitor larger than average", $"Would you like to keep the WindowScale setting of {Program.Scale:F2}?", false))
                        AppSettings.General.Set("WindowScale", "1.0");
                    else
                        AppServices.Dialogs.ShowInfo("In case the font scaling is incorrect, open RAMSettings.ini and change \"ScaleFonts=true\" to \"ScaleFonts=false\" without the quotes.");
            }
            if (!AppSettings.General.Exists("ScaleFonts")) AppSettings.General.Set("ScaleFonts", "true");
            if (!AppSettings.General.Exists("AutoCookieRefresh")) AppSettings.General.Set("AutoCookieRefresh", "true");
            if (!AppSettings.General.Exists("AutoCloseLastProcess")) AppSettings.General.Set("AutoCloseLastProcess", "true");
            if (!AppSettings.General.Exists("AutoCloseRobloxOnLaunch")) AppSettings.General.Set("AutoCloseRobloxOnLaunch", "false");
            if (!AppSettings.General.Exists("ShowPresence")) AppSettings.General.Set("ShowPresence", "true");
            if (!AppSettings.General.Exists("PresenceUpdateRate")) AppSettings.General.Set("PresenceUpdateRate", "5");
            // Multi-client support is the default for fresh installations so
            // the Launch selected workflow can open more than one Roblox
            // client without requiring a first-run settings change. An
            // existing user preference is preserved when the key is present.
            if (!AppSettings.General.Exists("EnableMultiRbx")) AppSettings.General.Set("EnableMultiRbx", "true");
            if (!AppSettings.General.Exists("UnlockFPS")) AppSettings.General.Set("UnlockFPS", "false");
            if (!AppSettings.General.Exists("MaxFPSValue")) AppSettings.General.Set("MaxFPSValue", "120");
            if (!AppSettings.General.Exists("EnablePotatoMode")) AppSettings.General.Set("EnablePotatoMode", "false");
            // Optional per-browser proxy routing. Keep this off by default so
            // normal installations retain the direct/system network route.
            // When enabled, RobloxBrowserLogin reads proxies.txt from the
            // application folder and validates entries before launching Chrome.
            if (!AppSettings.General.Exists("UseProxies")) AppSettings.General.Set("UseProxies", "false");
            if (!AppSettings.General.Exists("ProxyTimeout")) AppSettings.General.Set("ProxyTimeout", "5000");
            if (!AppSettings.General.Exists("ProxyTestLimit")) AppSettings.General.Set("ProxyTestLimit", "10");
            if (!AppSettings.General.Exists(AppSettingsKeys.General.PreferDirectLaunch)) AppSettings.General.Set(AppSettingsKeys.General.PreferDirectLaunch, "false");
            if (!AppSettings.General.Exists(AppSettingsKeys.General.SmartFastLaunch)) AppSettings.General.Set(AppSettingsKeys.General.SmartFastLaunch, "true");

            if (!AppSettings.Developer.Exists("DevMode")) AppSettings.Developer.Set("DevMode", "false");
            if (!AppSettings.Developer.Exists("EnableWebServer")) AppSettings.Developer.Set("EnableWebServer", "false");

            if (!AppSettings.WebServer.Exists("WebServerPort")) AppSettings.WebServer.Set("WebServerPort", "7963");
            if (!AppSettings.WebServer.Exists("AllowGetCookie")) AppSettings.WebServer.Set("AllowGetCookie", "false");
            if (!AppSettings.WebServer.Exists("AllowGetAccounts")) AppSettings.WebServer.Set("AllowGetAccounts", "false");
            if (!AppSettings.WebServer.Exists("AllowLaunchAccount")) AppSettings.WebServer.Set("AllowLaunchAccount", "false");
            if (!AppSettings.WebServer.Exists("AllowAccountEditing")) AppSettings.WebServer.Set("AllowAccountEditing", "false");
            if (!AppSettings.WebServer.Exists("Password")) AppSettings.WebServer.Set("Password", ""); else AppSettings.WebSocketPassword = CryptoHelper.UnprotectSecretString(AppSettings.WebServer.Get("Password") ?? string.Empty);
            if (!AppSettings.WebServer.Exists("EveryRequestRequiresPassword")) AppSettings.WebServer.Set("EveryRequestRequiresPassword", "false");
            if (!AppSettings.WebServer.Exists("AllowExternalConnections")) AppSettings.WebServer.Set("AllowExternalConnections", "false");

            if (!AppSettings.AccountControl.Exists("AllowExternalConnections")) AppSettings.AccountControl.Set("AllowExternalConnections", "false");
            if (!AppSettings.AccountControl.Exists("RelaunchDelay")) AppSettings.AccountControl.Set("RelaunchDelay", "60");
            if (!AppSettings.AccountControl.Exists("LauncherDelayNumber")) AppSettings.AccountControl.Set("LauncherDelayNumber", "9");

            ReplaceAccountsList(new List<Account>());
            AppServices.SelectionState.SelectedAccounts = new List<Account>();
            launchCoordinator = new LaunchCoordinator(ExecuteLaunchRequestAsync);
            browserService = BrowserService.Default;

            if (!AppSettings.Prompts.Exists("VCPrompted") && PrerequisiteService.NeedsVCRedist())
                vcredistInstallTask = PrerequisiteService.InstallVCRedistAsync(lifetimeCancellation.Token);
        }

        internal static void EnsureModernGeoIpLink(Shiro.Services.ISettingsService generalSettings)
        {
            if (!generalSettings.Exists("IPApiLink"))
            {
                generalSettings.Set("IPApiLink", DefaultGeoIpLink);
                return;
            }

            string? current = generalSettings.Get("IPApiLink");
            if (current != null && string.Equals(current.Trim(), LegacyGeoIpLink, StringComparison.OrdinalIgnoreCase))
            {
                generalSettings.Set("IPApiLink", DefaultGeoIpLink);
            }
        }

        public void RefreshView(object? obj = null)
        {
            RefreshModernDashboard();
        }

        private void RefreshModernDashboard()
        {
            var snapshot = AppServices.AccountRepository.GetAll();
            AppServices.HostUiBridge.NotifyAccountsReplaced(snapshot);
        }

        // Modern dashboard and Avalonia action entry points extracted to AccountManager.ModernActions.cs

        public static string ShowDialog(string text, string caption, string defaultText = "", bool big = false)
        {
            return AppServices.Dialogs.ShowInputPrompt(text, caption, defaultText);
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
