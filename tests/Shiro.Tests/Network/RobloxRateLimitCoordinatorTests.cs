#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
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

        [Fact]
        public async Task SessionAuditor_UnassignedAccountWithUseProxiesEnabled_TriggersStickyProxyScopeForRateLimitBackoff()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroStickyScope_" + Guid.NewGuid().ToString("N"));
            var prevProxySettings = ProxyBindingService.Settings;
            var prevFactory = SessionAuditorService.TestHttpClientFactory;
            var coordinator = RobloxRateLimitCoordinator.Instance;
            coordinator.ResetBackoff();
            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                var acc = new Account
                {
                    Username = "StickyScopeUser",
                    UserID = 8812,
                    SecurityToken = "_|WARNING:STICKY_TEST",
                    Proxy = string.Empty,
                    Valid = true
                };
                var repo = Substitute.For<IAccountRepository>();
                repo.GetAll().Returns(new[] { acc });
                using var repoScope = AppServices.Override<IAccountRepository>(repo);

                ProxyManagerService.InvalidateCache();
                ProxyManagerService.SaveProxies(new[]
                {
                    new ProxyItem { RawEntry = "10.9.8.7:8080", Host = "10.9.8.7", Port = 8080 }
                });

                var ini = new IniFile();
                var settings = ini.Section("General");
                settings.Set(AppSettingsKeys.General.UseProxies, "true");
                ProxyBindingService.Settings = settings;

                var handler = new FakeHttpHandler(_ =>
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
                    return resp;
                });
                using var client = new HttpClient(handler);
                SessionAuditorService.TestHttpClientFactory = _ => client;

                var result = await SessionAuditorService.AuditAccountAsync(acc);

                Assert.Equal(AccountSessionStatus.RateLimited, result.Status);
                Assert.Equal("10.9.8.7:8080", acc.Proxy);
                Assert.True(coordinator.IsBackingOffCategory(RateLimitCategory.SessionCheck, "10.9.8.7:8080"));
                Assert.False(coordinator.IsBackingOffCategory(RateLimitCategory.SessionCheck));
            }
            finally
            {
                SessionAuditorService.TestHttpClientFactory = prevFactory;
                ProxyBindingService.Settings = prevProxySettings;
                ProxyManagerService.InvalidateCache();
                coordinator.ResetBackoff();
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public async Task SharedHttpClient_NonRobloxHosts_DoNotTriggerOrResetRateLimitBackoff()
        {
            var coordinator = RobloxRateLimitCoordinator.Instance;
            coordinator.ResetBackoff();
            try
            {
                var handler = new FakeHttpHandler(req =>
                {
                    string host = req.RequestUri?.Host ?? string.Empty;
                    if (host.Equals("ipwho.is", StringComparison.OrdinalIgnoreCase) ||
                        host.Equals("evilroblox.com", StringComparison.OrdinalIgnoreCase) ||
                        host.Equals("games.roblox.com", StringComparison.OrdinalIgnoreCase))
                    {
                        var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                        resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
                        return resp;
                    }
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
                });
                SharedHttpClient.SetTestClient("default", new HttpClient(handler));

                using (var extReq = new HttpRequestMessage(HttpMethod.Get, "https://ipwho.is/1.1.1.1"))
                using (var extResp = await SharedHttpClient.SendAsync(extReq, retryTransient: false))
                {
                    Assert.Equal(HttpStatusCode.TooManyRequests, extResp.StatusCode);
                }
                using (var lookalikeReq = new HttpRequestMessage(HttpMethod.Get, "https://evilroblox.com/api"))
                using (var lookalikeResp = await SharedHttpClient.SendAsync(lookalikeReq, retryTransient: false))
                {
                    Assert.Equal(HttpStatusCode.TooManyRequests, lookalikeResp.StatusCode);
                }

                Assert.False(coordinator.IsBackingOffCategory(RateLimitCategory.General));
                Assert.Equal(0, coordinator.ConsecutiveRateLimitHits);

                using (var rbxReq = new HttpRequestMessage(HttpMethod.Get, "https://games.roblox.com/v1/games?universeIds=9"))
                using (var rbxResp = await SharedHttpClient.SendAsync(rbxReq, retryTransient: false))
                {
                    Assert.Equal(HttpStatusCode.TooManyRequests, rbxResp.StatusCode);
                }

                Assert.True(coordinator.IsBackingOffCategory(RateLimitCategory.General));
                Assert.Equal(1, coordinator.ConsecutiveRateLimitHits);

                using (var ghReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/x/y"))
                using (var ghResp = await SharedHttpClient.SendAsync(ghReq, retryTransient: false))
                {
                    Assert.Equal(HttpStatusCode.OK, ghResp.StatusCode);
                }

                Assert.True(coordinator.IsBackingOffCategory(RateLimitCategory.General));
                Assert.Equal(1, coordinator.ConsecutiveRateLimitHits);
            }
            finally
            {
                SharedHttpClient.ResetTestClient("default");
                coordinator.ResetBackoff();
            }
        }
    }
}
