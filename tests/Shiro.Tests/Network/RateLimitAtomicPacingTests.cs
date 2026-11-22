using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class RateLimitAtomicPacingTests
    {
        [Fact]
        public void ReserveNextSlotDelay_ConcurrentBurstAtSameTimestamp_StaggersSlotsDeterministically()
        {
            var coordinator = new RobloxRateLimitCoordinator();
            var baseUtc = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

            TimeSpan d0 = coordinator.ReserveNextSlotDelay(RateLimitCategory.SessionCheck, 200, baseUtc);
            TimeSpan d1 = coordinator.ReserveNextSlotDelay(RateLimitCategory.SessionCheck, 200, baseUtc);
            TimeSpan d2 = coordinator.ReserveNextSlotDelay(RateLimitCategory.SessionCheck, 200, baseUtc);
            TimeSpan d3 = coordinator.ReserveNextSlotDelay(RateLimitCategory.SessionCheck, 200, baseUtc);

            Assert.Equal(0, d0.TotalMilliseconds);
            Assert.Equal(200, d1.TotalMilliseconds);
            Assert.Equal(400, d2.TotalMilliseconds);
            Assert.Equal(600, d3.TotalMilliseconds);

            // Different category has independent slot timeline
            TimeSpan otherCat = coordinator.ReserveNextSlotDelay(RateLimitCategory.General, 200, baseUtc);
            Assert.Equal(0, otherCat.TotalMilliseconds);
        }

        [Fact]
        public void ReserveNextSlotDelay_AfterQuietPeriod_ResetsToZeroDelay()
        {
            var coordinator = new RobloxRateLimitCoordinator();
            var baseUtc = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

            TimeSpan first = coordinator.ReserveNextSlotDelay(RateLimitCategory.Authentication, 150, baseUtc);
            Assert.Equal(0, first.TotalMilliseconds);

            // 500ms later (> 150ms interval), the next request should execute immediately (0ms delay)
            TimeSpan later = coordinator.ReserveNextSlotDelay(RateLimitCategory.Authentication, 150, baseUtc.AddMilliseconds(500));
            Assert.Equal(0, later.TotalMilliseconds);
        }

        [Fact]
        public async Task ExecuteWithPacingAsync_ParallelCallers_DoNotFireSimultaneously()
        {
            var coordinator = new RobloxRateLimitCoordinator();
            var startTicks = new long[4];
            var baseSw = System.Diagnostics.Stopwatch.StartNew();

            var tasks = Enumerable.Range(0, 4).Select(i =>
                coordinator.ExecuteWithPacingAsync(
                    RateLimitCategory.SessionCheck,
                    () =>
                    {
                        startTicks[i] = baseSw.ElapsedMilliseconds;
                        return Task.FromResult(i);
                    },
                    minIntervalMs: 50)).ToArray();

            await Task.WhenAll(tasks);

            Array.Sort(startTicks);
            // Total span between the 1st and 4th caller must reflect staggered pacing (at least ~110ms for 3 intervals of 50ms)
            long totalSpreadMs = startTicks[3] - startTicks[0];
            Assert.True(totalSpreadMs >= 100, $"Expected parallel callers to be staggered by >= 100ms, but spread was {totalSpreadMs}ms");
        }
    }
}
