#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests.Network
{
    [Collection("StaticSeams")]
    public sealed class RobloxSettingsApiServiceTests : IDisposable
    {
        public RobloxSettingsApiServiceTests()
        {
            Account.TestHttpClientFactory = null;
        }

        public void Dispose()
        {
            Account.TestHttpClientFactory = null;
        }

        [Fact]
        public async Task SetFollowPrivacy_RetriesOnceWithCsrfToken()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "abc"))
                .Enqueue(HttpStatusCode.OK, "{}");

            Account.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = new Account
            {
                Username = "PrivacyUser",
                UserID = 12345,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.COOKIE.S",
                PinUnlocked = DateTime.Now.AddHours(1)
            };

            bool result = await RobloxSettingsApiService.Instance.SetFollowPrivacyAsync(account, 0);

            Assert.True(result);
            Assert.Equal(2, handler.Requests.Count);
            Assert.True(handler.Requests[1].Headers.TryGetValues("X-CSRF-TOKEN", out var csrfValues));
            Assert.Equal("abc", csrfValues.Single());
        }

        [Fact]
        public async Task SetFollowPrivacy_ReturnsFalseOn401()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Unauthorized, "{}");

            Account.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = new Account
            {
                Username = "PrivacyUser401",
                UserID = 12345,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.COOKIE.S",
                PinUnlocked = DateTime.MinValue
            };

            bool result = await RobloxSettingsApiService.Instance.SetFollowPrivacyAsync(account, 0);

            Assert.False(result);
            Assert.Single(handler.Requests);
            Assert.Contains("v1/account/pin", handler.Requests[0].RequestUri!.ToString());
        }

        [Fact]
        public async Task UnlockPin_SendsPinInBody()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "test-csrf"))
                .Enqueue(HttpStatusCode.OK, "{\"isEnabled\":false}");

            Account.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = new Account
            {
                Username = "PinUser",
                UserID = 12345,
                SecurityToken = "",
                PinUnlocked = DateTime.MinValue
            };

            bool result = await RobloxSettingsApiService.Instance.UnlockPinAsync(account, "1234");

            Assert.True(result);
            Assert.Equal(2, handler.Requests.Count);
            Assert.NotNull(handler.RequestBodies[^1]);
            Assert.Contains("\"pin\":\"1234\"", handler.RequestBodies[^1]!);
        }

        [Fact]
        public void ParseAccessCode_ExtractsCodeFromHtml()
        {
            var legacyResponse = new RestResponse
            {
                Content = "<html><script>Roblox.GameLauncher.joinPrivateGame(123456, '11111111-2222-3333-4444-555555555555');</script></html>"
            };
            bool legacySuccess = RobloxSettingsApiService.ParseAccessCode(legacyResponse, out string legacyCode);
            Assert.True(legacySuccess);
            Assert.Equal("11111111-2222-3333-4444-555555555555", legacyCode);

            var modernResponse = new RestResponse
            {
                Content = "{\"accessCode\":\"abcdef12-3456-7890-abcd-ef1234567890\"}"
            };
            bool modernSuccess = RobloxSettingsApiService.ParseAccessCode(modernResponse, out string modernCode);
            Assert.True(modernSuccess);
            Assert.Equal("abcdef12-3456-7890-abcd-ef1234567890", modernCode);

            var gameInstanceResponse = new RestResponse
            {
                Content = "{\"gameInstanceId\":\"98765432-10fe-dcba-0987-6543210fedcb\"}"
            };
            bool gameInstanceSuccess = RobloxSettingsApiService.ParseAccessCode(gameInstanceResponse, out string gameInstanceCode);
            Assert.True(gameInstanceSuccess);
            Assert.Equal("98765432-10fe-dcba-0987-6543210fedcb", gameInstanceCode);

            bool nullSuccess = RobloxSettingsApiService.ParseAccessCode(null!, out string nullCode);
            Assert.False(nullSuccess);
            Assert.Empty(nullCode);

            bool emptySuccess = RobloxSettingsApiService.ParseAccessCode(new RestResponse { Content = "" }, out string emptyCode);
            Assert.False(emptySuccess);
            Assert.Empty(emptyCode);
        }

        [Fact]
        public async Task CheckPinAsync_EdgeCases_HandleCorrectly()
        {
            bool nullAccount = await RobloxSettingsApiService.Instance.CheckPinAsync(null!);
            Assert.False(nullAccount);

            var unlockedAccount = new Account
            {
                Username = "AlreadyUnlocked",
                PinUnlocked = DateTime.Now.AddMinutes(10)
            };
            bool unlocked = await RobloxSettingsApiService.Instance.CheckPinAsync(unlockedAccount);
            Assert.True(unlocked);

            var emptyTokenAccount = new Account
            {
                Username = "EmptyTokenUser",
                SecurityToken = "",
                PinUnlocked = DateTime.MinValue
            };
            bool emptyToken = await RobloxSettingsApiService.Instance.CheckPinAsync(emptyTokenAccount, Internal: true);
            Assert.False(emptyToken);
        }

        [Fact]
        public async Task UnlockPinAsync_ValidationEdgeCases()
        {
            bool nullAccount = await RobloxSettingsApiService.Instance.UnlockPinAsync(null!, "1234");
            Assert.False(nullAccount);

            var acc = new Account { Username = "PinValidationUser" };
            bool shortPin = await RobloxSettingsApiService.Instance.UnlockPinAsync(acc, "12");
            Assert.False(shortPin);

            bool longPin = await RobloxSettingsApiService.Instance.UnlockPinAsync(acc, "12345");
            Assert.False(longPin);

            acc.PinUnlocked = DateTime.Now.AddMinutes(5);
            bool alreadyUnlocked = await RobloxSettingsApiService.Instance.UnlockPinAsync(acc, "1234");
            Assert.True(alreadyUnlocked);
        }

        [Fact]
        public async Task SetFollowPrivacyAsync_NullAccount_ReturnsFalse()
        {
            bool result = await RobloxSettingsApiService.Instance.SetFollowPrivacyAsync(null!, 0);
            Assert.False(result);
        }

        [Fact]
        public async Task ChangePasswordAsync_NullAccount_ReturnsFalse()
        {
            bool result = await RobloxSettingsApiService.Instance.ChangePasswordAsync(null!, "old", "new");
            Assert.False(result);
        }

        [Fact]
        public async Task ChangeEmailAsync_NullAccount_ReturnsFalse()
        {
            bool result = await RobloxSettingsApiService.Instance.ChangeEmailAsync(null!, "pass", "email@example.com");
            Assert.False(result);
        }

        [Fact]
        public async Task LogOutOfOtherSessionsAsync_NullAccount_ReturnsFalse()
        {
            bool result = await RobloxSettingsApiService.Instance.LogOutOfOtherSessionsAsync(null!);
            Assert.False(result);
        }

        [Fact]
        public async Task SetServerAsync_NullAccount_ReturnsFailed()
        {
            var (success, message) = await RobloxSettingsApiService.Instance.SetServerAsync(null!, 12345, "job-id");
            Assert.False(success);
            Assert.Equal("Account is null", message);
        }

        [Fact]
        public async Task SetDisplayNameAsync_NullAccount_CompletesSafely()
        {
            var exception = await Record.ExceptionAsync(() =>
                RobloxSettingsApiService.Instance.SetDisplayNameAsync(null!, "NewName"));
            Assert.Null(exception);
        }

        [Fact]
        public async Task SetAvatarAsync_NullAccount_CompletesSafely()
        {
            var exception = await Record.ExceptionAsync(() =>
                RobloxSettingsApiService.Instance.SetAvatarAsync(null!, "{}"));
            Assert.Null(exception);
        }

        [Fact]
        public async Task CheckPinAsync_VariousStates_EvaluatesExpectedly()
        {
            // 1. Account already unlocked
            var accountUnlocked = new Account
            {
                Username = "UnlockedPinUser",
                PinUnlocked = DateTime.Now.AddMinutes(10)
            };
            bool alreadyUnlocked = await RobloxSettingsApiService.Instance.CheckPinAsync(accountUnlocked);
            Assert.True(alreadyUnlocked);

            // 2. Account with no security token
            var accountNoToken = new Account
            {
                Username = "NoTokenPinUser",
                SecurityToken = "",
                PinUnlocked = DateTime.MinValue
            };
            bool noTokenResult = await RobloxSettingsApiService.Instance.CheckPinAsync(accountNoToken);
            Assert.False(noTokenResult);

            // 3. Network check: Pin disabled
            var handlerDisabled = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, "{\"isEnabled\":false}");
            Account.TestHttpClientFactory = _ => new HttpClient(handlerDisabled);

            var accountPinDisabled = new Account
            {
                Username = "DisabledPinUser",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.COOKIE.S",
                PinUnlocked = DateTime.MinValue
            };
            bool disabledResult = await RobloxSettingsApiService.Instance.CheckPinAsync(accountPinDisabled);
            Assert.True(disabledResult);
            Assert.True(accountPinDisabled.PinUnlocked > DateTime.Now);

            // 4. Network check: Pin enabled but locked (unlockedUntil = 0)
            var handlerLocked = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, "{\"isEnabled\":true,\"unlockedUntil\":0}");
            Account.TestHttpClientFactory = _ => new HttpClient(handlerLocked);

            var accountPinLocked = new Account
            {
                Username = "LockedPinUser",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.COOKIE.S",
                PinUnlocked = DateTime.MinValue
            };
            bool lockedResult = await RobloxSettingsApiService.Instance.CheckPinAsync(accountPinLocked);
            Assert.False(lockedResult);
        }

        [Fact]
        public async Task UnlockPinAsync_ValidationEdgeCases_ReturnExpected()
        {
            // Null account
            bool nullAcc = await RobloxSettingsApiService.Instance.UnlockPinAsync(null!, "1234");
            Assert.False(nullAcc);

            var account = new Account
            {
                Username = "PinTestUser",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.COOKIE.S",
                PinUnlocked = DateTime.MinValue
            };

            // Invalid PIN lengths
            bool len3 = await RobloxSettingsApiService.Instance.UnlockPinAsync(account, "123");
            Assert.False(len3);
            bool len5 = await RobloxSettingsApiService.Instance.UnlockPinAsync(account, "12345");
            Assert.False(len5);

            // Already unlocked returns true without calling network
            account.PinUnlocked = DateTime.Now.AddMinutes(5);
            bool unlockedResult = await RobloxSettingsApiService.Instance.UnlockPinAsync(account, "1234");
            Assert.True(unlockedResult);
        }

        [Fact]
        public void ParseAccessCode_ExtractsCodesFromVariousPayloads()
        {
            // Null or empty
            Assert.False(RobloxSettingsApiService.ParseAccessCode(null!, out _));
            Assert.False(RobloxSettingsApiService.ParseAccessCode(new RestResponse { Content = "" }, out _));

            // Legacy regex
            string legacyBody = "Roblox.GameLauncher.joinPrivateGame(12345, '11111111-2222-3333-4444-555555555555')";
            bool legacyParsed = RobloxSettingsApiService.ParseAccessCode(new RestResponse { Content = legacyBody }, out string legacyCode);
            Assert.True(legacyParsed);
            Assert.Equal("11111111-2222-3333-4444-555555555555", legacyCode);

            // Modern accessCode
            string modernAccess = "{\"accessCode\":\"abcdef12345678901234\"}";
            bool modernAccessParsed = RobloxSettingsApiService.ParseAccessCode(new RestResponse { Content = modernAccess }, out string accessCode);
            Assert.True(modernAccessParsed);
            Assert.Equal("abcdef12345678901234", accessCode);

            // Modern gameInstanceId
            string modernInstance = "{\"gameInstanceId\":\"fedcba09876543210987\"}";
            bool modernInstanceParsed = RobloxSettingsApiService.ParseAccessCode(new RestResponse { Content = modernInstance }, out string instanceCode);
            Assert.True(modernInstanceParsed);
            Assert.Equal("fedcba09876543210987", instanceCode);

            // Invalid text
            bool invalidParsed = RobloxSettingsApiService.ParseAccessCode(new RestResponse { Content = "Hello world" }, out string emptyCode);
            Assert.False(invalidParsed);
            Assert.Equal("", emptyCode);
        }

        [Fact]
        public async Task ChangeEmailAsync_SuccessfulFlow_ReturnsTrue()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "email-csrf-token"))
                .Enqueue(HttpStatusCode.OK, "{}");
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            var account = new Account
            {
                Username = "EmailChangeUser",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.COOKIE.S",
                PinUnlocked = DateTime.Now.AddHours(1)
            };

            bool changed = await RobloxSettingsApiService.Instance.ChangeEmailAsync(account, "CurrentPassword", "new@example.com");
            Assert.True(changed);
        }
    }
}
