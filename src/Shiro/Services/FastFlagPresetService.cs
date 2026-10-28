#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Shiro.Services
{
    public enum FastFlagProfile
    {
        Default = 0,
        Potato = 1,
        UltraFarm = 2,
        MaxPerformance = 3,
        HighQuality = 4
    }

    /// <summary>
    /// Engine for generating optimized FastFlags (ClientAppSettings.json) based on user activity profiles.
    /// Supports extreme lag reduction for mass multi-boxing, high frame rates, or visual fidelity presets.
    /// </summary>
    public static class FastFlagPresetService
    {
        public static JsonObject GenerateProfileSettings(FastFlagProfile profile, int targetFps = 0)
        {
            var settings = new JsonObject();

            switch (profile)
            {
                case FastFlagProfile.Potato:
                    settings["DFIntTaskSchedulerTargetFps"] = targetFps > 0 ? targetFps : 30;
                    settings["DFIntTextureCompositorLowResFactor"] = 4;
                    settings["DFFlagDisableDPIScale"] = true;
                    settings["FIntFRMMinGrassDistance"] = 0;
                    settings["FIntFRMMaxGrassDistance"] = 0;
                    settings["FIntRenderShadowIntensity"] = 0;
                    settings["FIntRenderShadowmapBias"] = 0;
                    settings["FFlagDisablePostFx"] = true;
                    break;

                case FastFlagProfile.UltraFarm:
                    // Extreme multi-boxing efficiency (lowest memory, 15 FPS background target)
                    settings["DFIntTaskSchedulerTargetFps"] = targetFps > 0 ? targetFps : 15;
                    settings["DFIntTextureCompositorLowResFactor"] = 8;
                    settings["DFFlagDisableDPIScale"] = true;
                    settings["FIntFRMMinGrassDistance"] = 0;
                    settings["FIntFRMMaxGrassDistance"] = 0;
                    settings["FIntRenderShadowIntensity"] = 0;
                    settings["FIntRenderShadowmapBias"] = 0;
                    settings["FFlagDisablePostFx"] = true;
                    settings["DFFlagDebugDisableTelemetry"] = true;
                    settings["FIntTerrainArraySliceSize"] = 0;
                    settings["DFIntMaxFrameBufferSize"] = 1;
                    break;

                case FastFlagProfile.MaxPerformance:
                    // High competitive frame rates, reduced input delay
                    settings["DFIntTaskSchedulerTargetFps"] = targetFps > 0 ? targetFps : 240;
                    settings["FFlagDisablePostFx"] = false;
                    settings["DFFlagDisableDPIScale"] = true;
                    settings["DFIntMaxFrameBufferSize"] = 1;
                    break;

                case FastFlagProfile.HighQuality:
                    // Visual fidelity & high quality rendering
                    settings["DFIntTaskSchedulerTargetFps"] = targetFps > 0 ? targetFps : 240;
                    settings["DFFlagDisableDPIScale"] = false;
                    settings["FIntFRMMinGrassDistance"] = 100;
                    settings["FIntFRMMaxGrassDistance"] = 500;
                    break;

                case FastFlagProfile.Default:
                default:
                    // Empty or vanilla defaults
                    break;
            }

            return settings;
        }

        public static FastFlagProfile ParseProfile(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return FastFlagProfile.Default;

            if (Enum.TryParse<FastFlagProfile>(name, true, out var result))
                return result;

            if (name.Contains("ultra", StringComparison.OrdinalIgnoreCase) || name.Contains("farm", StringComparison.OrdinalIgnoreCase))
                return FastFlagProfile.UltraFarm;

            if (name.Contains("potato", StringComparison.OrdinalIgnoreCase))
                return FastFlagProfile.Potato;

            if (name.Contains("perf", StringComparison.OrdinalIgnoreCase))
                return FastFlagProfile.MaxPerformance;

            if (name.Contains("hq", StringComparison.OrdinalIgnoreCase) || name.Contains("high", StringComparison.OrdinalIgnoreCase))
                return FastFlagProfile.HighQuality;

            return FastFlagProfile.Default;
        }
    }
}
