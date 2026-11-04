#nullable enable
using System;
using System.Collections.Generic;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class RobloxProcessServiceLifecycleBehaviorTests
    {
        [Fact]
        public void KillAllRobloxProcesses_ExecutesSafelyAndReturnsCount()
        {
            var ex = Record.Exception(() =>
            {
                int count = RobloxProcessService.KillAllRobloxProcesses();
                Assert.InRange(count, 0, int.MaxValue);
            });
            Assert.Null(ex);
        }

        [Fact]
        public void CloseAccountRobloxProcess_WithNullAccount_ReturnsFalse()
        {
            bool closed = RobloxProcessService.CloseAccountRobloxProcess(null!);
            Assert.False(closed);
        }

        [Fact]
        public void CloseAccountRobloxProcess_WithNonExistentPid_ResetsPidAndReturnsFalse()
        {
            var account = new Account
            {
                Username = "DeadPidUser",
                LastLaunchedProcessId = 999999
            };

            bool closed = RobloxProcessService.CloseAccountRobloxProcess(account);

            Assert.False(closed);
            Assert.Equal(0, account.LastLaunchedProcessId);
        }

        [Fact]
        public void CloseAccountRobloxProcess_WithEmptyAccountState_ReturnsFalse()
        {
            var account = new Account
            {
                Username = "NoProcessUser",
                LastLaunchedProcessId = 0,
                BrowserTrackerID = ""
            };

            bool closed = RobloxProcessService.CloseAccountRobloxProcess(account);
            Assert.False(closed);
        }

        [Fact]
        public void CloseAccountRobloxProcesses_NullOrEmptyList_ReturnsZero()
        {
            int countNull = RobloxProcessService.CloseAccountRobloxProcesses(null!);
            Assert.Equal(0, countNull);

            int countEmpty = RobloxProcessService.CloseAccountRobloxProcesses(Array.Empty<Account>());
            Assert.Equal(0, countEmpty);
        }

        [Fact]
        public void CloseAccountRobloxProcesses_SingleAccount_DelegatesCorrectly()
        {
            var account = new Account
            {
                Username = "SingleProcUser",
                LastLaunchedProcessId = 999998
            };

            int closedCount = RobloxProcessService.CloseAccountRobloxProcesses(new[] { account });

            Assert.Equal(0, closedCount);
            Assert.Equal(0, account.LastLaunchedProcessId);
        }

        [Fact]
        public void CloseAccountRobloxProcesses_MultipleAccounts_HandlesDeduplicationAndCleanup()
        {
            var acc1 = new Account { Username = "MultiProc1", LastLaunchedProcessId = 999991 };
            var acc2 = new Account { Username = "MultiProc2", LastLaunchedProcessId = 999992 };
            var acc3 = new Account { Username = "MultiProc3", LastLaunchedProcessId = 0 };

            int closed = RobloxProcessService.CloseAccountRobloxProcesses(new[] { acc1, acc2, acc3 });

            Assert.Equal(0, closed);
            Assert.Equal(0, acc1.LastLaunchedProcessId);
            Assert.Equal(0, acc2.LastLaunchedProcessId);
        }

        [Fact]
        public void IsProcessAlive_ValidatesPidBounds()
        {
            Assert.False(RobloxProcessService.IsProcessAlive(0));
            Assert.False(RobloxProcessService.IsProcessAlive(-1));
            Assert.False(RobloxProcessService.IsProcessAlive(-999));
            Assert.False(RobloxProcessService.IsProcessAlive(99999999));
        }

        [Fact]
        public void CleanZombieProcesses_ExecutesSafely()
        {
            var exception = Record.Exception(() => RobloxProcessService.CleanZombieProcesses());
            Assert.Null(exception);
        }

        [Fact]
        public void ClearRobloxClientSession_ExecutesSafely()
        {
            var exception = Record.Exception(() => RobloxProcessService.ClearRobloxClientSession());
            Assert.Null(exception);
        }

        [Fact]
        public void IsRobloxInstallerRunning_ExecutesSafely()
        {
            RobloxProcessService.InvalidateSnapshotCache();
            bool running = RobloxProcessService.IsRobloxInstallerRunning();
            Assert.False(running);
        }

        [Fact]
        public void ProcessExtensions_GetCommandLine_HandlesEdgeCasesAndCache()
        {
            // Null process
            Assert.Null(ProcessExtensions.GetCommandLine(null));

            // Negative and zero PID
            Assert.Null(ProcessExtensions.GetCommandLineByPid(0));
            Assert.Null(ProcessExtensions.GetCommandLineByPid(-1));

            // Current process lookup (exercises NtQueryInformationProcess and cache)
            using var currentProc = System.Diagnostics.Process.GetCurrentProcess();
            string? cmd1 = currentProc.GetCommandLine();
            string? cmd2 = ProcessExtensions.GetCommandLineByPid(currentProc.Id);

            Assert.Equal(cmd1, cmd2);
        }

        [Fact]
        public void CloseAccountRobloxProcess_WithBrowserTrackerId_ScansPidsAndReturnsFalse()
        {
            var account = new Account
            {
                Username = "TrackerUser",
                LastLaunchedProcessId = 0,
                BrowserTrackerID = "998877665544"
            };

            bool closed = RobloxProcessService.CloseAccountRobloxProcess(account);
            Assert.False(closed);
        }

        [Fact]
        public void CloseAccountRobloxProcesses_WithBrowserTrackerIds_ScansPidsAndReturnsZero()
        {
            var acc1 = new Account { Username = "Track1", LastLaunchedProcessId = 0, BrowserTrackerID = "11111" };
            var acc2 = new Account { Username = "Track2", LastLaunchedProcessId = 0, BrowserTrackerID = "22222" };

            int closed = RobloxProcessService.CloseAccountRobloxProcesses(new[] { acc1, acc2 });
            Assert.Equal(0, closed);
        }

        [Fact]
        public void ClearRobloxClientSession_WithoutKillingProcesses_ReturnsTrue()
        {
            bool cleared = RobloxProcessService.ClearRobloxClientSession(killProcesses: false);
            Assert.True(cleared);
        }

        [Fact]
        public void IsProcessAlive_ValidatesCurrentAndInvalidPids()
        {
            Assert.False(RobloxProcessService.IsProcessAlive(0));
            Assert.False(RobloxProcessService.IsProcessAlive(-1));
            Assert.True(RobloxProcessService.IsProcessAlive(Environment.ProcessId));
            Assert.False(RobloxProcessService.IsProcessAlive(9999999));
        }

        [Fact]
        public void ProcessDiscovery_MetricsReturnConsistentValues()
        {
            RobloxProcessService.InvalidateSnapshotCache();

            int clientCount = RobloxProcessService.GetRunningRobloxCount();
            int totalCount = RobloxProcessService.GetTotalRunningRobloxCount();
            bool anyRunning = RobloxProcessService.IsAnyRobloxRunning();
            var processes = RobloxProcessService.GetRunningRobloxProcesses();

            Assert.InRange(clientCount, 0, int.MaxValue);
            Assert.InRange(totalCount, 0, int.MaxValue);
            Assert.Equal(clientCount > 0, anyRunning);
            Assert.InRange(processes.Count, 0, int.MaxValue);
        }
    }
}
