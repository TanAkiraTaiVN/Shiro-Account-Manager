#nullable enable
using System;
using System.Collections.Generic;
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
        private const string PrivateLoopbackMachineJson = "{\"joinScript\":{\"MachineAddress\":\"127.0.0.1\"}}";
        private const string PrivateRfc1918MachineJson = "{\"joinScript\":{\"MachineAddress\":\"10.0.0.1\"}}";
        private const string PrivateLinkLocalMachineJson = "{\"joinScript\":{\"MachineAddress\":\"169.254.169.254\"}}";
        private const string InvalidIpMachineJson = "{\"joinScript\":{\"MachineAddress\":\"not-an-ip\"}}";
        private const string PublicGeoHostTestIpJson = "{\"joinScript\":{\"MachineAddress\":\"198.51.103.22\",\"DataCenterId\":999999}}";
        private const string PublicCsrfRetryIpJson = "{\"joinScript\":{\"MachineAddress\":\"198.51.104.77\",\"DataCenterId\":999999}}";
        private const string PublicWhitespaceIpJson = "{\"joinScript\":{\"MachineAddress\":\" 198.51.105.11 \",\"DataCenterId\":999999}}";
        private const string PublicMappedIpv6Json = "{\"joinScript\":{\"MachineAddress\":\"::ffff:198.51.106.12\",\"DataCenterId\":999999}}";
        private const string OsakaGeoJson = "{\"success\":true,\"city\":\"Osaka\",\"country_code\":\"JP\"}";
        public RobloxServerQueryServiceBehaviorTests()
        {
            RobloxServerQueryService.TestHttpClientFactory = null;
            RobloxServerQueryService.TestPingProbe = null;
            RobloxServerQueryService.TestGeoHttpGetString = null;
            RobloxAccountSessionClient.TestHttpClientFactory = null;
        }

        public void Dispose()
        {
            RobloxServerQueryService.TestHttpClientFactory = null;
            RobloxServerQueryService.TestPingProbe = null;
            RobloxServerQueryService.TestGeoHttpGetString = null;
            RobloxAccountSessionClient.TestHttpClientFactory = null;
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
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
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
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);

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
                // 3. GeoIP country property + unmocked ping path on public IP
                .Enqueue(HttpStatusCode.OK, "{\"joinScript\":{\"MachineAddress\":\"192.0.2.10\"}}");

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
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

        [Fact]
        public async Task QueryServerRegionAndPingAsync_WhenServerReturnsPrivateOrInvalidIp_BlocksPingAndGeoFetch()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, PrivateLoopbackMachineJson)
                .Enqueue(HttpStatusCode.OK, PrivateRfc1918MachineJson)
                .Enqueue(HttpStatusCode.OK, PrivateLinkLocalMachineJson)
                .Enqueue(HttpStatusCode.OK, InvalidIpMachineJson);
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);

            int pingCalls = 0;
            int geoCalls = 0;
            RobloxServerQueryService.TestPingProbe = _ =>
            {
                pingCalls++;
                return Task.FromResult<int?>(15);
            };
            RobloxServerQueryService.TestGeoHttpGetString = (_, _) =>
            {
                geoCalls++;
                return Task.FromResult(OsakaGeoJson);
            };

            var account = new Account
            {
                Username = "SsrfGuardUser",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--SsrfCookie12345"
            };
            string unknownRegion = LocalizationService.Get("ServerQuery.UnknownRegion");

            for (int i = 0; i < 4; i++)
            {
                var res = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(account, 1818, $"job-ssrf-{i}");
                Assert.False(res.Success);
                Assert.Null(res.PingMs);
                Assert.Equal(unknownRegion, res.Region);
            }

            Assert.Equal(0, pingCalls);
            Assert.Equal(0, geoCalls);
        }

        [Fact]
        public async Task QueryServerRegionAndPingAsync_WhenGeoTemplateNotHttps_SkipsInsecurePrimaryUrl()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, PublicGeoHostTestIpJson);
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            RobloxServerQueryService.TestPingProbe = _ => Task.FromResult<int?>(31);

            var requestedUrls = new List<string>();
            RobloxServerQueryService.TestGeoHttpGetString = (url, _) =>
            {
                requestedUrls.Add(url);
                return Task.FromResult(OsakaGeoJson);
            };

            var account = new Account
            {
                Username = "HttpsGeoUser",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--HttpsGeo12345"
            };

            var res = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(
                account,
                1818,
                "job-https-only",
                ipApiTemplate: "http://insecure.geo/<ip>");

            Assert.DoesNotContain(requestedUrls, u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("https://ipwho.is/198.51.103.22", requestedUrls);
            Assert.True(res.Success);
            Assert.Equal("Osaka, JP", res.Region);
        }

        [Fact]
        public async Task QueryServerRegionAndPingAsync_When403CsrfChallenge_RetriesWithRefreshedToken()
        {
            int calls = 0;
            var seenTokens = new List<string?>();
            var handler = new FakeHttpHandler(req =>
            {
                calls++;
                string? token = req.Headers.TryGetValues("X-CSRF-TOKEN", out var values)
                    ? string.Join(",", values)
                    : null;
                seenTokens.Add(token);

                if (calls == 1)
                {
                    var challenge = new HttpResponseMessage(HttpStatusCode.Forbidden);
                    challenge.Headers.TryAddWithoutValidation("x-csrf-token", "refreshed-csrf-789");
                    return challenge;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(PublicCsrfRetryIpJson)
                };
            });

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            RobloxServerQueryService.TestPingProbe = _ => Task.FromResult<int?>(25);
            RobloxServerQueryService.TestGeoHttpGetString = (_, _) => Task.FromResult(OsakaGeoJson);

            var account = new Account
            {
                Username = "CsrfQueryUser",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--CsrfCookie12345"
            };

            var res = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(account, 1818, "job-csrf-1");

            Assert.Equal(2, calls);
            Assert.Equal(new string?[] { null, "refreshed-csrf-789" }, seenTokens.ToArray());
            Assert.Equal("refreshed-csrf-789", account.CSRFToken);
            Assert.True(res.Success);
            Assert.Equal("198.51.104.77", res.Ip);
            Assert.Equal("Osaka, JP", res.Region);
        }

        [Theory]
        [InlineData("127.0.0.1", "https://ipwho.is/127.0.0.1", false)]
        [InlineData("127.255.0.1", "https://ipwho.is/127.255.0.1", false)]
        [InlineData("::1", "https://ipwho.is/::1", false)]
        [InlineData("10.0.0.1", "https://ipwho.is/10.0.0.1", false)]
        [InlineData("172.16.0.1", "https://ipwho.is/172.16.0.1", false)]
        [InlineData("172.31.255.254", "https://ipwho.is/172.31.255.254", false)]
        [InlineData("192.168.1.1", "https://ipwho.is/192.168.1.1", false)]
        [InlineData("169.254.169.254", "https://ipwho.is/169.254.169.254", false)]
        [InlineData("100.64.0.1", "https://ipwho.is/100.64.0.1", false)]
        [InlineData("100.127.255.254", "https://ipwho.is/100.127.255.254", false)]
        [InlineData("0.0.0.0", "https://ipwho.is/0.0.0.0", false)]
        [InlineData("224.0.0.1", "https://ipwho.is/224.0.0.1", false)]
        [InlineData("::", "https://ipwho.is/::", false)]
        [InlineData("fe80::1", "https://ipwho.is/fe80::1", false)]
        [InlineData("fc00::1", "https://ipwho.is/fc00::1", false)]
        [InlineData("fd12:3456:789a::1", "https://ipwho.is/fd12:3456:789a::1", false)]
        [InlineData("ff02::1", "https://ipwho.is/ff02::1", false)]
        [InlineData("::ffff:127.0.0.1", "https://ipwho.is/127.0.0.1", false)]
        [InlineData("::ffff:10.0.0.1", "https://ipwho.is/10.0.0.1", false)]
        [InlineData("fe80::1%eth0", "https://ipwho.is/1.1.1.1", false)]
        [InlineData("2001:4860:4860::8888%1", "https://ipwho.is/1.1.1.1", false)]
        [InlineData("2001:4860:4860::8888%eth0", "https://ipwho.is/1.1.1.1", false)]
        [InlineData("not-an-ip", "https://ipwho.is/not-an-ip", false)]
        [InlineData("", "https://ipwho.is/1.1.1.1", false)]
        [InlineData("1.1.1.1", "http://ipwho.is/1.1.1.1", false)]
        [InlineData("1.1.1.1", "file:///etc/passwd", false)]
        [InlineData("1.1.1.1", "https://127.0.0.1/1.1.1.1", false)]
        [InlineData("1.1.1.1", "https://localhost/1.1.1.1", false)]
        [InlineData("1.1.1.1", "https://localhost./1.1.1.1", false)]
        [InlineData("1.1.1.1", "https://foo.localhost./1.1.1.1", false)]
        [InlineData("1.1.1.1", "https://user:pass@ipwho.is/1.1.1.1", false)]
        [InlineData("1.1.1.1", "https://user@ipwho.is/1.1.1.1", false)]
        [InlineData("1.1.1.1", "", false)]
        [InlineData("1.1.1.1", "https://ipwho.is/1.1.1.1", true)]
        [InlineData(" 1.1.1.1 ", "https://ipwho.is/1.1.1.1", true)]
        [InlineData("::ffff:1.1.1.1", "https://ipwho.is/1.1.1.1", true)]
        [InlineData("172.32.0.1", "https://ipwho.is/172.32.0.1", true)]
        [InlineData("100.128.0.1", "https://ipwho.is/100.128.0.1", true)]
        [InlineData("2606:4700:4700::1111", "https://ipwho.is/2606:4700:4700::1111", true)]
        public void ServerQuery_TryValidatePublicTargetIpAndGeoUrl_BlocksSsrf(string ip, string url, bool expected)
        {
            bool actual = RobloxServerQueryService.TryValidatePublicTargetIpAndGeoUrl(ip, url, out IPAddress? parsedIp);
            Assert.Equal(expected, actual);
            if (expected)
                Assert.NotNull(parsedIp);
            else
                Assert.Null(parsedIp);
        }

        [Fact]
        public void ServerQuery_TryValidatePublicTargetIpAndGeoUrl_NormalizesWhitespaceAndIpv4MappedIp()
        {
            Assert.True(RobloxServerQueryService.TryValidatePublicTargetIpAndGeoUrl(" 1.1.1.1 ", "https://ipwho.is/1.1.1.1", out IPAddress? trimmed));
            Assert.Equal("1.1.1.1", trimmed!.ToString());

            Assert.True(RobloxServerQueryService.TryValidatePublicTargetIpAndGeoUrl("::ffff:198.51.105.9", "https://ipwho.is/198.51.105.9", out IPAddress? mapped));
            Assert.Equal("198.51.105.9", mapped!.ToString());
        }

        [Fact]
        public async Task QueryServerRegionAndPingAsync_WhenMachineAddressHasWhitespaceOrMappedIpv6_ConsumesNormalizedIp()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, PublicWhitespaceIpJson)
                .Enqueue(HttpStatusCode.OK, PublicMappedIpv6Json);
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);

            var pingTargets = new List<string>();
            var geoUrls = new List<string>();
            RobloxServerQueryService.TestPingProbe = target =>
            {
                pingTargets.Add(target);
                return Task.FromResult<int?>(19);
            };
            RobloxServerQueryService.TestGeoHttpGetString = (url, _) =>
            {
                geoUrls.Add(url);
                return Task.FromResult(OsakaGeoJson);
            };

            var account = new Account
            {
                Username = "NormalizedIpUser",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--NormCookie12345"
            };

            var wsResult = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(
                account, 1818, "job-ws-1", ipApiTemplate: "https://custom.geo/<ip>");
            var mappedResult = await RobloxServerQueryService.Default.QueryServerRegionAndPingAsync(
                account, 1818, "job-mapped-1", ipApiTemplate: "https://custom.geo/<ip>");

            Assert.True(wsResult.Success);
            Assert.Equal("198.51.105.11", wsResult.Ip);
            Assert.True(mappedResult.Success);
            Assert.Equal("198.51.106.12", mappedResult.Ip);
            Assert.Equal(new[] { "198.51.105.11", "198.51.106.12" }, pingTargets.ToArray());
            Assert.Equal(new[] { "https://custom.geo/198.51.105.11", "https://custom.geo/198.51.106.12" }, geoUrls.ToArray());
        }
    }
}
