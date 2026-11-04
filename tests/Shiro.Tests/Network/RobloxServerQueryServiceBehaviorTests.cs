#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests.Network
{
    [Collection("StaticSeams")]
    public class RobloxServerQueryServiceBehaviorTests : IDisposable
    {
        public RobloxServerQueryServiceBehaviorTests()
        {
            RobloxServerQueryService.TestHttpClientFactory = null;
        }

        public void Dispose()
        {
            RobloxServerQueryService.TestHttpClientFactory = null;
        }

        [Fact]
        public void Default_Instance_IsNotNullAndSingleton()
        {
            var inst1 = RobloxServerQueryService.Default;
            var inst2 = RobloxServerQueryService.Default;

            Assert.NotNull(inst1);
            Assert.Same(inst1, inst2);
        }

        [Fact]
        public async Task QueryServerRegionAndPingAsync_InvalidInputs_ReturnsFetchingRegionStatus()
        {
            var service = RobloxServerQueryService.Default;
            string fetchingRegion = LocalizationService.Get("ServerQuery.FetchingRegion");

            // Null account
            var resNullAcc = await service.QueryServerRegionAndPingAsync(null!, 12345, "job-id");
            Assert.False(resNullAcc.Success);
            Assert.Equal(fetchingRegion, resNullAcc.Region);

            // Invalid placeId
            var account = new Account { Username = "TestUser" };
            var resZeroPlace = await service.QueryServerRegionAndPingAsync(account, 0, "job-id");
            Assert.False(resZeroPlace.Success);
            Assert.Equal(fetchingRegion, resZeroPlace.Region);

            // Empty serverId
            var resEmptyServer = await service.QueryServerRegionAndPingAsync(account, 12345, "");
            Assert.False(resEmptyServer.Success);
            Assert.Equal(fetchingRegion, resEmptyServer.Region);
        }

        [Fact]
        public async Task QueryPublicServersAsync_InvalidPlaceId_ReturnsEmptyResult()
        {
            var service = RobloxServerQueryService.Default;

            var resZero = await service.QueryPublicServersAsync(0);
            Assert.False(resZero.Success);
            Assert.Empty(resZero.Servers);

            var resNegative = await service.QueryPublicServersAsync(-100);
            Assert.False(resNegative.Success);
            Assert.Empty(resNegative.Servers);
        }

        [Fact]
        public async Task QueryPublicServersAsync_ValidResponse_ParsesServersAndNextCursor()
        {
            string json = @"{
                ""nextPageCursor"": ""next_cursor_page_2"",
                ""data"": [
                    {
                        ""id"": ""job-server-abc"",
                        ""maxPlayers"": 30,
                        ""playing"": 24,
                        ""ping"": 68,
                        ""fps"": ""59.8""
                    },
                    {
                        ""id"": ""job-server-xyz"",
                        ""maxPlayers"": 50,
                        ""playing"": 12,
                        ""ping"": 110,
                        ""fps"": ""60.0""
                    }
                ]
            }";

            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, json);

            RobloxServerQueryService.TestHttpClientFactory = () => new HttpClient(handler);

            var service = RobloxServerQueryService.Default;
            var result = await service.QueryPublicServersAsync(placeId: 1818, cursor: "initial_cursor", limit: 25);

            Assert.True(result.Success);
            Assert.Equal("next_cursor_page_2", result.NextPageCursor);
            Assert.Equal(2, result.Servers.Count);

            var s1 = result.Servers[0];
            Assert.Equal("job-server-abc", s1.id);
            Assert.Equal(30, s1.maxPlayers);
            Assert.Equal(24, s1.playing);
            Assert.Equal(68, s1.ping);
            Assert.Equal("59.8", s1.fps);

            var s2 = result.Servers[1];
            Assert.Equal("job-server-xyz", s2.id);
            Assert.Equal(50, s2.maxPlayers);
            Assert.Equal(12, s2.playing);
            Assert.Equal(110, s2.ping);
            Assert.Equal("60.0", s2.fps);
        }

        [Fact]
        public async Task QueryPublicServersAsync_ErrorResponse_ReturnsUnsuccessfulResult()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.InternalServerError, "Server Error");

            RobloxServerQueryService.TestHttpClientFactory = () => new HttpClient(handler);

            var service = RobloxServerQueryService.Default;
            var result = await service.QueryPublicServersAsync(placeId: 1818);

            Assert.False(result.Success);
            Assert.Empty(result.Servers);
        }
    }
}
