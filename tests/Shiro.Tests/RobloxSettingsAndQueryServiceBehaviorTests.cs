#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class RobloxSettingsAndQueryServiceBehaviorTests : IDisposable
    {
        private readonly RobloxSettingsApiService _settingsService;
        private readonly RobloxServerQueryService _queryService;

        public RobloxSettingsAndQueryServiceBehaviorTests()
        {
            _settingsService = RobloxSettingsApiService.Default;
            _queryService = RobloxServerQueryService.Default;
            RobloxServerQueryService.TestHttpClientFactory = null;
        }

        public void Dispose()
        {
            Account.TestHttpClientFactory = null;
            RobloxServerQueryService.TestHttpClientFactory = null;
        }

        [Fact]
        public async Task CheckPinAsync_NullOrUnlockedAccount_ReturnsExpected()
        {
            Assert.False(await _settingsService.CheckPinAsync(null!));

            var unlockedAcc = new Account
            {
                Username = "UnlockedPinUser",
                PinUnlocked = DateTime.Now.AddMinutes(5)
            };
            Assert.True(await _settingsService.CheckPinAsync(unlockedAcc));

            var noTokenAcc = new Account
            {
                Username = "NoTokenUser",
                SecurityToken = ""
            };
            Assert.False(await _settingsService.CheckPinAsync(noTokenAcc));
        }

        [Fact]
        public async Task CheckPinAsync_WhenPinNotEnabled_MarksUnlockedAndReturnsTrue()
        {
            var acc = new Account
            {
                Username = "NoPinRequiredUser",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.VALIDTOKEN123"
            };

            var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"isEnabled\":false,\"unlockedUntil\":null}")
            });
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            bool ok = await _settingsService.CheckPinAsync(acc);

            Assert.True(ok);
            Assert.True(acc.PinUnlocked > DateTime.Now);
        }

        [Fact]
        public async Task CheckPinAsync_WhenPinEnabledAndLocked_ReturnsFalse()
        {
            var acc = new Account
            {
                Username = "PinLockedUser",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.VALIDTOKEN123"
            };

            var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"isEnabled\":true,\"unlockedUntil\":0}")
            });
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            bool ok = await _settingsService.CheckPinAsync(acc);

            Assert.False(ok);
        }

        [Fact]
        public async Task UnlockPinAsync_ValidationAndEdgeCases()
        {
            // Null account or invalid length pin returns false
            Assert.False(await _settingsService.UnlockPinAsync(null!, "1234"));
            Assert.False(await _settingsService.UnlockPinAsync(new Account(), "123"));
            Assert.False(await _settingsService.UnlockPinAsync(new Account(), "12345"));

            // Account already unlocked returns true immediately
            var unlockedAcc = new Account
            {
                Username = "AlreadyUnlocked",
                PinUnlocked = DateTime.Now.AddMinutes(10)
            };
            Assert.True(await _settingsService.UnlockPinAsync(unlockedAcc, "1234"));
        }

        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("   ", "")]
        [InlineData("192.168.1.100", "192.168.1")]
        [InlineData("10.0.50.25", "10.0.50")]
        [InlineData("singletoken", "singletoken")]
        public void GetIpv4SubnetPrefix_ExtractsSubnet(string? ip, string expected)
        {
            var method = typeof(RobloxServerQueryService).GetMethod("GetIpv4SubnetPrefix", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            string actual = (string)method!.Invoke(null, new object?[] { ip })!;
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(null, false, "")]
        [InlineData("", false, "")]
        [InlineData("{\"success\":false}", false, "")]
        [InlineData("{\"status\":\"fail\"}", false, "")]
        [InlineData("{\"city\":\"Tokyo\",\"countryCode\":\"JP\"}", true, "Tokyo, JP")]
        [InlineData("{\"city\":\"\",\"countryCode\":\"US\"}", true, "US")]
        [InlineData("{\"city\":\"Singapore\",\"countryCode\":\"\"}", true, "Singapore")]
        public void TryParseRegionFromJson_ParsesCorrectly(string? geoJson, bool expectedSuccess, string expectedRegion)
        {
            var method = typeof(RobloxServerQueryService).GetMethod("TryParseRegionFromJson", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var parameters = new object?[] { geoJson, null };
            bool success = (bool)method!.Invoke(null, parameters)!;
            string region = (string)parameters[1]!;

            Assert.Equal(expectedSuccess, success);
            if (expectedSuccess)
            {
                Assert.Equal(expectedRegion, region);
            }
        }

        [Fact]
        public async Task QueryServerRegionAndPingAsync_InvalidInputs_ReturnsDefaultResult()
        {
            var res1 = await _queryService.QueryServerRegionAndPingAsync(null!, 123, "server-1");
            Assert.False(res1.Success);

            var acc = new Account { Username = "TestUser" };
            var res2 = await _queryService.QueryServerRegionAndPingAsync(acc, 0, "server-1");
            Assert.False(res2.Success);

            var res3 = await _queryService.QueryServerRegionAndPingAsync(acc, 123, "");
            Assert.False(res3.Success);
        }

        [Fact]
        public async Task QueryPublicServersAsync_InvalidPlaceId_ReturnsEmptyResult()
        {
            var res0 = await _queryService.QueryPublicServersAsync(0);
            Assert.Empty(res0.Servers);

            var resNeg = await _queryService.QueryPublicServersAsync(-100);
            Assert.Empty(resNeg.Servers);
        }

        [Fact]
        public async Task QueryPublicServersAsync_WithTestClient_ParsesServerListResult()
        {
            string fakeResponse = "{\"previousPageCursor\":null,\"nextPageCursor\":\"cursor_2\",\"data\":[{\"id\":\"job-111\",\"maxPlayers\":12,\"playing\":8,\"playerTokens\":[],\"fps\":60.0,\"ping\":45}]}";
            var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(fakeResponse)
            });

            RobloxServerQueryService.TestHttpClientFactory = () => new HttpClient(handler);

            var result = await _queryService.QueryPublicServersAsync(18973, limit: 10);

            Assert.Equal("cursor_2", result.NextPageCursor);
            Assert.Single(result.Servers);
            Assert.Equal("job-111", result.Servers[0].id);
            Assert.Equal(8, result.Servers[0].playing);
            Assert.Equal(12, result.Servers[0].maxPlayers);
        }
    }
}
