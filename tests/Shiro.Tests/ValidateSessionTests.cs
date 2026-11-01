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
        public async Task Validate_UsersOk_LiveStore401_ButFriendsOk_IsValid()
        {
            var handler = new FakeHandler(req => req.RequestUri.Host switch
            {
                "users.roblox.com" => Json(HttpStatusCode.OK, "{\"id\":42,\"name\":\"Tester\"}"),
                "friends.roblox.com" => Json(HttpStatusCode.OK, "{\"count\":3}"),
                "accountsettings.roblox.com" => Json(HttpStatusCode.Unauthorized),
                _ => Json(HttpStatusCode.OK)
            });
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            var acc = NewAccount();
            Assert.True(await acc.ValidateSessionAsync());
            Assert.True(acc.Valid);
        }

        [Fact]
        public async Task Validate_Users401_ButFriendsOk_IsValid()
        {
            var handler = new FakeHandler(req => req.RequestUri.Host == "friends.roblox.com"
                ? Json(HttpStatusCode.OK, "{\"count\":3}")
                : Json(HttpStatusCode.Unauthorized));
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            var acc = NewAccount();
            Assert.True(await acc.ValidateSessionAsync());
            Assert.True(acc.Valid);
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

        [Fact]
        public void ParseModerationText_NormalRobloxPage_NotBanned()
        {
            string normalHtml = "<!DOCTYPE html><html><head><title>Roblox</title></head><body><div id=\"content\">Welcome</div></body></html>";
            var (isBanned, badge, reason) = Account.ParseModerationText(normalHtml, "https://www.roblox.com/not-approved");
            Assert.False(isBanned);
            Assert.Null(badge);
        }

        [Fact]
        public void ParseModerationText_GenuineBanPage_DetectsBan()
        {
            string banHtml = "<div id=\"not-approved-page\"><h2>Reviewed: 10/28/2026</h2><p>Reason: Inappropriate language</p><p>Banned for 1 day</p></div>";
            var (isBanned, badge, reason) = Account.ParseModerationText(banHtml, "https://www.roblox.com/not-approved");
            Assert.True(isBanned);
            Assert.Contains("1 ngày", badge);
        }

        [Fact]
        public void ParseModerationText_WarningNotice_DetectsWarning()
        {
            string warnHtml = "<div class=\"not-approved-page\"><h2>Warning</h2><p>Please review community guidelines</p><button>I Agree</button></div>";
            var (isBanned, badge, reason) = Account.ParseModerationText(warnHtml, "https://www.roblox.com/not-approved");
            Assert.True(isBanned);
            Assert.Contains("Cảnh báo", badge);
        }

        [Fact]
        public async Task LogOutOfOtherSessionsAsync_WhenCookieRotatedInExecuteAccountRestAsync_ReturnsTrue()
        {
            string rotatedToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H." + new string('R', 939) + ".S";
            var handler = new FakeHandler(req =>
            {
                var resp = Json(HttpStatusCode.OK, "{}");
                resp.Headers.TryAddWithoutValidation("x-csrf-token", "csrf-123");
                if (req.RequestUri!.AbsolutePath.Contains("logoutfromallsessionsandreauthenticate", StringComparison.OrdinalIgnoreCase))
                {
                    resp.Headers.TryAddWithoutValidation("Set-Cookie", $".ROBLOSECURITY={rotatedToken}; Path=/; Domain=.roblox.com; HttpOnly");
                }
                return resp;
            });
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            var acc = NewAccount();
            acc.CSRFToken = "csrf-123";
            acc.TokenSet = DateTime.Now;
            acc.PinUnlocked = DateTime.Now.AddMinutes(5);

            bool ok = await acc.LogOutOfOtherSessionsAsync(Internal: true);
            Assert.True(ok);
            Assert.Equal(rotatedToken, acc.SecurityToken);
        }

        [Fact]
        public async Task Validate_WhenUsersEndpointRotatesCookie_SubsequentProbesUseRotatedCookie()
        {
            string rotatedToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H." + new string('N', 939) + ".S";
            var handler = new FakeHandler(req =>
            {
                if (req.RequestUri!.Host == "users.roblox.com")
                {
                    var resp = Json(HttpStatusCode.OK, "{\"id\":42,\"name\":\"Tester\"}");
                    resp.Headers.TryAddWithoutValidation("Set-Cookie", $".ROBLOSECURITY={rotatedToken}; Path=/; Domain=.roblox.com; HttpOnly");
                    return resp;
                }

                string cookieHeader = string.Join(";", req.Headers.TryGetValues("Cookie", out var vals) ? vals : Array.Empty<string>());
                if (req.RequestUri.Host == "accountsettings.roblox.com")
                {
                    // Reject old token, accept rotated token
                    return cookieHeader.Contains(rotatedToken, StringComparison.Ordinal)
                        ? Json(HttpStatusCode.OK, "{\"emailAddress\":\"a@b.c\"}")
                        : Json(HttpStatusCode.Unauthorized);
                }

                return Json(HttpStatusCode.OK);
            });
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            var acc = NewAccount();
            Assert.True(await acc.ValidateSessionAsync());
            Assert.True(acc.Valid);
            Assert.Equal(rotatedToken, acc.SecurityToken);
        }
    }
}
