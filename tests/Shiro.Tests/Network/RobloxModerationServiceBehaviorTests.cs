#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
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
            Account.TestHttpClientFactory = null;
        }

        public void Dispose()
        {
            Account.TestHttpClientFactory = null;
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
                .Enqueue(HttpStatusCode.Found, "", ("Location", "https://example.com/other"));
            Account.TestHttpClientFactory = _ => new HttpClient(extHandler);
            Assert.False((await svc.CheckModerationStatusAsync(acc, acc.SecurityToken, CancellationToken.None)).IsBanned);

            // 2. Redirect to /discover -> false
            var discHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Found, "", ("Location", "https://www.roblox.com/discover"));
            Account.TestHttpClientFactory = _ => new HttpClient(discHandler);
            Assert.False((await svc.CheckModerationStatusAsync(acc, acc.SecurityToken, CancellationToken.None)).IsBanned);

            // 3. Redirect to /not-approved, second request returns 401 -> false & clears AccountStatusIssue
            acc.AccountStatusIssue = "OldIssue";
            var unauthHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Found, "", ("Location", "/not-approved"))
                .Enqueue(HttpStatusCode.Unauthorized, "");
            Account.TestHttpClientFactory = _ => new HttpClient(unauthHandler);
            Assert.False((await svc.CheckModerationStatusAsync(acc, acc.SecurityToken, CancellationToken.None)).IsBanned);
            Assert.Equal(string.Empty, acc.AccountStatusIssue);

            // 4. Redirect to /not-approved, second request redirects to /login -> false
            var loginRedirHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Found, "", ("Location", "/not-approved"))
                .Enqueue(HttpStatusCode.Found, "", ("Location", "/NewLogin"));
            Account.TestHttpClientFactory = _ => new HttpClient(loginRedirHandler);
            Assert.False((await svc.CheckModerationStatusAsync(acc, acc.SecurityToken, CancellationToken.None)).IsBanned);

            // 5. Redirect to /not-approved, second request returns login HTML -> false
            var loginHtmlHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Found, "", ("Location", "/not-approved"))
                .Enqueue(HttpStatusCode.OK, "<input id=\"login-username\" />");
            Account.TestHttpClientFactory = _ => new HttpClient(loginHtmlHandler);
            Assert.False((await svc.CheckModerationStatusAsync(acc, acc.SecurityToken, CancellationToken.None)).IsBanned);
        }
    }
}
