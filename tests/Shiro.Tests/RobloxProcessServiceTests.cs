using System.Linq;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class RobloxProcessServiceTests
    {
        [Fact]
        public void RobloxProcessNames_ContainsExpectedStandardProcesses()
        {
            var names = RobloxProcessService.RobloxProcessNames;
            Assert.NotNull(names);
            Assert.Contains("RobloxPlayerBeta", names);
            Assert.Contains("RobloxCrashHandler", names);
            Assert.Contains("RobloxPlayerLauncher", names);
        }

        [Fact]
        public void GetRunningRobloxProcesses_ExecutesWithoutException()
        {
            var ex = Record.Exception(() => RobloxProcessService.GetRunningRobloxProcesses());
            Assert.Null(ex);
        }

        [Fact]
        public void IsAnyRobloxRunning_ExecutesWithoutException()
        {
            bool running = RobloxProcessService.IsAnyRobloxRunning();
            int count = RobloxProcessService.GetRunningRobloxCount();
            Assert.Equal(count > 0, running);
        }

        [Fact]
        public void GetRunningRobloxCount_ReturnsNonNegative()
        {
            int count = RobloxProcessService.GetRunningRobloxCount();
            int actualProcs = RobloxProcessService.GetRunningRobloxProcesses().Count;
            Assert.Equal(actualProcs, count);
        }

        [Fact]
        public void GetTotalRunningRobloxCount_ReturnsNonNegative()
        {
            int total = RobloxProcessService.GetTotalRunningRobloxCount();
            int running = RobloxProcessService.GetRunningRobloxCount();
            Assert.True(total >= running);
            var ex = Record.Exception(() => RobloxProcessService.GetTotalRunningRobloxCount());
            Assert.Null(ex);
        }

        [Fact]
        public void RefreshNotificationArea_ExecutesSafelyWithoutException()
        {
            var exception = Record.Exception(() => Utilities.RefreshNotificationArea());
            Assert.Null(exception);
        }

        [Fact]
        public void QueueRefreshNotificationArea_ExecutesSafelyWithoutException()
        {
            var exception = Record.Exception(() => Utilities.QueueRefreshNotificationArea(50));
            Assert.Null(exception);
        }

        [Fact]
        public void ThoroughCleanNotificationArea_ExecutesSafelyWithoutException()
        {
            var exception = Record.Exception(() => Utilities.ThoroughCleanNotificationArea());
            Assert.Null(exception);
        }

        [Fact]
        public void CleanZombieProcesses_ExecutesSafelyWithoutException()
        {
            var exception = Record.Exception(() => RobloxProcessService.CleanZombieProcesses());
            Assert.Null(exception);
        }

        [Fact]
        public void GetRunningRobloxPids_ExecutesSafelyWithoutException()
        {
            var pids = RobloxProcessService.GetRunningRobloxPids();
            Assert.NotNull(pids);
            int runningCount = RobloxProcessService.GetRunningRobloxCount();
            Assert.Equal(runningCount, pids.Count);
        }

        [Fact]
        public void CloseAccountRobloxProcess_NullAccount_ReturnsFalse()
        {
            bool result = RobloxProcessService.CloseAccountRobloxProcess(null!);
            Assert.False(result);
        }

        [Fact]
        public void CloseAccountRobloxProcess_NonExistentPid_ReturnsFalseGracefully()
        {
            var acc = new Account
            {
                Username = "NonExistentUser",
                LastLaunchedProcessId = 99999999
            };
            bool result = RobloxProcessService.CloseAccountRobloxProcess(acc);
            Assert.False(result);
        }

        [Fact]
        public void CloseAccountRobloxProcesses_NullOrEmptyList_ReturnsZero()
        {
            Assert.Equal(0, RobloxProcessService.CloseAccountRobloxProcesses(null!));
            Assert.Equal(0, RobloxProcessService.CloseAccountRobloxProcesses(System.Array.Empty<Account>()));
        }

        [Fact]
        public async System.Threading.Tasks.Task RefreshLiveProcessStatus_DoesNotBlockCallingThread()
        {
            var vm = new Shiro.ViewModels.MainViewModel();
            var acc = new Account { UserID = 1001, Username = "TestLiveAccount", Valid = true };
            vm.LoadAccounts(new System.Collections.Generic.List<Account> { acc });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            vm.RefreshLiveProcessStatus();
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 50, $"RefreshLiveProcessStatus took {sw.ElapsedMilliseconds}ms, expected < 50ms");
            await System.Threading.Tasks.Task.Delay(150);
            Assert.True(vm.RunningRobloxProcessCount >= 0);
        }
    }
}
