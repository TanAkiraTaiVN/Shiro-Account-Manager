#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class RobloxProcessServiceBehaviorTests
    {
        [Fact]
        public void RobloxProcessNames_ContainsExpectedProcessNames()
        {
            Assert.Contains("RobloxPlayerBeta", RobloxProcessService.RobloxProcessNames);
            Assert.Contains("RobloxPlayerLauncher", RobloxProcessService.RobloxProcessNames);
            Assert.Contains("RobloxCrashHandler", RobloxProcessService.RobloxProcessNames);
        }

        [Fact]
        public void IsProcessAlive_CurrentProcess_ReturnsTrue()
        {
            int currentPid = Environment.ProcessId;
            bool alive = RobloxProcessService.IsProcessAlive(currentPid);
            Assert.True(alive);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(2147483640)]
        public void IsProcessAlive_InvalidOrNonExistentPid_ReturnsFalse(int pid)
        {
            bool alive = RobloxProcessService.IsProcessAlive(pid);
            Assert.False(alive);
        }

        [Fact]
        public void InvalidateSnapshotCache_ExecutesCleanly()
        {
            var ex = Record.Exception(() => RobloxProcessService.InvalidateSnapshotCache());
            Assert.Null(ex);
        }

        [Fact]
        public void IsRobloxInstallerRunning_ReturnsBoolean()
        {
            bool running = RobloxProcessService.IsRobloxInstallerRunning();
            // In unit test environment, usually false
            Assert.False(running);
        }

        [Fact]
        public void GetRunningRobloxCount_MatchesGetRunningRobloxPidsCount()
        {
            int count = RobloxProcessService.GetRunningRobloxCount();
            var pids = RobloxProcessService.GetRunningRobloxPids();

            Assert.True(count >= 0);
            Assert.Equal(pids.Count, count);
        }

        [Fact]
        public void GetTotalRunningRobloxCount_ReturnsNonNegative()
        {
            int count = RobloxProcessService.GetTotalRunningRobloxCount();
            Assert.Equal(RobloxProcessService.GetRunningRobloxCount(), count);
        }

        [Fact]
        public void IsAnyRobloxRunning_MatchesCount()
        {
            bool any = RobloxProcessService.IsAnyRobloxRunning();
            int count = RobloxProcessService.GetRunningRobloxCount();

            Assert.Equal(count > 0, any);
        }

        [Fact]
        public void GetRunningRobloxProcesses_ReturnsList()
        {
            var procs = RobloxProcessService.GetRunningRobloxProcesses();
            Assert.Equal(RobloxProcessService.GetRunningRobloxCount(), procs.Count);
        }

        [Fact]
        public void CloseAccountRobloxProcess_WithNullAccount_ReturnsFalse()
        {
            bool result = RobloxProcessService.CloseAccountRobloxProcess(null!);
            Assert.False(result);
        }

        [Fact]
        public void CloseAccountRobloxProcess_WithInactiveAccount_ReturnsFalse()
        {
            var acc = new Account { Username = "InactiveUser", LastLaunchedProcessId = 0, BrowserTrackerID = "" };
            bool result = RobloxProcessService.CloseAccountRobloxProcess(acc);
            Assert.False(result);
        }

        [Fact]
        public void CloseAccountRobloxProcesses_WithNullOrEmpty_ReturnsZero()
        {
            int r1 = RobloxProcessService.CloseAccountRobloxProcesses(null!);
            Assert.Equal(0, r1);

            int r2 = RobloxProcessService.CloseAccountRobloxProcesses(new List<Account>());
            Assert.Equal(0, r2);
        }

        [Fact]
        public void CleanZombieProcesses_ReturnsNonNegative()
        {
            int cleaned = -1;
            var ex = Record.Exception(() => cleaned = RobloxProcessService.CleanZombieProcesses());
            Assert.Null(ex);
            Assert.Equal(0, cleaned);
        }

        [Fact]
        public void ClearRobloxClientSession_WithoutKillingProcesses_ExecutesCleanly()
        {
            bool success = RobloxProcessService.ClearRobloxClientSession(killProcesses: false);
            Assert.True(success);
        }
    }
}
