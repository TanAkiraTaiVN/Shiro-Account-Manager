#nullable enable
using System;
using Shiro.Classes;

namespace Shiro.Services
{
    public enum SettingsPreset
    {
        Basic,
        Balanced,
        AfkFarming,
        Custom
    }

    public static class SettingsPresetService
    {
        public static SettingsPreset CurrentPreset { get; private set; } = SettingsPreset.Basic;

        public static event Action<SettingsPreset>? PresetApplied;

        public static void ApplyPreset(SettingsPreset preset, bool patchRobloxNow = true)
        {
            if (AccountManager.General == null)
                return;

            CurrentPreset = preset;

            switch (preset)
            {
                case SettingsPreset.Basic:
                    // Standard casual gaming: Full fidelity, standard 60 FPS, all effects on
                    AccountManager.General.Set("UnlockFPS", "false");
                    AccountManager.General.Set("MaxFPSValue", "60");
                    AccountManager.General.Set("EnablePotatoMode", "false");
                    AccountManager.General.Set("FastFlag_DisableShadows", "false");
                    AccountManager.General.Set("FastFlag_DisableParticles", "false");
                    AccountManager.General.Set("FastFlag_DisablePostFx", "false");
                    AccountManager.General.Set("FastFlag_DisableGrass", "false");
                    AccountManager.General.Set("FastFlag_LowResTextures", "false");
                    AccountManager.General.Set("FastFlag_DisableLightGrid", "false");
                    AccountManager.General.Set("DisableImages", "false");
                    AccountManager.General.Set("AutoCleanZombiesOnLaunch", "true");
                    AccountManager.General.Set("AutoCookieRefresh", "true");
                    AccountManager.General.Set("ShowPresence", "true");
                    AccountManager.General.Set("PresenceUpdateRate", "5");
                    AccountManager.General.Set("AsyncJoin", "false");
                    AccountManager.General.Set("AccountJoinDelay", "5");
                    break;

                case SettingsPreset.Balanced:
                    // Multi-accounting (3-6 accounts): Smooth 60 FPS, disable heavy shadows and post FX
                    AccountManager.General.Set("UnlockFPS", "true");
                    AccountManager.General.Set("MaxFPSValue", "60");
                    AccountManager.General.Set("EnablePotatoMode", "false");
                    AccountManager.General.Set("FastFlag_DisableShadows", "true");
                    AccountManager.General.Set("FastFlag_DisableParticles", "false");
                    AccountManager.General.Set("FastFlag_DisablePostFx", "true");
                    AccountManager.General.Set("FastFlag_DisableGrass", "true");
                    AccountManager.General.Set("FastFlag_LowResTextures", "false");
                    AccountManager.General.Set("FastFlag_DisableLightGrid", "false");
                    AccountManager.General.Set("DisableImages", "false");
                    AccountManager.General.Set("AutoCleanZombiesOnLaunch", "true");
                    AccountManager.General.Set("AutoCookieRefresh", "true");
                    AccountManager.General.Set("ShowPresence", "true");
                    AccountManager.General.Set("PresenceUpdateRate", "10");
                    AccountManager.General.Set("AsyncJoin", "false");
                    AccountManager.General.Set("AccountJoinDelay", "4");
                    break;

                case SettingsPreset.AfkFarming:
                    // 24/7 AFK farming / Treo máy: Potato graphics, 4x textures, 30 FPS cap, low CPU/RAM/GPU
                    AccountManager.General.Set("UnlockFPS", "true");
                    AccountManager.General.Set("MaxFPSValue", "30");
                    AccountManager.General.Set("EnablePotatoMode", "true");
                    AccountManager.General.Set("FastFlag_DisableShadows", "true");
                    AccountManager.General.Set("FastFlag_DisableParticles", "true");
                    AccountManager.General.Set("FastFlag_DisablePostFx", "true");
                    AccountManager.General.Set("FastFlag_DisableGrass", "true");
                    AccountManager.General.Set("FastFlag_LowResTextures", "true");
                    AccountManager.General.Set("FastFlag_DisableLightGrid", "true");
                    AccountManager.General.Set("DisableImages", "true");
                    AccountManager.General.Set("AutoCleanZombiesOnLaunch", "true");
                    AccountManager.General.Set("AutoCookieRefresh", "true");
                    AccountManager.General.Set("ShowPresence", "true");
                    AccountManager.General.Set("PresenceUpdateRate", "15");
                    AccountManager.General.Set("AsyncJoin", "false");
                    AccountManager.General.Set("AccountJoinDelay", "3");
                    break;

                case SettingsPreset.Custom:
                    break;
            }

            AccountManager.General.Set(AppSettingsKeys.General.SettingsPreset, preset.ToString());
            AccountManager.SaveSettings();

            if (patchRobloxNow)
            {
                try
                {
                    ClientSettingsPatcher.PatchSettings();
                }
                catch (Exception ex)
                {
                    Program.Logger?.Warn($"PatchSettings on PresetApplied failed: {ex.Message}");
                }
            }

            PresetApplied?.Invoke(preset);
        }

        public static SettingsPreset DetectCurrentPreset()
        {
            if (AccountManager.General == null)
                return SettingsPreset.Basic;

            string saved = AccountManager.General.Get<string>(AppSettingsKeys.General.SettingsPreset) ?? string.Empty;
            if (Enum.TryParse<SettingsPreset>(saved, true, out var parsed))
            {
                CurrentPreset = parsed;
                return parsed;
            }

            bool potato = AccountManager.General.Get<bool>("EnablePotatoMode");
            int maxFps = AccountManager.General.Get<int>("MaxFPSValue");
            bool shadowsDisabled = AccountManager.General.Get<bool>("FastFlag_DisableShadows");

            if (potato && maxFps == 30)
                return SettingsPreset.AfkFarming;

            if (shadowsDisabled && !potato && maxFps == 60)
                return SettingsPreset.Balanced;

            if (!potato && !shadowsDisabled)
                return SettingsPreset.Basic;

            return SettingsPreset.Custom;
        }

        public static string GetPresetDisplayName(SettingsPreset preset)
        {
            return preset switch
            {
                SettingsPreset.Basic => Loc.Get("Preset_BasicTitle", "Cơ bản"),
                SettingsPreset.Balanced => Loc.Get("Preset_BalancedTitle", "Cân bằng"),
                SettingsPreset.AfkFarming => Loc.Get("Preset_AfkTitle", "Treo máy siêu mát"),
                SettingsPreset.Custom => Loc.Get("Preset_Custom", "Tùy chỉnh riêng"),
                _ => preset.ToString()
            };
        }
    }
}
