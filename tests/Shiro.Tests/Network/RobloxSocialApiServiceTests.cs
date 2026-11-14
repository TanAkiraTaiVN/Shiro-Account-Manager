#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests.Network
{
    [Collection("StaticSeams")]
    public sealed class RobloxSocialApiServiceTests : IDisposable
    {
        public RobloxSocialApiServiceTests()
        {
            Account.TestHttpClientFactory = null;
        }

        public void Dispose()
        {
            Account.TestHttpClientFactory = null;
        }

        [Fact]
        public void ExtractBlockedUserIdsFromJson_ParsesAndIgnoresGarbage()
        {
            string validJson = "{\"blockedUserIds\":[1,2]}";
            var ids = RobloxSocialApiService.ExtractBlockedUserIdsFromJson(validJson);
            Assert.Equal(new long[] { 1, 2 }, ids);

            string garbage = "not json";
            var garbageIds = RobloxSocialApiService.ExtractBlockedUserIdsFromJson(garbage);
            Assert.Empty(garbageIds);
        }

        [Fact]
        public void IsUserIdInBlockedListJson_TrueOnlyForMember()
        {
            string json = "{\"blockedUserIds\":[1,2]}";
            Assert.True(RobloxSocialApiService.IsUserIdInBlockedListJson(json, 2));
            Assert.False(RobloxSocialApiService.IsUserIdInBlockedListJson(json, 3));
        }

        [Fact]
        public async Task UnblockEveryone_UnblocksEachId()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, "{\"blockedUserIds\":[5,6]}")
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "csrf-token-1"))
                .Enqueue(HttpStatusCode.OK, "{}")
                .Enqueue(HttpStatusCode.OK, "{}");

            Account.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = new Account
            {
                Username = "Unblocker",
                UserID = 12345,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.COOKIE.S"
            };

            var (success, message) = await RobloxSocialApiService.Instance.UnblockEveryoneAsync(account);

            Assert.True(success);
            Assert.Equal("Unblocked everyone", message);
            Assert.Contains(handler.Requests, r => r.RequestUri!.ToString().Contains("/5/unblock-user"));
            Assert.Contains(handler.Requests, r => r.RequestUri!.ToString().Contains("/6/unblock-user"));
        }
    }
}
