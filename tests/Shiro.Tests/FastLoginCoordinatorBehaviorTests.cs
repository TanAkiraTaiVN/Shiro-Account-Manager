#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class FastLoginCoordinatorBehaviorTests
    {

        [Fact]
        public void FingerprintCookie_ProducesSha256Hex()
        {
            string empty = FastLoginCoordinator.FingerprintCookie(null);
            Assert.Equal(64, empty.Length);

            string fp1 = FastLoginCoordinator.FingerprintCookie("cookie-1");
            string fp2 = FastLoginCoordinator.FingerprintCookie("cookie-1");
            string fp3 = FastLoginCoordinator.FingerprintCookie("cookie-2");

            Assert.Equal(64, fp1.Length);
            Assert.Equal(fp1, fp2);
            Assert.NotEqual(fp1, fp3);
        }

        [Fact]
        public void IsModerationOrLockedUrl_IdentifiesModerationUrls()
        {
            Assert.False(FastLoginCoordinator.IsModerationOrLockedUrl(null));
            Assert.False(FastLoginCoordinator.IsModerationOrLockedUrl(string.Empty));
            Assert.False(FastLoginCoordinator.IsModerationOrLockedUrl("https://www.roblox.com/home"));

            Assert.True(FastLoginCoordinator.IsModerationOrLockedUrl("https://www.roblox.com/not-approved"));
            Assert.True(FastLoginCoordinator.IsModerationOrLockedUrl("https://www.roblox.com/account-status"));
            Assert.True(FastLoginCoordinator.IsModerationOrLockedUrl("https://www.roblox.com/banned"));
            Assert.True(FastLoginCoordinator.IsModerationOrLockedUrl("https://www.roblox.com/moderation"));
            Assert.True(FastLoginCoordinator.IsModerationOrLockedUrl("https://www.roblox.com/account-locked"));
            Assert.True(FastLoginCoordinator.IsModerationOrLockedUrl("https://www.roblox.com/security-notification"));
        }

        [Theory]
        [InlineData("User is not authenticated", false, "UNAUTHENTICATED_MODERATION")]
        [InlineData("Incorrect username or password", false, "WRONG_PASSWORD")]
        [InlineData("Account is locked", false, "ACCOUNT_LOCKED")]
        [InlineData("You are banned for 1 day", true, "BAN_1_DAY")]
        [InlineData("Banned for 3 days", true, "BAN_3_DAYS")]
        [InlineData("Suspension for 7-day period", true, "BAN_7_DAYS")]
        [InlineData("Account banned 14-day duration", true, "BAN_14_DAYS")]
        [InlineData("Account deleted permanently", true, "TERMINATED")]
        [InlineData("Warning issued for chat", true, "WARNING")]
        [InlineData("Your account has been suspended", true, "ACCOUNT_BANNED")]
        [InlineData("Unknown moderation screen", true, "LOCKED_OR_BANNED")]
        public void ClassifyDomIssueCode_CategorizesAccurately(string text, bool isMod, string expectedCode)
        {
            string code = FastLoginCoordinator.ClassifyDomIssueCode(text, isMod);
            Assert.Equal(expectedCode, code);

            var (badge, reason) = FastLoginCoordinator.MapDomIssueCode(code);
            Assert.False(string.IsNullOrEmpty(badge));
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void Scripts_GenerateExpectedContent()
        {
            string cssJs = FastLoginCoordinator.GetLoginPageEnhancementScript();
            Assert.Contains("html, body", cssJs);
            Assert.Contains("shiro_password_captured", cssJs);

            string fillScript = FastLoginCoordinator.BuildCredentialFillScript("testuser", "testpass", forceClick: true);
            Assert.Contains("testuser", fillScript);
            Assert.Contains("testpass", fillScript);
            Assert.Contains("forceClick = true", fillScript);
        }

        [Fact]
        public async Task ProcessCapturedSessionAsync_ValidSessionSuccess_UpdatesFieldsAndSaves()
        {
            var repo = Substitute.For<IAccountRepository>();
            using var _ = AppServices.Override<IAccountRepository>(repo);

            var acc = new Account { Username = "shiro_user", SecurityToken = "old_token", Valid = true };
            var result = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: acc,
                cookieValue: "new_cookie_value",
                expectedUsername: "shiro_user",
                password: "NewPass123",
                alias: "ShiroMain",
                group: "VIP",
                queueCount: 1,
                currentQueueAccount: acc);

            Assert.Equal(FastLoginCaptureStatus.Success, result.Status);
            Assert.Equal("NewPass123", acc.Password);
            Assert.Equal("ShiroMain", acc.Alias);
            Assert.Equal("VIP", acc.Group);
            await repo.Received(1).SaveAsync(true);
        }

        [Fact]
        public async Task ProcessCapturedSessionAsync_UsernameMismatch_ReturnsMismatch()
        {
            var acc = new Account { Username = "actual_user", Valid = true };
            var result = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: acc,
                cookieValue: "cookie",
                expectedUsername: "expected_user",
                password: null,
                alias: null,
                group: null,
                queueCount: 0,
                currentQueueAccount: null);

            Assert.Equal(FastLoginCaptureStatus.UsernameMismatch, result.Status);
        }

        [Fact]
        public async Task ProcessCapturedSessionAsync_QueueAccountIssues_ClassifiedCorrectly()
        {
            var queueAcc = new Account { Username = "queue_user" };

            // 1. Captured account is banned or locked
            var bannedAcc = new Account { Username = "banned", Valid = false, AccountStatusIssue = "Banned 7 Days" };
            var banResult = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: bannedAcc,
                cookieValue: "ban_cookie",
                expectedUsername: null,
                password: null,
                alias: null,
                group: null,
                queueCount: 1,
                currentQueueAccount: queueAcc);

            Assert.Equal(FastLoginCaptureStatus.AccountIssue, banResult.Status);
            Assert.Equal("ban_cookie", queueAcc.SecurityToken);

            // 2. DOM detects UNAUTHENTICATED_MODERATION
            var unauthResult = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: null,
                cookieValue: "c1",
                expectedUsername: null,
                password: null,
                alias: null,
                group: null,
                queueCount: 1,
                currentQueueAccount: queueAcc,
                detectDomIssueAsync: () => Task.FromResult<(string?, string?)>(("UNAUTHENTICATED_MODERATION", "Opening ban")));

            Assert.Equal(FastLoginCaptureStatus.UnauthenticatedModeration, unauthResult.Status);

            // 3. DOM detects other issue
            queueAcc.SecurityToken = string.Empty;
            var domIssueResult = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: null,
                cookieValue: "c2",
                expectedUsername: null,
                password: null,
                alias: null,
                group: null,
                queueCount: 1,
                currentQueueAccount: queueAcc,
                detectDomIssueAsync: () => Task.FromResult<(string?, string?)>(("BAN_1_DAY", "Banned 1 day")));

            Assert.Equal(FastLoginCaptureStatus.AccountIssue, domIssueResult.Status);
            Assert.Equal("c2", queueAcc.SecurityToken);

            // 4. Retry polling when no issue detected
            var retryResult = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: null,
                cookieValue: "c3",
                expectedUsername: null,
                password: null,
                alias: null,
                group: null,
                queueCount: 0,
                currentQueueAccount: null);

            Assert.Equal(FastLoginCaptureStatus.RetryPolling, retryResult.Status);
        }
    }
}
