#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class RobloxDirectLauncherCookieGuardTests
    {
        private const string TestToken = "_|WARNING:-DO-NOT-SHARE-THIS.--test-guard-cookie-payload-with-over-100-characters-for-validation-purposes-1234567890";

        private static void WithIsolatedLocalStorage(string prefix, Action<string> testBody, Action? extraCleanup = null)
        {
            string isolated = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(isolated);
            RobloxDirectLauncher.ResetCookieGuardStateForTest();
            RobloxDirectLauncher.TestDefaultLocalStorageDirOverride = isolated;
            try
            {
                testBody(isolated);
            }
            finally
            {
                RobloxDirectLauncher.ResetCookieGuardStateForTest();
                extraCleanup?.Invoke();
                if (Directory.Exists(isolated))
                {
                    try { Directory.Delete(isolated, true); }
                    catch (IOException) { }
                }
            }
        }

        private static async Task WithIsolatedLocalStorageAsync(string prefix, Func<string, Task> testBody, Action? extraCleanup = null)
        {
            string isolated = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(isolated);
            RobloxDirectLauncher.ResetCookieGuardStateForTest();
            RobloxDirectLauncher.TestDefaultLocalStorageDirOverride = isolated;
            try
            {
                await testBody(isolated);
            }
            finally
            {
                RobloxDirectLauncher.ResetCookieGuardStateForTest();
                extraCleanup?.Invoke();
                if (Directory.Exists(isolated))
                {
                    try { Directory.Delete(isolated, true); }
                    catch (IOException) { }
                }
            }
        }

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

                bool trackerExtracted = RobloxDirectLauncher.TryExtractTrackerMetadataFromCookiesDatFile(
                    cookiesPath,
                    out _,
                    out string? extractedBtid);
                Assert.True(trackerExtracted);
                Assert.Equal("btid_test_123", extractedBtid);
                Assert.DoesNotContain(";", extractedBtid ?? string.Empty);
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
        public void BuildHomeLaunchArguments_FormatsTicketAndTrackerId()
        {
            Assert.Equal("--app", RobloxDirectLauncher.BuildHomeLaunchArguments(null));
            Assert.Equal("--app", RobloxDirectLauncher.BuildHomeLaunchArguments(""));
            Assert.Equal("--app -t \"ticket_home_1\"", RobloxDirectLauncher.BuildHomeLaunchArguments("ticket_home_1"));
            Assert.Equal("--app -t \"ticket_home_1\" -b 123456789012", RobloxDirectLauncher.BuildHomeLaunchArguments("ticket_home_1", "123456789012"));
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
            WithIsolatedLocalStorage("Shiro-RestoreTest-", isolated =>
            {
                RobloxDirectLauncher.TestIsAnyRobloxRunningOverride = () => false;

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

                // 3. Schedule methods execute cleanly within isolated temp directory
                var ex = Record.Exception(() =>
                {
                    RobloxDirectLauncher.ScheduleOriginalCookieRestore(10);
                    RobloxDirectLauncher.SchedulePostSpawnCookieRestore(12345);
                    RobloxDirectLauncher.SchedulePostSpawnCookieRestore(0);
                });
                Assert.Null(ex);
            });
        }

        [Fact]
        public void SchedulePostSpawnCookieRestore_WhenNoBackupPending_IsNoOpAndDoesNotCancelActiveRestoreTimer()
        {
            var restoreCtsField = typeof(RobloxDirectLauncher).GetField("_restoreCts", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(restoreCtsField);

            WithIsolatedLocalStorage("Shiro-NoBackupSchedule-", _ =>
            {
                RobloxDirectLauncher.TestIsAnyRobloxRunningOverride = () => false;
                RobloxDirectLauncher.RestoreOriginalCookies();

                RobloxDirectLauncher.ScheduleOriginalCookieRestore(60_000);
                var initialCts = restoreCtsField!.GetValue(null) as CancellationTokenSource;
                Assert.NotNull(initialCts);
                Assert.False(initialCts!.IsCancellationRequested);

                RobloxDirectLauncher.SchedulePostSpawnCookieRestore(12345);

                var afterCts = restoreCtsField.GetValue(null) as CancellationTokenSource;
                Assert.Same(initialCts, afterCts);
                Assert.False(initialCts.IsCancellationRequested);
            });
        }

        [Fact]
        public async Task ScheduleOriginalCookieRestore_WhileRobloxRunning_DefersWithoutCancellingActiveTimer()
        {
            var restoreCtsField = typeof(RobloxDirectLauncher).GetField("_restoreCts", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(restoreCtsField);

            var prevPidOverride = RobloxProcessService.TestGetRunningRobloxPidsOverride;
            await WithIsolatedLocalStorageAsync(
                "Shiro-DeferSchedule-",
                async _ =>
                {
                    RobloxProcessService.TestGetRunningRobloxPidsOverride = () => new[] { Environment.ProcessId };
                    RobloxDirectLauncher.ScheduleOriginalCookieRestore(25);

                    var initialCts = restoreCtsField!.GetValue(null) as CancellationTokenSource;
                    Assert.NotNull(initialCts);

                    await Task.Delay(120);

                    var afterDelayCts = restoreCtsField.GetValue(null) as CancellationTokenSource;
                    Assert.False(initialCts!.IsCancellationRequested);
                    Assert.Same(initialCts, afterDelayCts);
                },
                extraCleanup: () => RobloxProcessService.TestGetRunningRobloxPidsOverride = prevPidOverride);
        }

        [Fact]
        public void RestoreOriginalCookies_WhenCookiesDatLocked_KeepsNoOrigSentinelIntact()
        {
            WithIsolatedLocalStorage("Shiro-LockedRestore-", isolated =>
            {
                string cookiesDat = Path.Combine(isolated, "RobloxCookies.dat");
                string sentinel = Path.Combine(isolated, "RobloxCookies.dat.shiro-none");
                File.WriteAllText(cookiesDat, "dummy-cookie-json");
                File.WriteAllText(sentinel, "1");

                using (new FileStream(cookiesDat, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    bool restored = RobloxDirectLauncher.RestoreOriginalCookies(isolated);
                    Assert.False(restored);
                    Assert.True(File.Exists(cookiesDat));
                    Assert.True(File.Exists(sentinel));
                }

                bool restoredAfterUnlock = RobloxDirectLauncher.RestoreOriginalCookies(isolated);
                Assert.True(restoredAfterUnlock);
                Assert.False(File.Exists(cookiesDat));
                Assert.False(File.Exists(sentinel));
            });
        }

        [Fact]
        public void CookieGuard_InjectAccountCookie_ZeroesPlainBytesInFinally()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string? repoRoot = null;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                {
                    repoRoot = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }

            Assert.NotNull(repoRoot);
            string sourcePath = Path.Combine(repoRoot!, "src", "Shiro", "Services", "Launch", "RobloxDirectLauncher.CookieGuard.cs");
            string source = File.ReadAllText(sourcePath);
            Assert.Contains("ZeroMemory", source, StringComparison.Ordinal);
            Assert.Contains("finally", source, StringComparison.Ordinal);
        }

        [Fact]
        public void ProtectAndZeroPlaintext_ZeroesPlainBytesOnSuccessAndOnException()
        {
            byte[] successPlain = System.Text.Encoding.UTF8.GetBytes("secret-cookie-token-1");
            byte[] encrypted = RobloxDirectLauncher.ProtectAndZeroPlaintext(
                successPlain,
                bytes => bytes.ToArray());

            Assert.NotEmpty(encrypted);
            Assert.All(successPlain, b => Assert.Equal(0, b));

            byte[] failurePlain = System.Text.Encoding.UTF8.GetBytes("secret-cookie-token-2");
            Assert.Throws<InvalidOperationException>(() =>
                RobloxDirectLauncher.ProtectAndZeroPlaintext(
                    failurePlain,
                    _ => throw new InvalidOperationException("DPAPI protect simulated failure")));

            Assert.All(failurePlain, b => Assert.Equal(0, b));
        }

        [Fact]
        public async Task SchedulePostSpawnCookieRestore_WithInjectedCookie_DefersWhileRobloxRunningAndRestoresOnExit()
        {
            await WithIsolatedLocalStorageAsync("Shiro-PostSpawnRestore-", async isolated =>
            {
                bool robloxRunning = true;
                int runningChecks = 0;
                RobloxDirectLauncher.TestIsAnyRobloxRunningOverride = () =>
                {
                    Interlocked.Increment(ref runningChecks);
                    return Volatile.Read(ref robloxRunning);
                };
                RobloxDirectLauncher.TestCookieGuardWaitForClientUnlockedOverride = (_, _, _) => Task.FromResult(true);
                RobloxDirectLauncher.TestPostSpawnSettleDelayMsOverride = 10;
                RobloxDirectLauncher.TestCookieRestoreRetryDelayMsOverride = 15;

                bool injected = RobloxDirectLauncher.InjectAccountCookieToLocalStorage(
                    TestToken, browserTrackerId: "123456789012", localStorageDirectoryOverride: null, username: "PostSpawnUser");
                Assert.True(injected);
                Assert.True(RobloxDirectLauncher.HasPendingCookieBackup);

                var restoreCtsField = typeof(RobloxDirectLauncher).GetField("_restoreCts", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(restoreCtsField);

                RobloxDirectLauncher.SchedulePostSpawnCookieRestore(44556);
                var activeCts = restoreCtsField!.GetValue(null) as CancellationTokenSource;
                Assert.NotNull(activeCts);

                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref runningChecks) >= 2, TimeSpan.FromSeconds(3)));
                Assert.False(activeCts!.IsCancellationRequested);
                Assert.Same(activeCts, restoreCtsField.GetValue(null));

                Volatile.Write(ref robloxRunning, false);
                Assert.True(SpinWait.SpinUntil(() => !RobloxDirectLauncher.HasPendingCookieBackup, TimeSpan.FromSeconds(3)));
                Assert.False(File.Exists(Path.Combine(isolated, "RobloxCookies.dat")));
                Assert.False(File.Exists(Path.Combine(isolated, "RobloxCookies.dat.shiro-none")));
                await Task.CompletedTask;
            });
        }

        [Fact]
        public void RestoreOriginalCookiesCore_DeleteFailureMock_PreservesSentinelAndPendingState()
        {
            WithIsolatedLocalStorage("Shiro-MockDeleteFail-", isolated =>
            {
                string cookiesDat = Path.Combine(isolated, "RobloxCookies.dat");
                string sentinel = Path.Combine(isolated, "RobloxCookies.dat.shiro-none");
                File.WriteAllText(cookiesDat, "cookie-temp");
                File.WriteAllText(sentinel, "1");
                Assert.True(RobloxDirectLauncher.HasPendingCookieBackup);

                bool failedRestore = RobloxDirectLauncher.RestoreOriginalCookiesCore(
                    localStorageDirectoryOverride: null,
                    deleteFileOverride: path =>
                    {
                        if (string.Equals(path, cookiesDat, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Simulated sharing violation");
                        File.Delete(path);
                    });

                Assert.False(failedRestore);
                Assert.True(File.Exists(cookiesDat));
                Assert.True(File.Exists(sentinel));
                Assert.True(RobloxDirectLauncher.HasPendingCookieBackup);

                Assert.True(RobloxDirectLauncher.RestoreOriginalCookiesCore(localStorageDirectoryOverride: null));
                Assert.False(File.Exists(cookiesDat));
                Assert.False(File.Exists(sentinel));
                Assert.False(RobloxDirectLauncher.HasPendingCookieBackup);
            });
        }
    }
}
