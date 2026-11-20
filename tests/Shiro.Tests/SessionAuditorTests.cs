#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class SessionAuditorTests : IDisposable
    {
        private const string TestToken =
            "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_TESTTOKEN123";

        public SessionAuditorTests()
        {
            SessionAuditorService.TestHttpClientFactory = null;
        }

        public void Dispose()
        {
            SessionAuditorService.TestHttpClientFactory = null;
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
            var handler = new FakeHttpHandler(req =>
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
            var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
            SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(handler);

            var acc = new Account { Username = "ExpiredUser", SecurityToken = TestToken, Valid = true };
            var result = await SessionAuditorService.AuditAccountAsync(acc);

            Assert.Equal(AccountSessionStatus.ExpiredCookie, result.Status);
            Assert.False(acc.Valid);
        }

        [Fact]
        public async Task AuditAccountAsync_WhenRedirectedToModeration_EnqueuesModerationAlertAndReturnsModerated()
        {
            WebhookAlert? capturedAlert = null;
            Action<WebhookAlert> handler = a => capturedAlert = a;
            WebhookNotificationService.AlertDispatched += handler;

            try
            {
                var auditHandler = new FakeHttpHandler(req =>
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.Redirect);
                    resp.Headers.Location = new Uri("https://www.roblox.com/not-approved");
                    return resp;
                });
                SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(auditHandler);

                var acc = new Account { Username = "ModUser", SecurityToken = TestToken, Valid = true };
                var result = await SessionAuditorService.AuditAccountAsync(acc);

                Assert.Equal(AccountSessionStatus.Moderated, result.Status);
                Assert.False(acc.Valid);
                Assert.NotNull(capturedAlert);
                Assert.Equal(WebhookEventType.ModerationWarning, capturedAlert.EventType);
                Assert.Equal("ModUser", capturedAlert.AccountUsername);
                Assert.Contains("moderation notice", capturedAlert.Description);
            }
            finally
            {
                WebhookNotificationService.AlertDispatched -= handler;
            }
        }

        [Fact]
        public async Task AuditAllAccountsAsync_ReportsProgress_AndAuditsAllAccounts()
        {
            var handler = new FakeHttpHandler(req =>
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
                new Account { Username = "User1", SecurityToken = TestToken, UserID = 999 },
                new Account { Username = "User2", SecurityToken = TestToken, UserID = 999 },
                new Account { Username = "User3", SecurityToken = TestToken, UserID = 999 },
                new Account { Username = "User4", SecurityToken = TestToken, UserID = 999 }
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

        [Fact]
        public async Task AuditAllAccountsAsync_DeduplicatesInputAccounts()
        {
            var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":100,\"name\":\"DedupUser\"}")
            });
            SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(handler);

            var acc = new Account { Username = "DedupUser", SecurityToken = TestToken, UserID = 100 };
            var duplicates = new List<Account> { acc, acc, acc };

            var results = await SessionAuditorService.AuditAllAccountsAsync(duplicates);
            Assert.Single(results);
            Assert.True(results.ContainsKey(acc));
        }

        private const string AuditorValidUserJson = "{\"id\":5150,\"name\":\"AuditorUser\"}";
        private const string AuditorRobuxJson = "{\"robux\":250}";
        private const string AuditorPendingJson = "{\"pendingRobuxTotal\":40}";
        private const string AuditorEmptyJson = "{}";

        [Fact]
        public async Task AuditAccountAsync_SerializesWithAccountSessionGate_AcrossConcurrentValidateAndAudit()
        {
            var prevAccFactory = RobloxAccountSessionClient.TestHttpClientFactory;
            var validateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseValidate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int auditProbeCalls = 0;

            try
            {
                RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(async (req, ct) =>
                {
                    if (req.RequestUri!.AbsoluteUri.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                    {
                        validateEntered.TrySetResult();
                        await releaseValidate.Task.WaitAsync(ct);
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AuditorValidUserJson) };
                    }
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AuditorEmptyJson) };
                }));

                SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(req =>
                {
                    if (req.RequestUri!.AbsoluteUri.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref auditProbeCalls);
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AuditorValidUserJson) };
                    }
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AuditorEmptyJson) };
                }));

                var acc = new Account { Username = "AuditorUser", UserID = 5150, SecurityToken = TestToken, Valid = true };
                Task<bool> validateTask = acc.ValidateSessionAsync();
                await validateEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

                Task<AccountSessionAuditResult> auditTask = SessionAuditorService.AuditAccountAsync(acc);
                await Task.Delay(80);
                Assert.Equal(0, Volatile.Read(ref auditProbeCalls));

                releaseValidate.TrySetResult();
                Assert.True(await validateTask);
                AccountSessionAuditResult auditRes = await auditTask;
                Assert.Equal(AccountSessionStatus.Valid, auditRes.Status);
                Assert.Equal(1, auditProbeCalls);
            }
            finally
            {
                releaseValidate.TrySetResult();
                RobloxAccountSessionClient.TestHttpClientFactory = prevAccFactory;
                RobloxRateLimitCoordinator.Instance.ResetBackoff();
            }
        }

        [Fact]
        public async Task AuditAccountAsync_On429_NotifiesRateLimitCoordinatorScopedByProxy_AndEnforcesBackoff()
        {
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
            var prevSettings = ProxyBindingService.Settings;
            var proxySettings = Substitute.For<ISettingsService>();
            proxySettings.Exists(AppSettingsKeys.General.UseProxies).Returns(true);
            proxySettings.Get<bool>(AppSettingsKeys.General.UseProxies, false).Returns(true);
            ProxyBindingService.Settings = proxySettings;
            try
            {
                int probeCalls = 0;
                SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(req =>
                {
                    if (req.RequestUri!.AbsoluteUri.Contains("users/authenticated", StringComparison.OrdinalIgnoreCase))
                    {
                        int callNum = Interlocked.Increment(ref probeCalls);
                        if (callNum == 1)
                        {
                            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                            resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(4));
                            return resp;
                        }
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AuditorValidUserJson) };
                    }
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AuditorEmptyJson) };
                }));

                var accProxy1 = new Account
                {
                    Username = "AuditorUser",
                    UserID = 5150,
                    SecurityToken = TestToken,
                    Proxy = "http://10.0.0.1:8080",
                    Valid = true
                };
                var accProxy2 = new Account
                {
                    Username = "AuditorUser",
                    UserID = 5150,
                    SecurityToken = TestToken,
                    Proxy = "http://10.0.0.2:8080",
                    Valid = true
                };

                var first = await SessionAuditorService.AuditAccountAsync(accProxy1);
                Assert.Equal(AccountSessionStatus.RateLimited, first.Status);
                Assert.True(accProxy1.Valid);
                Assert.True(RobloxRateLimitCoordinator.Instance.IsBackingOffCategory(RateLimitCategory.SessionCheck, "10.0.0.1:8080"));
                Assert.False(RobloxRateLimitCoordinator.Instance.IsBackingOffCategory(RateLimitCategory.SessionCheck));

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => SessionAuditorService.AuditAccountAsync(accProxy1, cts.Token));
                Assert.Equal(1, probeCalls);

                var otherRes = await SessionAuditorService.AuditAccountAsync(accProxy2);
                Assert.Equal(AccountSessionStatus.Valid, otherRes.Status);
                Assert.Equal(2, probeCalls);
            }
            finally
            {
                ProxyBindingService.Settings = prevSettings;
                RobloxRateLimitCoordinator.Instance.ResetBackoff();
            }
        }

        [Fact]
        public async Task AuditAccountAsync_WhenValidityOrRobuxChanges_MarksDirtyAndQueuesDeferredSave()
        {
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
            var repo = Substitute.For<IAccountRepository>();
            try
            {
                SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(req =>
                {
                    string url = req.RequestUri!.AbsoluteUri;
                    if (url.Contains("users/authenticated", StringComparison.OrdinalIgnoreCase))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AuditorValidUserJson) };
                    if (url.Contains("/currency", StringComparison.OrdinalIgnoreCase))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AuditorRobuxJson) };
                    if (url.Contains("/transaction-totals", StringComparison.OrdinalIgnoreCase))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AuditorPendingJson) };
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }));

                using var _ = AppServices.Override<IAccountRepository>(repo);
                var acc = new Account
                {
                    Username = "AuditorUser",
                    UserID = 5150,
                    SecurityToken = TestToken,
                    Valid = false,
                    RobuxBalance = 0,
                    PendingRobux = 0,
                    IsDirty = false
                };

                var first = await SessionAuditorService.AuditAccountAsync(acc);
                Assert.Equal(AccountSessionStatus.Valid, first.Status);
                Assert.True(acc.IsDirty);
                repo.Received(1).QueueDeferredSave();

                acc.IsDirty = false;
                repo.ClearReceivedCalls();

                var second = await SessionAuditorService.AuditAccountAsync(acc);
                Assert.Equal(AccountSessionStatus.Valid, second.Status);
                Assert.False(acc.IsDirty);
                repo.DidNotReceive().QueueDeferredSave();
            }
            finally
            {
                RobloxRateLimitCoordinator.Instance.ResetBackoff();
            }
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.Forbidden)]
        public async Task AuditAccountAsync_On401Or403ModerationProbe_DispatchesAlertOutsideSessionGate(HttpStatusCode initialStatus)
        {
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
            string moderationJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["punishedUserId"] = 7701,
                ["punishmentType"] = "Banned for 7 Days",
                ["messageToUser"] = "Violation"
            });

            var acc = new Account
            {
                Username = "GateCheckUser",
                UserID = 7701,
                SecurityToken = TestToken,
                Valid = true
            };

            int alertCount = 0;
            bool gateAcquiredInAlert = false;
            bool observedValidInAlert = true;
            string observedIssueInAlert = string.Empty;
            WebhookAlert? capturedAlert = null;

            void OnAlert(WebhookAlert alert)
            {
                if (!string.Equals(alert.AccountUsername, acc.Username, StringComparison.Ordinal))
                    return;

                alertCount++;
                capturedAlert = alert;
                using var gateCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                Task<bool> gateTask = Task.Run(async () =>
                {
                    try
                    {
                        return await acc.RunWithSessionGateAsync(() =>
                        {
                            observedValidInAlert = acc.Valid;
                            observedIssueInAlert = acc.AccountStatusIssue;
                            return Task.FromResult(true);
                        }, gateCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                });
                gateAcquiredInAlert = gateTask.Wait(TimeSpan.FromSeconds(2)) && gateTask.Result;
            }

            WebhookNotificationService.AlertDispatched += OnAlert;
            try
            {
                var handler = new FakeHttpHandler(req =>
                {
                    string url = req.RequestUri!.AbsoluteUri;
                    if (url.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                        return new HttpResponseMessage(initialStatus);
                    if (url.Contains("friends.roblox.com/v1/my/friends/count", StringComparison.OrdinalIgnoreCase))
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    if (url.Contains("usermoderation.roblox.com/v1/not-approved", StringComparison.OrdinalIgnoreCase))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(moderationJson) };
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                });

                SessionAuditorService.TestHttpClientFactory = _ => new HttpClient(handler);

                AccountSessionAuditResult result = await SessionAuditorService.AuditAccountAsync(acc);

                Assert.Equal(AccountSessionStatus.Moderated, result.Status);
                Assert.False(acc.Valid);
                Assert.Equal(1, alertCount);
                Assert.NotNull(capturedAlert);
                Assert.Equal(WebhookEventType.ModerationWarning, capturedAlert!.EventType);
                Assert.True(gateAcquiredInAlert, "Webhook AlertDispatched fired while account._sessionGate was still held.");
                Assert.False(observedValidInAlert);
                Assert.False(string.IsNullOrWhiteSpace(observedIssueInAlert));
            }
            finally
            {
                WebhookNotificationService.AlertDispatched -= OnAlert;
                RobloxRateLimitCoordinator.Instance.ResetBackoff();
            }
        }
    }
}
