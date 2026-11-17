#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public sealed class AuditSessionAuditorRegressionTests : IDisposable
    {
        private const string TestCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--" + "AUDIT_TEST_TOKEN_12345";

        public void Dispose()
        {
            SessionAuditorService.TestHttpClientFactory = null;
            Account.TestHttpClientFactory = null;
        }

        [Fact]
        public async Task Bug11_SessionAuditor_WhenPrimary401AndSecondary200_PreservesValidSession()
        {
            var handler = new FakeHttpHandler(req =>
            {
                string url = req.RequestUri!.AbsoluteUri;
                if (url.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);

                if (url.Contains("friends.roblox.com/v1/my/friends/count", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"count\":4}") };

                if (url.Contains("economy.roblox.com/v1/users/", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"robux\":250}") };

                if (url.Contains("transaction-totals", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"pendingRobuxTotal\":10}") };

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

            SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(handler);

            var acc = new Account { Username = "Transient401User", UserID = 9001, SecurityToken = TestCookie, Valid = true };
            var result = await SessionAuditorService.AuditAccountAsync(acc);

            Assert.Equal(AccountSessionStatus.Valid, result.Status);
            Assert.True(acc.Valid);
            Assert.Equal(250, result.RobuxBalance);
            Assert.Equal(10, result.PendingRobux);
        }

        [Fact]
        public async Task Bug11_SessionAuditor_WhenPrimary401AndModerationDetected_ReturnsModeratedAndSetsBadge()
        {
            WebhookAlert? capturedAlert = null;
            void OnAlert(WebhookAlert a) => capturedAlert = a;
            WebhookNotificationService.AlertDispatched += OnAlert;

            try
            {
                var handler = new FakeHttpHandler(req =>
                {
                    string url = req.RequestUri!.AbsoluteUri;
                    if (url.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized);

                    if (url.Contains("friends.roblox.com/v1/my/friends/count", StringComparison.OrdinalIgnoreCase))
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized);

                    if (url.Contains("usermoderation.roblox.com/v1/not-approved", StringComparison.OrdinalIgnoreCase))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"punishedUserId\":9002,\"punishmentType\":\"Banned for 7 Days\",\"messageToUser\":\"Violation\"}")
                        };
                    }

                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                });

                SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(handler);

                var acc = new Account { Username = "Mod401User", UserID = 9002, SecurityToken = TestCookie, Valid = true };
                var result = await SessionAuditorService.AuditAccountAsync(acc);

                Assert.Equal(AccountSessionStatus.Moderated, result.Status);
                Assert.False(acc.Valid);
                Assert.False(string.IsNullOrWhiteSpace(acc.AccountStatusIssue));
                Assert.NotNull(capturedAlert);
                Assert.Equal(WebhookEventType.ModerationWarning, capturedAlert!.EventType);
            }
            finally
            {
                WebhookNotificationService.AlertDispatched -= OnAlert;
            }
        }
    }
}
