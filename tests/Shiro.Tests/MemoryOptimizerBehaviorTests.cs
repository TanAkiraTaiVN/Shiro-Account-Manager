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
    public class MemoryOptimizerBehaviorTests
    {
        [Fact]
        public void ConfigureAutoTrim_UpdatesSettingsAndProperties()
        {
            MemoryOptimizer.ConfigureAutoTrim(true, intervalMinutes: 5, dynamicThreshold: false);
            Assert.True(MemoryOptimizer.AutoTrimEnabled);

            MemoryOptimizer.ConfigureAutoTrim(false);
            Assert.False(MemoryOptimizer.AutoTrimEnabled);
        }

        [Fact]
        public void TotalBytesSaved_InitialState_IsNonNegative()
        {
            long saved = MemoryOptimizer.TotalBytesSaved;
            Assert.Equal(0, saved);
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void IsAutoLowerPriorityActive_ReflectsAppSetting(bool configuredValue, bool expected)
        {
            AppSettings.General.Set("AutoLowerProcessPriority", configuredValue.ToString().ToLowerInvariant());
            bool active = MemoryOptimizer.IsAutoLowerPriorityActive();
            Assert.Equal(expected, active);
        }

        [Theory]
        // Foreground: only trimmed if memory exceeds ForegroundMaxMemoryThresholdBytes (1.2 GB)
        [InlineData(100L * 1024 * 1024, ProcessPriorityClass.Normal, true, true, false)]
        [InlineData(1500L * 1024 * 1024, ProcessPriorityClass.Normal, true, true, true)]
        // Background: trimmed if workingSet exceeds background threshold (350 MB)
        [InlineData(400L * 1024 * 1024, ProcessPriorityClass.BelowNormal, false, false, true)]
        // Background: trimmed if lowerPriority is enabled and priority is NOT BelowNormal
        [InlineData(200L * 1024 * 1024, ProcessPriorityClass.Normal, false, true, true)]
        // Background: NOT trimmed if below threshold, lowerPriority enabled, and already BelowNormal
        [InlineData(200L * 1024 * 1024, ProcessPriorityClass.BelowNormal, false, true, false)]
        // Background: NOT trimmed if below threshold and lowerPriority is disabled
        [InlineData(200L * 1024 * 1024, ProcessPriorityClass.Normal, false, false, false)]
        public void ShouldTriggerPeriodicTrim_EvaluatesTruthTableCorrectly(
            long workingSetBytes,
            ProcessPriorityClass priority,
            bool isForeground,
            bool lowerPriorityEnabled,
            bool expectedTrigger)
        {
            bool trigger = MemoryOptimizer.ShouldTriggerPeriodicTrim(
                workingSetBytes,
                priority,
                isForeground,
                lowerPriorityEnabled,
                backgroundThresholdBytes: 350L * 1024 * 1024);

            Assert.Equal(expectedTrigger, trigger);
        }

        [Fact]
        public void EvaluateWithPids_WithNullOrEmptyPids_ClearsLastPidWithoutThrowing()
        {
            AppSettings.General.Set("AutoLowerProcessPriority", "true");
            var ex1 = Record.Exception(() => MemoryOptimizer.EvaluateWithPids(0, null!));
            Assert.Null(ex1);
            var ex2 = Record.Exception(() => MemoryOptimizer.EvaluateWithPids(1234, new List<int>()));
            Assert.Null(ex2);
        }

        [Fact]
        public void EvaluateWithPids_WithPidsList_HandlesUnknownForegroundPid()
        {
            AppSettings.General.Set("AutoLowerProcessPriority", "true");
            var pids = new List<int> { 99999, 88888 };
            var ex = Record.Exception(() => MemoryOptimizer.EvaluateWithPids(11111, pids));
            Assert.Null(ex);
        }

        [Fact]
        public void TrimRobloxClients_WhenNoRobloxRunning_ReturnsZeroSaved()
        {
            var (bytesSaved, count) = MemoryOptimizer.TrimRobloxClients(lowerPriority: false);
            Assert.Equal(0, bytesSaved);
            Assert.Equal(0, count);
        }

        [Fact]
        public void OptimizeRobloxClients_WhenNoRobloxRunning_ReturnsZeroStats()
        {
            var (bytesSaved, count, prioChanged) = MemoryOptimizer.OptimizeRobloxClients(lowerPriority: false);
            Assert.Equal(0, bytesSaved);
            Assert.Equal(0, count);
            Assert.Equal(0, prioChanged);
        }

        [Fact]
        public void EvaluateWithPids_FocusSwitch_ExercisesWin32FocusProfiles()
        {
            AppSettings.General.Set("AutoLowerProcessPriority", "true");
            int currentPid = Environment.ProcessId;
            var pids = new List<int> { currentPid };

            Assert.True(currentPid > 0);
            Assert.Single(pids);
            Assert.Equal(currentPid, pids[0]);

            try
            {
                // Switch current process to foreground
                MemoryOptimizer.EvaluateWithPids((uint)currentPid, pids);

                // Switch current process to background
                MemoryOptimizer.EvaluateWithPids(0, pids);
            }
            finally
            {
                using var cur = Process.GetCurrentProcess();
                try { cur.PriorityClass = ProcessPriorityClass.Normal; }
                catch (Exception) { }
            }
        }

        [Fact]
        public void NotifyForegroundChanged_ExecutesSafely()
        {
#pragma warning disable CS0618
            var ex = Record.Exception(() => MemoryOptimizer.NotifyForegroundChanged(IntPtr.Zero, 0));
#pragma warning restore CS0618
            Assert.Null(ex);
        }
    }
}
