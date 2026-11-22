using System;
using System.IO;
using System.Text.Json;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
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
        public void ScrubLegacyFpsFastFlag_ScrubsLegacyFpsFlag_WithoutWritingFastFlag()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroFpsPatchTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string settingsDir = Path.Combine(tempDir, "ClientSettings");
                Directory.CreateDirectory(settingsDir);
                string jsonPath = Path.Combine(settingsDir, "ClientAppSettings.json");
                File.WriteAllText(jsonPath, "{\"CustomExistingFlag\":true,\"DFIntTaskSchedulerTargetFps\":144}");

                // 1. Scrubs DFIntTaskSchedulerTargetFps and does not write it back
                ClientSettingsPatcher.ScrubLegacyFpsFastFlag(tempDir);
                using (var doc = JsonDocument.Parse(File.ReadAllText(jsonPath)))
                {
                    Assert.True(doc.RootElement.GetProperty("CustomExistingFlag").GetBoolean());
                    Assert.False(doc.RootElement.TryGetProperty(ClientSettingsPatcher.FpsFlagKey, out _));
                }

                // 2. Repeated scrub ensures DFIntTaskSchedulerTargetFps remains absent
                ClientSettingsPatcher.ScrubLegacyFpsFastFlag(tempDir);
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

        [Theory]
        [InlineData("ToolbarWindow32", true)]
        [InlineData("SysPager", true)]
        [InlineData("TrayNotifyWnd", true)]
        [InlineData("Windows.UI.Composition.DesktopWindowContentBridge", true)]
        [InlineData("toolbarwindow32", true)]
        [InlineData("syspager", true)]
        [InlineData("traynotifywnd", true)]
        [InlineData("windows.ui.composition.desktopwindowcontentbridge", true)]
        [InlineData("Button", false)]
        [InlineData("", false)]
        [InlineData("Static", false)]
        [InlineData("WorkerW", false)]
        public void Win32TrayHelper_IsTrayNotificationChildClass_MatchesExpectedClasses(string className, bool expected)
        {
            bool actual = Win32TrayHelper.IsTrayNotificationChildClass(className.AsSpan());
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void Win32TrayHelper_ThoroughCleanNotificationArea_ExecutesCleanlyWithBothFlags()
        {
            var exFalse = Record.Exception(() => Win32TrayHelper.ThoroughCleanNotificationArea(forceOpenFlyout: false));
            var exTrue = Record.Exception(() => Win32TrayHelper.ThoroughCleanNotificationArea(forceOpenFlyout: true));
            Assert.Null(exFalse);
            Assert.Null(exTrue);
        }

        [Fact]
        public void AntiAfkService_FocuslessAdvisory_LogsOncePerCycleAndResets()
        {
            // Reset before test
            AntiAfkService.ResetFocuslessAdvisory();
            Assert.False(AntiAfkService.HasLoggedFocuslessAdvisory);

            // When foreground fallback is allowed, advisory should NOT be logged
            bool loggedWhenAllowed = AntiAfkService.MaybeLogFocuslessAdvisory(allowForegroundFallback: true);
            Assert.False(loggedWhenAllowed);
            Assert.False(AntiAfkService.HasLoggedFocuslessAdvisory);

            // When foreground fallback is disallowed, advisory should be logged exactly once
            bool loggedFirstTime = AntiAfkService.MaybeLogFocuslessAdvisory(allowForegroundFallback: false);
            Assert.True(loggedFirstTime);
            Assert.True(AntiAfkService.HasLoggedFocuslessAdvisory);

            // Second attempt in same cycle must be suppressed (no spam)
            bool loggedSecondTime = AntiAfkService.MaybeLogFocuslessAdvisory(allowForegroundFallback: false);
            Assert.False(loggedSecondTime);
            Assert.True(AntiAfkService.HasLoggedFocuslessAdvisory);

            // Calling SetConfig / Reset should clear the flag for the next session
            AntiAfkService.ResetFocuslessAdvisory();
            Assert.False(AntiAfkService.HasLoggedFocuslessAdvisory);

            // New cycle logs once again
            bool loggedNewCycle = AntiAfkService.MaybeLogFocuslessAdvisory(allowForegroundFallback: false);
            Assert.True(loggedNewCycle);
            Assert.True(AntiAfkService.HasLoggedFocuslessAdvisory);

            // Clean up
            AntiAfkService.ResetFocuslessAdvisory();
        }
    }
}
