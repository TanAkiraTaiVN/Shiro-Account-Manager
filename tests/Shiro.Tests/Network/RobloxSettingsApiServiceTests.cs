#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests.Network
{
    [Collection("StaticSeams")]
    public sealed class RobloxSettingsApiServiceTests : IDisposable
    {
        public RobloxSettingsApiServiceTests()
        {
            Account.TestHttpClientFactory = null;
        }

        public void Dispose()
        {
            Account.TestHttpClientFactory = null;
        }

        [Fact]
        public async Task SetFollowPrivacy_RetriesOnceWithCsrfToken()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "abc"))
                .Enqueue(HttpStatusCode.OK, "{}");

            Account.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = new Account
            {
                Username = "PrivacyUser",
                UserID = 12345,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.COOKIE.S",
                PinUnlocked = DateTime.Now.AddHours(1)
            };

            bool result = await RobloxSettingsApiService.Instance.SetFollowPrivacyAsync(account, 0);

            Assert.True(result);
            Assert.Equal(2, handler.Requests.Count);
            Assert.True(handler.Requests[1].Headers.TryGetValues("X-CSRF-TOKEN", out var csrfValues));
            Assert.Equal("abc", csrfValues.Single());
        }

        [Fact]
        public async Task SetFollowPrivacy_ReturnsFalseOn401()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Unauthorized, "{}");

            Account.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = new Account
            {
                Username = "PrivacyUser401",
                UserID = 12345,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.COOKIE.S",
                PinUnlocked = DateTime.MinValue
            };

            bool result = await RobloxSettingsApiService.Instance.SetFollowPrivacyAsync(account, 0);

            Assert.False(result);
            Assert.Single(handler.Requests);
            Assert.Contains("v1/account/pin", handler.Requests[0].RequestUri!.ToString());
        }

        [Fact]
        public async Task UnlockPin_SendsPinInBody()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "test-csrf"))
                .Enqueue(HttpStatusCode.OK, "{\"isEnabled\":false}");

            Account.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = new Account
            {
                Username = "PinUser",
                UserID = 12345,
                SecurityToken = "",
                PinUnlocked = DateTime.MinValue
            };

            bool result = await RobloxSettingsApiService.Instance.UnlockPinAsync(account, "1234");

            Assert.True(result);
            Assert.Equal(2, handler.Requests.Count);
            Assert.NotNull(handler.RequestBodies[^1]);
            Assert.Contains("\"pin\":\"1234\"", handler.RequestBodies[^1]!);
        }

        [Fact]
        public void ParseAccessCode_ExtractsCodeFromHtml()
        {
            var legacyResponse = new RestResponse
            {
                Content = "<html><script>Roblox.GameLauncher.joinPrivateGame(123456, '11111111-2222-3333-4444-555555555555');</script></html>"
            };
            bool legacySuccess = RobloxSettingsApiService.ParseAccessCode(legacyResponse, out string legacyCode);
            Assert.True(legacySuccess);
            Assert.Equal("11111111-2222-3333-4444-555555555555", legacyCode);

            var modernResponse = new RestResponse
            {
                Content = "{\"accessCode\":\"abcdef12-3456-7890-abcd-ef1234567890\"}"
            };
            bool modernSuccess = RobloxSettingsApiService.ParseAccessCode(modernResponse, out string modernCode);
            Assert.True(modernSuccess);
            Assert.Equal("abcdef12-3456-7890-abcd-ef1234567890", modernCode);
        }
    }
}
