#nullable enable
using System;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class ForegroundWindowServiceBehaviorTests : IDisposable
    {
        public ForegroundWindowServiceBehaviorTests()
        {
            ForegroundWindowService.Stop();
        }

        public void Dispose()
        {
            ForegroundWindowService.Stop();
        }

        [Fact]
        public void ShouldDispatchProcessChange_WhenNewPidIsZero_ReturnsFalseAndPreservesLastPid()
        {
            uint lastPid = 1234;

            bool dispatched = ForegroundWindowService.ShouldDispatchProcessChange(0, ref lastPid);

            Assert.False(dispatched);
            Assert.Equal(1234u, lastPid);
        }

        [Fact]
        public void ShouldDispatchProcessChange_WhenPidMatchesLastPid_ReturnsFalse()
        {
            uint lastPid = 5678;

            bool dispatched = ForegroundWindowService.ShouldDispatchProcessChange(5678, ref lastPid);

            Assert.False(dispatched);
            Assert.Equal(5678u, lastPid);
        }

        [Fact]
        public void ShouldDispatchProcessChange_WhenPidDiffers_ReturnsTrueAndUpdatesLastPid()
        {
            uint lastPid = 1000;

            bool dispatched = ForegroundWindowService.ShouldDispatchProcessChange(2000, ref lastPid);

            Assert.True(dispatched);
            Assert.Equal(2000u, lastPid);
        }

        [Fact]
        public void Start_And_Stop_ManagesHookLifecycle()
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.False(ForegroundWindowService.Start());
                return;
            }

            bool started = ForegroundWindowService.Start();
            Assert.True(started);
            Assert.True(ForegroundWindowService.IsHookActive);

            // Re-entrant start returns true without breaking hook
            bool secondStart = ForegroundWindowService.Start();
            Assert.True(secondStart);
            Assert.True(ForegroundWindowService.IsHookActive);

            ForegroundWindowService.Stop();
            Assert.False(ForegroundWindowService.IsHookActive);

            // Idempotent stop
            ForegroundWindowService.Stop();
            Assert.False(ForegroundWindowService.IsHookActive);
        }

        [Fact]
        public void LastForegroundProcessId_ReturnsNonNegativePid()
        {
            uint pid = ForegroundWindowService.LastForegroundProcessId;
            Assert.InRange(pid, 0u, uint.MaxValue);
        }

        [Fact]
        public void ForegroundProcessChanged_SubscriptionCanBeAddedAndRemoved()
        {
            int callCount = 0;
            Action<IntPtr, uint> handler = (hwnd, pid) => callCount++;

            ForegroundWindowService.ForegroundProcessChanged += handler;
            ForegroundWindowService.ForegroundProcessChanged -= handler;

            Assert.Equal(0, callCount);
        }
    }
}
