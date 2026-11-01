#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shiro;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class SessionAuditorTests : IDisposable
    {
        private const string TestToken =
            "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_TESTTOKEN123";

        public SessionAuditorTests()
        {
            SessionAuditorService.TestHttpClientFactory = null;
            Account.TestHttpClientFactory = null;
        }

        public void Dispose()
        {
            SessionAuditorService.TestHttpClientFactory = null;
            Account.TestHttpClientFactory = null;
        }

        [Fact]
        public void EvaluateAuditStatus_WhenHttpResponseIs401_ReturnsExpiredCookie()
        {
            var status = SessionAuditorService.MapStatusCodeToSessionStatus(HttpStatusCode.Unauthorized);
            Assert.Equal(AccountSessionStatus.ExpiredCookie, status);
        }

        [Fact]
        public void EvaluateAuditStatus_WhenHttpResponseIs403_ReturnsTwoStepOrBlocked()
        {
            var status = SessionAuditorService.MapStatusCodeToSessionStatus(HttpStatusCode.Forbidden);
            Assert.Equal(AccountSessionStatus.VerificationRequired, status);
        }

        [Fact]
        public void EvaluateAuditStatus_WhenHttpResponseIs200_ReturnsValid()
        {
            var status = SessionAuditorService.MapStatusCodeToSessionStatus(HttpStatusCode.OK);
            Assert.Equal(AccountSessionStatus.Valid, status);
        }

        [Fact]
        public void EvaluateAuditStatus_WhenHttpResponseIs429_ReturnsRateLimited()
        {
            var status = SessionAuditorService.MapStatusCodeToSessionStatus((HttpStatusCode)429);
            Assert.Equal(AccountSessionStatus.RateLimited, status);
        }

        [Fact]
        public void EvaluateAuditStatus_WhenHttpResponseIsUnknown_ReturnsUnknown()
        {
            var status = SessionAuditorService.MapStatusCodeToSessionStatus(HttpStatusCode.InternalServerError);
            Assert.Equal(AccountSessionStatus.Unknown, status);
        }

        [Fact]
        public async Task AuditAccountAsync_WhenAccountNullOrNoToken_ReturnsExpiredOrUnknown()
        {
            var nullResult = await SessionAuditorService.AuditAccountAsync(null!);
            Assert.Equal(AccountSessionStatus.Unknown, nullResult.Status);

            var noTokenAcc = new Account { Username = "NoToken", SecurityToken = "" };
            var noTokenResult = await SessionAuditorService.AuditAccountAsync(noTokenAcc);
            Assert.Equal(AccountSessionStatus.ExpiredCookie, noTokenResult.Status);
        }

        [Fact]
        public async Task AuditAccountAsync_WhenValidSession_FetchesRobuxAndUpdatesAccount()
        {
            var handler = new FakeAuditHandler(req =>
            {
                if (req.RequestUri != null && req.RequestUri.Host == "users.roblox.com")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":12345,\"name\":\"TestAuditor\"}")
                    };
                }
                if (req.RequestUri != null && req.RequestUri.Host == "economy.roblox.com")
                {
                    if (req.RequestUri.AbsolutePath.Contains("/currency"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"robux\":450}")
                        };
                    }
                    if (req.RequestUri.AbsolutePath.Contains("/transaction-totals"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"pendingRobuxTotal\":75}")
                        };
                    }
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(handler);

            var acc = new Account { Username = "TestAuditor", SecurityToken = TestToken, UserID = 12345, Valid = false };
            var result = await SessionAuditorService.AuditAccountAsync(acc);

            Assert.Equal(AccountSessionStatus.Valid, result.Status);
            Assert.Equal(450, result.RobuxBalance);
            Assert.Equal(75, result.PendingRobux);
            Assert.True(acc.Valid);
            Assert.Equal(450, acc.RobuxBalance);
            Assert.Equal(75, acc.PendingRobux);
        }

        [Fact]
        public async Task AuditAccountAsync_WhenUnauthorized_MarksAccountInvalid()
        {
            var handler = new FakeAuditHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
            SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(handler);

            var acc = new Account { Username = "ExpiredUser", SecurityToken = TestToken, Valid = true };
            var result = await SessionAuditorService.AuditAccountAsync(acc);

            Assert.Equal(AccountSessionStatus.ExpiredCookie, result.Status);
            Assert.False(acc.Valid);
        }

        [Fact]
        public async Task AuditAllAccountsAsync_ReportsProgress_AndAuditsAllAccounts()
        {
            var handler = new FakeAuditHandler(req =>
            {
                if (req.RequestUri != null && req.RequestUri.Host == "users.roblox.com")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":999,\"name\":\"AuditedUser\"}")
                    };
                }
                if (req.RequestUri != null && req.RequestUri.AbsolutePath.Contains("/currency"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"robux\":100}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

            SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(handler);

            var accounts = new List<Account>
            {
                new Account { Username = "User1", SecurityToken = TestToken, UserID = 1 },
                new Account { Username = "User2", SecurityToken = TestToken, UserID = 2 },
                new Account { Username = "User3", SecurityToken = TestToken, UserID = 3 },
                new Account { Username = "User4", SecurityToken = TestToken, UserID = 4 }
            };

            var reportedProgress = new List<AuditProgress>();
            var progress = new Progress<AuditProgress>(p =>
            {
                lock (reportedProgress)
                {
                    reportedProgress.Add(p);
                }
            });

            var results = await SessionAuditorService.AuditAllAccountsAsync(accounts, progress);

            Assert.Equal(4, results.Count);
            foreach (var acc in accounts)
            {
                Assert.True(results.ContainsKey(acc));
                Assert.Equal(AccountSessionStatus.Valid, results[acc].Status);
            }
        }

        private sealed class FakeAuditHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
            public FakeAuditHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(_responder(request));
        }
    }
}
