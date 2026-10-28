using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shiro;
using Shiro.Http;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("RateLimitCoordinator")]
    public class ValidateSessionTests : IDisposable
    {
        private const string FullToken =
            "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_" +
            "HEADER.PAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOADPAYLOAD.SIG";

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
            public int Calls;
            public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                return Task.FromResult(_responder(request));
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string body = "{}") =>
            new HttpResponseMessage(code) { Content = new StringContent(body) };

        public ValidateSessionTests() => RobloxRateLimitCoordinator.Instance.ResetBackoff();

        public void Dispose()
        {
            Account.TestHttpClientFactory = null;
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
        }

        private static Account NewAccount() => new Account { Username = "Tester", UserID = 42, SecurityToken = FullToken, Valid = true, LastUse = DateTime.Now };

        [Fact]
        public async Task Validate_UsersOk_AndLiveStoreOk_IsValid()
        {
            var handler = new FakeHandler(req => req.RequestUri.Host == "users.roblox.com"
                ? Json(HttpStatusCode.OK, "{\"id\":42,\"name\":\"Tester\"}")
                : Json(HttpStatusCode.OK));
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            var acc = NewAccount();
            Assert.True(await acc.ValidateSessionAsync());
            Assert.True(acc.Valid);
        }

        [Fact]
        public async Task Validate_UsersOk_LiveStoreUnauthorized_IsExpired()
        {
            var handler = new FakeHandler(req => req.RequestUri.Host == "users.roblox.com"
                ? Json(HttpStatusCode.OK, "{\"id\":42,\"name\":\"Tester\"}")
                : Json(HttpStatusCode.Unauthorized));
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            var acc = NewAccount();
            Assert.False(await acc.ValidateSessionAsync());
            Assert.False(acc.Valid);
        }

        [Fact]
        public async Task Validate_Users401_IsExpired()
        {
            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.Unauthorized)));

            var acc = NewAccount();
            Assert.False(await acc.ValidateSessionAsync());
            Assert.False(acc.Valid);
        }

        [Fact]
        public async Task Validate_UserIdMismatch_IsExpired()
        {
            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHandler(_ =>
                Json(HttpStatusCode.OK, "{\"id\":999,\"name\":\"Other\"}")));

            var acc = NewAccount();
            Assert.False(await acc.ValidateSessionAsync());
            Assert.False(acc.Valid);
        }

        [Fact]
        public async Task Validate_ServerError_PreservesExistingValidity()
        {
            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.ServiceUnavailable)));

            var acc = NewAccount();
            Assert.True(await acc.ValidateSessionAsync());
            Assert.True(acc.Valid);
        }

        [Fact]
        public async Task Validate_OldShortLivedCookie_StillValidatedAgainstServer()
        {
            var handler = new FakeHandler(_ => Json(HttpStatusCode.OK));
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            string shortToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H." + new string('A', 790) + ".S";
            var acc = new Account { Username = "Old", UserID = 1, SecurityToken = shortToken, Valid = true, LastUse = DateTime.Now.AddHours(-2) };

            await acc.ValidateSessionAsync();
            Assert.True(handler.Calls > 0);
        }
    }
}
