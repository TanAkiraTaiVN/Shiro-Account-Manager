#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    /// <summary>
    /// Strongly-typed application settings and runtime state accessor.
    /// Synchronizes directly with the underlying INI storage and DI-registered state services.
    /// </summary>
    public class AppSettings
    {
        private static readonly Lazy<AppSettings> _currentInstance = new(() => new AppSettings());
        public static AppSettings Current => _currentInstance.Value;

        private static IniFile? _activeIniFile;
        private static readonly IniFile FallbackIniFile = new();

        [AllowNull]
        public static IniFile ActiveIniFile
        {
            get => _activeIniFile ?? FallbackIniFile;
            set
            {
                _activeIniFile = value;
                _generalOverride = null;
            }
        }

        public IniFile IniFile => ActiveIniFile;

        private static ISettingsService? _generalOverride;

        /// <summary>
        /// General application settings service. Can be overridden in unit tests to prevent cross-test contamination.
        /// </summary>
        public static ISettingsService General
        {
            get => _generalOverride ?? (ISettingsService)ActiveIniFile.Section("General");
            set => _generalOverride = value;
        }

        public static IniSection WebServer => ActiveIniFile.Section("WebServer");
        public static IniSection Developer => ActiveIniFile.Section("Developer");
        public static IniSection Watcher => ActiveIniFile.Section("Watcher");
        public static IniSection Prompts => ActiveIniFile.Section("Prompts");

        public static string? WebSocketPassword
        {
            get => AppServices.ServerBrowserState.WebSocketPassword;
            set => AppServices.ServerBrowserState.WebSocketPassword = value;
        }

        public static List<ServerData>? CachedServers => AppServices.ServerBrowserState.CachedServers;
        public static List<Shiro.Game>? RecentGames => AppServices.ServerBrowserState.RecentGames;

        public static string CurrentPlaceId
        {
            get => AppServices.ServerBrowserState.CurrentPlaceId;
            set => AppServices.ServerBrowserState.CurrentPlaceId = value;
        }

        public static string CurrentJobId
        {
            get => AppServices.ServerBrowserState.CurrentJobId;
            set => AppServices.ServerBrowserState.CurrentJobId = value;
        }

        public static bool ShuffleJobId
        {
            get => AppServices.ServerBrowserState.ShuffleJobId;
            set => AppServices.ServerBrowserState.ShuffleJobId = value;
        }

        public static string? CurrentClientVersion
        {
            get => AppServices.ServerBrowserState.CurrentClientVersion;
            set => AppServices.ServerBrowserState.CurrentClientVersion = value;
        }

        public static Task LoadClientVersionAsync(CancellationToken cancellationToken = default) =>
            AppServices.ServerBrowserState.LoadClientVersionAsync(cancellationToken);

        public static Account? SelectedAccount
        {
            get => AppServices.SelectionState.SelectedAccount;
            set => AppServices.SelectionState.SelectedAccount = value;
        }

        public static Account? LastValidAccount
        {
            get => AppServices.SelectionState.LastValidAccount;
            set => AppServices.SelectionState.LastValidAccount = value;
        }

        public static List<Account> SelectedAccounts
        {
            get => AppServices.SelectionState.SelectedAccounts;
            set => AppServices.SelectionState.SelectedAccounts = value;
        }

        public static int MinimumAccountJoinDelaySeconds => 1;
        public static int MaximumAccountJoinDelaySeconds => 3600;

        private static readonly AsyncLocal<int> _batchDepth = new();
        private static readonly AsyncLocal<bool> _saveDeferredInBatch = new();

        public static void Save()
        {
            if (_batchDepth.Value > 0)
            {
                _saveDeferredInBatch.Value = true;
                return;
            }

            AppServices.HostUiBridge.SaveSettings();
        }

        public IDisposable BeginBatch() => BeginBatchScope();

        public static IDisposable BeginBatchScope()
        {
            _batchDepth.Value++;
            return new SettingsBatchScope();
        }

        private sealed class SettingsBatchScope : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                int depth = _batchDepth.Value;
                if (depth > 0)
                    _batchDepth.Value = depth - 1;

                if (_batchDepth.Value == 0 && _saveDeferredInBatch.Value)
                {
                    _saveDeferredInBatch.Value = false;
                    Save();
                }
            }
        }

        public static void QueueDeferredSave() => AppServices.HostUiBridge.QueueDeferredSaveSettings();

        // ================= HOST UI / CONTROLLER BRIDGE =================

        public static void RefreshAccountView(Account? account = null) =>
            AppServices.HostUiBridge.RefreshAccountView(account);

        public static void UpdateAccountValidityOnUi(Account account, bool valid) =>
            AppServices.HostUiBridge.UpdateAccountValidityOnUi(account, valid);

        public static void SchedulePresenceRefresh(Account? account) =>
            AppServices.HostUiBridge.SchedulePresenceRefresh(account);

        public static void SignalNextLaunchAccount() => AppServices.HostUiBridge.SignalNextLaunchAccount();
        public static void CancelLaunching() => AppServices.HostUiBridge.CancelLaunching();
        public static void UpdateMultiRoblox() => AppServices.HostUiBridge.UpdateMultiRoblox();
        public static void CloseAllRobloxFromUi() => AppServices.HostUiBridge.CloseAllRobloxFromUi();

        public static Task LaunchAppHomeFromHostAsync(Account account) =>
            AppServices.HostUiBridge.LaunchAppHomeFromHostAsync(account);

        public static Task JoinServerFromHostAsync(IReadOnlyList<Account> accounts, long placeId, string jobId) =>
            AppServices.HostUiBridge.JoinServerFromHostAsync(accounts, placeId, jobId);

        public static void MultiLaunchHomeFromHost() => AppServices.HostUiBridge.MultiLaunchHomeFromHost();

        public static List<Account> GetCurrentSelectedAccountsFromHost() =>
            AppServices.HostUiBridge.GetCurrentSelectedAccountsFromHost();

        public static void SetDashboardStatus(string message) =>
            AppServices.HostUiBridge.SetDashboardStatus(message);

        public static void EnsureBackendInitialized() =>
            AppServices.HostUiBridge.EnsureBackendInitialized();

        public static void InitializePostStartup() =>
            AppServices.HostUiBridge.InitializePostStartup();

        public static Task RestartWebServerAsync(int port, bool allowExternal) =>
            AppServices.HostUiBridge.RestartWebServerAsync(port, allowExternal);

        public static Task StopWebServerAsync() =>
            AppServices.HostUiBridge.StopWebServerAsync();

        public static void SetAutoCookieRefreshEnabled(bool enabled) =>
            AppServices.HostUiBridge.SetAutoCookieRefreshEnabled(enabled);

        public static Task ResetEncryptionAsync(bool prompt) =>
            AppServices.HostUiBridge.ResetEncryptionAsync(prompt);

        public static void ResetOverrides()
        {
            _generalOverride = null;
        }

        public IniSection GetSection(string sectionName) =>
            ActiveIniFile.Section(sectionName);

        private bool GetBool(string section, string key, bool fallback = false)
        {
            var sec = GetSection(section);
            if (sec != null && sec.Exists(key))
            {
                try { return sec.Get<bool>(key); }
                catch (Exception ex) { Program.Logger?.Debug($"AppSettings parse bool '{key}' failed: {ex.Message}"); }
            }
            return fallback;
        }

        private void SetBool(string section, string key, bool value)
        {
            GetSection(section)?.Set(key, value ? "true" : "false");
            Save();
        }

        private int GetInt(string section, string key, int fallback = 0)
        {
            var sec = GetSection(section);
            if (sec != null && sec.Exists(key))
            {
                try { return sec.Get<int>(key); }
                catch (Exception ex) { Program.Logger?.Debug($"AppSettings parse int '{key}' failed: {ex.Message}"); }
            }
            return fallback;
        }

        private void SetInt(string section, string key, int value)
        {
            GetSection(section)?.Set(key, value.ToString(CultureInfo.InvariantCulture));
            Save();
        }

        private string GetString(string section, string key, string fallback = "")
        {
            var sec = GetSection(section);
            if (sec != null && sec.Exists(key))
            {
                return sec.Get(key) ?? fallback;
            }
            return fallback;
        }

        private void SetString(string section, string key, string value)
        {
            GetSection(section)?.Set(key, value ?? string.Empty);
            Save();
        }

        // ================= GENERAL SETTINGS =================

        public bool EnableMultiRbx
        {
            get => GetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.EnableMultiRbx, true);
            set => SetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.EnableMultiRbx, value);
        }

        public int AccountJoinDelay
        {
            get => GetInt(AppSettingsKeys.Sections.General, AppSettingsKeys.General.AccountJoinDelay, 8);
            set => SetInt(AppSettingsKeys.Sections.General, AppSettingsKeys.General.AccountJoinDelay, Math.Clamp(value, 1, 3600));
        }

        public bool SavePasswords
        {
            get => GetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.SavePasswords, true);
            set => SetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.SavePasswords, value);
        }

        public bool AutoCookieRefresh
        {
            get => GetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.AutoCookieRefresh, true);
            set => SetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.AutoCookieRefresh, value);
        }

        public bool ShowPresence
        {
            get => GetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.ShowPresence, true);
            set => SetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.ShowPresence, value);
        }

        public int PresenceUpdateRate
        {
            get => GetInt(AppSettingsKeys.Sections.General, AppSettingsKeys.General.PresenceUpdateRate, 5);
            set => SetInt(AppSettingsKeys.Sections.General, AppSettingsKeys.General.PresenceUpdateRate, Math.Max(1, value));
        }

        public bool UnlockFPS
        {
            get => GetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.UnlockFPS, false);
            set => SetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.UnlockFPS, value);
        }

        public int MaxFPSValue
        {
            get => GetInt(AppSettingsKeys.Sections.General, AppSettingsKeys.General.MaxFPSValue, 120);
            set => SetInt(AppSettingsKeys.Sections.General, AppSettingsKeys.General.MaxFPSValue, value);
        }

        public bool EnablePotatoMode
        {
            get => GetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.EnablePotatoMode, false);
            set => SetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.EnablePotatoMode, value);
        }

        public string AccentTheme
        {
            get => GetString(AppSettingsKeys.Sections.General, AppSettingsKeys.General.AccentTheme, "Monochrome");
            set => SetString(AppSettingsKeys.Sections.General, AppSettingsKeys.General.AccentTheme, value);
        }

        public string SavedPlaceId
        {
            get => GetString(AppSettingsKeys.Sections.General, AppSettingsKeys.General.SavedPlaceId, "5315046213");
            set => SetString(AppSettingsKeys.Sections.General, AppSettingsKeys.General.SavedPlaceId, value);
        }

        public bool AsyncJoin
        {
            get => GetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.AsyncJoin, false);
            set => SetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.AsyncJoin, value);
        }

        public bool CheckForUpdates
        {
            get => GetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.CheckForUpdates, true);
            set => SetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.CheckForUpdates, value);
        }

        public bool UseProxies
        {
            get => GetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.UseProxies, false);
            set => SetBool(AppSettingsKeys.Sections.General, AppSettingsKeys.General.UseProxies, value);
        }

        public int ProxyTimeout
        {
            get => GetInt(AppSettingsKeys.Sections.General, "ProxyTimeout", 5000);
            set => SetInt(AppSettingsKeys.Sections.General, "ProxyTimeout", value);
        }

        public int ProxyTestLimit
        {
            get => GetInt(AppSettingsKeys.Sections.General, "ProxyTestLimit", 10);
            set => SetInt(AppSettingsKeys.Sections.General, "ProxyTestLimit", value);
        }

        // ================= DEVELOPER & WEBSERVER SETTINGS =================

        public bool DevMode
        {
            get => GetBool(AppSettingsKeys.Sections.Developer, AppSettingsKeys.Developer.DevMode, false);
            set => SetBool(AppSettingsKeys.Sections.Developer, AppSettingsKeys.Developer.DevMode, value);
        }

        public bool EnableWebServer
        {
            get => GetBool(AppSettingsKeys.Sections.Developer, AppSettingsKeys.Developer.EnableWebServer, false);
            set => SetBool(AppSettingsKeys.Sections.Developer, AppSettingsKeys.Developer.EnableWebServer, value);
        }

        public int WebServerPort
        {
            get => GetInt(AppSettingsKeys.Sections.WebServer, AppSettingsKeys.WebServer.WebServerPort, 7963);
            set => SetInt(AppSettingsKeys.Sections.WebServer, AppSettingsKeys.WebServer.WebServerPort, value);
        }
    }
}
