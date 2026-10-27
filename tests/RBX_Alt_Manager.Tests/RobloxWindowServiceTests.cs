using System;
using Xunit;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.Tests
{
    public class RobloxWindowServiceTests
    {
        [Theory]
        [InlineData(1, 1, 1)]
        [InlineData(2, 2, 1)]
        [InlineData(3, 2, 2)]
        [InlineData(4, 2, 2)]
        [InlineData(5, 3, 2)]
        [InlineData(6, 3, 2)]
        [InlineData(7, 3, 3)]
        [InlineData(9, 3, 3)]
        [InlineData(10, 4, 3)]
        [InlineData(12, 4, 3)]
        [InlineData(16, 4, 4)]
        [InlineData(20, 5, 4)]
        public void CalculateOptimalGrid_AutoMode_ReturnsExpectedDimensions(int windowCount, int expectedCols, int expectedRows)
        {
            var (cols, rows) = RobloxWindowService.CalculateOptimalGrid(windowCount, 0, 0);

            Assert.Equal(expectedCols, cols);
            Assert.Equal(expectedRows, rows);
            Assert.True(cols * rows >= windowCount, "Grid must hold at least all windows");
        }

        [Fact]
        public void CalculateOptimalGrid_CustomPresetWithinCapacity_RetainsPreset()
        {
            // 3 windows in 2x2 grid
            var (cols, rows) = RobloxWindowService.CalculateOptimalGrid(3, 2, 2);

            Assert.Equal(2, cols);
            Assert.Equal(2, rows);
        }

        [Fact]
        public void CalculateOptimalGrid_CustomPresetOverflow_ExpandsRowsDynamically()
        {
            // 5 windows requested with 2x2 preset -> must expand rows to 3 so windows don't spill off-screen
            var (cols, rows) = RobloxWindowService.CalculateOptimalGrid(5, 2, 2);

            Assert.Equal(2, cols);
            Assert.Equal(3, rows);
            Assert.True(cols * rows >= 5);
        }

        [Theory]
        [InlineData("Chrome_WidgetWin_0", true)]
        [InlineData("Chrome_WidgetWin_1", true)]
        [InlineData("CEF-OSC-WIDGET", true)]
        [InlineData("CiceroUIWndFrame", true)]
        [InlineData("Direct3DWindowClass", true)]
        [InlineData("GDI+ Hook Window Class", true)]
        [InlineData("WorkerW", true)]
        [InlineData("WINDOWSCLIENT", false)]
        [InlineData("WindowsClient", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsExcludedUtilityClass_IdentifiesKnownBackgroundClasses(string? className, bool shouldExclude)
        {
            bool isExcluded = RobloxWindowService.IsExcludedUtilityClass(className!);
            Assert.Equal(shouldExclude, isExcluded);
        }
    }
}
