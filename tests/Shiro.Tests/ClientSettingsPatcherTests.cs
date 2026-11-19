#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class ClientSettingsPatcherTests
    {
        [Fact]
        public void ApplyFpsLimitToDirectory_WhenUnlocked_DoesNotWriteFastFlag_AndScrubsLegacyFastFlag()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro_Patcher_Test_" + Guid.NewGuid());
            string settingsDir = Path.Combine(tempDir, "ClientSettings");
            Directory.CreateDirectory(settingsDir);
            try
            {
                string jsonPath = Path.Combine(settingsDir, "ClientAppSettings.json");
                File.WriteAllText(jsonPath, "{\"DFIntTaskSchedulerTargetFps\": 144, \"FStringCustom\": \"Test\"}");

                ClientSettingsPatcher.ApplyFpsLimitToDirectory(tempDir, unlockFps: true, targetFps: 144);

                Assert.True(File.Exists(jsonPath));
                string content = File.ReadAllText(jsonPath);
                Assert.DoesNotContain(ClientSettingsPatcher.FpsFlagKey, content);
                Assert.Contains("FStringCustom", content);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void ApplyFpsLimitToDirectory_WhenLocked_RemovesFpsKeyPreservingOtherKeys()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro_Patcher_Test_" + Guid.NewGuid());
            string settingsDir = Path.Combine(tempDir, "ClientSettings");
            Directory.CreateDirectory(settingsDir);
            try
            {
                string jsonPath = Path.Combine(settingsDir, "ClientAppSettings.json");
                File.WriteAllText(jsonPath, "{\"DFIntTaskSchedulerTargetFps\": 144, \"FStringCustom\": \"Test\"}");
                ClientSettingsPatcher.ApplyFpsLimitToDirectory(tempDir, unlockFps: false, targetFps: 60);
                string content = File.ReadAllText(jsonPath);
                Assert.DoesNotContain(ClientSettingsPatcher.FpsFlagKey, content);
                Assert.Contains("FStringCustom", content);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void ApplyFpsLimitToDirectory_WhenNoExistingFile_DoesNotCreateClientAppSettings()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro_Patcher_Test_" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                ClientSettingsPatcher.ApplyFpsLimitToDirectory(tempDir, unlockFps: true, targetFps: 144);
                string jsonPath = Path.Combine(tempDir, "ClientSettings", "ClientAppSettings.json");
                Assert.False(File.Exists(jsonPath));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Theory]
        [InlineData(144, 144)]
        [InlineData(360, 240)]
        [InlineData(10, 30)]
        [InlineData(0, -1)]
        [InlineData(-10, -1)]
        public void UpdateBasicSettingsXmlContent_FramerateCap_ReplacesExistingNodeAndClamps(int inputCap, int expectedCap)
        {
            string xml = @"<roblox><Item class=""UserGameSettings""><Properties><int name=""FramerateCap"">60</int><int name=""GraphicsQualityLevel"">10</int></Properties></Item></roblox>";

            string updated = ClientSettingsPatcher.UpdateBasicSettingsXmlContent(
                xml,
                graphicsQualityLevel: null,
                fullscreen: null,
                out bool modified,
                framerateCap: inputCap);

            Assert.True(modified);
            Assert.Contains($@"<int name=""FramerateCap"">{expectedCap}</int>", updated);
            Assert.Contains(@"<int name=""GraphicsQualityLevel"">10</int>", updated);
        }

        [Fact]
        public void UpdateBasicSettingsXmlContent_FramerateCap_WhenUnchanged_ReportsNotModified()
        {
            string xml = @"<roblox><Item class=""UserGameSettings""><Properties><int name=""FramerateCap"">-1</int></Properties></Item></roblox>";

            string updated = ClientSettingsPatcher.UpdateBasicSettingsXmlContent(
                xml,
                graphicsQualityLevel: null,
                fullscreen: null,
                out bool modified,
                framerateCap: -1);

            Assert.False(modified);
            Assert.Equal(xml, updated);
        }

        [Fact]
        public void UpdateBasicSettingsXmlContent_FramerateCap_InsertsNodeWhenMissingInUserGameSettings()
        {
            string xml = @"<roblox><Item class=""UserGameSettings""><Properties><token name=""SavedQualityLevel"">10</token></Properties></Item></roblox>";

            string updated = ClientSettingsPatcher.UpdateBasicSettingsXmlContent(
                xml,
                graphicsQualityLevel: null,
                fullscreen: null,
                out bool modified,
                framerateCap: 144);

            Assert.True(modified);
            Assert.Contains(@"<int name=""FramerateCap"">144</int>", updated);
            Assert.Contains(@"<token name=""SavedQualityLevel"">10</token>", updated);
        }

        [Fact]
        public void ApplyFpsLimit_UpdatesGlobalBasicSettingsXml()
        {
            string tempXml = Path.Combine(Path.GetTempPath(), $"Shiro_GBS_{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(tempXml, @"<roblox><Item class=""UserGameSettings""><Properties><int name=""FramerateCap"">-1</int></Properties></Item></roblox>");

                ClientSettingsPatcher.ApplyFpsLimit(unlockFps: true, targetFps: 144, xmlPathOverride: tempXml);
                string updated = File.ReadAllText(tempXml);
                Assert.Contains(@"<int name=""FramerateCap"">144</int>", updated);

                ClientSettingsPatcher.ApplyFpsLimit(unlockFps: false, targetFps: 60, xmlPathOverride: tempXml);
                string reverted = File.ReadAllText(tempXml);
                Assert.Contains(@"<int name=""FramerateCap"">-1</int>", reverted);
            }
            finally
            {
                if (File.Exists(tempXml))
                    File.Delete(tempXml);
            }
        }

        [Fact]
        public void ConfigureGlobalBasicSettings_WithFramerateCap_UpdatesXml()
        {
            string tempXml = Path.Combine(Path.GetTempPath(), $"Shiro_GBS_{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(tempXml, @"<roblox><Item class=""UserGameSettings""><Properties><int name=""GraphicsQualityLevel"">10</int><bool name=""Fullscreen"">false</bool><int name=""FramerateCap"">-1</int></Properties></Item></roblox>");

                bool success = ClientSettingsPatcher.ConfigureGlobalBasicSettings(
                    graphicsQualityLevel: 1,
                    fullscreen: true,
                    framerateCap: 200,
                    settingsPathOverride: tempXml);

                Assert.True(success);
                string updated = File.ReadAllText(tempXml);
                Assert.Contains(@"<int name=""GraphicsQualityLevel"">1</int>", updated);
                Assert.Contains(@"<bool name=""Fullscreen"">true</bool>", updated);
                Assert.Contains(@"<int name=""FramerateCap"">200</int>", updated);
            }
            finally
            {
                if (File.Exists(tempXml))
                    File.Delete(tempXml);
            }
        }

        [Fact]
        public void PatchSettings_WithSettingsPathOverride_AppliesPotatoModeAndFpsLimit()
        {
            string tempXml = Path.Combine(Path.GetTempPath(), $"Shiro_GBS_{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(tempXml, @"<roblox><Item class=""UserGameSettings""><Properties><int name=""GraphicsQualityLevel"">10</int><int name=""FramerateCap"">-1</int></Properties></Item></roblox>");

                AppSettings.General.Set("EnablePotatoMode", "true");
                AppSettings.General.Set("UnlockFPS", "true");
                AppSettings.General.Set("MaxFPSValue", "200");

                ClientSettingsPatcher.PatchSettings(settingsPathOverride: tempXml);

                string updated = File.ReadAllText(tempXml);
                Assert.Contains(@"<int name=""GraphicsQualityLevel"">1</int>", updated);
                Assert.Contains(@"<int name=""FramerateCap"">200</int>", updated);
            }
            finally
            {
                AppSettings.General.Set("EnablePotatoMode", "false");
                AppSettings.General.Set("UnlockFPS", "false");
                AppSettings.General.Set("MaxFPSValue", "144");
                if (File.Exists(tempXml))
                    File.Delete(tempXml);
            }
        }

        [Fact]
        public void ClientSettingsPatcher_SelfClosingPropertiesAndNegativeGraphics_InsertsAndReplacesWithoutScanningHost()
        {
            string selfClosingXml = @"<roblox><Item class=""UserGameSettings"" referent=""RBX0""><Properties /></Item></roblox>";
            string expanded = ClientSettingsPatcher.UpdateBasicSettingsXmlContent(
                selfClosingXml,
                graphicsQualityLevel: 1,
                fullscreen: false,
                out bool modifiedSelfClosing,
                framerateCap: 144);

            Assert.True(modifiedSelfClosing);
            var parsedDoc = XDocument.Parse(expanded);
            var props = parsedDoc.Descendants("Properties").Single();
            Assert.Equal("1", props.Elements("int").Single(e => (string?)e.Attribute("name") == "GraphicsQualityLevel").Value);
            Assert.Equal("false", props.Elements("bool").Single(e => (string?)e.Attribute("name") == "Fullscreen").Value);
            Assert.Equal("144", props.Elements("int").Single(e => (string?)e.Attribute("name") == "FramerateCap").Value);

            string negativeGraphicsXml = @"<roblox><Item class=""UserGameSettings""><Properties><int name=""GraphicsQualityLevel"">-1</int></Properties></Item></roblox>";
            string replacedNegative = ClientSettingsPatcher.UpdateBasicSettingsXmlContent(
                negativeGraphicsXml,
                graphicsQualityLevel: 1,
                fullscreen: null,
                out bool modifiedNegative,
                framerateCap: null);

            Assert.True(modifiedNegative);
            Assert.Contains(@"<int name=""GraphicsQualityLevel"">1</int>", replacedNegative);

            string tempXml = Path.Combine(Path.GetTempPath(), $"Shiro_GBS_Batch_{Guid.NewGuid():N}.xml");
            var ini = new IniFile();
            var general = ini["General"];
            general.Set("EnablePotatoMode", "true");
            general.Set("UnlockFPS", "true");
            general.Set("MaxFPSValue", "165");
            general.Set("PurgeClientSettings", "false");
            int hostScans = 0;

            using var settingsScope = AppServices.Override<ISettingsService>(general);
            var prevScanHook = ClientSettingsPatcher.TestOnFindRobloxVersionFoldersCalled;
            try
            {
                File.WriteAllText(tempXml, selfClosingXml);
                ClientSettingsPatcher.TestOnFindRobloxVersionFoldersCalled = () => hostScans++;

                ClientSettingsPatcher.PatchSettings(settingsPathOverride: tempXml);
                ClientSettingsPatcher.ApplyFpsLimit(unlockFps: true, targetFps: 165, xmlPathOverride: tempXml);

                Assert.Equal(0, hostScans);
                var patchedDoc = XDocument.Parse(File.ReadAllText(tempXml));
                var patchedProps = patchedDoc.Descendants("Properties").Single();
                Assert.Equal("1", patchedProps.Elements("int").Single(e => (string?)e.Attribute("name") == "GraphicsQualityLevel").Value);
                Assert.Equal("165", patchedProps.Elements("int").Single(e => (string?)e.Attribute("name") == "FramerateCap").Value);
            }
            finally
            {
                ClientSettingsPatcher.TestOnFindRobloxVersionFoldersCalled = prevScanHook;
                if (File.Exists(tempXml))
                    File.Delete(tempXml);
            }
        }
    }
}
