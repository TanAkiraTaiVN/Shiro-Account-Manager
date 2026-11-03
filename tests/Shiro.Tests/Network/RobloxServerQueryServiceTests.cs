#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests.Network
{
    [Collection("StaticSeams")]
    public sealed class RobloxServerQueryServiceTests : IDisposable
    {
        public RobloxServerQueryServiceTests()
        {
            RobloxServerQueryService.TestHttpClientFactory = null;
        }

        public void Dispose()
        {
            RobloxServerQueryService.TestHttpClientFactory = null;
        }

        [Fact]
        public async Task QueryPublicServers_MapsPayload()
        {
            string payload = @"{
  ""nextPageCursor"": ""cursor_page_2"",
  ""data"": [
    {
      ""id"": ""server-guid-1"",
      ""maxPlayers"": 20,
      ""playing"": 15,
      ""ping"": 45,
      ""fps"": ""59.9""
    },
    {
      ""id"": ""server-guid-2"",
      ""maxPlayers"": 30,
      ""playing"": 28,
      ""ping"": 60,
      ""fps"": ""60.0""
    }
  ]
}";

            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, payload);

            RobloxServerQueryService.TestHttpClientFactory = () => new HttpClient(handler);

            var result = await RobloxServerQueryService.Default.QueryPublicServersAsync(123456);

            Assert.True(result.Success);
            Assert.Equal("cursor_page_2", result.NextPageCursor);
            Assert.Equal(2, result.Servers.Count);

            Assert.Equal("server-guid-1", result.Servers[0].id);
            Assert.Equal(20, result.Servers[0].maxPlayers);
            Assert.Equal(15, result.Servers[0].playing);
            Assert.Equal(45, result.Servers[0].ping);
            Assert.Equal("59.9", result.Servers[0].fps);

            Assert.Equal("server-guid-2", result.Servers[1].id);
            Assert.Equal(30, result.Servers[1].maxPlayers);
            Assert.Equal(28, result.Servers[1].playing);
            Assert.Equal(60, result.Servers[1].ping);
            Assert.Equal("60.0", result.Servers[1].fps);
        }

        [Fact]
        public async Task QueryPublicServers_429_ReturnsFailureWithoutThrow()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.TooManyRequests, "{}", ("Retry-After", "5"));

            RobloxServerQueryService.TestHttpClientFactory = () => new HttpClient(handler);

            var result = await RobloxServerQueryService.Default.QueryPublicServersAsync(123456);

            Assert.False(result.Success);
            Assert.Empty(result.Servers);
        }
    }
}
