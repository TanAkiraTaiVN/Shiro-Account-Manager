using System;
using System.Globalization;

namespace RBX_Alt_Manager.Services
{
    /// <summary>
    /// Strongly-typed application settings accessor.
    /// Eliminates magic strings and provides compile-time safety and IntelliSense across the entire app.
    /// Synchronizes directly with the underlying INI storage.
    /// </summary>
    public class AppSettings
    {
        private static readonly Lazy<AppSettings> _currentInstance = new(() => new AppSettings());
        public static AppSettings Current => _currentInstance.Value;

        private IniSection GetSection(string sectionName) =>
            AccountManager.IniSettings?.Section(sectionName);

        private bool GetBool(string section, string key, bool fallback = false)
        {
            var sec = GetSection(section);
            if (sec != null && sec.Exists(key))
            {
                try { return sec.Get<bool>(key); } catch { }
            }
            return fallback;
        }

        private void SetBool(string section, string key, bool value)
        {
            GetSection(section)?.Set(key, value ? "true" : "false");
            AccountManager.SaveSettings();
        }

        private int GetInt(string section, string key, int fallback = 0)
        {
            var sec = GetSection(section);
            if (sec != null && sec.Exists(key))
            {
                try { return sec.Get<int>(key); } catch { }
            }
            return fallback;
        }

        private void SetInt(string section, string key, int value)
        {
            GetSection(section)?.Set(key, value.ToString(CultureInfo.InvariantCulture));
            AccountManager.SaveSettings();
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
            AccountManager.SaveSettings();
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

        public bool UseChromeHome
        {
            get => GetBool(AppSettingsKeys.Sections.General, "UseChromeHome", false);
            set => SetBool(AppSettingsKeys.Sections.General, "UseChromeHome", value);
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

        public int NexusPort
        {
            get => GetInt(AppSettingsKeys.Sections.AccountControl, "NexusPort", 5242);
            set => SetInt(AppSettingsKeys.Sections.AccountControl, "NexusPort", value);
        }
    }
}
