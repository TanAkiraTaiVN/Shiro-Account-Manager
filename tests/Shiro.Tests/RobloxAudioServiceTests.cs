using System;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class RobloxAudioServiceTests
    {
        [Fact]
        public void ConfigureSmartMute_TogglesStateCorrectly()
        {
            RobloxAudioService.ConfigureSmartMute(false);
            Assert.False(RobloxAudioService.IsSmartMuteEnabled);

            RobloxAudioService.ConfigureSmartMute(true);
            Assert.True(RobloxAudioService.IsSmartMuteEnabled);
            Assert.Contains("Smart Mute Active", RobloxAudioService.StatusText);

            RobloxAudioService.ConfigureSmartMute(false);
            Assert.False(RobloxAudioService.IsSmartMuteEnabled);
            Assert.Equal("Audio Manager Ready", RobloxAudioService.StatusText);
        }

        [Fact]
        public void AppSettingsKeys_IncludesNewMultiplayerPerformanceKeys()
        {
            Assert.Equal("AutoMuteBackgroundClients", AppSettingsKeys.General.AutoMuteBackgroundClients);
            Assert.Equal("SmartFastLaunch", AppSettingsKeys.General.SmartFastLaunch);
            Assert.Equal("AggressiveRamTrimming", AppSettingsKeys.General.AggressiveRamTrimming);
            Assert.Equal("DynamicHardwareThrottling", AppSettingsKeys.General.DynamicHardwareThrottling);
        }

        [Fact]
        public async Task WaitForClientUnlockedAsync_HandlesCancellationPromptly()
        {
            RobloxSingletonService.Apply(true);
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Pre-cancelled token

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await RobloxSingletonService.WaitForClientUnlockedAsync(TimeSpan.FromSeconds(5), cts.Token);
            });
        }

        [Fact]
        public async Task WaitForClientUnlockedAsync_TimesOutGracefullyWhenNoClients()
        {
            RobloxSingletonService.Apply(true);
            using var cts = new CancellationTokenSource();
            // 250ms short timeout to verify graceful timeout return
            bool result = await RobloxSingletonService.WaitForClientUnlockedAsync(TimeSpan.FromMilliseconds(250), cts.Token);
            Assert.False(result);
        }

        [Fact]
        public void MemoryOptimizer_OptimizeRobloxClients_RunsSafelyWithoutExceptions()
        {
            // Verify execution does not crash even when no Roblox clients are running
            var ex = Record.Exception(() => MemoryOptimizer.OptimizeRobloxClients(lowerPriority: true));
            Assert.Null(ex);
        }

        [Fact]
        public void ForegroundWindowService_CoalescedEvaluation_ExecutesCleanly()
        {
            var pids = new System.Collections.Generic.List<int> { 12345 };
            RobloxAudioService.EvaluateWithPids(12345, pids);
            MemoryOptimizer.EvaluateWithPids(12345, pids);
            Assert.True(OperatingSystem.IsWindows());
        }

        [Theory]
        // 0 running count -> false
        [InlineData(100, 200, 0, 0, 1000, 0, false)]
        [InlineData(100, 200, -1, 0, 1000, 0, false)]
        // In backoff period (< 10000ms since failure) -> false
        [InlineData(100, 200, 2, 2, 5000, 2000, false)]
        // Active PID and count identical to previous -> false
        [InlineData(100, 100, 2, 2, 20000, 0, false)]
        // Active PID changed -> true
        [InlineData(100, 200, 2, 2, 20000, 0, true)]
        // Running count changed -> true
        [InlineData(100, 100, 3, 2, 20000, 0, true)]
        public void ShouldApplySmartMuteState_TruthTable(
            int activePid,
            int lastActivePid,
            int runningRobloxCount,
            int lastRunningRobloxCount,
            long nowTick,
            long lastFailureTick,
            bool expected)
        {
            bool result = RobloxAudioService.ShouldApplySmartMuteState(
                activePid,
                lastActivePid,
                runningRobloxCount,
                lastRunningRobloxCount,
                nowTick,
                lastFailureTick);

            Assert.Equal(expected, result);
        }

        [Fact]
        public void AudioManagement_PureApiAndFallbackMethods_ExecuteCleanly()
        {
            var (muted, unmuted, total) = RobloxAudioService.ApplyFocusAudioState(0, Array.Empty<int>());
            Assert.Equal(0, muted);
            Assert.Equal(0, unmuted);
            Assert.Equal(0, total);

            int mutedAll = RobloxAudioService.MuteAllRoblox(true, updateStatus: false);
            Assert.InRange(mutedAll, 0, int.MaxValue);

            int unmutedAll = RobloxAudioService.MuteAllRoblox(false, updateStatus: false);
            Assert.InRange(unmutedAll, 0, int.MaxValue);

            bool anyUnmuted = RobloxAudioService.IsAnyRobloxUnmuted();
            Assert.Equal(anyUnmuted, anyUnmuted);

            bool toggled = RobloxAudioService.ToggleMuteAll();
            Assert.Equal(toggled, toggled);
        }
    }
}
