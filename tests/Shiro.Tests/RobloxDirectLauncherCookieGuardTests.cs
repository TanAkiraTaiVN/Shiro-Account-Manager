#nullable enable
using System;
using System.IO;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class RobloxDirectLauncherCookieGuardTests
    {
        private const string TestToken = "_|WARNING:-DO-NOT-SHARE-THIS.--test-guard-cookie-payload-with-over-100-characters-for-validation-purposes-1234567890";

        [Fact]
        public void CookieGuardScope_ManagesActiveScopeCount()
        {
            int baseline = RobloxDirectLauncher.ActiveCookieGuardCount;

            using (var scope1 = RobloxDirectLauncher.BeginCookieGuardScope(5000))
            {
                Assert.True(RobloxDirectLauncher.IsCookieGuardHeld);
                Assert.Equal(baseline + 1, RobloxDirectLauncher.ActiveCookieGuardCount);

                using (var scope2 = RobloxDirectLauncher.BeginCookieGuardScope(5000))
                {
                    Assert.True(RobloxDirectLauncher.IsCookieGuardHeld);
                    Assert.Equal(baseline + 2, RobloxDirectLauncher.ActiveCookieGuardCount);
                }

                Assert.True(RobloxDirectLauncher.IsCookieGuardHeld);
                Assert.Equal(baseline + 1, RobloxDirectLauncher.ActiveCookieGuardCount);
            }

            Assert.Equal(baseline, RobloxDirectLauncher.ActiveCookieGuardCount);
            if (baseline == 0)
            {
                Assert.False(RobloxDirectLauncher.IsCookieGuardHeld);
            }
        }

        [Fact]
        public void CookieGuardScope_DoubleDispose_IsIdempotent()
        {
            int baseline = RobloxDirectLauncher.ActiveCookieGuardCount;
            var scope = RobloxDirectLauncher.BeginCookieGuardScope(5000);
            Assert.Equal(baseline + 1, RobloxDirectLauncher.ActiveCookieGuardCount);

            scope.Dispose();
            Assert.Equal(baseline, RobloxDirectLauncher.ActiveCookieGuardCount);

            // Second dispose should be a no-op
            scope.Dispose();
            Assert.Equal(baseline, RobloxDirectLauncher.ActiveCookieGuardCount);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void InjectAccountCookieToLocalStorage_EmptyToken_ReturnsFalse(string? token)
        {
            Assert.False(RobloxDirectLauncher.InjectAccountCookieToLocalStorage(token!));
        }

        [Fact]
        public void InjectAccountCookie_And_TryExtractCookie_RoundTripsSuccessfully()
        {
            string isolated = Path.Combine(Path.GetTempPath(), "Shiro-CookieGuard-" + Guid.NewGuid().ToString("N"));
            try
            {
                bool injected = RobloxDirectLauncher.InjectAccountCookieToLocalStorage(
                    TestToken,
                    browserTrackerId: "btid_test_123",
                    expiryUtc: DateTimeOffset.UtcNow.AddDays(30),
                    localStorageDirectoryOverride: isolated,
                    username: "GuardTestUser");

                Assert.True(injected);

                string cookiesPath = Path.Combine(isolated, "RobloxCookies.dat");
                Assert.True(File.Exists(cookiesPath));

                string? extracted = RobloxDirectLauncher.TryExtractCookieFromCookiesDatFile(cookiesPath);
                Assert.NotNull(extracted);
                Assert.Equal(TestToken, extracted);
            }
            finally
            {
                if (Directory.Exists(isolated))
                {
                    try { Directory.Delete(isolated, true); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public void InjectAccountCookie_UnsupportedVersion_SkipsAndReturnsFalse()
        {
            string isolated = Path.Combine(Path.GetTempPath(), "Shiro-CookieVer-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(isolated);
                string cookiesPath = Path.Combine(isolated, "RobloxCookies.dat");
                // Unsupported version "99"
                File.WriteAllText(cookiesPath, "{\"CookiesVersion\":\"99\",\"CookiesData\":\"dGVzdA==\"}");

                bool injected = RobloxDirectLauncher.InjectAccountCookieToLocalStorage(
                    TestToken,
                    localStorageDirectoryOverride: isolated);

                Assert.False(injected);
            }
            finally
            {
                if (Directory.Exists(isolated))
                {
                    try { Directory.Delete(isolated, true); }
                    catch (IOException) { }
                }
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("non_existent_cookies.dat")]
        public void TryExtractCookieFromCookiesDatFile_InvalidFile_ReturnsNull(string? path)
        {
            Assert.Null(RobloxDirectLauncher.TryExtractCookieFromCookiesDatFile(path!));
        }

        [Fact]
        public void TryExtractCookieFromCookiesDatFile_CorruptedContent_ReturnsNullSafely()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "corrupted_" + Guid.NewGuid().ToString("N") + ".dat");
            try
            {
                File.WriteAllText(tempFile, "{ not valid json at all }");
                Assert.Null(RobloxDirectLauncher.TryExtractCookieFromCookiesDatFile(tempFile));
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public void CanSyncRotatedCookieForTarget_ChecksUserIdAndBrowserTrackerMatches()
        {
            var target = new Shiro.Account
            {
                Username = "SyncTargetUser",
                UserID = 12345,
                BrowserTrackerID = "TRACKER_AAA"
            };

            // Null target -> false
            Assert.False(RobloxDirectLauncher.CanSyncRotatedCookieForTarget(null, 12345, "TRACKER_AAA", null));

            // Mismatched concrete UserID -> false
            Assert.False(RobloxDirectLauncher.CanSyncRotatedCookieForTarget(target, 99999, "TRACKER_AAA", null));

            // Matching concrete UserID -> true
            Assert.True(RobloxDirectLauncher.CanSyncRotatedCookieForTarget(target, 12345, null, null));

            // Matching Account BrowserTrackerID -> true
            Assert.True(RobloxDirectLauncher.CanSyncRotatedCookieForTarget(target, 0, "TRACKER_AAA", null));

            // Matching Injected BrowserTrackerID -> true
            Assert.True(RobloxDirectLauncher.CanSyncRotatedCookieForTarget(target, 0, "TRACKER_INJECTED", "TRACKER_INJECTED"));

            // Mismatched Tracker with no UserID -> false
            Assert.False(RobloxDirectLauncher.CanSyncRotatedCookieForTarget(target, 0, "TRACKER_UNKNOWN", "TRACKER_DIFFERENT"));
        }

        [Fact]
        public void GenerateRandomTrackerId_Generates12DigitNumericString()
        {
            string id = RobloxDirectLauncher.GenerateRandomTrackerId();
            Assert.InRange(id.Length, 11, 12);
            bool isNumber = long.TryParse(id, out long parsed);
            Assert.True(isNumber);
            Assert.True(parsed > 0);
        }

        [Fact]
        public void BuildGameLaunchArguments_FormatsCorrectCommandLine()
        {
            string argsWithoutTracker = RobloxDirectLauncher.BuildGameLaunchArguments("ticket123", "https://roblox.com/join");
            Assert.Equal("--app -t \"ticket123\" -j \"https://roblox.com/join\"", argsWithoutTracker);

            string argsWithTracker = RobloxDirectLauncher.BuildGameLaunchArguments("ticket123", "https://roblox.com/join", "987654321");
            Assert.Equal("--app -t \"ticket123\" -j \"https://roblox.com/join\" -b 987654321", argsWithTracker);
        }

        [Fact]
        public void LaunchDirect_EmptyTicketOrJoinUrl_ReturnsFailureResult()
        {
            var res1 = RobloxDirectLauncher.LaunchDirect("", "https://roblox.com/join", "User1");
            Assert.False(res1.Success);
            Assert.Equal("Ticket or JoinUrl is empty.", res1.ErrorMessage);

            var res2 = RobloxDirectLauncher.LaunchDirect("ticket123", "", "User1");
            Assert.False(res2.Success);
            Assert.Equal("Ticket or JoinUrl is empty.", res2.ErrorMessage);
        }

        [Fact]
        public void TryExtractTrackerMetadataFromCookiesDatFile_NonExistentFile_ReturnsFalse()
        {
            bool extracted = RobloxDirectLauncher.TryExtractTrackerMetadataFromCookiesDatFile(
                "non_existent_file.dat",
                out long userId,
                out string? browserTrackerId);

            Assert.False(extracted);
            Assert.Equal(0, userId);
            Assert.Null(browserTrackerId);
        }

        [Fact]
        public void RestoreOriginalCookies_WithIsolatedDirectory_RestoresAndCleansBackup()
        {
            string isolated = Path.Combine(Path.GetTempPath(), "Shiro-RestoreTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(isolated);

            try
            {
                // 1. When no backup exists
                bool restoredNoBackup = RobloxDirectLauncher.RestoreOriginalCookies(isolated);
                Assert.False(restoredNoBackup);

                // 2. When backup exists
                string cookiesDat = Path.Combine(isolated, "RobloxCookies.dat");
                string backup = Path.Combine(isolated, "RobloxCookies.dat.shiro-orig");
                File.WriteAllText(backup, "{\"CookiesVersion\":\"1\",\"CookiesData\":\"test\"}");

                bool restored = RobloxDirectLauncher.RestoreOriginalCookies(isolated);
                Assert.True(restored);
                Assert.True(File.Exists(cookiesDat));
                Assert.False(File.Exists(backup));

                // 3. Schedule methods execute cleanly
                var ex = Record.Exception(() =>
                {
                    RobloxDirectLauncher.ScheduleOriginalCookieRestore(10);
                    RobloxDirectLauncher.SchedulePostSpawnCookieRestore(12345);
                    RobloxDirectLauncher.SchedulePostSpawnCookieRestore(0);
                });
                Assert.Null(ex);
            }
            finally
            {
                if (Directory.Exists(isolated))
                {
                    try { Directory.Delete(isolated, true); }
                    catch (IOException) { }
                }
            }
        }
    }
}
