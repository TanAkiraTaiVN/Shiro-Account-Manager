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
    [Collection("SerialGlobalState")]
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
        private const string ModerationSevenDayBanHtml = "<div>Reviewed: today. Ban 7 days</div>";

        private readonly Func<Account, HttpClient>? _previousAuditorClientFactory;
        private readonly Func<Account, HttpClient>? _previousAccountClientFactory;
        private readonly AppLanguage _previousLanguage;

        public P0AuthNetworkAndModerationTests()
        {
            _previousAuditorClientFactory = SessionAuditorService.TestHttpClientFactory;
            _previousAccountClientFactory = RobloxAccountSessionClient.TestHttpClientFactory;
            _previousLanguage = LocalizationService.CurrentLanguage;
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
        }

        public void Dispose()
        {
            SessionAuditorService.TestHttpClientFactory = _previousAuditorClientFactory;
            RobloxAccountSessionClient.TestHttpClientFactory = _previousAccountClientFactory;
            LocalizationService.SetLanguage(_previousLanguage, saveSetting: false);
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

        [Fact]
        public void ParseModerationText_WhenUiLanguageIsVietnamese_StillDetectsCanonicalEnglishHtml()
        {
            LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);

            var communityStandards = RobloxModerationService.ParseModerationText("<div>Community Standards violation</div>");
            Assert.True(communityStandards.IsBanned);
            Assert.Equal(LocalizationService.Get("FastLogin.LockedOrBannedBadge"), communityStandards.IssueBadge);
            Assert.Equal(LocalizationService.Get("FastLogin.AccountLockedOrModerated"), communityStandards.Reason);

            var accountLocked = RobloxModerationService.ParseModerationText("<div>Account Locked - Reset your password</div>");
            Assert.True(accountLocked.IsBanned);
            Assert.Equal(LocalizationService.Get("FastLogin.SecurityLockoutShort"), accountLocked.IssueBadge);
            Assert.Equal(LocalizationService.Get("Moderation.AccountLockedReason"), accountLocked.Reason);

            var sevenDayBan = RobloxModerationService.ParseModerationText("<div>Moderator Note: Ban 7 Days</div>");
            Assert.True(sevenDayBan.IsBanned);
            Assert.Equal(LocalizationService.Get("FastLogin.BanSevenDaysShort"), sevenDayBan.IssueBadge);
            Assert.Equal(LocalizationService.Get("Moderation.BanSevenDaysReason"), sevenDayBan.Reason);
        }

        [Fact]
        public void ParseModerationText_WhenWarningAppearsWithoutModerationMarker_ReturnsNotBanned()
        {
            var benignBanner = RobloxModerationService.ParseModerationText(
                "<div class='banner'>Warning: browser extension detected</div>");

            Assert.False(benignBanner.IsBanned);
            Assert.Null(benignBanner.IssueBadge);
            Assert.Null(benignBanner.Reason);

            var genuineWarning = RobloxModerationService.ParseModerationText(
                "<div class='not-approved'><h2>Warning</h2><p>Moderator Note: Please review Community Standards</p><button>I Agree</button></div>");

            Assert.True(genuineWarning.IsBanned);
            Assert.Equal(LocalizationService.Get("Msg_WarningTitle"), genuineWarning.IssueBadge);
            Assert.Equal(LocalizationService.Get("Moderation.WarningNoticeReason"), genuineWarning.Reason);
        }

        [Fact]
        public void ModerationLocalizationKeys_AreUnscrambledAndMatchParsedReasons()
        {
            foreach (AppLanguage lang in new[] { AppLanguage.English, AppLanguage.Vietnamese })
            {
                LocalizationService.SetLanguage(lang, saveSetting: false);

                Assert.Equal(
                    LocalizationService.Get("Moderation.BanOneDayReason"),
                    RobloxModerationService.ParseModerationText("Reviewed: today. Ban 1 day").Reason);
                Assert.Equal(
                    LocalizationService.Get("Moderation.BanThreeDaysReason"),
                    RobloxModerationService.ParseModerationText("Reviewed: today. Ban 3 days").Reason);
                Assert.Equal(
                    LocalizationService.Get("Moderation.BanSevenDaysReason"),
                    RobloxModerationService.ParseModerationText("Reviewed: today. Ban 7 days").Reason);
                Assert.Equal(
                    LocalizationService.Get("Moderation.BanFourteenDaysReason"),
                    RobloxModerationService.ParseModerationText("Reviewed: today. Ban 14 days").Reason);
                Assert.Equal(
                    LocalizationService.Get("Moderation.BanPermanentReason"),
                    RobloxModerationService.ParseModerationText("Reviewed: today. Account terminated").Reason);
                Assert.Equal(
                    LocalizationService.Get("Moderation.WarningNoticeReason"),
                    RobloxModerationService.ParseModerationText("Reviewed: today. Warning issued").Reason);
                Assert.Equal(
                    LocalizationService.Get("Moderation.AccountLockedReason"),
                    RobloxModerationService.ParseModerationText("Reviewed: today. Account locked").Reason);
            }
        }

        [Fact]
        public async Task SessionAuditor_AuditAccountAsync_WithPreCanceledToken_ThrowsWithoutMutatingValid()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var emptyTokenAccount = new Account
            {
                Username = "PreCanceledEmptyToken",
                UserID = 9001,
                SecurityToken = string.Empty,
                Valid = true
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => SessionAuditorService.AuditAccountAsync(emptyTokenAccount, cts.Token));
            Assert.True(emptyTokenAccount.Valid);

            var tokenAccount = new Account
            {
                Username = "PreCanceledValidToken",
                UserID = 9002,
                SecurityToken = ValidToken,
                Valid = true
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => SessionAuditorService.AuditAccountAsync(tokenAccount, cts.Token));
            Assert.True(tokenAccount.Valid);
        }

        [Fact]
        public async Task RobloxModerationService_WhenApiProbeTimesOut_FallbackWebProbeGetsFreshTimeoutAndDetectsBan()
        {
            bool apiProbeTimedOut = false;
            int webProbeCalls = 0;

            var handler = new FakeHttpHandler(async (req, ct) =>
            {
                string url = req.RequestUri!.AbsoluteUri;
                if (url.Contains("usermoderation.roblox.com/v1/not-approved", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        apiProbeTimedOut = true;
                        throw;
                    }
                }

                if (url.Contains("www.roblox.com/home", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref webProbeCalls);
                    Assert.False(ct.IsCancellationRequested);
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri("/not-approved", UriKind.Relative);
                    return redirect;
                }

                if (url.Contains("www.roblox.com/not-approved", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref webProbeCalls);
                    Assert.False(ct.IsCancellationRequested);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(ModerationSevenDayBanHtml)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);

            var account = new Account
            {
                Username = "SplitTimeoutModUser",
                UserID = 7788,
                SecurityToken = ValidToken
            };

            var svc = new RobloxModerationService();
            var result = await svc.CheckModerationStatusAsync(
                account,
                ValidToken,
                httpClientOverride: null,
                CancellationToken.None,
                dispatchAlert: false);

            Assert.True(apiProbeTimedOut);
            Assert.Equal(2, webProbeCalls);
            Assert.True(result.IsBanned);
            Assert.Equal(LocalizationService.Get("FastLogin.BanSevenDaysShort"), result.IssueBadge);
        }

        [Fact]
        public async Task RobloxModerationService_WhenCallerCancelsDuringApiProbe_DoesNotInvokeWebFallback()
        {
            using var callerCts = new CancellationTokenSource();
            int webCalls = 0;

            var handler = new FakeHttpHandler((req, _) =>
            {
                string url = req.RequestUri!.AbsoluteUri;
                if (url.Contains("usermoderation.roblox.com/v1/not-approved", StringComparison.OrdinalIgnoreCase))
                {
                    callerCts.Cancel();
                    throw new OperationCanceledException(callerCts.Token);
                }

                Interlocked.Increment(ref webCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);

            var account = new Account
            {
                Username = "CallerCancelModUser",
                UserID = 7789,
                SecurityToken = ValidToken
            };

            var svc = new RobloxModerationService();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.CheckModerationStatusAsync(
                account,
                ValidToken,
                httpClientOverride: null,
                callerCts.Token,
                dispatchAlert: false));

            Assert.Equal(0, webCalls);
        }

        [Fact]
        public async Task RobloxModerationService_WhenCallerCancelsDuringRedirectFollowUp_DoesNotReportBanOrEnqueueAlert()
        {
            using var callerCts = new CancellationTokenSource();
            int followUpCalls = 0;
            int alertCount = 0;
            bool prevNotifyModeration = WebhookNotificationService.NotifyOnModeration;
            WebhookNotificationService.NotifyOnModeration = true;
            void OnAlert(WebhookAlert alert) => Interlocked.Increment(ref alertCount);
            WebhookNotificationService.AlertDispatched += OnAlert;

            try
            {
                var handler = new FakeHttpHandler((req, _) =>
                {
                    string url = req.RequestUri!.AbsoluteUri;
                    if (url.Contains("usermoderation.roblox.com/v1/not-approved", StringComparison.OrdinalIgnoreCase))
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

                    if (url.Contains("www.roblox.com/home", StringComparison.OrdinalIgnoreCase))
                    {
                        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                        redirect.Headers.Location = new Uri("/not-approved", UriKind.Relative);
                        return Task.FromResult(redirect);
                    }

                    if (url.Contains("www.roblox.com/not-approved", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref followUpCalls);
                        callerCts.Cancel();
                        throw new OperationCanceledException(callerCts.Token);
                    }

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                });

                RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);

                var account = new Account
                {
                    Username = "RedirectCancelModUser",
                    UserID = 7790,
                    SecurityToken = ValidToken
                };

                var svc = new RobloxModerationService();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    svc.CheckModerationStatusAsync(account, ValidToken, callerCts.Token));

                Assert.Equal(1, followUpCalls);
                Assert.Equal(0, alertCount);
            }
            finally
            {
                WebhookNotificationService.AlertDispatched -= OnAlert;
                WebhookNotificationService.NotifyOnModeration = prevNotifyModeration;
            }
        }
    }
}
