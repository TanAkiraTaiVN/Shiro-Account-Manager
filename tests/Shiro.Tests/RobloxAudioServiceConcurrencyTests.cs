using System;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class RobloxAudioServiceConcurrencyTests
    {
        [Fact]
        public async Task ConcurrentInvalidateAndClearCache_WithSmartMuteToggle_DoesNotThrowOrDeadlock()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            Task t1 = Task.Run(() =>
            {
                int pid = 1000;
                while (!cts.IsCancellationRequested)
                {
                    RobloxAudioService.InvalidateCachedAudioSession(pid++);
                }
            });
            Task t2 = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    RobloxAudioService.ClearAudioSessionCache();
                }
            });
            Task t3 = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    RobloxAudioService.ConfigureSmartMute(false);
                }
            });

            await Task.WhenAll(t1, t2, t3);
            Assert.False(RobloxAudioService.IsSmartMuteEnabled);
        }
    }
}
