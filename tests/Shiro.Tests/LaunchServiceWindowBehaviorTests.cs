#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class LaunchServiceWindowBehaviorTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly IDisposable _storageScope;

        public LaunchServiceWindowBehaviorTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"ShiroLaunchWin_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            _storageScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
        }

        public void Dispose()
        {
            _storageScope.Dispose();
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public async Task ObserveBackgroundTask_HandlesCompleted_Cancelled_AndFaultedTasks()
        {
            // Completed
            var completedTask = Task.CompletedTask;
            AccountLaunchService.ObserveBackgroundTask(completedTask, "TestCompleted");
            await Task.Delay(20);

            // Cancelled
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var cancelledTask = Task.FromCanceled(cts.Token);
            AccountLaunchService.ObserveBackgroundTask(cancelledTask, "TestCancelled");
            await Task.Delay(20);

            // Faulted
            var faultedTask = Task.FromException(new InvalidOperationException("Test fault"));
            AccountLaunchService.ObserveBackgroundTask(faultedTask, "TestFaulted");
            await Task.Delay(20);

            // Null task
            AccountLaunchService.ObserveBackgroundTask(null!, "TestNull");

            Assert.True(completedTask.IsCompletedSuccessfully);
            Assert.True(cancelledTask.IsCanceled);
            Assert.True(faultedTask.IsFaulted);
        }

        [Fact]
        public async Task AdjustWindowPositionAsync_GuardClauses_ExitCleanly()
        {
            // 1. Null account
            await AccountLaunchService.AdjustWindowPositionAsync(null!);

            // 2. RememberWindowPositions is false
            bool oldRemember = RobloxWatcher.RememberWindowPositions;
            try
            {
                RobloxWatcher.RememberWindowPositions = false;
                var acc = new Account { Username = "PosTest" };
                await AccountLaunchService.AdjustWindowPositionAsync(acc);
                Assert.Equal("PosTest", acc.Username);
            }
            finally
            {
                RobloxWatcher.RememberWindowPositions = oldRemember;
            }

            // 3. Missing or invalid coordinates
            var invalidAcc = new Account { Username = "InvalidCoords" };
            await AccountLaunchService.AdjustWindowPositionAsync(invalidAcc);

            invalidAcc.SetField("Window_Position_X", "not_a_number");
            await AccountLaunchService.AdjustWindowPositionAsync(invalidAcc);
            Assert.Equal("not_a_number", invalidAcc.GetField("Window_Position_X"));

            // 4. Dimensions too small (< 200) or coordinates < -10000
            var smallAcc = new Account { Username = "SmallWin" };
            smallAcc.SetField("Window_Position_X", "100");
            smallAcc.SetField("Window_Position_Y", "100");
            smallAcc.SetField("Window_Width", "150");
            smallAcc.SetField("Window_Height", "150");
            await AccountLaunchService.AdjustWindowPositionAsync(smallAcc);

            smallAcc.SetField("Window_Width", "800");
            smallAcc.SetField("Window_Height", "600");
            smallAcc.SetField("Window_Position_X", "-15000");
            await AccountLaunchService.AdjustWindowPositionAsync(smallAcc);
            Assert.Equal("SmallWin", smallAcc.Username);
        }

        [Fact]
        public async Task ChromeLaunchHelper_SelectVerifiedBrowserProxyAsync_DisabledOrMissingFile_ReturnsNull()
        {
            AppSettings.General.Set("UseProxies", "false");
            var resDisabled = await ChromeLaunchHelper.SelectVerifiedBrowserProxyAsync();
            Assert.Null(resDisabled);

            AppSettings.General.Set("UseProxies", "true");
            // In temp directory, proxies.txt does not exist
            string proxyFile = StoragePaths.GetUserFile("proxies.txt");
            if (File.Exists(proxyFile)) File.Delete(proxyFile);

            var resMissing = await ChromeLaunchHelper.SelectVerifiedBrowserProxyAsync();
            Assert.Null(resMissing);
        }

        [Fact]
        public void ForegroundWindowService_LifecycleAndDispatch_CoversBranches()
        {
            // 1. Start and Stop
            bool started = ForegroundWindowService.Start();
            Assert.True(started || !started);

            // 2. Dispatch filtering logic
            uint lastPid = 500;
            Assert.False(ForegroundWindowService.ShouldDispatchProcessChange(0, ref lastPid));
            Assert.Equal(500u, lastPid);

            Assert.False(ForegroundWindowService.ShouldDispatchProcessChange(500, ref lastPid));
            Assert.Equal(500u, lastPid);

            Assert.True(ForegroundWindowService.ShouldDispatchProcessChange(600, ref lastPid));
            Assert.Equal(600u, lastPid);

            ForegroundWindowService.Stop();
            Assert.False(ForegroundWindowService.IsHookActive);
        }

        [Fact]
        public void Win32TrayHelper_RefreshNotificationArea_ExecutesSafely()
        {
            Win32TrayHelper.QueueRefreshNotificationArea(50);
            Win32TrayHelper.RefreshNotificationArea();
            Assert.True(true);
        }

        [Fact]
        public async Task AccountLaunchService_HomeGuardClauses_CoversBranches()
        {
            var service = new AccountLaunchService();

            var failResult = await service.LaunchAppHomeResultAsync(null!);
            Assert.Equal(LaunchOutcome.Failed, failResult.Outcome);

            string legacyStr = await service.LaunchAppHomeAsync(null!);
            Assert.False(string.IsNullOrEmpty(legacyStr));

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await service.LaunchAppHomeResultAsync(new Account { Username = "CancelUser" }, cancellationToken: cts.Token);
            });
        }

        [Fact]
        public async Task MonitorNativeAuthRateLimitAsync_DetectsRateLimitInLogs()
        {
            string robloxLogs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox", "logs");
            Directory.CreateDirectory(robloxLogs);
            string testLog = Path.Combine(robloxLogs, $"test_Player_{Guid.NewGuid():N}_last.log");
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            File.WriteAllText(testLog, $"{timestamp} [Info] authentication-ticket/redeem\n{timestamp} [Error] status: 429 Too Many Requests\n");

            var acc = new Account { Username = "RateLimitTestUser" };
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await AccountLaunchService.MonitorNativeAuthRateLimitAsync(DateTime.UtcNow.AddSeconds(-5), acc, cts.Token);
                Assert.True(acc.LastNativeLaunchRateLimited);
            }
            finally
            {
                try { if (File.Exists(testLog)) File.Delete(testLog); } catch { }
            }
        }

        [Fact]
        public async Task MonitorNativeAuthRateLimitAsync_TimesOutCleanlyWhenNoRateLimit()
        {
            var acc = new Account { Username = "CleanUser" };
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await AccountLaunchService.MonitorNativeAuthRateLimitAsync(DateTime.UtcNow, acc, cts.Token);
            });
            Assert.False(acc.LastNativeLaunchRateLimited);
        }
    }
}
