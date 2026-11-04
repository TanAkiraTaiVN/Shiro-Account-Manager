using System;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class RobloxRateLimitCoordinatorTests
    {
        [Fact]
        public void Instance_IsSingleton()
        {
            var inst1 = RobloxRateLimitCoordinator.Instance;
            var inst2 = RobloxRateLimitCoordinator.Instance;
            Assert.NotNull(inst1);
            Assert.Same(inst1, inst2);
        }

        [Fact]
        public void ResetBackoff_ClearsBackoffState()
        {
            var coordinator = RobloxRateLimitCoordinator.Instance;
            coordinator.RecordRateLimitHit(RateLimitCategory.General, null, defaultBackoffSeconds: 10);
            Assert.True(coordinator.IsBackingOff);

            coordinator.ResetBackoff();
            Assert.False(coordinator.IsBackingOff);
            Assert.Equal(TimeSpan.Zero, coordinator.RemainingBackoff);
        }

        [Fact]
        public async Task ExecuteWithPacingAsync_ExecutesActionSuccessfully()
        {
            var coordinator = RobloxRateLimitCoordinator.Instance;
            coordinator.ResetBackoff();
            int counter = 0;

            int result = await coordinator.ExecuteWithPacingAsync(
                RateLimitCategory.General,
                () =>
                {
                    counter++;
                    return Task.FromResult(42);
                },
                minIntervalMs: 10);

            Assert.Equal(42, result);
            Assert.Equal(1, counter);
        }

        [Fact]
        public void RecordRateLimitHit_ActivatesBackoff()
        {
            var coordinator = RobloxRateLimitCoordinator.Instance;
            coordinator.ResetBackoff();

            coordinator.RecordRateLimitHit(RateLimitCategory.General, null, defaultBackoffSeconds: 2);

            Assert.True(coordinator.IsBackingOff);
            Assert.True(coordinator.RemainingBackoff > TimeSpan.Zero);

            // Clean up backoff so other tests are not delayed
            coordinator.ResetBackoff();
        }

        [Fact]
        public void RecordRateLimitHit_ConsecutiveHits_IncreaseExponentially()
        {
            var coordinator = RobloxRateLimitCoordinator.Instance;
            coordinator.ResetBackoff();

            try
            {
                coordinator.RecordRateLimitHit(RateLimitCategory.Authentication, null, defaultBackoffSeconds: 3);
                double b1 = coordinator.RemainingBackoff.TotalSeconds;
                Assert.Equal(1, coordinator.ConsecutiveRateLimitHits);
                Assert.InRange(b1, 2.5, 4.5);

                coordinator.RecordRateLimitHit(RateLimitCategory.Authentication, null, defaultBackoffSeconds: 3);
                double b2 = coordinator.RemainingBackoff.TotalSeconds;
                Assert.Equal(2, coordinator.ConsecutiveRateLimitHits);
                Assert.True(b2 > b1, $"Expected b2 ({b2}) > b1 ({b1})");
                Assert.InRange(b2, 5.5, 7.5);

                coordinator.RecordRateLimitHit(RateLimitCategory.Authentication, null, defaultBackoffSeconds: 3);
                double b3 = coordinator.RemainingBackoff.TotalSeconds;
                Assert.Equal(3, coordinator.ConsecutiveRateLimitHits);
                Assert.True(b3 > b2, $"Expected b3 ({b3}) > b2 ({b2})");
                Assert.InRange(b3, 11.5, 13.5);
            }
            finally
            {
                coordinator.ResetBackoff();
            }
        }

        [Fact]
        public void RecordRateLimitHit_RespectsRetryAfterHeaderAndClampsMax()
        {
            var coordinator = RobloxRateLimitCoordinator.Instance;
            coordinator.ResetBackoff();

            try
            {
                using var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));

                coordinator.RecordRateLimitHit(RateLimitCategory.General, response, defaultBackoffSeconds: 3);
                Assert.InRange(coordinator.RemainingBackoff.TotalSeconds, 6.5, 8.5);

                coordinator.ResetBackoff();
                using var hugeResponse = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
                hugeResponse.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(999));

                coordinator.RecordRateLimitHit(RateLimitCategory.General, hugeResponse, defaultBackoffSeconds: 3);
                Assert.InRange(coordinator.RemainingBackoff.TotalSeconds, 119.0, 122.0);
            }
            finally
            {
                coordinator.ResetBackoff();
            }
        }
    }
}
