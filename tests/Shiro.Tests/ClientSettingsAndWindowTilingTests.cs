#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class ClientSettingsAndWindowTilingTests : IDisposable
    {
        private readonly string _tempDir;

        public ClientSettingsAndWindowTilingTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"ShiroPatchTile_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void ClientSettingsPatcher_ScrubLegacyFpsFastFlag_ScrubsLegacyFlagAndCleansFolder()
        {
            string versionFolder = Path.Combine(_tempDir, "version-12345");
            string settingsDir = Path.Combine(versionFolder, "ClientSettings");
            Directory.CreateDirectory(settingsDir);
            string jsonPath = Path.Combine(settingsDir, "ClientAppSettings.json");

            // 1. Legacy file with custom key + FPS key -> scrubs FPS, preserves custom key
            File.WriteAllText(jsonPath, "{\"DFIntTaskSchedulerTargetFps\": 144, \"FStringOther\": \"Value\"}");
            ClientSettingsPatcher.ScrubLegacyFpsFastFlag(versionFolder);
            Assert.True(File.Exists(jsonPath));
            string json1 = File.ReadAllText(jsonPath);
            Assert.DoesNotContain(ClientSettingsPatcher.FpsFlagKey, json1);
            Assert.Contains("FStringOther", json1);

            // 2. File with only legacy FPS key -> scrubs and deletes file + folder
            File.WriteAllText(jsonPath, "{\"DFIntTaskSchedulerTargetFps\": 240}");
            ClientSettingsPatcher.ScrubLegacyFpsFastFlag(versionFolder);
            Assert.False(File.Exists(jsonPath));
            Assert.False(Directory.Exists(settingsDir));
        }

        [Fact]
        public void RobloxWindowService_TilingHelpers_ExecuteSafely()
        {
            // 1. Empty PIDs
            var emptyList = RobloxWindowService.FindWindowsForPids(new HashSet<uint>());
            Assert.Empty(emptyList);

            var emptyMap = RobloxWindowService.FindWindowMapForPids(new HashSet<uint>());
            Assert.Empty(emptyMap);

            // 2. Safe enumeration for current process
            uint currentPid = (uint)Environment.ProcessId;
            IntPtr found = RobloxWindowService.FindWindowForPid(currentPid);
            Assert.IsType<IntPtr>(found);

            var pidsSet = new HashSet<uint> { currentPid };
            var map = RobloxWindowService.FindWindowMapForPids(pidsSet);
            Assert.NotNull(map);

            // 3. TileWindows when no clients running
            var (tiledCount, desc) = RobloxWindowService.TileWindows();
            Assert.InRange(tiledCount, 0, int.MaxValue);
            Assert.False(string.IsNullOrEmpty(desc));
        }

        [Fact]
        public void RobloxWindowService_UtilityAndVisibility_CoversBranches()
        {
            // Utility exclusions
            Assert.True(RobloxWindowService.IsExcludedUtilityClass("Chrome_WidgetWin_0"));
            Assert.True(RobloxWindowService.IsExcludedUtilityClass("CEF-OSC-WIDGET"));
            Assert.True(RobloxWindowService.IsExcludedUtilityClass("CiceroUIWndFrame"));
            Assert.False(RobloxWindowService.IsExcludedUtilityClass("RobloxPlayer"));
            Assert.False(RobloxWindowService.IsExcludedUtilityClass(string.Empty));
            Assert.False(RobloxWindowService.IsExcludedUtilityClass(null!));

            // Window evaluation and names
            Assert.Equal(RobloxWindowService.WindowMatchQuality.None, RobloxWindowService.EvaluateRobloxWindow(IntPtr.Zero));
            Assert.Equal(string.Empty, RobloxWindowService.GetWindowClassName(IntPtr.Zero));
            Assert.Equal(string.Empty, RobloxWindowService.GetWindowTextString(IntPtr.Zero));

            // Grid calculations
            var (c0, r0) = RobloxWindowService.CalculateOptimalGrid(0);
            Assert.Equal((1, 1), (c0, r0));

            var (cCustom, rCustom) = RobloxWindowService.CalculateOptimalGrid(4, 2, 2);
            Assert.Equal((2, 2), (cCustom, rCustom));

            var (cAuto, rAuto) = RobloxWindowService.CalculateOptimalGrid(6);
            Assert.InRange(cAuto, 1, 6);
            Assert.InRange(rAuto, 1, 6);

            // Work area
            var workArea = RobloxWindowService.GetTargetWorkArea();
            Assert.True(workArea.Width > 0 && workArea.Height > 0);

            // Visibility toggle and actions when no clients
            Assert.False(RobloxWindowService.AreWindowsHidden);
            var (hiddenCount, _) = RobloxWindowService.HideAllWindows();
            Assert.Equal(0, hiddenCount);

            var (shownCount, _) = RobloxWindowService.ShowAllWindows();
            Assert.Equal(0, shownCount);

            var (nowHidden, affected) = RobloxWindowService.ToggleVisibility();
            Assert.False(nowHidden);
            Assert.Equal(0, affected);
        }
    }
}
