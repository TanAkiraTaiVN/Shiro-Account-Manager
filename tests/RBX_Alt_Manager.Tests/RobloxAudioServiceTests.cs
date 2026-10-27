using System;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Services;
using Xunit;

namespace RBX_Alt_Manager.Tests
{
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
            using var cts = new CancellationTokenSource();
            // 250ms short timeout to verify graceful timeout return
            bool result = await RobloxSingletonService.WaitForClientUnlockedAsync(TimeSpan.FromMilliseconds(250), cts.Token);
            Assert.False(result);
        }

        [Fact]
        public void MemoryOptimizer_OptimizeRobloxClients_RunsSafelyWithoutExceptions()
        {
            // Verify execution does not crash even when no Roblox clients are running
            var (bytesSaved, clientCount, prioCount) = MemoryOptimizer.OptimizeRobloxClients(lowerPriority: true);
            Assert.True(bytesSaved >= 0);
            Assert.True(clientCount >= 0);
            Assert.True(prioCount >= 0);
        }
    }
}
