#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests.Network
{
    [Collection("StaticSeams")]
    public sealed class RobloxModerationServiceBehaviorTests : IDisposable
    {
        public RobloxModerationServiceBehaviorTests()
        {
            RobloxAccountSessionClient.TestHttpClientFactory = null;
        }

        public void Dispose()
        {
            RobloxAccountSessionClient.TestHttpClientFactory = null;
        }

        [Fact]
        public void ParseModerationText_CoversUrlFallbacksAndDurations()
        {
            // Empty content + security notification URL
            var sec = RobloxModerationService.ParseModerationText("", "https://www.roblox.com/security-notification");
            Assert.True(sec.IsBanned);
            Assert.Equal(LocalizationService.Get("FastLogin.SecurityLockoutShort"), sec.IssueBadge);

            // Empty content + not-approved URL
            var modUrl = RobloxModerationService.ParseModerationText("", "https://www.roblox.com/not-approved");
            Assert.True(modUrl.IsBanned);
            Assert.Equal(LocalizationService.Get("FastLogin.LockedOrBannedBadge"), modUrl.IssueBadge);

            // Empty content + normal URL
            var normalUrl = RobloxModerationService.ParseModerationText("", "https://www.roblox.com/home");
            Assert.False(normalUrl.IsBanned);

            // Unauthenticated moderation response
            Assert.False(RobloxModerationService.ParseModerationText("{\"errors\":[{\"code\":9002}]}").IsBanned);

            // Durations & keywords
            Assert.Equal(
                LocalizationService.Get("FastLogin.BanSevenDaysShort"),
                RobloxModerationService.ParseModerationText("Reviewed: today. Ban 7-day violation").IssueBadge);
            Assert.Equal(
                LocalizationService.Get("FastLogin.BanThreeDaysShort"),
                RobloxModerationService.ParseModerationText("Reviewed: today. Ban 3-day violation").IssueBadge);
            Assert.Equal(
                LocalizationService.Get("FastLogin.BanOneDayShort"),
                RobloxModerationService.ParseModerationText("Reviewed: today. Ban 1-day violation").IssueBadge);
            Assert.Equal(
                LocalizationService.Get("FastLogin.BanFourteenDaysShort"),
                RobloxModerationService.ParseModerationText("Reviewed: today. Ban 14-day violation").IssueBadge);
            Assert.Equal(
                LocalizationService.Get("FastLogin.BanPermanentShort"),
                RobloxModerationService.ParseModerationText("Reviewed: today. Account terminated").IssueBadge);
            Assert.Equal(
                LocalizationService.Get("Msg_WarningTitle"),
                RobloxModerationService.ParseModerationText("Reviewed: today. Warning issued").IssueBadge);
            Assert.Equal(
                LocalizationService.Get("FastLogin.SecurityLockoutShort"),
                RobloxModerationService.ParseModerationText("Reviewed: today. Please reset your password").IssueBadge);
            Assert.Equal(
                LocalizationService.Get("FastLogin.LockedOrBannedBadge"),
                RobloxModerationService.ParseModerationText("Moderator note: generic violation").IssueBadge);
        }

        [Fact]
        public async Task CheckModerationStatusAsync_HandlesRedirectsAndUnauthorized()
        {
            var svc = RobloxModerationService.Default;
            var nullAcc = await svc.CheckModerationStatusAsync(null!, "tok", CancellationToken.None);
            Assert.False(nullAcc.IsBanned);

            var acc = new Account { Username = "ModTestUser", SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--ModCookie1234" };

            // 1. Redirect to external non-roblox host -> false
            var extHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, "{}")
                .Enqueue(HttpStatusCode.Found, "", ("Location", "https://example.com/other"));
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(extHandler);
            Assert.False((await svc.CheckModerationStatusAsync(acc, acc.SecurityToken, CancellationToken.None)).IsBanned);

            // 2. Redirect to /discover -> false
            var discHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, "{}")
                .Enqueue(HttpStatusCode.Found, "", ("Location", "https://www.roblox.com/discover"));
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(discHandler);
            Assert.False((await svc.CheckModerationStatusAsync(acc, acc.SecurityToken, CancellationToken.None)).IsBanned);

            // 3. Redirect to /not-approved, second request returns 401 -> false & clears AccountStatusIssue
            acc.AccountStatusIssue = "OldIssue";
            var unauthHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, "{}")
                .Enqueue(HttpStatusCode.Found, "", ("Location", "/not-approved"))
                .Enqueue(HttpStatusCode.Unauthorized, "");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(unauthHandler);
            Assert.False((await svc.CheckModerationStatusAsync(acc, acc.SecurityToken, CancellationToken.None)).IsBanned);
            Assert.Equal(string.Empty, acc.AccountStatusIssue);

            // 4. Redirect to /not-approved, second request redirects to /login -> false
            var loginRedirHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, "{}")
                .Enqueue(HttpStatusCode.Found, "", ("Location", "/not-approved"))
                .Enqueue(HttpStatusCode.Found, "", ("Location", "/NewLogin"));
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(loginRedirHandler);
            Assert.False((await svc.CheckModerationStatusAsync(acc, acc.SecurityToken, CancellationToken.None)).IsBanned);

            // 5. Redirect to /not-approved, second request returns login HTML -> false
            var loginHtmlHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, "{}")
                .Enqueue(HttpStatusCode.Found, "", ("Location", "/not-approved"))
                .Enqueue(HttpStatusCode.OK, "<input id=\"login-username\" />");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(loginHtmlHandler);
            Assert.False((await svc.CheckModerationStatusAsync(acc, acc.SecurityToken, CancellationToken.None)).IsBanned);

            // 6. usermoderation.roblox.com/v1/not-approved returns structured 7-day ban JSON -> detects ban directly without www.roblox.com/home
            var apiBanHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, "{\"punishedUserId\":10911124164,\"interventionId\":\"f9f89b0e\",\"punishmentId\":0,\"messageToUser\":\"Modified client\",\"beginDate\":\"2026-11-01T00:00:00Z\",\"endDate\":\"2026-11-08T00:00:00Z\"}");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(apiBanHandler);
            var apiBanResult = await svc.CheckModerationStatusAsync(acc, acc.SecurityToken, CancellationToken.None);
            Assert.True(apiBanResult.IsBanned);
            Assert.Equal(LocalizationService.Get("FastLogin.BanSevenDaysShort"), apiBanResult.IssueBadge);
        }

        [Fact]
        public void ClassifyAuthOrModerationApiPayload_MultiDigitBanDays_DoesNotMisclassifyAsShortBan()
        {
            Assert.Equal("ACCOUNT_BANNED", FastLoginCoordinator.ClassifyAuthOrModerationApiPayload("{\"punishmentType\":\"Ban 365 Days\"}", 200));
            Assert.Equal("ACCOUNT_BANNED", FastLoginCoordinator.ClassifyAuthOrModerationApiPayload("{\"punishmentType\":\"Ban 21 Days\"}", 200));
            Assert.Equal("BAN_14_DAYS", FastLoginCoordinator.ClassifyAuthOrModerationApiPayload("{\"punishmentType\":\"Ban 14 Days\"}", 200));
            Assert.Equal("BAN_7_DAYS", FastLoginCoordinator.ClassifyAuthOrModerationApiPayload("{\"punishmentType\":\"Ban 7 Days\"}", 200));
            Assert.Equal("BAN_3_DAYS", FastLoginCoordinator.ClassifyAuthOrModerationApiPayload("{\"punishmentType\":\"Ban 3 Days\"}", 200));
            Assert.Equal("BAN_1_DAY", FastLoginCoordinator.ClassifyAuthOrModerationApiPayload("{\"punishmentType\":\"Ban 1 Day\"}", 200));
        }

        [Fact]
        public async Task AuditAccountAsync_WhenModeratedOn403_DoesNotDuplicateModerationCheckOrWebhook()
        {
            int notApprovedCalls = 0;
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler((req, _) =>
            {
                string uri = req.RequestUri!.AbsoluteUri;
                if (uri.Contains("users.roblox.com/v1/users/authenticated", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("{\"errors\":[{\"code\":0,\"message\":\"Account has been moderated\"}]}")
                    });
                }

                if (uri.Contains("usermoderation.roblox.com/v1/not-approved", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref notApprovedCalls);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));

            var acc = new Account
            {
                Username = "AuditModeratedUser",
                UserID = 808,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--AuditCookie1234",
                Valid = true
            };

            var auditResult = await SessionAuditorService.AuditAccountAsync(acc);

            Assert.Equal(AccountSessionStatus.Moderated, auditResult.Status);
            Assert.False(acc.Valid);
            Assert.Equal(0, notApprovedCalls);
        }
    }
}
