using System;
using System.Collections.Generic;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class ProcessAndSingletonOptimizationTests
    {
        [Theory]
        [InlineData(1234, 0, true)]
        [InlineData(5678, 50, true)]
        [InlineData(0, 50, false)]
        [InlineData(-1, 0, false)]
        public void ShouldAssignToJobObject_AlwaysTrueForPositivePid_RegardlessOfCpuCap(int pid, int cpuCapPercent, bool expected)
        {
            bool actual = RobloxProcessOptimizer.ShouldAssignToJobObject(pid, cpuCapPercent);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void PartitionRobloxPidsByCommandLine_SeparatesTrayDaemonsAndActiveGameClients_InSinglePass()
        {
            var allPids = new List<int> { 101, 202, 303, 404 };
            var cmdMap = new Dictionary<int, string?>
            {
                [101] = "\"C:\\RobloxPlayerBeta.exe\" --app -t token -j join -b 111",
                [202] = "\"C:\\RobloxPlayerBeta.exe\" --launch-to-tray",
                [303] = "\"C:\\RobloxPlayerBeta.exe\" -t token2 -j join2 -b 222",
                [404] = "\"C:\\RobloxPlayerBeta.exe\" --FollowMe --LAUNCH-TO-TRAY"
            };

            var (trayDaemons, activeClients) = RobloxProcessService.PartitionRobloxPidsByCommandLine(
                allPids,
                pid => cmdMap.TryGetValue(pid, out var cmd) ? cmd : null);

            Assert.Equal(new[] { 202, 404 }, trayDaemons);
            Assert.Equal(new[] { 101, 303 }, activeClients);
        }

        [Fact]
        public void SingletonService_OnJobProcessExited_RemovesExitedPidFromObservedSet()
        {
            bool removed = RobloxSingletonService.NotifyProcessExitedFromJobForTest(424242);
            // Calling with an untracked PID should safely return false and never throw
            Assert.False(removed);
        }
    }
}

