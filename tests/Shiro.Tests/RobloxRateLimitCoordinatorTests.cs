#nullable enable
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
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

        [Fact]
        public async Task RecordRateLimitHit_ScopesBackoffByProxyEndpoint_WithoutBlockingOtherProxiesOrDirect()
        {
            var coordinator = new RobloxRateLimitCoordinator();
            coordinator.RecordRateLimitHit(RateLimitCategory.General, null, defaultBackoffSeconds: 10, proxyScope: "http://10.0.0.1:8080");

            Assert.True(coordinator.IsBackingOffCategory(RateLimitCategory.General, "10.0.0.1:8080"));
            Assert.False(coordinator.IsBackingOffCategory(RateLimitCategory.General, "10.0.0.2:8080"));
            Assert.False(coordinator.IsBackingOffCategory(RateLimitCategory.General));

            var sw = Stopwatch.StartNew();
            int res = await coordinator.ExecuteWithPacingAsync(
                RateLimitCategory.General,
                () => Task.FromResult(99),
                proxyScope: "10.0.0.2:8080",
                minIntervalMs: 0);
            sw.Stop();

            Assert.Equal(99, res);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"Unrelated proxy waited {sw.ElapsedMilliseconds}ms.");

            coordinator.RecordRateLimitHit(RateLimitCategory.General, null, defaultBackoffSeconds: 10, proxyScope: "10.0.0.2:8080");
            Assert.Equal(2, coordinator.ConsecutiveRateLimitHits);

            coordinator.RecordSuccess(RateLimitCategory.General, "http://10.0.0.1:8080");
            Assert.Equal(1, coordinator.ConsecutiveRateLimitHits);
        }

        [Fact]
        public void RecordRateLimitHit_EnqueuesWebhookAlertOutsideBackoffLock_NoCrossThreadDeadlock()
        {
            var coordinator = new RobloxRateLimitCoordinator();
            bool probeCompleted = false;
            bool observedBackingOff = false;

            void OnAlert(WebhookAlert alert)
            {
                var probeTask = Task.Run(() =>
                {
                    observedBackingOff = coordinator.IsBackingOffCategory(RateLimitCategory.SessionCheck, "10.0.0.5:8080");
                    var remaining = coordinator.GetRemainingBackoff(RateLimitCategory.SessionCheck, "10.0.0.5:8080");
                    if (remaining < TimeSpan.Zero)
                        observedBackingOff = false;
                });
                probeCompleted = probeTask.Wait(TimeSpan.FromSeconds(2));
            }

            WebhookNotificationService.AlertDispatched += OnAlert;
            try
            {
                coordinator.RecordRateLimitHit(RateLimitCategory.SessionCheck, null, defaultBackoffSeconds: 5, proxyScope: "10.0.0.5:8080");
                Assert.True(probeCompleted, "AlertDispatched subscriber deadlocked on _backoffLock while querying coordinator.");
                Assert.True(observedBackingOff);
            }
            finally
            {
                WebhookNotificationService.AlertDispatched -= OnAlert;
            }
        }
    }
}
