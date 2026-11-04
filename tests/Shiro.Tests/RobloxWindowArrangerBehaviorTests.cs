#nullable enable
using System;
using System.Collections.Generic;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class RobloxWindowArrangerBehaviorTests
    {
        private static readonly RobloxWindowService.DesktopRect SampleWorkArea = new()
        {
            Left = 0,
            Top = 0,
            Width = 1920,
            Height = 1080
        };

        [Fact]
        public void CalculatePlacements_ZeroOrNegativeCount_ReturnsEmpty()
        {
            var resZero = RobloxWindowArrangerService.CalculatePlacements(SampleWorkArea, 0, RobloxWindowLayoutMode.AutoGrid);
            Assert.Empty(resZero);

            var resNeg = RobloxWindowArrangerService.CalculatePlacements(SampleWorkArea, -5, RobloxWindowLayoutMode.Grid2x2);
            Assert.Empty(resNeg);
        }

        [Theory]
        [InlineData(1, RobloxWindowLayoutMode.AutoGrid)]
        [InlineData(4, RobloxWindowLayoutMode.Grid2x2)]
        [InlineData(6, RobloxWindowLayoutMode.Grid3x2)]
        [InlineData(12, RobloxWindowLayoutMode.Grid4x3)]
        [InlineData(5, RobloxWindowLayoutMode.AutoGrid)]
        public void CalculatePlacements_GridModes_FillsDeterministicSlots(int count, RobloxWindowLayoutMode mode)
        {
            var placements = RobloxWindowArrangerService.CalculatePlacements(SampleWorkArea, count, mode);

            Assert.Equal(count, placements.Count);
            foreach (var slot in placements)
            {
                Assert.InRange(slot.X, SampleWorkArea.Left, SampleWorkArea.Width);
                Assert.InRange(slot.Y, SampleWorkArea.Top, SampleWorkArea.Height);
                Assert.True(slot.Width > 0);
                Assert.True(slot.Height > 0);
            }
        }

        [Fact]
        public void CalculatePlacements_CascadeCorner_CalculatesOffsetStack()
        {
            var placements = RobloxWindowArrangerService.CalculatePlacements(SampleWorkArea, 3, RobloxWindowLayoutMode.CascadeCorner);

            Assert.Equal(3, placements.Count);
            Assert.Equal(placements[0].Width, placements[1].Width);
            Assert.Equal(placements[0].Height, placements[1].Height);
            Assert.True(placements[0].X >= placements[1].X);
            Assert.True(placements[0].Y >= placements[1].Y);
        }

        [Theory]
        [InlineData(RobloxWindowLayoutMode.AutoGrid)]
        [InlineData(RobloxWindowLayoutMode.Grid2x2)]
        [InlineData(RobloxWindowLayoutMode.Grid3x2)]
        [InlineData(RobloxWindowLayoutMode.Grid4x3)]
        [InlineData(RobloxWindowLayoutMode.CascadeCorner)]
        public void ArrangeWindows_ExecutesSafely(RobloxWindowLayoutMode mode)
        {
            var (arranged, desc) = RobloxWindowArrangerService.ArrangeWindows(mode);

            Assert.InRange(arranged, 0, int.MaxValue);
            Assert.False(string.IsNullOrWhiteSpace(desc));
        }

        [Fact]
        public void ResolveRunningAccountPids_NullOrEmpty_ReturnsEmptyDictionary()
        {
            var nullRes = RobloxWindowArrangerService.ResolveRunningAccountPids(null);
            Assert.Empty(nullRes);

            var emptyRes = RobloxWindowArrangerService.ResolveRunningAccountPids(Array.Empty<Account>());
            Assert.Empty(emptyRes);
        }

        [Fact]
        public void ResolveRunningAccountPids_DeadPidsAndInvalidTrackers_IgnoresAccounts()
        {
            var acc1 = new Account { Username = "Dead1", LastLaunchedProcessId = 999991 };
            var acc2 = new Account { Username = "Dead2", BrowserTrackerID = "999888777" };

            var dict = RobloxWindowArrangerService.ResolveRunningAccountPids(new[] { acc1, acc2 });

            Assert.Empty(dict);
        }

        [Fact]
        public void FocusAccountWindow_NullOrInactiveAccount_ReturnsFalse()
        {
            bool nullRes = RobloxWindowArrangerService.FocusAccountWindow(null);
            Assert.False(nullRes);

            var inactive = new Account { Username = "InactiveUser", LastLaunchedProcessId = 0 };
            bool inactiveRes = RobloxWindowArrangerService.FocusAccountWindow(inactive);
            Assert.False(inactiveRes);
        }
    }
}
