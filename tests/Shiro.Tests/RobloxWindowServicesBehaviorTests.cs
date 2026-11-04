#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class RobloxWindowServicesBehaviorTests
    {
        [Theory]
        [InlineData(0, 1, 1)]
        [InlineData(1, 1, 1)]
        [InlineData(2, 2, 1)]
        [InlineData(4, 2, 2)]
        [InlineData(6, 3, 2)]
        [InlineData(9, 3, 3)]
        public void CalculateOptimalGrid_ComputesExpectedColsAndRows(int windowCount, int expectedCols, int expectedRows)
        {
            var (cols, rows) = RobloxWindowService.CalculateOptimalGrid(windowCount);
            Assert.Equal(expectedCols, cols);
            Assert.Equal(expectedRows, rows);
        }

        [Fact]
        public void CalculateOptimalGrid_WithCustomDimensions_HonorsOverrides()
        {
            var (cols, rows) = RobloxWindowService.CalculateOptimalGrid(4, customCols: 4, customRows: 1);
            Assert.Equal(4, cols);
            Assert.Equal(1, rows);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("Chrome_WidgetWin_0", true)]
        [InlineData("CEF_SubProcess", true)]
        [InlineData("CiceroUIWndFrame", true)]
        [InlineData("Device_Direct3D_Window", true)]
        [InlineData("Window_GDI+_Class", true)]
        [InlineData("WorkerW", true)]
        [InlineData("RobloxPlayerBeta", false)]
        public void IsExcludedUtilityClass_IdentifiesSystemClasses(string? className, bool expected)
        {
            bool excluded = RobloxWindowService.IsExcludedUtilityClass(className!);
            Assert.Equal(expected, excluded);
        }

        [Fact]
        public void GetTargetWorkArea_ReturnsValidDesktopRect()
        {
            var area = RobloxWindowService.GetTargetWorkArea();
            Assert.InRange(area.Width, 1, 100000);
            Assert.InRange(area.Height, 1, 100000);
        }

        [Fact]
        public void WindowPlacements_CalculatePlacements_ReturnsCalculatedSlots()
        {
            var area = new RobloxWindowService.DesktopRect(0, 0, 1920, 1080);
            var slots0 = RobloxWindowArrangerService.CalculatePlacements(area, 0, RobloxWindowLayoutMode.AutoGrid);
            Assert.Empty(slots0);

            var slots4 = RobloxWindowArrangerService.CalculatePlacements(area, 4, RobloxWindowLayoutMode.Grid2x2);
            Assert.Equal(4, slots4.Count);
            Assert.Equal(960, slots4[0].Width);
            Assert.Equal(540, slots4[0].Height);

            var slots3 = RobloxWindowArrangerService.CalculatePlacements(area, 6, RobloxWindowLayoutMode.Grid3x2);
            Assert.Equal(6, slots3.Count);
            Assert.Equal(640, slots3[0].Width);
            Assert.Equal(540, slots3[0].Height);
        }

        [Fact]
        public void ResolveRunningAccountPids_NullOrEmpty_ReturnsEmptyDictionary()
        {
            var r1 = RobloxWindowArrangerService.ResolveRunningAccountPids(null);
            Assert.Empty(r1);

            var r2 = RobloxWindowArrangerService.ResolveRunningAccountPids(new List<Account>());
            Assert.Empty(r2);
        }

        [Fact]
        public void FocusAccountWindow_WithNull_ReturnsFalse()
        {
            bool focused = RobloxWindowArrangerService.FocusAccountWindow(null);
            Assert.False(focused);
        }

        [Fact]
        public void RobloxAudioService_SmartMute_ConfiguresState()
        {
            RobloxAudioService.ConfigureSmartMute(true);
            Assert.True(RobloxAudioService.IsSmartMuteEnabled);

            RobloxAudioService.ConfigureSmartMute(false);
            Assert.False(RobloxAudioService.IsSmartMuteEnabled);
        }

        [Fact]
        public void RobloxAudioService_EvaluateWithPids_WithNullOrEmpty_ReturnsSafely()
        {
            var ex1 = Record.Exception(() => RobloxAudioService.EvaluateWithPids(0, null!));
            Assert.Null(ex1);

            var ex2 = Record.Exception(() => RobloxAudioService.EvaluateWithPids(1234, new List<int>()));
            Assert.Null(ex2);
        }

        [Fact]
        public void StreamerModeService_Masking_MasksSensitiveInformation()
        {
            Assert.Equal(string.Empty, StreamerModeService.MaskUsername(null));
            Assert.Equal(string.Empty, StreamerModeService.MaskUsername(""));
            Assert.Equal("*** R$", StreamerModeService.MaskRobux(5000));
            Assert.Equal("***-***", StreamerModeService.MaskJobId("job-xyz-123"));

            string masked = StreamerModeService.MaskUsername("VeryLongPlayerName");
            Assert.StartsWith("V", masked);
            Assert.Contains("*", masked);
        }

        [Fact]
        public void StreamerModeService_Properties_ReflectState()
        {
            StreamerModeService.Initialize();
            Assert.False(StreamerModeService.IsEnabled);
            Assert.False(StreamerModeService.AutoDetectEnabled);
            Assert.False(StreamerModeService.IsActive);
        }

        [Fact]
        public void RobloxProcessOptimizer_SafelyRejectsInvalidPids()
        {
            bool prio = RobloxProcessOptimizer.SetProcessPriority(-1, RobloxProcessOptimizer.NORMAL_PRIORITY_CLASS);
            Assert.False(prio);

            bool aff = RobloxProcessOptimizer.SetProcessAffinity(-1, 0);
            Assert.False(aff);

            int lowered = RobloxProcessOptimizer.LowerPriorityForPids(null!);
            Assert.Equal(0, lowered);

            int restored = RobloxProcessOptimizer.RestorePriorityForPids(null!);
            Assert.Equal(0, restored);

            var ex = Record.Exception(() => RobloxProcessOptimizer.OptimizeLaunchedProcess(-1));
            Assert.Null(ex);
        }

        [Fact]
        public void ProcessExtensions_GetCommandLine_HandlesEdgeCases()
        {
            Assert.Null(ProcessExtensions.GetCommandLine(null));
            Assert.Null(ProcessExtensions.GetCommandLineByPid(-1));
            Assert.Null(ProcessExtensions.GetCommandLineByPid(0));

            using var current = Process.GetCurrentProcess();
            string? cmd = current.GetCommandLine();
            Assert.NotNull(cmd);
            Assert.NotEmpty(cmd!);
        }

        [Fact]
        public void ClipboardHelper_SetTextSafe_ExecutesCleanly()
        {
            var ex1 = Record.Exception(() => ClipboardHelper.SetClipboardTextSafe(null!));
            Assert.Null(ex1);

            var ex2 = Record.Exception(() => ClipboardHelper.SetText("Shiro Unit Test Text"));
            Assert.Null(ex2);
        }

        [Fact]
        public void Win32TrayHelper_QueueAndRefresh_ExecutesCleanly()
        {
            var ex1 = Record.Exception(() => Win32TrayHelper.QueueRefreshNotificationArea(100));
            Assert.Null(ex1);

            var ex2 = Record.Exception(() => Win32TrayHelper.RefreshNotificationArea());
            Assert.Null(ex2);

            var ex3 = Record.Exception(() => Win32TrayHelper.ThoroughCleanNotificationArea(false));
            Assert.Null(ex3);
        }

        [Fact]
        public void ForegroundWindowService_Properties_ReflectState()
        {
            Assert.False(ForegroundWindowService.IsHookActive);
            uint lastPid = ForegroundWindowService.LastForegroundProcessId;
            Assert.Equal(lastPid, ForegroundWindowService.LastForegroundProcessId);
        }

        [Fact]
        public void EvaluateRobloxWindow_HandlesZeroAndInvalidHwnd()
        {
            Assert.Equal(RobloxWindowService.WindowMatchQuality.None, RobloxWindowService.EvaluateRobloxWindow(IntPtr.Zero));
            Assert.Equal(string.Empty, RobloxWindowService.GetWindowClassName(IntPtr.Zero));
            Assert.Equal(string.Empty, RobloxWindowService.GetWindowTextString(IntPtr.Zero));
            Assert.Equal(IntPtr.Zero, RobloxWindowService.FindWindowForPid(0));
            Assert.Empty(RobloxWindowService.FindWindowsForPids(new HashSet<uint>()));
            var (hidden, _) = RobloxWindowService.HideAllWindows();
            Assert.InRange(hidden, 0, int.MaxValue);
            var (shown, _) = RobloxWindowService.ShowAllWindows();
            Assert.InRange(shown, 0, int.MaxValue);
        }
    }
}
