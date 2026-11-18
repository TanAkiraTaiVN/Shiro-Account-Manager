#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public sealed class P0AuthNetworkAndModerationTests : IDisposable
    {
        private const string ValidToken =
            "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_" +
            "AUDIT_TEST_TOKEN_PAYLOAD_1234567890";

        private const string WrongUserAuthJson = "{\"id\":999,\"name\":\"DifferentUser\"}";
        private const string MatchedUserAuthJson = "{\"id\":123,\"name\":\"ExpectedUser\"}";
        private const string CurrencyJson = "{\"robux\":500}";
        private const string EmptyJsonObject = "{}";
        private const string ErrorJsonPayload = "{\"errors\":[{\"code\":0,\"message\":\"Unknown error\"}]}";
        private const string CaptivePortalHtml = "<!DOCTYPE html><html><head><title>Wi-Fi</title></head><body>Captive Portal</body></html>";
        private const string LoginChallengeHtml = "<!DOCTYPE html><html><body><div class=\"login-base-container\"><input id=\"login-username\" /></div></body></html>";

        private readonly Func<Account, HttpClient>? _previousAuditorClientFactory;
        private readonly Func<Account, HttpClient>? _previousAccountClientFactory;

        public P0AuthNetworkAndModerationTests()
        {
            _previousAuditorClientFactory = SessionAuditorService.TestHttpClientFactory;
            _previousAccountClientFactory = Account.TestHttpClientFactory;
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
        }

        public void Dispose()
        {
            SessionAuditorService.TestHttpClientFactory = _previousAuditorClientFactory;
            Account.TestHttpClientFactory = _previousAccountClientFactory;
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
        }

        [Fact]
        public async Task SessionAuditor_WhenAuthenticatedUserIdMismatches_MarksInvalidAndNotValid()
        {
            int economyCalls = 0;
            var handler = new FakeHttpHandler(req =>
            {
                string url = req.RequestUri!.AbsoluteUri;
                if (url.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(WrongUserAuthJson) };

                if (url.Contains("economy.roblox.com", StringComparison.OrdinalIgnoreCase))
                    Interlocked.Increment(ref economyCalls);

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(handler);

            var account = new Account
            {
                Username = "ExpectedUser",
                UserID = 123,
                SecurityToken = ValidToken,
                Valid = true
            };

            AccountSessionAuditResult result = await SessionAuditorService.AuditAccountAsync(account);

            Assert.Equal(AccountSessionStatus.ExpiredCookie, result.Status);
            Assert.False(account.Valid);
            Assert.Equal(123, account.UserID);
            Assert.Equal("ExpectedUser", account.Username);
            Assert.Equal(0, economyCalls);
        }

        [Fact]
        public async Task SessionAuditor_WhenAuthenticatedReturnsHtml200_RejectsInsteadOfValid()
        {
            var captiveHandler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CaptivePortalHtml)
            });

            SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(captiveHandler);

            var captiveAccount = new Account
            {
                Username = "CaptiveUser",
                UserID = 123,
                SecurityToken = ValidToken,
                Valid = true
            };

            AccountSessionAuditResult captiveResult = await SessionAuditorService.AuditAccountAsync(captiveAccount);

            Assert.Equal(AccountSessionStatus.Unknown, captiveResult.Status);
            Assert.True(captiveAccount.Valid);

            var loginHandler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(LoginChallengeHtml)
            });

            SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(loginHandler);

            var loginAccount = new Account
            {
                Username = "LoginHtmlUser",
                UserID = 123,
                SecurityToken = ValidToken,
                Valid = true
            };

            AccountSessionAuditResult loginResult = await SessionAuditorService.AuditAccountAsync(loginAccount);

            Assert.Equal(AccountSessionStatus.ExpiredCookie, loginResult.Status);
            Assert.False(loginAccount.Valid);
        }

        [Fact]
        public async Task SessionAuditor_WhenFriendsFallbackReturnsEmptyOrErrorJsonWithoutCount_RejectsAsExpired()
        {
            string activePayload = EmptyJsonObject;
            var handler = new FakeHttpHandler(req =>
            {
                string url = req.RequestUri!.AbsoluteUri;
                if (url.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);

                if (url.Contains("friends.roblox.com/v1/my/friends/count", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(activePayload) };

                if (url.Contains("www.roblox.com/home", StringComparison.OrdinalIgnoreCase))
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri("https://www.roblox.com/login");
                    return redirect;
                }

                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            });

            SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(handler);

            var account1 = new Account
            {
                Username = "EmptyFriendsUser",
                UserID = 123,
                SecurityToken = ValidToken,
                Valid = true
            };

            AccountSessionAuditResult res1 = await SessionAuditorService.AuditAccountAsync(account1);

            Assert.Equal(AccountSessionStatus.ExpiredCookie, res1.Status);
            Assert.False(account1.Valid);

            activePayload = ErrorJsonPayload;
            var account2 = new Account
            {
                Username = "ErrorFriendsUser",
                UserID = 123,
                SecurityToken = ValidToken,
                Valid = true
            };

            AccountSessionAuditResult res2 = await SessionAuditorService.AuditAccountAsync(account2);

            Assert.Equal(AccountSessionStatus.ExpiredCookie, res2.Status);
            Assert.False(account2.Valid);
        }

        [Fact]
        public async Task SessionAuditor_WhenCallerCancellationRequested_RethrowsOperationCanceledException()
        {
            using var secondaryCts = new CancellationTokenSource();
            var secondaryHandler = new FakeHttpHandler(req =>
            {
                string url = req.RequestUri!.AbsoluteUri;
                if (url.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);

                if (url.Contains("friends.roblox.com/v1/my/friends/count", StringComparison.OrdinalIgnoreCase))
                {
                    secondaryCts.Cancel();
                    throw new OperationCanceledException(secondaryCts.Token);
                }

                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            });

            SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(secondaryHandler);

            var account1 = new Account
            {
                Username = "CancelSecondaryUser",
                UserID = 123,
                SecurityToken = ValidToken,
                Valid = true
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => SessionAuditorService.AuditAccountAsync(account1, secondaryCts.Token));

            using var economyCts = new CancellationTokenSource();
            var economyHandler = new FakeHttpHandler(req =>
            {
                string url = req.RequestUri!.AbsoluteUri;
                if (url.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MatchedUserAuthJson) };

                if (url.Contains("/currency", StringComparison.OrdinalIgnoreCase))
                {
                    economyCts.Cancel();
                    throw new OperationCanceledException(economyCts.Token);
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CurrencyJson) };
            });

            SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(economyHandler);

            var account2 = new Account
            {
                Username = "CancelEconomyUser",
                UserID = 123,
                SecurityToken = ValidToken,
                Valid = true
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => SessionAuditorService.AuditAccountAsync(account2, economyCts.Token));
        }
    }
}
