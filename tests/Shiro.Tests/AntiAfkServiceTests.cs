using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StoragePathsCollection")]
    public class AntiAfkServiceTests
    {
        [Fact]
        public void SetConfig_ClampsIntervalCorrectly()
        {
            // Test disabling
            AntiAfkService.SetConfig(false, 10);
            Assert.False(AntiAfkService.IsEnabled);
            Assert.Equal("Anti-AFK Inactive", AntiAfkService.StatusText);

            // Test enabling with upper clamp
            AntiAfkService.SetConfig(true, 50);
            Assert.True(AntiAfkService.IsEnabled);
            Assert.Contains("18m", AntiAfkService.StatusText);

            // Test enabling with lower clamp
            AntiAfkService.SetConfig(true, -5);
            Assert.True(AntiAfkService.IsEnabled);
            Assert.Contains("1m", AntiAfkService.StatusText);

            // Clean up by stopping
            AntiAfkService.Stop();
            Assert.False(AntiAfkService.IsEnabled);
        }

        [Fact]
        public void Toggle_InvertsEnabledState()
        {
            AntiAfkService.Stop();
            Assert.False(AntiAfkService.IsEnabled);

            AntiAfkService.Toggle();
            Assert.True(AntiAfkService.IsEnabled);

            AntiAfkService.Toggle();
            Assert.False(AntiAfkService.IsEnabled);
        }

        [Fact]
        public void ComputeNextDueUtc_ClampsAndAppliesJitter()
        {
            var now = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
            var nextDue = AntiAfkService.ComputeNextDueUtc(now, 8, 10);
            Assert.Equal(now.AddSeconds((8 * 60) + 10), nextDue);

            var clampedLow = AntiAfkService.ComputeNextDueUtc(now, -5, -100);
            Assert.Equal(now.AddSeconds(30), clampedLow);

            var clampedHigh = AntiAfkService.ComputeNextDueUtc(now, 50, 0);
            Assert.Equal(now.AddSeconds(18 * 60), clampedHigh);
        }

        [Fact]
        public async Task ExecutePassAsync_ExecutesSafelyAcrossEmptyAndNonExistentPids()
        {
            int sentEmpty = await AntiAfkService.ExecutePassAsync(System.Array.Empty<int>());
            Assert.Equal(0, sentEmpty);

            int sentDead = await AntiAfkService.ExecutePassAsync(new[] { 999991 });
            Assert.Equal(0, sentDead);
        }

        [Fact]
        public void StatusChanged_Event_FiresOnStateChange()
        {
            bool changed = false;
            System.Action onChange = () => changed = true;
            AntiAfkService.StatusChanged += onChange;
            try
            {
                AntiAfkService.SetConfig(true, 10);
                Assert.True(changed);
                Assert.InRange(AntiAfkService.TapsSent, 0, int.MaxValue);
            }
            finally
            {
                AntiAfkService.StatusChanged -= onChange;
                AntiAfkService.Stop();
            }
        }
    }
}
