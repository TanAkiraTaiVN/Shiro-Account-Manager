#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class FastLoginCoordinatorBehaviorTests : IDisposable
    {
        private readonly IAccountRepository _origRepo;
        private readonly IAppLogger _origLogger;

        public FastLoginCoordinatorBehaviorTests()
        {
            _origRepo = AppServices.AccountRepository;
            _origLogger = FastLoginCoordinator.Logger;
        }

        public void Dispose()
        {
            AppServices.AccountRepository = _origRepo;
            FastLoginCoordinator.Logger = _origLogger;
        }

        [Fact]
        public void FingerprintCookie_ComputesExpectedSha256Hex()
        {
            string expectedEmpty = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Empty)));
            Assert.Equal(expectedEmpty, FastLoginCoordinator.FingerprintCookie(null));
            Assert.Equal(expectedEmpty, FastLoginCoordinator.FingerprintCookie(string.Empty));

            string token = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you";
            string expectedTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            Assert.Equal(expectedTokenHash, FastLoginCoordinator.FingerprintCookie(token));
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("https://www.roblox.com/home", false)]
        [InlineData("https://www.roblox.com/games/1818", false)]
        [InlineData("https://www.roblox.com/not-approved", true)]
        [InlineData("https://www.roblox.com/account-status", true)]
        [InlineData("https://www.roblox.com/banned", true)]
        [InlineData("https://www.roblox.com/moderation", true)]
        [InlineData("https://www.roblox.com/account-locked", true)]
        [InlineData("https://www.roblox.com/security-notification", true)]
        [InlineData("HTTPS://ROBLOX.COM/BANNED/DETAILS", true)]
        public void IsModerationOrLockedUrl_ClassifiesUrlsCorrectly(string? url, bool expected)
        {
            bool actual = FastLoginCoordinator.IsModerationOrLockedUrl(url);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void ClassifyDomIssueCode_EmptyInputs_ReturnsExpectedByUrlFlag()
        {
            Assert.Equal(string.Empty, FastLoginCoordinator.ClassifyDomIssueCode(null, isModUrl: false));
            Assert.Equal(string.Empty, FastLoginCoordinator.ClassifyDomIssueCode("   ", isModUrl: false));
            Assert.Equal("LOCKED_OR_BANNED", FastLoginCoordinator.ClassifyDomIssueCode(null, isModUrl: true));
            Assert.Equal("LOCKED_OR_BANNED", FastLoginCoordinator.ClassifyDomIssueCode("   ", isModUrl: true));
        }

        [Theory]
        [InlineData("User is not authenticated at this time", "UNAUTHENTICATED_MODERATION")]
        [InlineData("Error occurred {\"code\":9002}", "UNAUTHENTICATED_MODERATION")]
        [InlineData("System error: {\"code\": 9002, \"msg\":\"expired\"}", "UNAUTHENTICATED_MODERATION")]
        [InlineData("Incorrect username or password. Please try again.", "WRONG_PASSWORD")]
        [InlineData("Invalid username or password supplied", "WRONG_PASSWORD")]
        public void ClassifyDomIssueCode_AuthenticationAndCredentialErrors_IdentifiedRegardlessOfModUrl(string text, string expectedCode)
        {
            Assert.Equal(expectedCode, FastLoginCoordinator.ClassifyDomIssueCode(text, isModUrl: false));
            Assert.Equal(expectedCode, FastLoginCoordinator.ClassifyDomIssueCode(text, isModUrl: true));
        }

        [Theory]
        [InlineData("Banned for 14 day period", "BAN_14_DAYS")]
        [InlineData("Account suspended for a 14-day duration", "BAN_14_DAYS")]
        [InlineData("Banned for 7 day period", "BAN_7_DAYS")]
        [InlineData("Suspended for 7-day period", "BAN_7_DAYS")]
        [InlineData("Account banned for 3 day period", "BAN_3_DAYS")]
        [InlineData("A 3-day suspension has been applied", "BAN_3_DAYS")]
        [InlineData("Banned for 1 day period", "BAN_1_DAY")]
        [InlineData("A 1-day suspension has been placed", "BAN_1_DAY")]
        [InlineData("Your account has been deleted for violations", "TERMINATED")]
        [InlineData("Account has been terminated by administrator", "TERMINATED")]
        [InlineData("This is a moderation warning for asset creation", "WARNING")]
        [InlineData("Account is locked for security reasons", "ACCOUNT_LOCKED")]
        [InlineData("Please reset your password before continuing", "ACCOUNT_LOCKED")]
        [InlineData("A security notification has been generated", "ACCOUNT_LOCKED")]
        [InlineData("Account is not approved for access", "ACCOUNT_BANNED")]
        [InlineData("Your user has been banned", "ACCOUNT_BANNED")]
        [InlineData("You are moderated from this service", "ACCOUNT_BANNED")]
        [InlineData("Your privileges are suspended indefinitely", "ACCOUNT_BANNED")]
        [InlineData("Some random moderation reason text", "LOCKED_OR_BANNED")]
        public void ClassifyDomIssueCode_ModerationUrlPatterns_ClassifiedProperly(string text, string expectedCode)
        {
            string actual = FastLoginCoordinator.ClassifyDomIssueCode(text, isModUrl: true);
            Assert.Equal(expectedCode, actual);
        }

        [Fact]
        public void ClassifyDomIssueCode_NonModUrl_RecognizesLockoutOrDefaultsEmpty()
        {
            Assert.Equal("ACCOUNT_LOCKED", FastLoginCoordinator.ClassifyDomIssueCode("Account locked by security", isModUrl: false));
            Assert.Equal("ACCOUNT_LOCKED", FastLoginCoordinator.ClassifyDomIssueCode("You must reset your password", isModUrl: false));
            Assert.Equal("ACCOUNT_LOCKED", FastLoginCoordinator.ClassifyDomIssueCode("Received a security notification", isModUrl: false));
            Assert.Equal(string.Empty, FastLoginCoordinator.ClassifyDomIssueCode("Welcome back to Roblox homepage", isModUrl: false));
        }

        [Theory]
        [InlineData(null, null, null)]
        [InlineData("", null, null)]
        [InlineData("   ", null, null)]
        [InlineData("UNKNOWN_CODE_XYZ", null, null)]
        [InlineData("UNAUTHENTICATED_MODERATION", "UNAUTHENTICATED_MODERATION", "FastLogin.CookieExpiredOpeningBan")]
        [InlineData("BAN_14_DAYS", "FastLogin.BanFourteenDaysShort", "FastLogin.BannedFourteenDays")]
        [InlineData("BAN_7_DAYS", "FastLogin.BanSevenDaysShort", "FastLogin.BannedSevenDays")]
        [InlineData("BAN_3_DAYS", "FastLogin.BanThreeDaysShort", "FastLogin.BannedThreeDays")]
        [InlineData("BAN_1_DAY", "FastLogin.BanOneDayShort", "FastLogin.BannedOneDay")]
        [InlineData("TERMINATED", "FastLogin.BanPermanentShort", "FastLogin.AccountDeletedPermanent")]
        [InlineData("WARNING", "Msg_WarningTitle", "FastLogin.ModerationWarning")]
        [InlineData("ACCOUNT_LOCKED", "FastLogin.SecurityLockoutShort", "FastLogin.SecurityLockout")]
        [InlineData("ACCOUNT_BANNED", "FastLogin.LockedOrBannedBadge", "FastLogin.AccountBannedOrSuspended")]
        [InlineData("LOCKED_OR_BANNED", "FastLogin.LockedOrBannedBadge", "FastLogin.AccountLockedOrModerated")]
        [InlineData("WRONG_PASSWORD", "MainWindow.WrongPasswordTitle", "FastLogin.IncorrectUsernameOrPassword")]
        public void MapDomIssueCode_MapsCodesToExpectedLocalizedStrings(string? code, string? expectedBadgeKey, string? expectedReasonKey)
        {
            var (badge, reason) = FastLoginCoordinator.MapDomIssueCode(code);

            if (expectedBadgeKey == null)
            {
                Assert.Null(badge);
                Assert.Null(reason);
            }
            else
            {
                string expectedBadge = expectedBadgeKey == "UNAUTHENTICATED_MODERATION"
                    ? "UNAUTHENTICATED_MODERATION"
                    : LocalizationService.Get(expectedBadgeKey);
                string expectedReason = LocalizationService.Get(expectedReasonKey!);

                Assert.Equal(expectedBadge, badge);
                Assert.Equal(expectedReason, reason);
            }
        }

        [Fact]
        public void ClassifyDomIssueText_Overloads_DelegateCorrectly()
        {
            var result1 = FastLoginCoordinator.ClassifyDomIssueText("https://roblox.com/banned", "banned for 7 day violation");
            Assert.Equal(LocalizationService.Get("FastLogin.BanSevenDaysShort"), result1.IssueBadge);

            var result2 = FastLoginCoordinator.ClassifyDomIssueText("banned for 3 day violation", isModUrl: true);
            Assert.Equal(LocalizationService.Get("FastLogin.BanThreeDaysShort"), result2.IssueBadge);
        }

        [Fact]
        public void ScriptGenerators_ProduceValidJavaScript()
        {
            string enhanceScript = FastLoginCoordinator.GetLoginPageEnhancementScript();
            Assert.Contains("#onetrust-consent-sdk", enhanceScript);
            Assert.Contains("shiro_password_captured", enhanceScript);

            string fillScript = FastLoginCoordinator.BuildCredentialFillScript("TestUser", "SecretPassword123", forceClick: true);
            Assert.Contains("\"TestUser\"", fillScript);
            Assert.Contains("\"SecretPassword123\"", fillScript);
            Assert.Contains("const forceClick = true", fillScript);

            string fillScriptNoPass = FastLoginCoordinator.BuildCredentialFillScript("OtherUser", null, forceClick: false);
            Assert.Contains("\"OtherUser\"", fillScriptNoPass);
            Assert.Contains("const hasPass = false", fillScriptNoPass);
            Assert.Contains("const forceClick = false", fillScriptNoPass);
        }

        [Fact]
        public void Logger_Property_EnforcesNonNullAndPermitsAssignment()
        {
            var mockLogger = Substitute.For<IAppLogger>();
            FastLoginCoordinator.Logger = mockLogger;
            Assert.Same(mockLogger, FastLoginCoordinator.Logger);

            Assert.Throws<ArgumentNullException>(() => FastLoginCoordinator.Logger = null!);
        }

        [Fact]
        public async Task ProcessCapturedSessionAsync_ValidAccount_MatchingUser_ReturnsSuccessAndSaves()
        {
            var mockRepo = Substitute.For<IAccountRepository>();
            AppServices.AccountRepository = mockRepo;

            var captured = new Account
            {
                Username = "ValidUser",
                UserID = 12345,
                SecurityToken = "test_token"
            };

            var result = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: captured,
                cookieValue: "cookie_val",
                expectedUsername: "validuser",
                password: "newPassword",
                alias: "MainAlt",
                group: "VIP",
                queueCount: 1,
                currentQueueAccount: captured);

            Assert.Equal(FastLoginCaptureStatus.Success, result.Status);
            Assert.Same(captured, result.Account);
            Assert.Equal("newPassword", captured.Password);
            Assert.Equal("MainAlt", captured.Alias);
            Assert.Equal("VIP", captured.Group);
            Assert.Equal(string.Empty, captured.AccountStatusIssue);
            await mockRepo.Received(1).SaveAsync(true);
        }

        [Fact]
        public async Task ProcessCapturedSessionAsync_ValidAccount_MismatchedUser_ReturnsUsernameMismatch()
        {
            var captured = new Account
            {
                Username = "DifferentUser",
                UserID = 99999,
                SecurityToken = "token"
            };

            var result = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: captured,
                cookieValue: "cookie_val",
                expectedUsername: "ExpectedUser",
                password: "pw",
                alias: null,
                group: null,
                queueCount: 1,
                currentQueueAccount: captured);

            Assert.Equal(FastLoginCaptureStatus.UsernameMismatch, result.Status);
            Assert.Same(captured, result.Account);
        }

        [Fact]
        public async Task ProcessCapturedSessionAsync_BannedAccountInQueue_ReturnsAccountIssueAndPopulatesToken()
        {
            var queueAccount = new Account { Username = "BannedUser", SecurityToken = "" };
            var captured = new Account { Username = "BannedUser", Valid = false, AccountStatusIssue = "Banned 7 Days" };

            var result = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: captured,
                cookieValue: "captured_cookie",
                expectedUsername: null,
                password: null,
                alias: null,
                group: null,
                queueCount: 1,
                currentQueueAccount: queueAccount);

            Assert.Equal(FastLoginCaptureStatus.AccountIssue, result.Status);
            Assert.Equal("captured_cookie", queueAccount.SecurityToken);
            Assert.Equal("Banned 7 Days", result.IssueBadge);
        }

        [Fact]
        public async Task ProcessCapturedSessionAsync_DomModerationDetection_HandlesUnauthenticatedAndAccountIssues()
        {
            var queueAccount = new Account { Username = "UnauthQueueUser" };

            // Case 1: UNAUTHENTICATED_MODERATION
            var result1 = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: null,
                cookieValue: "some_cookie",
                expectedUsername: null,
                password: null,
                alias: null,
                group: null,
                queueCount: 1,
                currentQueueAccount: queueAccount,
                detectDomIssueAsync: () => Task.FromResult<(string?, string?)>(("UNAUTHENTICATED_MODERATION", "Session Expired")));

            Assert.Equal(FastLoginCaptureStatus.UnauthenticatedModeration, result1.Status);

            // Case 2: Other DOM badge (e.g. Security Lockout)
            var result2 = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: null,
                cookieValue: "some_cookie",
                expectedUsername: null,
                password: null,
                alias: null,
                group: null,
                queueCount: 1,
                currentQueueAccount: queueAccount,
                detectDomIssueAsync: () => Task.FromResult<(string?, string?)>(("Security Lockout", "Reset Password Required")));

            Assert.Equal(FastLoginCaptureStatus.AccountIssue, result2.Status);
            Assert.Equal("Security Lockout", result2.IssueBadge);
            Assert.Equal("Reset Password Required", result2.Reason);

            // Case 3: Empty badge -> RetryPolling
            var result3 = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: null,
                cookieValue: "some_cookie",
                expectedUsername: null,
                password: null,
                alias: null,
                group: null,
                queueCount: 1,
                currentQueueAccount: queueAccount,
                detectDomIssueAsync: () => Task.FromResult<(string?, string?)>((null, null)));

            Assert.Equal(FastLoginCaptureStatus.RetryPolling, result3.Status);
        }
    }
}
