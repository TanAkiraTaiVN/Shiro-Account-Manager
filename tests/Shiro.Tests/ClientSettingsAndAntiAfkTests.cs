using System;
using System.IO;
using System.Text.Json;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class ClientSettingsAndAntiAfkTests
    {
        [Fact]
        public void UpdateBasicSettingsXmlContent_ReplacesGraphicsFullscreenAndFramerateCap()
        {
            string xml = @"<roblox><Item class=""UserGameSettings""><Properties><int name=""GraphicsQualityLevel"">10</int><bool name=""Fullscreen"">true</bool><int name=""FramerateCap"">-1</int></Properties></Item></roblox>";

            string updated = ClientSettingsPatcher.UpdateBasicSettingsXmlContent(
                xml,
                graphicsQualityLevel: 1,
                fullscreen: false,
                out bool modified,
                framerateCap: 240);

            Assert.True(modified);
            Assert.Contains(@"<int name=""GraphicsQualityLevel"">1</int>", updated);
            Assert.Contains(@"<bool name=""Fullscreen"">false</bool>", updated);
            Assert.Contains(@"<int name=""FramerateCap"">240</int>", updated);
        }

        [Fact]
        public void ApplyFpsLimitToDirectory_ScrubsLegacyFpsFlag_WithoutWritingFastFlag()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroFpsPatchTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string settingsDir = Path.Combine(tempDir, "ClientSettings");
                Directory.CreateDirectory(settingsDir);
                string jsonPath = Path.Combine(settingsDir, "ClientAppSettings.json");
                File.WriteAllText(jsonPath, "{\"CustomExistingFlag\":true,\"DFIntTaskSchedulerTargetFps\":144}");

                // 1. Unlock FPS -> scrubs DFIntTaskSchedulerTargetFps and does not write it back
                ClientSettingsPatcher.ApplyFpsLimitToDirectory(tempDir, unlockFps: true, targetFps: 240);
                using (var doc = JsonDocument.Parse(File.ReadAllText(jsonPath)))
                {
                    Assert.True(doc.RootElement.GetProperty("CustomExistingFlag").GetBoolean());
                    Assert.False(doc.RootElement.TryGetProperty(ClientSettingsPatcher.FpsFlagKey, out _));
                }

                // 2. Disable FPS unlock -> ensures DFIntTaskSchedulerTargetFps remains absent
                ClientSettingsPatcher.ApplyFpsLimitToDirectory(tempDir, unlockFps: false, targetFps: 144);
                using (var doc2 = JsonDocument.Parse(File.ReadAllText(jsonPath)))
                {
                    Assert.True(doc2.RootElement.GetProperty("CustomExistingFlag").GetBoolean());
                    Assert.False(doc2.RootElement.TryGetProperty(ClientSettingsPatcher.FpsFlagKey, out _));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
            }
        }

        [Fact]
        public void AntiAfkService_RemoveClientState_SafelyRemovesTrackedPid()
        {
            Assert.False(AntiAfkService.RemoveClientState(0));
            Assert.False(AntiAfkService.RemoveClientState(888888));
        }
    }
}
