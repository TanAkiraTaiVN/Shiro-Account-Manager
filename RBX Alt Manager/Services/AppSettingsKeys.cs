#nullable enable
using System;

namespace RBX_Alt_Manager.Services
{
    /// <summary>
    /// Strongly-typed configuration constants and section keys for INI configuration.
    /// Eliminates magic strings and typos across the entire application.
    /// </summary>
    public static class AppSettingsKeys
    {
        public static class Sections
        {
            public const string General = "General";
            public const string Developer = "Developer";
            public const string WebServer = "WebServer";
            public const string AccountControl = "AccountControl";
            public const string Watcher = "Watcher";
            public const string Prompts = "Prompts";
        }

        public static class General
        {
            public const string CheckForUpdates = "CheckForUpdates";
            public const string SavePasswords = "SavePasswords";
            public const string HideRbxAlert = "HideRbxAlert";
            public const string DisableAgingAlert = "DisableAgingAlert";
            public const string AutoCookieRefresh = "AutoCookieRefresh";
            public const string ShowPresence = "ShowPresence";
            public const string PresenceUpdateRate = "PresenceUpdateRate";
            public const string DisableImages = "DisableImages";
            public const string EnableMultiRbx = "EnableMultiRbx";
            public const string AsyncJoin = "AsyncJoin";
            public const string AccountJoinDelay = "AccountJoinDelay";
            public const string ShuffleChoosesLowestServer = "ShuffleChoosesLowestServer";
            public const string ServerRegionFormat = "ServerRegionFormat";
            public const string MaxRecentGames = "MaxRecentGames";
            public const string UnlockFPS = "UnlockFPS";
            public const string MaxFPSValue = "MaxFPSValue";
            public const string EnablePotatoMode = "EnablePotatoMode";
            public const string SavedPlaceId = "SavedPlaceId";
            public const string SavedFollowUser = "SavedFollowUser";
            public const string AccentTheme = "AccentTheme";
            public const string HideUsernames = "HideUsernames";
            public const string UseProxies = "UseProxies";
            public const string AutoLowerProcessPriority = "AutoLowerProcessPriority";
            public const string UseEmbeddedBrowser = "UseEmbeddedBrowser";
            public const string WindowScale = "WindowScale";
            public const string ScaleFonts = "ScaleFonts";
            public const string AutoCleanZombiesOnLaunch = "AutoCleanZombiesOnLaunch";
            public const string PreferDirectLaunch = "PreferDirectLaunch";
            public const string FastFlagProfile = "FastFlagProfile";
            public const string AutoMuteBackgroundClients = "AutoMuteBackgroundClients";
            public const string SmartFastLaunch = "SmartFastLaunch";
            public const string SmartLaunchMinDelaySeconds = "SmartLaunchMinDelaySeconds";
            public const string AggressiveRamTrimming = "AggressiveRamTrimming";
            public const string DynamicHardwareThrottling = "DynamicHardwareThrottling";
            public const string Language = "Language";
            public const string SettingsPreset = "SettingsPreset";

            // Potato Mode Fast Flags
            public const string FastFlag_DisableShadows = "FastFlag_DisableShadows";
            public const string FastFlag_DisableParticles = "FastFlag_DisableParticles";
            public const string FastFlag_DisablePostFx = "FastFlag_DisablePostFx";
            public const string FastFlag_DisableGrass = "FastFlag_DisableGrass";
            public const string FastFlag_LowResTextures = "FastFlag_LowResTextures";
            public const string FastFlag_DisableLightGrid = "FastFlag_DisableLightGrid";
        }

        public static class Developer
        {
            public const string DevMode = "DevMode";
            public const string EnableWebServer = "EnableWebServer";
        }

        public static class WebServer
        {
            public const string WebServerPort = "WebServerPort";
            public const string AllowRemoteAccess = "AllowRemoteAccess";
            public const string AllowExternalConnections = "AllowExternalConnections";
            public const string Password = "Password";
        }

        public static class Watcher
        {
            public const string Enabled = "Enabled";
            public const string ExitOnBeta = "ExitOnBeta";
            public const string ExitIfNoConnection = "ExitIfNoConnection";
            public const string SaveWindowPositions = "SaveWindowPositions";
            public const string VerifyDataModel = "VerifyDataModel";
            public const string IgnoreExistingProcesses = "IgnoreExistingProcesses";
            public const string CloseRbxWindowTitle = "CloseRbxWindowTitle";
            public const string CloseRbxMemory = "CloseRbxMemory";
            public const string ExpectedWindowTitle = "ExpectedWindowTitle";
        }

        public static class AccountControl
        {
            public const string NexusPort = "NexusPort";
            public const string AllowExternalConnections = "AllowExternalConnections";
            public const string StartOnLaunch = "StartOnLaunch";
            public const string AutoMinimizeEnabled = "AutoMinimizeEnabled";
            public const string AutoCloseEnabled = "AutoCloseEnabled";
            public const string InternetCheck = "InternetCheck";
            public const string UsePresence = "UsePresence";
            public const string AutoMinimizeInterval = "AutoMinimizeInterval";
            public const string AutoCloseInterval = "AutoCloseInterval";
            public const string MaxInstances = "MaxInstances";
            public const string SaveOutput = "SaveOutput";
            public const string RelaunchDelay = "RelaunchDelay";
            public const string LauncherDelayNumber = "LauncherDelayNumber";
        }
    }
}
