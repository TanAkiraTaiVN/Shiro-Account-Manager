using System;
using System.Collections.Generic;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class AudioSessionCacheTests
    {
        [Fact]
        public void AreAllPidsCached_ReturnsTrueOnlyWhenEveryRunningPidHasValidPointer()
        {
            var runningPids = new List<int> { 1001, 1002 };
            var cache = new Dictionary<int, IntPtr>
            {
                [1001] = new IntPtr(0x1111),
                [1002] = new IntPtr(0x2222)
            };

            Assert.True(RobloxAudioService.AreAllPidsCached(runningPids, cache));

            // Missing 1003
            var runningWithNew = new List<int> { 1001, 1002, 1003 };
            Assert.False(RobloxAudioService.AreAllPidsCached(runningWithNew, cache));

            // Zero pointer is invalid
            cache[1002] = IntPtr.Zero;
            Assert.False(RobloxAudioService.AreAllPidsCached(runningPids, cache));

            // Empty running list returns false (no cached work to run)
            Assert.False(RobloxAudioService.AreAllPidsCached(Array.Empty<int>(), cache));
        }

        [Fact]
        public void InvalidateAndClearAudioSessionCache_SafeWhenEmptyOrNonExistentPid()
        {
            RobloxAudioService.ClearAudioSessionCache();
            Assert.Equal(0, RobloxAudioService.CachedAudioSessionCount);

            bool removed = RobloxAudioService.InvalidateCachedAudioSession(999999);
            Assert.False(removed);
        }
    }
}
