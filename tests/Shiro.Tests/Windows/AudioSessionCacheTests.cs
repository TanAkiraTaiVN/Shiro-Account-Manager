using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AudioSessionCacheTests
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ComReleaseDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SimpleAudioSetMuteDelegate(IntPtr self, int mute, IntPtr eventContext);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SimpleAudioGetMuteDelegate(IntPtr self, IntPtr muteOut);

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

        [Fact]
        public void RunOnWasapiMtaThread_WhenWorkExceedsTimeout_ThrowsTimeoutWithoutLateDisposeCrash()
        {
            using var gate = new ManualResetEventSlim(false);
            Assert.Throws<TimeoutException>(() => RobloxAudioService.RunOnWasapiMtaThread(() =>
            {
                gate.Wait(300);
                return 1;
            }, timeoutMs: 25));
            gate.Set();

            // Verify MTA worker completes the late item cleanly and remains healthy.
            int next = RobloxAudioService.RunOnWasapiMtaThread(() => 42);
            Assert.Equal(42, next);
        }

        [Fact]
        public void MultiSessionCache_StoresMultiplePointersPerPidAndReleasesAll()
        {
            var cache = new Dictionary<int, IReadOnlyList<IntPtr>>
            {
                [1001] = new[] { new IntPtr(0x1111), new IntPtr(0x2222) }
            };
            Assert.True(RobloxAudioService.AreAllPidsCached(new[] { 1001 }, cache));

            cache[1001] = new[] { new IntPtr(0x1111), IntPtr.Zero };
            Assert.False(RobloxAudioService.AreAllPidsCached(new[] { 1001 }, cache));

            cache[1001] = Array.Empty<IntPtr>();
            Assert.False(RobloxAudioService.AreAllPidsCached(new[] { 1001 }, cache));
        }

        [Fact]
        public void MultiSessionCache_TryForEachAndInvalidate_VisitsAndReleasesAllPointersPerPid()
        {
            IntPtr vtable = Marshal.AllocHGlobal(IntPtr.Size * 8);
            IntPtr obj1 = Marshal.AllocHGlobal(IntPtr.Size);
            IntPtr obj2 = Marshal.AllocHGlobal(IntPtr.Size);
            var released = new List<IntPtr>();
            var muteStates = new Dictionary<IntPtr, int> { [obj1] = 0, [obj2] = 0 };

            ComReleaseDelegate onRelease = self =>
            {
                lock (released)
                {
                    released.Add(self);
                }
                return 0;
            };
            SimpleAudioSetMuteDelegate onSetMute = (self, mute, _) =>
            {
                muteStates[self] = mute;
                return 0;
            };
            SimpleAudioGetMuteDelegate onGetMute = (self, muteOut) =>
            {
                Marshal.WriteInt32(muteOut, muteStates[self]);
                return 0;
            };

            Marshal.WriteIntPtr(vtable, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(onRelease));
            Marshal.WriteIntPtr(vtable, 5 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(onSetMute));
            Marshal.WriteIntPtr(vtable, 6 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(onGetMute));
            Marshal.WriteIntPtr(obj1, vtable);
            Marshal.WriteIntPtr(obj2, vtable);

            try
            {
                RobloxAudioService.ClearAudioSessionCache();
                RobloxAudioService.SeedCachedAudioSessionsForTest(1001, obj1, obj2);
                Assert.True(RobloxAudioService.HasCachedAudioSessionForTest(1001));

                int visited = 0;
                bool ok = RobloxAudioService.TryForEachCachedAudioSession(new[] { 1001 }, (pid, volume) =>
                {
                    visited++;
                    Assert.Equal(1001u, pid);
                    Assert.True(volume.TrySetMute(true));
                    return true;
                });

                Assert.True(ok);
                Assert.Equal(2, visited);
                Assert.Equal(1, muteStates[obj1]);
                Assert.Equal(1, muteStates[obj2]);

                bool invalidated = RobloxAudioService.InvalidateCachedAudioSession(1001);
                Assert.True(invalidated);
                Assert.False(RobloxAudioService.HasCachedAudioSessionForTest(1001));
                Assert.Equal(2, released.Count);
                Assert.Contains(obj1, released);
                Assert.Contains(obj2, released);

                released.Clear();
                RobloxAudioService.SeedCachedAudioSessionsForTest(1001, obj1, obj2);
                RobloxAudioService.ClearAudioSessionCache();
                Assert.Equal(0, RobloxAudioService.CachedAudioSessionCount);
                Assert.Equal(2, released.Count);
            }
            finally
            {
                RobloxAudioService.ClearAudioSessionCache();
                GC.KeepAlive(onRelease);
                GC.KeepAlive(onSetMute);
                GC.KeepAlive(onGetMute);
                Marshal.FreeHGlobal(obj1);
                Marshal.FreeHGlobal(obj2);
                Marshal.FreeHGlobal(vtable);
            }
        }
    }
}
