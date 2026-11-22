using System.Diagnostics;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class UnifiedResourceOptimizerTests
    {
        [Theory]
        [InlineData(true, false, 10, RobloxProcessOptimizer.NORMAL_PRIORITY_CLASS)]
        [InlineData(true, true, 5, RobloxProcessOptimizer.NORMAL_PRIORITY_CLASS)]
        [InlineData(false, false, 10, RobloxProcessOptimizer.BELOW_NORMAL_PRIORITY_CLASS)]
        [InlineData(false, true, 30, RobloxProcessOptimizer.BELOW_NORMAL_PRIORITY_CLASS)]
        [InlineData(false, true, 15, RobloxBackgroundThrottlerService.IDLE_PRIORITY_CLASS)]
        [InlineData(false, true, 5, RobloxBackgroundThrottlerService.IDLE_PRIORITY_CLASS)]
        public void ComputeTargetPriorityClass_UnifiesMemoryOptimizerAndBackgroundThrottler(
            bool isForeground,
            bool throttlerEnabled,
            int targetBackgroundFps,
            uint expectedPriorityClass)
        {
            uint actual = MemoryOptimizer.ComputeTargetPriorityClass(isForeground, throttlerEnabled, targetBackgroundFps);
            Assert.Equal(expectedPriorityClass, actual);
        }

        [Fact]
        public void BackgroundThrottler_OnForegroundWindowChanged_TracksThrottledStateAndSynchronizesFocus()
        {
            var throttler = RobloxBackgroundThrottlerService.Instance;
            bool prev = throttler.IsEnabled;
            try
            {
                throttler.IsEnabled = true;
                throttler.TargetBackgroundFps = 10;

                throttler.OnForegroundWindowChanged(activeProcessId: 1001, runningRobloxPids: new[] { 1001, 1002 });

                Assert.False(throttler.IsProcessThrottled(1001));
                Assert.True(throttler.IsProcessThrottled(1002));

                throttler.OnForegroundWindowChanged(activeProcessId: 1002, runningRobloxPids: new[] { 1001, 1002 });

                Assert.True(throttler.IsProcessThrottled(1001));
                Assert.False(throttler.IsProcessThrottled(1002));
            }
            finally
            {
                throttler.IsEnabled = prev;
                throttler.RestoreAllThrottledProcesses();
            }
        }
    }
}
