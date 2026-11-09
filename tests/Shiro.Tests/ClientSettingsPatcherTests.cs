#nullable enable
using System;
using System.IO;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class ClientSettingsPatcherTests
    {
        [Fact]
        public void ApplyFpsLimitToDirectory_WhenUnlocked_GeneratesValidJsonWithTargetFps()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro_Patcher_Test_" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                ClientSettingsPatcher.ApplyFpsLimitToDirectory(tempDir, unlockFps: true, targetFps: 144);
                string jsonPath = Path.Combine(tempDir, "ClientSettings", "ClientAppSettings.json");
                Assert.True(File.Exists(jsonPath));
                string content = File.ReadAllText(jsonPath);
                Assert.Contains("\"DFIntTaskSchedulerTargetFps\": 144", content);
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
                File.WriteAllText(Path.Combine(settingsDir, "ClientAppSettings.json"), "{\"DFIntTaskSchedulerTargetFps\": 144, \"FStringCustom\": \"Test\"}");
                ClientSettingsPatcher.ApplyFpsLimitToDirectory(tempDir, unlockFps: false, targetFps: 60);
                string content = File.ReadAllText(Path.Combine(settingsDir, "ClientAppSettings.json"));
                Assert.DoesNotContain("DFIntTaskSchedulerTargetFps", content);
                Assert.Contains("FStringCustom", content);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
