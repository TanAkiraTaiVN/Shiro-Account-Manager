using System;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StoragePathsCollection")]
    public class SettingsPresetServiceTests
    {
        public SettingsPresetServiceTests()
        {
            AccountManager.IniSettings = new IniFile();
            AccountManager.General = AccountManager.IniSettings.Section("General");
        }

        [Fact]
        public void ApplyPreset_Basic_ConfiguresStandardSettings()
        {
            SettingsPresetService.ApplyPreset(SettingsPreset.Basic, patchRobloxNow: false);

            Assert.Equal("false", AccountManager.General.Get<string>("UnlockFPS"));
            Assert.Equal("60", AccountManager.General.Get<string>("MaxFPSValue"));
            Assert.Equal("false", AccountManager.General.Get<string>("EnablePotatoMode"));
            Assert.Equal("false", AccountManager.General.Get<string>("FastFlag_DisableShadows"));
            Assert.Equal("false", AccountManager.General.Get<string>("FastFlag_LowResTextures"));
            Assert.Equal("false", AccountManager.General.Get<string>("DisableImages"));
            Assert.Equal(SettingsPreset.Basic, SettingsPresetService.CurrentPreset);
            Assert.Equal(SettingsPreset.Basic, SettingsPresetService.DetectCurrentPreset());
        }

        [Fact]
        public void ApplyPreset_Balanced_ConfiguresBalancedSettings()
        {
            SettingsPresetService.ApplyPreset(SettingsPreset.Balanced, patchRobloxNow: false);

            Assert.Equal("true", AccountManager.General.Get<string>("UnlockFPS"));
            Assert.Equal("60", AccountManager.General.Get<string>("MaxFPSValue"));
            Assert.Equal("false", AccountManager.General.Get<string>("EnablePotatoMode"));
            Assert.Equal("true", AccountManager.General.Get<string>("FastFlag_DisableShadows"));
            Assert.Equal("true", AccountManager.General.Get<string>("FastFlag_DisableGrass"));
            Assert.Equal("false", AccountManager.General.Get<string>("DisableImages"));
            Assert.Equal(SettingsPreset.Balanced, SettingsPresetService.CurrentPreset);
            Assert.Equal(SettingsPreset.Balanced, SettingsPresetService.DetectCurrentPreset());
        }

        [Fact]
        public void ApplyPreset_AfkFarming_ConfiguresExtremePerformanceSettings()
        {
            SettingsPresetService.ApplyPreset(SettingsPreset.AfkFarming, patchRobloxNow: false);

            Assert.Equal("true", AccountManager.General.Get<string>("UnlockFPS"));
            Assert.Equal("30", AccountManager.General.Get<string>("MaxFPSValue"));
            Assert.Equal("true", AccountManager.General.Get<string>("EnablePotatoMode"));
            Assert.Equal("true", AccountManager.General.Get<string>("FastFlag_DisableShadows"));
            Assert.Equal("true", AccountManager.General.Get<string>("FastFlag_DisableParticles"));
            Assert.Equal("true", AccountManager.General.Get<string>("FastFlag_LowResTextures"));
            Assert.Equal("true", AccountManager.General.Get<string>("DisableImages"));
            Assert.Equal(SettingsPreset.AfkFarming, SettingsPresetService.CurrentPreset);
            Assert.Equal(SettingsPreset.AfkFarming, SettingsPresetService.DetectCurrentPreset());
        }

        [Fact]
        public void ApplyPreset_Triggers_PresetAppliedEvent()
        {
            SettingsPreset received = SettingsPreset.Basic;
            bool eventFired = false;

            void Handler(SettingsPreset preset)
            {
                received = preset;
                eventFired = true;
            }

            SettingsPresetService.PresetApplied += Handler;
            try
            {
                SettingsPresetService.ApplyPreset(SettingsPreset.AfkFarming, patchRobloxNow: false);
                Assert.True(eventFired);
                Assert.Equal(SettingsPreset.AfkFarming, received);
            }
            finally
            {
                SettingsPresetService.PresetApplied -= Handler;
            }
        }

        [Theory]
        [InlineData(SettingsPreset.Basic)]
        [InlineData(SettingsPreset.Balanced)]
        [InlineData(SettingsPreset.AfkFarming)]
        [InlineData(SettingsPreset.Custom)]
        public void GetPresetDisplayName_ReturnsNonEmptyName(SettingsPreset preset)
        {
            string name = SettingsPresetService.GetPresetDisplayName(preset);
            Assert.False(string.IsNullOrWhiteSpace(name));
        }

        [Fact]
        public void FastFlagPresetService_GeneratesExpectedProfiles()
        {
            var potato = FastFlagPresetService.GenerateProfileSettings(FastFlagProfile.Potato, 30);
            Assert.NotNull(potato);
            Assert.Equal(30, (int)potato["DFIntTaskSchedulerTargetFps"]);
            Assert.Equal(4, (int)potato["DFIntTextureCompositorLowResFactor"]);

            var ultraFarm = FastFlagPresetService.GenerateProfileSettings(FastFlagProfile.UltraFarm, 15);
            Assert.NotNull(ultraFarm);
            Assert.Equal(15, (int)ultraFarm["DFIntTaskSchedulerTargetFps"]);
            Assert.Equal(8, (int)ultraFarm["DFIntTextureCompositorLowResFactor"]);
            // Byfron protection: ensure dangerous crashpad debug flags are omitted
            Assert.False(ultraFarm.ContainsKey("FFlagDebugDisableCrashpad"));

            var maxPerf = FastFlagPresetService.GenerateProfileSettings(FastFlagProfile.MaxPerformance, 240);
            Assert.NotNull(maxPerf);
            Assert.Equal(240, (int)maxPerf["DFIntTaskSchedulerTargetFps"]);
        }
    }
}
