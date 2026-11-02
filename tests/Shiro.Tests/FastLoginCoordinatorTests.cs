using System;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class FastLoginCoordinatorTests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("https://www.roblox.com/home", false)]
        [InlineData("https://www.roblox.com/not-approved", true)]
        [InlineData("https://www.roblox.com/account-status", true)]
        [InlineData("https://www.roblox.com/banned", true)]
        [InlineData("https://www.roblox.com/moderation/v2", true)]
        [InlineData("https://www.roblox.com/account-locked", true)]
        [InlineData("https://www.roblox.com/security-notification", true)]
        public void IsModerationOrLockedUrl_DetectsModerationPaths(string? url, bool expected)
        {
            Assert.Equal(expected, FastLoginCoordinator.IsModerationOrLockedUrl(url));
        }

        [Theory]
        [InlineData(null, false, null)]
        [InlineData("", true, "Bị khóa / Ban")]
        [InlineData("User is not authenticated", true, "UNAUTHENTICATED_MODERATION")]
        [InlineData("{\"code\": 9002}", false, "UNAUTHENTICATED_MODERATION")]
        [InlineData("Incorrect username or password.", false, "Sai mật khẩu")]
        [InlineData("Banned for 14 Days", true, "Ban 14 ngày")]
        [InlineData("Banned for 7 Days", true, "Ban 7 ngày")]
        [InlineData("Banned for 3 Days", true, "Ban 3 ngày")]
        [InlineData("Banned for 1 Day", true, "Ban 1 ngày")]
        [InlineData("Your account has been terminated", true, "Ban vĩnh viễn")]
        [InlineData("Warning from moderation", true, "Cảnh báo")]
        [InlineData("Your account has been locked for security", false, "Khóa bảo mật")]
        [InlineData("Account suspended", true, "Bị khóa / Ban")]
        public void ClassifyDomIssueText_MapsExpectedBadges(string? text, bool isModUrl, string? expectedBadge)
        {
            var (badge, _) = FastLoginCoordinator.ClassifyDomIssueText(text, isModUrl);
            Assert.Equal(expectedBadge, badge);
        }

        [Fact]
        public void ScriptsAndFingerprint_GenerateNonEmptyOutput()
        {
            string fp1 = FastLoginCoordinator.FingerprintCookie("cookie-a");
            string fp2 = FastLoginCoordinator.FingerprintCookie("cookie-b");
            Assert.NotEqual(fp1, fp2);
            Assert.Equal(64, fp1.Length);

            string enhanceScript = FastLoginCoordinator.GetLoginPageEnhancementScript();
            Assert.Contains("shiro_password_captured", enhanceScript);

            string fillScript = FastLoginCoordinator.BuildCredentialFillScript("TestUser", "TestPass", forceClick: true);
            Assert.Contains("TestUser", fillScript);
            Assert.Contains("TestPass", fillScript);
        }

        [Fact]
        public async Task ProcessCapturedSessionAsync_HandlesAllOutcomes()
        {
            // 1. Valid account matching expected username -> Success
            var acc1 = new Account { Username = "PlayerOne", Valid = true };
            var validResult = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: acc1,
                cookieValue: "token1",
                expectedUsername: "PlayerOne",
                password: "pass1",
                alias: "MyAlias",
                group: "MyGroup",
                queueCount: 1,
                currentQueueAccount: acc1);
            Assert.Equal(FastLoginCaptureStatus.Success, validResult.Status);
            Assert.Equal("pass1", validResult.Account?.Password);
            Assert.Equal("MyAlias", validResult.Account?.Alias);
            Assert.Equal("MyGroup", validResult.Account?.Group);

            // 2. Valid account with different username -> UsernameMismatch
            var acc2 = new Account { Username = "WrongUser", Valid = true };
            var mismatchResult = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: acc2,
                cookieValue: "token2",
                expectedUsername: "ExpectedUser",
                password: null,
                alias: null,
                group: null,
                queueCount: 1,
                currentQueueAccount: null);
            Assert.Equal(FastLoginCaptureStatus.UsernameMismatch, mismatchResult.Status);

            // 3. Invalid account with IsBannedOrLocked -> AccountIssue
            var queueAcc = new Account { Username = "QueuedUser" };
            var bannedAcc = new Account
            {
                Username = "QueuedUser",
                Valid = false,
                AccountStatusIssue = "Ban 7 ngày"
            };
            var bannedResult = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: bannedAcc,
                cookieValue: "token3",
                expectedUsername: "QueuedUser",
                password: null,
                alias: null,
                group: null,
                queueCount: 1,
                currentQueueAccount: queueAcc);
            Assert.Equal(FastLoginCaptureStatus.AccountIssue, bannedResult.Status);
            Assert.Equal("Ban 7 ngày", bannedResult.IssueBadge);

            // 4. Invalid account with DOM UNAUTHENTICATED_MODERATION -> UnauthenticatedModeration
            var unauthModResult = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: null,
                cookieValue: "token4",
                expectedUsername: "QueuedUser",
                password: null,
                alias: null,
                group: null,
                queueCount: 1,
                currentQueueAccount: queueAcc,
                detectDomIssueAsync: () => Task.FromResult<(string?, string?)>(("UNAUTHENTICATED_MODERATION", "Expired")));
            Assert.Equal(FastLoginCaptureStatus.UnauthenticatedModeration, unauthModResult.Status);

            // 5. Invalid account with DOM issue badge -> AccountIssue
            var domIssueResult = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: null,
                cookieValue: "token5",
                expectedUsername: "QueuedUser",
                password: null,
                alias: null,
                group: null,
                queueCount: 1,
                currentQueueAccount: queueAcc,
                detectDomIssueAsync: () => Task.FromResult<(string?, string?)>(("Khóa bảo mật", "Locked")));
            Assert.Equal(FastLoginCaptureStatus.AccountIssue, domIssueResult.Status);
            Assert.Equal("Khóa bảo mật", domIssueResult.IssueBadge);

            // 6. Invalid account with no issue -> RetryPolling
            var retryResult = await FastLoginCoordinator.ProcessCapturedSessionAsync(
                capturedAccount: null,
                cookieValue: "token6",
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
