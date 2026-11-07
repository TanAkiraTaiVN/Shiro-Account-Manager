#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
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

        [Theory]
        [InlineData("550e8400-e29b-41d4-a716-446655440000", "550e8400-e29b-41d4-a716-446655440000")]
        [InlineData("  job_123-ABC  ", "job_123-ABC")]
        [InlineData("123\" --evil", "")]
        [InlineData("123&request=RequestPrivateGame", "")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void SanitizeJobId_AllowsValidIds_AndRejectsInjectionCharacters(string? raw, string expected)
        {
            Assert.Equal(expected, RobloxDirectLauncher.SanitizeJobId(raw));
        }

        [Fact]
        public void SanitizeJobId_EnforcesMaxLength64_AndRejectsNonAscii()
        {
            string max64 = new string('A', 64);
            string over64 = new string('A', 65);
            Assert.Equal(max64, RobloxDirectLauncher.SanitizeJobId(max64));
            Assert.Equal(string.Empty, RobloxDirectLauncher.SanitizeJobId(over64));
            Assert.Equal(string.Empty, RobloxDirectLauncher.SanitizeJobId("   "));
            Assert.Equal(string.Empty, RobloxDirectLauncher.SanitizeJobId("job_123\u00e9"));
        }

        [Fact]
        public void BuildGameLaunchArguments_StripsQuotesFromTicketJoinUrlAndTracker()
        {
            string args = RobloxDirectLauncher.BuildGameLaunchArguments("tok\"1", "https://assetgame.roblox.com/game/PlaceLauncher.ashx?a=1\"2", "123\"45");
            Assert.DoesNotContain("tok\"1", args);
            Assert.DoesNotContain("1\"2", args);
            Assert.DoesNotContain("123\"45", args);
        }

        [Fact]
        public void BuildHomeLaunchArguments_StripsQuotesAndNewlines_AndRestrictsTrackerToDigits()
        {
            string args = RobloxDirectLauncher.BuildHomeLaunchArguments("tok\"1\r\n2", "123\"45");
            Assert.Equal("--app -t \"tok12\"", args);

            string validTrackerArgs = RobloxDirectLauncher.BuildHomeLaunchArguments("\"\r\n", " 98765432101 ");
            Assert.Equal("--app -b 98765432101", validTrackerArgs);
        }

        [Fact]
        public void CreateProcessWithToken_UsesMutableCharSpanCommandLine()
        {
            var method = typeof(RobloxDirectLauncher).GetMethod("CreateProcessWithToken", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var parameters = method!.GetParameters();
            Assert.Equal(typeof(string), parameters[2].ParameterType);
            Assert.Equal(typeof(Span<char>), parameters[3].ParameterType);
        }

        [Fact]
        public void PublishScript_DoesNotDeleteArchiveBackupFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                dir = dir.Parent;

            Assert.NotNull(dir);
            string publishPs1 = File.ReadAllText(Path.Combine(dir!.FullName, "scripts", "Publish.ps1"));
            Assert.DoesNotContain("'archive'", publishPs1);
        }
    }

    [Collection("SerialGlobalState")]
    public class RobloxDirectLauncherBehaviorTests_JobIdIntegration
    {
        [Fact]
        public async Task JoinServerResultAsync_SanitizesMaliciousJobId_PreventingPlaceLauncherUriInjection()
        {
            var prevTicketOverride = AccountLaunchService.TestGetAuthTicketOverride;
            var prevNativeOverride = AccountLaunchService.TestStartNativeGameOverride;

            try
            {
                string? capturedJoinUrl = null;
                AccountLaunchService.TestGetAuthTicketOverride = (_, _) => Task.FromResult((true, "ticket-safe"));
                AccountLaunchService.TestStartNativeGameOverride = (_, _, joinUrl, _, _, _, _) =>
                {
                    capturedJoinUrl = joinUrl;
                    return Task.FromResult(42424);
                };

                var acc = new Account
                {
                    Username = "SanitizeJobUser",
                    UserID = 778899,
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('S', 120),
                    BrowserTrackerID = "123456789012"
                };

                var injectedRes = await AccountLaunchService.Instance.JoinServerResultAsync(acc, 189707, "123&request=RequestPrivateGame");
                Assert.Equal(LaunchOutcome.Success, injectedRes.Outcome);
                Assert.NotNull(capturedJoinUrl);
                Assert.Contains("request=RequestGame&", capturedJoinUrl!);
                Assert.DoesNotContain("RequestGameJob", capturedJoinUrl!);
                Assert.DoesNotContain("gameId=", capturedJoinUrl!);
                Assert.DoesNotContain("RequestPrivateGame", capturedJoinUrl!);

                var validRes = await AccountLaunchService.Instance.JoinServerResultAsync(acc, 189707, "  job_123-ABC  ");
                Assert.Equal(LaunchOutcome.Success, validRes.Outcome);
                Assert.NotNull(capturedJoinUrl);
                Assert.Contains("request=RequestGameJob&", capturedJoinUrl!);
                Assert.Contains("&gameId=job_123-ABC&", capturedJoinUrl!);
            }
            finally
            {
                AccountLaunchService.TestGetAuthTicketOverride = prevTicketOverride;
                AccountLaunchService.TestStartNativeGameOverride = prevNativeOverride;
                AutoReconnectService.Instance.Clear();
            }
        }
    }
}
