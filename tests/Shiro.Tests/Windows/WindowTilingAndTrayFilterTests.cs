using System;
using System.Collections.Generic;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class WindowTilingAndTrayFilterTests
    {
        [Fact]
        public void ComputeTilePlacements_CalculatesExactCoordinatesForFourWindows()
        {
            var windows = new List<IntPtr> { (IntPtr)101, (IntPtr)102, (IntPtr)103, (IntPtr)104 };
            var workArea = new RobloxWindowService.DesktopRect(0, 0, 1920, 1080);

            var (cols, rows, placements) = RobloxWindowService.ComputeTilePlacements(windows, workArea);

            Assert.Equal(2, cols);
            Assert.Equal(2, rows);
            Assert.Equal(4, placements.Count);
            Assert.Equal(((IntPtr)101, 0, 0, 960, 540), placements[0]);
            Assert.Equal(((IntPtr)102, 960, 0, 960, 540), placements[1]);
            Assert.Equal(((IntPtr)103, 0, 540, 960, 540), placements[2]);
            Assert.Equal(((IntPtr)104, 960, 540, 960, 540), placements[3]);
        }

        [Theory]
        [InlineData("RobloxPlayerBeta.exe --app -t ticket -j url", false)]
        [InlineData("RobloxPlayerBeta.exe --launch-to-tray", true)]
        [InlineData("RobloxPlayerBeta.exe --LAUNCH-TO-TRAY --watcher", true)]
        [InlineData(null, false)]
        [InlineData("", false)]
        public void IsLaunchToTrayCommandLine_DetectsTrayDaemonFlag(string? cmd, bool expected)
        {
            Assert.Equal(expected, ProcessExtensions.IsLaunchToTrayCommandLine(cmd));
        }

        [Fact]
        public void IsLaunchToTrayProcess_UsesCommandLineOverride()
        {
            var prev = ProcessExtensions.TestCommandLineOverride;
            try
            {
                ProcessExtensions.TestCommandLineOverride = pid =>
                    pid == 4242 ? "C:\\RobloxPlayerBeta.exe --launch-to-tray" : "C:\\RobloxPlayerBeta.exe --app";

                Assert.True(ProcessExtensions.IsLaunchToTrayProcess(4242));
                Assert.False(ProcessExtensions.IsLaunchToTrayProcess(4243));
            }
            finally
            {
                ProcessExtensions.TestCommandLineOverride = prev;
            }
        }
    }
}
