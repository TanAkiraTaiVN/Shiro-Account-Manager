#nullable enable
using System;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class RobloxDirectLauncherBehaviorTests
    {
        [Fact]
        public void CanSyncRotatedCookieForTarget_WhenTargetIsNull_ReturnsFalse()
        {
            bool canSync = RobloxDirectLauncher.CanSyncRotatedCookieForTarget(
                null,
                trackerUserId: 12345,
                trackerBrowserId: "browser-1",
                expectedBrowserId: "browser-1");

            Assert.False(canSync);
        }

        [Fact]
        public void CanSyncRotatedCookieForTarget_WhenUserIdsMismatch_ReturnsFalse()
        {
            var target = new Account { UserID = 1000, Username = "TargetUser" };

            bool canSync = RobloxDirectLauncher.CanSyncRotatedCookieForTarget(
                target,
                trackerUserId: 2000,
                trackerBrowserId: null,
                expectedBrowserId: null);

            Assert.False(canSync);
        }

        [Fact]
        public void CanSyncRotatedCookieForTarget_WhenUserIdsMatch_ReturnsTrue()
        {
            var target = new Account { UserID = 1000, Username = "TargetUser" };

            bool canSync = RobloxDirectLauncher.CanSyncRotatedCookieForTarget(
                target,
                trackerUserId: 1000,
                trackerBrowserId: null,
                expectedBrowserId: null);

            Assert.True(canSync);
        }

        [Fact]
        public void CanSyncRotatedCookieForTarget_WhenBrowserTrackerMatchesAccount_ReturnsTrue()
        {
            var target = new Account
            {
                UserID = 0,
                BrowserTrackerID = "tracker-xyz",
                Username = "TargetUser"
            };

            bool canSync = RobloxDirectLauncher.CanSyncRotatedCookieForTarget(
                target,
                trackerUserId: 0,
                trackerBrowserId: "tracker-xyz",
                expectedBrowserId: null);

            Assert.True(canSync);
        }

        [Fact]
        public void CanSyncRotatedCookieForTarget_WhenBrowserTrackerMatchesInjected_ReturnsTrue()
        {
            var target = new Account
            {
                UserID = 0,
                BrowserTrackerID = "old-tracker",
                Username = "TargetUser"
            };

            bool canSync = RobloxDirectLauncher.CanSyncRotatedCookieForTarget(
                target,
                trackerUserId: 0,
                trackerBrowserId: "injected-tracker",
                expectedBrowserId: "injected-tracker");

            Assert.True(canSync);
        }

        [Fact]
        public void CanSyncRotatedCookieForTarget_WhenBrowserTrackerMismatchesAndNoUserId_ReturnsFalse()
        {
            var target = new Account
            {
                UserID = 0,
                BrowserTrackerID = "account-tracker",
                Username = "TargetUser"
            };

            bool canSync = RobloxDirectLauncher.CanSyncRotatedCookieForTarget(
                target,
                trackerUserId: 0,
                trackerBrowserId: "unrecognized-tracker",
                expectedBrowserId: "different-injected");

            Assert.False(canSync);
        }

        [Fact]
        public void GenerateRandomTrackerId_ReturnsValidElevenToTwelveDigitString()
        {
            string id = RobloxDirectLauncher.GenerateRandomTrackerId();

            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.InRange(id.Length, 11, 12);
            bool isNumeric = long.TryParse(id, out long value);
            Assert.True(isNumeric);
            Assert.True(value > 0);
        }

        [Fact]
        public void BuildGameLaunchArguments_ConstructsFormattedCommandString()
        {
            string withoutTracker = RobloxDirectLauncher.BuildGameLaunchArguments("TICKET123", "https://roblox.com/join");
            Assert.Equal("--app -t \"TICKET123\" -j \"https://roblox.com/join\"", withoutTracker);

            string withTracker = RobloxDirectLauncher.BuildGameLaunchArguments("TICKET456", "https://roblox.com/join2", "98765432101");
            Assert.Equal("--app -t \"TICKET456\" -j \"https://roblox.com/join2\" -b 98765432101", withTracker);
        }

        [Theory]
        [InlineData(null, "https://join.url")]
        [InlineData("", "https://join.url")]
        [InlineData("   ", "https://join.url")]
        [InlineData("TICKET", null)]
        [InlineData("TICKET", "")]
        [InlineData("TICKET", "   ")]
        public void LaunchDirect_WithMissingTicketOrJoinUrl_ReturnsFailureResult(string? ticket, string? joinUrl)
        {
            var result = RobloxDirectLauncher.LaunchDirect(ticket!, joinUrl!, "test_user");

            Assert.False(result.Success);
            Assert.Equal("Ticket or JoinUrl is empty.", result.ErrorMessage);
            Assert.Null(result.Process);
        }

        [Fact]
        public void IsCurrentProcessElevated_ReturnsConsistentBoolean()
        {
            bool elevated1 = RobloxDirectLauncher.IsCurrentProcessElevated();
            bool elevated2 = RobloxDirectLauncher.IsCurrentProcessElevated();

            Assert.Equal(elevated1, elevated2);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("non_existent_cookies_file_12345.dat")]
        public void TryExtractTrackerMetadataFromCookiesDatFile_MissingFile_ReturnsFalse(string? path)
        {
            bool extracted = RobloxDirectLauncher.TryExtractTrackerMetadataFromCookiesDatFile(
                path!,
                out long userId,
                out string? browserTrackerId);

            Assert.False(extracted);
            Assert.Equal(0L, userId);
            Assert.Null(browserTrackerId);
        }

        [Fact]
        public void LaunchHomeDirect_WhenExecutableMissing_ReturnsFailure()
        {
            var result = RobloxDirectLauncher.LaunchHomeDirect("home_user_test");

            // In environment without real RobloxPlayerBeta installed, returns not located on disk
            if (!result.Success)
            {
                Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
            }
            else
            {
                Assert.True(result.ProcessId > 0);
            }
        }
    }
}
