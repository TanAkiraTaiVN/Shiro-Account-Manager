#nullable enable
using System;

namespace RBX_Alt_Manager.Services
{
    internal sealed class SettingsService
    {
        private readonly System.Threading.Lock sync = new();
        public IniFile File { get; }

        public SettingsService(IniFile file)
        {
            File = file ?? throw new ArgumentNullException(nameof(file));
        }

        public T? Get<T>(string section, string key, T? fallback = default(T))
        {
            lock (sync)
            {
                IniSection? target = File.Section(section);
                if (target == null || !target.Exists(key))
                    return fallback;

                try { return target.Get<T>(key); }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"SettingsService parse '{key}' in '{section}' failed: {ex.Message}");
                    return fallback;
                }
            }
        }

        public void Set(string section, string key, string value, string? comment = null)
        {
            lock (sync)
                File.Section(section)?.Set(key, value, comment);
        }

        public void Save(string fileName = "RAMSettings.json")
        {
            lock (sync)
                File.Save(fileName);
        }
    }

    /// <summary>
    /// Centralized strongly-typed setting keys to eliminate magic strings and typos.
    /// </summary>
    internal static class SettingKeys
    {
        public const string GeneralSection = "General";
        public const string FastFlagsSection = "FastFlags";

        // General keys
        public const string UnlockFPS = "UnlockFPS";
        public const string MaxFPSValue = "MaxFPSValue";
        public const string EnablePotatoMode = "EnablePotatoMode";
        public const string PotatoTargetFPS = "PotatoTargetFPS";
        public const string HideUsernames = "HideUsernames";
        public const string AccountJoinDelay = "AccountJoinDelay";
        public const string ScaleFonts = "ScaleFonts";
        public const string WindowScale = "WindowScale";
        public const string ShuffleJobID = "ShuffleJobID";
        public const string AllowMultipleInstances = "AllowMultipleInstances";
        public const string AutoCloseRobloxOnLaunch = "AutoCloseRobloxOnLaunch";
        public const string AutoCleanZombiesOnLaunch = "AutoCleanZombiesOnLaunch";
        public const string ShowPresence = "ShowPresence";
        public const string PresenceUpdateRate = "PresenceUpdateRate";
        public const string CustomClientSettings = "CustomClientSettings";
        public const string SavedPlaceId = "SavedPlaceId";
        public const string SavedFollowUser = "SavedFollowUser";
        public const string AutoCheckUpdates = "AutoCheckUpdates";

        // FastFlag keys
        public const string FastFlag_DisableShadows = "FastFlag_DisableShadows";
        public const string FastFlag_DisableParticles = "FastFlag_DisableParticles";
        public const string FastFlag_DisablePostFx = "FastFlag_DisablePostFx";
        public const string FastFlag_DisableGrass = "FastFlag_DisableGrass";
        public const string FastFlag_LowResTextures = "FastFlag_LowResTextures";
        public const string FastFlag_DisableLightGrid = "FastFlag_DisableLightGrid";
    }
}
