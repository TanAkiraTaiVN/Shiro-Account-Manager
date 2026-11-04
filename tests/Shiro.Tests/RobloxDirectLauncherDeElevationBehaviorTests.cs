#nullable enable
using System;
using System.Diagnostics;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class RobloxDirectLauncherDeElevationBehaviorTests
    {
        [Fact]
        public void IsCurrentProcessElevated_ReturnsValidBoolean()
        {
            bool isElevated = RobloxDirectLauncher.IsCurrentProcessElevated();
            Assert.Equal(isElevated, RobloxDirectLauncher.IsCurrentProcessElevated());
        }

        [Fact]
        public void SpawnRobloxProcess_StandardFallback_SpawnsSuccessfully()
        {
            AppSettings.General.Set(AppSettingsKeys.General.EnableTokenDeElevation, "false");
            using var proc = RobloxDirectLauncher.SpawnRobloxProcess("cmd.exe", "/c exit 0", "TestUser");
            Assert.NotNull(proc);
            proc!.WaitForExit(3000);
            Assert.True(proc.HasExited);
        }

        [Fact]
        public void TrySpawnDeElevated_ExecutesTokenDuplicationPipeline()
        {
            bool res = RobloxDirectLauncher.TrySpawnDeElevated("cmd.exe", "/c exit 0", out Process? deElevated);
            if (res && deElevated != null)
            {
                deElevated.WaitForExit(3000);
                deElevated.Dispose();
            }
            Assert.IsType<bool>(res);

            bool failRes = RobloxDirectLauncher.TrySpawnDeElevated("C:\\non_existent_executable_123456.exe", string.Empty, out _);
            Assert.False(failRes);
        }
    }
}
