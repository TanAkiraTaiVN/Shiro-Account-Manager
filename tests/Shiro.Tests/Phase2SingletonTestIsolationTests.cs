#nullable enable
using System;
using System.Threading;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class Phase2SingletonTestIsolationTests
    {
        [Fact]
        public void CreateSuffixedNativeOpsForTest_IsolatesFromExternalHeldMutex_WhileCompilingRealSddl()
        {
            Assert.Equal("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;IU)S:(ML;;NW;;;LW)", RobloxSingletonService.RestrictedInteractiveSddl);

            if (!OperatingSystem.IsWindows())
                return;

            string occupiedMutexName = $"ROBLOX_singletonMutex_occupied_{Guid.NewGuid():N}";
            string suffix = $"_isolated_{Guid.NewGuid():N}";

            using var occupiedMutex = new Mutex(initiallyOwned: true, occupiedMutexName, out bool createdNew);
            Assert.True(createdNew);

            try
            {
                RobloxSingletonService.Stop();
                RobloxSingletonService.NativeOpsOverrideForTest =
                    RobloxSingletonService.CreateSuffixedNativeOpsForTest(suffix);

                RobloxSingletonService.EnsureMutex(true);

                Assert.True(RobloxSingletonService.MutexHeld);
                Assert.False(RobloxSingletonService.AccessDenied);
                Assert.Equal("Mutex Active • Multi-Instance Running", RobloxSingletonService.StatusText);
            }
            finally
            {
                RobloxSingletonService.NativeOpsOverrideForTest = null;
                RobloxSingletonService.Stop();
                if (createdNew)
                    occupiedMutex.ReleaseMutex();
            }

            Assert.False(RobloxSingletonService.MutexHeld);
        }
    }
}
