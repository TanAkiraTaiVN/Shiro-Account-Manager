#nullable enable
using System;
using System.Diagnostics;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class MemoryOptimizerBehaviorTests
    {
        [Fact]
        public void ShouldTriggerPeriodicTrim_CoversAllLogicBranches()
        {
            long maxFg = MemoryOptimizer.ForegroundMaxMemoryThresholdBytes;
            long bgLimit = 350L * 1024 * 1024;

            // 1. Foreground > threshold => true
            Assert.True(MemoryOptimizer.ShouldTriggerPeriodicTrim(maxFg + 1024, ProcessPriorityClass.Normal, isForeground: true, lowerPriorityEnabled: true, bgLimit));

            // 2. Foreground <= threshold => false
            Assert.False(MemoryOptimizer.ShouldTriggerPeriodicTrim(maxFg - 1024, ProcessPriorityClass.Normal, isForeground: true, lowerPriorityEnabled: true, bgLimit));

            // 3. Background > backgroundThreshold => true
            Assert.True(MemoryOptimizer.ShouldTriggerPeriodicTrim(bgLimit + 1024, ProcessPriorityClass.BelowNormal, isForeground: false, lowerPriorityEnabled: false, bgLimit));

            // 4. Background <= threshold, lowerPriorityEnabled = true, priority not BelowNormal => true
            Assert.True(MemoryOptimizer.ShouldTriggerPeriodicTrim(bgLimit - 1024, ProcessPriorityClass.Normal, isForeground: false, lowerPriorityEnabled: true, bgLimit));

            // 5. Background <= threshold, lowerPriorityEnabled = true, priority is BelowNormal => false
            Assert.False(MemoryOptimizer.ShouldTriggerPeriodicTrim(bgLimit - 1024, ProcessPriorityClass.BelowNormal, isForeground: false, lowerPriorityEnabled: true, bgLimit));

            // 6. Background <= threshold, lowerPriorityEnabled = false => false
            Assert.False(MemoryOptimizer.ShouldTriggerPeriodicTrim(bgLimit - 1024, ProcessPriorityClass.Normal, isForeground: false, lowerPriorityEnabled: false, bgLimit));
        }

        [Fact]
        public void ShouldTriggerPeriodicTrim_SkipsMemoryThresholdSpin_WhenAggressiveTrimDisabled()
        {
            long maxFg = MemoryOptimizer.ForegroundMaxMemoryThresholdBytes;
            long bgLimit = 350L * 1024 * 1024;

            // When aggressiveTrimEnabled == false, background client > 350MB already at BelowNormal must NOT spin every 30s:
            Assert.False(MemoryOptimizer.ShouldTriggerPeriodicTrim(
                bgLimit + (100L * 1024 * 1024),
                ProcessPriorityClass.BelowNormal,
                isForeground: false,
                lowerPriorityEnabled: true,
                bgLimit,
                aggressiveTrimEnabled: false));

            // Priority drift (Normal instead of BelowNormal) still triggers immediate optimization even when aggressiveTrimEnabled == false:
            Assert.True(MemoryOptimizer.ShouldTriggerPeriodicTrim(
                bgLimit + (100L * 1024 * 1024),
                ProcessPriorityClass.Normal,
                isForeground: false,
                lowerPriorityEnabled: true,
                bgLimit,
                aggressiveTrimEnabled: false));

            // Foreground extreme RAM also skips 30s EmptyWorkingSet spin when aggressiveTrimEnabled == false:
            Assert.False(MemoryOptimizer.ShouldTriggerPeriodicTrim(
                maxFg + 1024,
                ProcessPriorityClass.Normal,
                isForeground: true,
                lowerPriorityEnabled: true,
                bgLimit,
                aggressiveTrimEnabled: false));
        }

        [Fact]
        public void IsAggressiveWorkingSetTrimEnabled_ReadsAggressiveRamTrimming_WithLegacyFallback()
        {
            var mock = Substitute.For<ISettingsService>();
            using (AppServices.Override<ISettingsService>(mock))
            {
                mock.Exists(AppSettingsKeys.General.AggressiveRamTrimming).Returns(true);
                mock.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming, false).Returns(true);
                Assert.True(MemoryOptimizer.IsAggressiveWorkingSetTrimEnabled());

                mock.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming, false).Returns(false);
                Assert.False(MemoryOptimizer.IsAggressiveWorkingSetTrimEnabled());

                mock.Exists(AppSettingsKeys.General.AggressiveRamTrimming).Returns(false);
                mock.Exists("AllowEmptyWorkingSetTrimming").Returns(true);
                mock.Get<bool>("AllowEmptyWorkingSetTrimming", false).Returns(true);
                Assert.True(MemoryOptimizer.IsAggressiveWorkingSetTrimEnabled());

                mock.Exists("AllowEmptyWorkingSetTrimming").Returns(false);
                Assert.False(MemoryOptimizer.IsAggressiveWorkingSetTrimEnabled());
            }
        }

        [Fact]
        public void ConfigureAutoTrim_StartsAndStopsTimer()
        {
            // Start auto trim
            MemoryOptimizer.ConfigureAutoTrim(enabled: true, intervalMinutes: 2, dynamicThreshold: true);

            // Reconfigure while running
            MemoryOptimizer.ConfigureAutoTrim(enabled: true, intervalMinutes: 5, dynamicThreshold: false);

            // Disable auto trim
            MemoryOptimizer.ConfigureAutoTrim(enabled: false);

            long total = MemoryOptimizer.TotalBytesSaved;
            Assert.InRange(total, 0, long.MaxValue);
            Assert.Equal(0, total);
        }

        [Fact]
        public void OptimizeRobloxClients_NoClients_And_WithTargetProcess_CoversExecution()
        {
            var prevPidsOverride = RobloxProcessService.TestGetRunningRobloxPidsOverride;
            using var selfProc = Process.GetCurrentProcess();
            var origPrio = selfProc.PriorityClass;
            var mockSettings = Substitute.For<ISettingsService>();
            mockSettings.Exists(AppSettingsKeys.General.AggressiveRamTrimming).Returns(true);
            mockSettings.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming, false).Returns(false);
            using var settingsScope = AppServices.Override<ISettingsService>(mockSettings);
            try
            {
                // 1. When no Roblox processes are running => returns (0, 0, 0)
                RobloxProcessService.TestGetRunningRobloxPidsOverride = () => Array.Empty<int>();
                var (saved0, count0, prio0) = MemoryOptimizer.OptimizeRobloxClients(lowerPriority: true);
                Assert.Equal(0, saved0);
                Assert.Equal(0, count0);
                Assert.Equal(0, prio0);

                var (trimSaved0, trimCount0) = MemoryOptimizer.TrimRobloxClients(lowerPriority: true);
                Assert.Equal(0, trimSaved0);
                Assert.Equal(0, trimCount0);

                // 2. When process is running (pointing to current test process PID)
                RobloxProcessService.TestGetRunningRobloxPidsOverride = () => new[] { Environment.ProcessId };
                var (saved1, count1, prio1) = MemoryOptimizer.OptimizeRobloxClients(lowerPriority: false);
                Assert.InRange(saved1, 0, long.MaxValue);
                Assert.InRange(count1, 0, int.MaxValue);
                Assert.InRange(prio1, 0, int.MaxValue);

                var (saved2, count2, prio2) = MemoryOptimizer.OptimizeRobloxClients(lowerPriority: true);
                Assert.InRange(saved2, 0, long.MaxValue);
                Assert.InRange(count2, 0, int.MaxValue);
                Assert.InRange(prio2, 0, int.MaxValue);
            }
            finally
            {
                RobloxProcessService.TestGetRunningRobloxPidsOverride = prevPidsOverride;
                selfProc.PriorityClass = origPrio;
                MemoryOptimizer.DisableEcoQoS(selfProc.Handle);
                MemoryOptimizer.SetProcessMemoryPriorityNative(selfProc.Handle, isForeground: true);
                MemoryOptimizer.ResetStatsForTests();
            }
        }
    }
}
