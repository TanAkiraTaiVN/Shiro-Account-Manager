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
    }
}
