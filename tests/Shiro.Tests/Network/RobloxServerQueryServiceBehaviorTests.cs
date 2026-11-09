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
    public class RobloxServerQueryServiceBehaviorTests : IDisposable
    {
        public RobloxServerQueryServiceBehaviorTests()
        {
            RobloxServerQueryService.TestHttpClientFactory = null;
            RobloxServerQueryService.TestPingProbe = null;
            RobloxServerQueryService.TestGeoHttpGetString = null;
            Account.TestHttpClientFactory = null;
        }

        public void Dispose()
        {
            RobloxServerQueryService.TestHttpClientFactory = null;
            RobloxServerQueryService.TestPingProbe = null;
            RobloxServerQueryService.TestGeoHttpGetString = null;
            Account.TestHttpClientFactory = null;
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
        public void Ctor_NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new RobloxServerQueryService(null!, AppServices.GeneralSettings));
            Assert.Throws<ArgumentNullException>(() => new RobloxServerQueryService(AppServices.Logger, null!));
        }

        [Fact]
        public async Task QueryServerRegionAndPingAsync_InvalidInputs_ReturnsFetchingRegionStatus()
        {
            var service = RobloxServerQueryService.Default;
            string fetchingRegion = LocalizationService.Get("ServerQuery.FetchingRegion");

            var resNullAcc = await service.QueryServerRegionAndPingAsync(null!, 12345, "job-id");
            Assert.False(resNullAcc.Success);
            Assert.Equal(fetchingRegion, resNullAcc.Region);

            var account = new Account { Username = "TestUser" };
            var resZeroPlace = await service.QueryServerRegionAndPingAsync(account, 0, "job-id");
            Assert.False(resZeroPlace.Success);
            Assert.Equal(fetchingRegion, resZeroPlace.Region);

            var resEmptyServer = await service.QueryServerRegionAndPingAsync(account, 12345, "");
            Assert.False(resEmptyServer.Success);
            Assert.Equal(fetchingRegion, resEmptyServer.Region);
        }

        [Fact]
        public async Task QueryServerRegionAndPingAsync_GeoIpFallback_ResolvesCityAndCountry()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, "{\"joinScript\":{\"MachineAddress\":\"198.51.100.42\",\"DataCenterId\":999999}}")
                .Enqueue(HttpStatusCode.OK, "{\"joinScript\":{\"MachineAddress\":\"198.51.100.43\",\"DataCenterId\":999999}}");
            Account.TestHttpClientFactory = _ => new HttpClient(handler);
            RobloxServerQueryService.TestPingProbe = _ => Task.FromResult<int?>(42);
            RobloxServerQueryService.TestGeoHttpGetString = (url, _) =>
            {
                if (url.Contains("custom.geo"))
                    return Task.FromResult("{\"status\":\"fail\"}");
                return Task.FromResult("{\"success\":true,\"city\":\"Tokyo\",\"country_code\":\"JP\"}");
            };

            var account = new Account
            {
                Username = "GeoUser",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--GeoCookie12345"
            };

            var res = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(
                account,
                1818,
                "job-geo-1",
                ipApiTemplate: "https://custom.geo/<ip>");

            Assert.True(res.Success);
            Assert.Equal("198.51.100.42", res.Ip);
            Assert.Equal(42, res.PingMs);
            Assert.Equal("Tokyo, JP", res.Region);
            Assert.Equal("JP", res.CountryCode);

            // Second call for same subnet hits cache
            var resCached = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(
                account,
                1818,
                "job-geo-2");
            Assert.Equal(42, resCached.PingMs);
        }

        [Fact]
        public async Task QueryServerRegionAndPingAsync_MissingMachineAddress_SetsStatusOrUnknownRegion()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, "{\"status\":\"GameFull\"}")
                .Enqueue(HttpStatusCode.OK, "{}");
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            var account = new Account
            {
                Username = "NoIpUser",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--NoIpCookie12345"
            };

            var res = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(account, 1818, "job-full");
            Assert.False(res.Success);
            Assert.Equal("GameFull", res.Region);

            var resUnknown = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(account, 1818, "job-empty");
            Assert.False(resUnknown.Success);
            Assert.Equal(LocalizationService.Get("ServerQuery.UnknownRegion"), resUnknown.Region);
        }

        [Fact]
        public async Task QueryServerRegionAndPingAsync_OfflineDatacenterAndGeoIpVariants_Work()
        {
            var handler = new FakeHttpHandler()
                // 1. Offline datacenter resolution via root dataCenterId=295, direct ping null -> fallback SamplePingIp
                .Enqueue(HttpStatusCode.OK, "{\"joinScript\":{\"MachineAddress\":\"203.0.113.55\"},\"dataCenterId\":295}")
                // 2. GeoIP countryCode only (no city)
                .Enqueue(HttpStatusCode.OK, "{\"joinScript\":{\"MachineAddress\":\"198.51.101.10\"}}")
                // 3. GeoIP country property + real localhost ping
                .Enqueue(HttpStatusCode.OK, "{\"joinScript\":{\"MachineAddress\":\"127.0.0.1\"}}");

            Account.TestHttpClientFactory = _ => new HttpClient(handler);
            RobloxServerQueryService.TestPingProbe = ip =>
                Task.FromResult<int?>(ip == "203.0.113.55" ? null : 19);

            int geoCall = 0;
            RobloxServerQueryService.TestGeoHttpGetString = (_, _) =>
            {
                geoCall++;
                if (geoCall == 1)
                    return Task.FromResult("{\"countryCode\":\"DE\"}");
                if (geoCall == 2)
                    return Task.FromResult("{\"success\":false}");
                return Task.FromResult("{\"city\":\"Berlin\",\"country\":\"Germany\"}");
            };

            var account = new Account
            {
                Username = "DcUser",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--DcCookie12345"
            };

            var dcRes = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(account, 1818, "job-dc");
            Assert.True(dcRes.Success);
            Assert.Equal(19, dcRes.PingMs);

            var deRes = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(account, 1818, "job-de");
            Assert.True(deRes.Success);
            Assert.Equal("DE", deRes.Region);

            // Real localhost ping path (TestPingProbe = null)
            RobloxServerQueryService.TestPingProbe = null;
            var localRes = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(
                account,
                1818,
                "job-local",
                ipApiTemplate: "https://custom.geo/<ip>");
            Assert.True(localRes.Success);
            Assert.Equal("Berlin, Germany", localRes.Region);
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
