using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class PlayerSearchPipelineAndAuditorTests
    {
        private sealed class ConcurrentProbeHandler : HttpMessageHandler
        {
            private int _inFlightEconomyCalls;
            public int MaxObservedConcurrentEconomyCalls { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string url = request.RequestUri?.AbsoluteUri ?? string.Empty;
                if (url.Contains("/v1/users/authenticated", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":777,\"name\":\"ShiroUser\",\"displayName\":\"Shiro\"}")
                    };
                }

                if (url.Contains("economy.roblox.com", StringComparison.Ordinal))
                {
                    int current = Interlocked.Increment(ref _inFlightEconomyCalls);
                    if (current > MaxObservedConcurrentEconomyCalls)
                        MaxObservedConcurrentEconomyCalls = current;

                    await Task.Delay(60, cancellationToken).ConfigureAwait(false);
                    Interlocked.Decrement(ref _inFlightEconomyCalls);

                    string body = url.Contains("/currency", StringComparison.Ordinal)
                        ? "{\"robux\":1500}"
                        : "{\"pendingRobuxTotal\":350}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        [Fact]
        public async Task AuditAccountAsync_FetchesCurrencyAndPendingRobuxConcurrently()
        {
            using var handler = new ConcurrentProbeHandler();
            using var client = new HttpClient(handler);
            var prevFactory = SessionAuditorService.TestHttpClientFactory;
            try
            {
                SessionAuditorService.TestHttpClientFactory = _ => client;
                var account = new Account { Username = "ShiroUser", SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS._test" };

                var result = await SessionAuditorService.AuditAccountAsync(account);

                Assert.Equal(AccountSessionStatus.Valid, result.Status);
                Assert.Equal(1500, result.RobuxBalance);
                Assert.Equal(350, result.PendingRobux);
                Assert.Equal(2, handler.MaxObservedConcurrentEconomyCalls);
            }
            finally
            {
                SessionAuditorService.TestHttpClientFactory = prevFactory;
            }
        }
    }
}
