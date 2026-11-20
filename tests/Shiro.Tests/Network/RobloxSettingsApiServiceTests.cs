#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NSubstitute;
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
            RobloxAccountSessionClient.TestHttpClientFactory = null;
        }

        public void Dispose()
        {
            RobloxAccountSessionClient.TestHttpClientFactory = null;
        }

        private static Account CreateUnlockedAccount(string name = "TestUser") => new()
        {
            Username = name,
            UserID = 12345,
            SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.COOKIE.S"
        };

        [Fact]
        public async Task SetFollowPrivacy_RetriesOnceWithCsrfToken()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "abc"))
                .Enqueue(HttpStatusCode.OK, "{}");

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = CreateUnlockedAccount("PrivacyUser");

            bool result = await RobloxSettingsApiService.Instance.SetFollowPrivacyAsync(account, 0);

            Assert.True(result);
            Assert.Equal(2, handler.Requests.Count);
            Assert.True(handler.Requests[1].Headers.TryGetValues("X-CSRF-TOKEN", out var csrfValues));
            Assert.Equal("abc", csrfValues.Single());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public async Task SetFollowPrivacy_ModernFails_FallsBackToLegacyAndSucceeds(int privacy)
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "csrf-tok"))
                .Enqueue(HttpStatusCode.BadRequest, "{}")
                .Enqueue(HttpStatusCode.OK, "{}");

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = CreateUnlockedAccount($"PrivacyFallback_{privacy}");

            bool result = await RobloxSettingsApiService.Instance.SetFollowPrivacyAsync(account, privacy);
            Assert.True(result);
            Assert.Equal(3, handler.Requests.Count);
        }

        [Fact]
        public async Task SetFollowPrivacy_BothModernAndFallbackFail_ReturnsFalse()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "csrf-tok"))
                .Enqueue(HttpStatusCode.BadRequest, "{}")
                .Enqueue(HttpStatusCode.InternalServerError, "{}");

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = CreateUnlockedAccount("PrivacyBothFail");

            bool result = await RobloxSettingsApiService.Instance.SetFollowPrivacyAsync(account, 4);
            Assert.False(result);
        }

        [Fact]
        public async Task SetFollowPrivacy_ReturnsFalseOn401()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "test-csrf"))
                .Enqueue(HttpStatusCode.Unauthorized, "{}")
                .Enqueue(HttpStatusCode.Unauthorized, "{}");

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = new Account
            {
                Username = "PrivacyUser401",
                UserID = 12345,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.COOKIE.S"
            };

            bool result = await RobloxSettingsApiService.Instance.SetFollowPrivacyAsync(account, 0);

            Assert.False(result);
            Assert.NotEmpty(handler.Requests);
            Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.ToString().Contains("v1/account/pin"));
            Assert.Contains(handler.Requests, r => r.RequestUri!.ToString().Contains("user-settings"));
        }

        [Fact]
        public async Task SetFollowPrivacy_EmptySecurityToken_ReturnsFalseWithoutHttpRequest()
        {
            var handler = new FakeHttpHandler();
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = new Account { Username = "NoTokenPrivacyUser", SecurityToken = "" };

            bool result = await RobloxSettingsApiService.Instance.SetFollowPrivacyAsync(account, 0);

            Assert.False(result);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task LogOutOfOtherSessions_EmptySecurityToken_ReturnsFalseWithoutHttpRequest()
        {
            var handler = new FakeHttpHandler();
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = new Account { Username = "NoTokenLogoutUser", SecurityToken = "" };

            bool result = await RobloxSettingsApiService.Instance.LogOutOfOtherSessionsAsync(account);

            Assert.False(result);
            Assert.Empty(handler.Requests);
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
        public async Task ChangePasswordAsync_SuccessAndFailurePaths()
        {
            Assert.False(await RobloxSettingsApiService.Instance.ChangePasswordAsync(null!, "old", "new"));

            // HTTP 200 without rotated cookie must fail closed (HIGH-F4) and preserve original password
            var okHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "pw-csrf"))
                .Enqueue(HttpStatusCode.OK, "{}");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(okHandler);
            var acc1 = CreateUnlockedAccount("PwUser1");
            acc1.Password = "old";
            Assert.False(await RobloxSettingsApiService.Instance.ChangePasswordAsync(acc1, "old", "newPass1"));
            Assert.Equal("old", acc1.Password);

            // Success with rotated cookie
            string validRotatedCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_" + new string('A', 120);
            var rotHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "pw-csrf"))
                .Enqueue(HttpStatusCode.OK, "{}", ("Set-Cookie", $".ROBLOSECURITY={validRotatedCookie}; path=/; domain=.roblox.com; HttpOnly"));
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(rotHandler);
            var acc2 = CreateUnlockedAccount("PwUser2");
            Assert.True(await RobloxSettingsApiService.Instance.ChangePasswordAsync(acc2, "old", "newPass2"));
            Assert.Equal("newPass2", acc2.Password);

            // Failure status code
            var failHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "pw-csrf"))
                .Enqueue(HttpStatusCode.BadRequest, "{}");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(failHandler);
            var acc3 = CreateUnlockedAccount("PwUser3");
            Assert.False(await RobloxSettingsApiService.Instance.ChangePasswordAsync(acc3, "old", "newPass3"));
        }

        [Fact]
        public async Task ChangeEmailAsync_SuccessAndFailurePaths()
        {
            Assert.False(await RobloxSettingsApiService.Instance.ChangeEmailAsync(null!, "pass", "email@example.com"));

            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "email-csrf-token"))
                .Enqueue(HttpStatusCode.OK, "{}");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = CreateUnlockedAccount("EmailChangeUser");
            Assert.True(await RobloxSettingsApiService.Instance.ChangeEmailAsync(account, "CurrentPassword", "new@example.com"));

            var failHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "email-csrf-token"))
                .Enqueue(HttpStatusCode.BadRequest, "{}");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(failHandler);
            var failAccount = CreateUnlockedAccount("EmailFailUser");
            Assert.False(await RobloxSettingsApiService.Instance.ChangeEmailAsync(failAccount, "CurrentPassword", "new@example.com"));
        }

        [Fact]
        public async Task LogOutOfOtherSessionsAsync_PrimaryFallbackAndRotationPaths()
        {
            Assert.False(await RobloxSettingsApiService.Instance.LogOutOfOtherSessionsAsync(null!));

            // 1. Primary OK with rotated cookie -> true
            string validRotatedCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_" + new string('A', 120);
            var rotHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "lo-csrf"))
                .Enqueue(HttpStatusCode.OK, "{}", ("Set-Cookie", $".ROBLOSECURITY={validRotatedCookie}; path=/; domain=.roblox.com"));
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(rotHandler);
            var acc1 = CreateUnlockedAccount("LogoutUser1");
            Assert.True(await RobloxSettingsApiService.Instance.LogOutOfOtherSessionsAsync(acc1));

            // 2. Primary fails, fallback OK without rotated cookie -> false
            var fbNoRotHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "lo-csrf"))
                .Enqueue(HttpStatusCode.InternalServerError, "{}")
                .Enqueue(HttpStatusCode.OK, "{}");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(fbNoRotHandler);
            var acc2 = CreateUnlockedAccount("LogoutUser2");
            Assert.False(await RobloxSettingsApiService.Instance.LogOutOfOtherSessionsAsync(acc2));

            // 3. Both primary and fallback fail -> false
            var bothFailHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "lo-csrf"))
                .Enqueue(HttpStatusCode.InternalServerError, "{}")
                .Enqueue(HttpStatusCode.BadRequest, "{}");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(bothFailHandler);
            var acc3 = CreateUnlockedAccount("LogoutUser3");
            Assert.False(await RobloxSettingsApiService.Instance.LogOutOfOtherSessionsAsync(acc3));
        }

        [Fact]
        public async Task SetServerAsync_HandlesSuccessJoinScriptNullAndError()
        {
            var (nullOk, nullMsg) = await RobloxSettingsApiService.Instance.SetServerAsync(null!, 12345, "job-id");
            Assert.False(nullOk);
            Assert.Equal("Account is null", nullMsg);

            // Success normal
            var okHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "srv-csrf"))
                .Enqueue(HttpStatusCode.OK, "{\"joinScriptUrl\":\"https://gamejoin.roblox.com/join\"}");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(okHandler);
            var acc1 = CreateUnlockedAccount("SrvUser1");
            var (s1, m1) = await RobloxSettingsApiService.Instance.SetServerAsync(acc1, 1818, "job-1");
            Assert.True(s1);
            Assert.Equal("Success", m1);

            // Success with joinScriptUrl:null
            var nullScriptHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "srv-csrf"))
                .Enqueue(HttpStatusCode.OK, "{\"joinScriptUrl\":null,\"status\":12}");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(nullScriptHandler);
            var acc2 = CreateUnlockedAccount("SrvUser2");
            var (s2, m2) = await RobloxSettingsApiService.Instance.SetServerAsync(acc2, 1818, "job-2");
            Assert.True(s2);
            Assert.Contains("joinScriptUrl", m2);

            // Error response
            var failHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "srv-csrf"))
                .Enqueue(HttpStatusCode.BadRequest, "bad request");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(failHandler);
            var acc3 = CreateUnlockedAccount("SrvUser3");
            var (s3, m3) = await RobloxSettingsApiService.Instance.SetServerAsync(acc3, 1818, "job-3");
            Assert.False(s3);
            Assert.Contains("Failed", m3);
        }

        [Fact]
        public async Task SetDisplayNameAsync_HandlesSuccessAndErrors()
        {
            await RobloxSettingsApiService.Instance.SetDisplayNameAsync(null!, "NewName");

            var okHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "dp-csrf"))
                .Enqueue(HttpStatusCode.OK, "{}");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(okHandler);
            var acc1 = CreateUnlockedAccount("DpUser1");
            await RobloxSettingsApiService.Instance.SetDisplayNameAsync(acc1, "CoolName");
            Assert.Equal(2, okHandler.Requests.Count);

            var errJsonHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "dp-csrf"))
                .Enqueue(HttpStatusCode.BadRequest, "{\"errors\":[{\"message\":\"Display name is too short\"}]}");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(errJsonHandler);
            var acc2 = CreateUnlockedAccount("DpUser2");
            var ex = await Assert.ThrowsAsync<Exception>(() => RobloxSettingsApiService.Instance.SetDisplayNameAsync(acc2, "A"));
            Assert.Equal("Display name is too short", ex.Message);

            var badJsonHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "dp-csrf"))
                .Enqueue(HttpStatusCode.BadRequest, "not-json");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(badJsonHandler);
            var acc3 = CreateUnlockedAccount("DpUser3");
            var ex2 = await Assert.ThrowsAsync<Exception>(() => RobloxSettingsApiService.Instance.SetDisplayNameAsync(acc3, "B"));
            Assert.Contains("Something went wrong", ex2.Message);
        }

        [Fact]
        public async Task SetAvatarAsync_AppliesAttributesAndHandlesMissingAssets()
        {
            Assert.False(await RobloxSettingsApiService.Instance.SetAvatarAsync(null!, "{}"));
            var acc = CreateUnlockedAccount("AvatarUser");
            Assert.False(await RobloxSettingsApiService.Instance.SetAvatarAsync(acc, ""));
            Assert.False(await RobloxSettingsApiService.Instance.SetAvatarAsync(acc, "not-valid-json"));
            Assert.False(await RobloxSettingsApiService.Instance.SetAvatarAsync(acc, "[1, 2, 3]"));
            Assert.False(await RobloxSettingsApiService.Instance.SetAvatarAsync(acc, "{}"));

            var fakeDialogs = Substitute.For<IDialogService>();
            using var _ = AppServices.Override<IDialogService>(fakeDialogs);

            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "av-csrf"))
                .Enqueue(HttpStatusCode.OK, "{\"validation\":{\"invalidAssets\":[{\"id\":111},{\"id\":222}]}}");

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            string avatarPayload = @"{
                ""playerAvatarType"": ""R15"",
                ""scales"": { ""height"": 1.0 },
                ""bodyColors"": { ""headColorId"": 1 },
                ""assets"": [ { ""id"": 111 }, { ""id"": 222 } ]
            }";

            Assert.True(await RobloxSettingsApiService.Instance.SetAvatarAsync(acc, avatarPayload));

            Assert.Equal(2, handler.Requests.Count);
            var patchRequest = handler.Requests[1];
            Assert.Equal("PATCH", patchRequest.Method.Method);
            Assert.Contains("v4/avatar", patchRequest.RequestUri?.ToString());
            Assert.True(patchRequest.Headers.TryGetValues("X-CSRF-TOKEN", out var csrfVals));
            Assert.Equal("av-csrf", csrfVals.FirstOrDefault());

            string? body = handler.RequestBodies.Count > 1 ? handler.RequestBodies[1] : null;
            Assert.NotNull(body);
            Assert.Contains("\"updateTypes\":[\"UpdateAvatarType\",\"UpdateScales\",\"UpdateBodyColors\",\"UpdateAssets\"]", body);
            Assert.Contains("\"playerAvatarType\":\"R15\"", body);
            Assert.Contains("\"headColor\":\"#F2F3F3\"", body);

            fakeDialogs.Received(1).OpenMissingAssetsDialog(acc, Arg.Is<long[]>(arr => arr.SequenceEqual(new long[] { 111, 222 })));
        }

        [Fact]
        public async Task SetAvatarAsync_403ForbiddenWithRefreshedCsrfToken_RetriesPatchOnce()
        {
            var acc = CreateUnlockedAccount("RetryCsrfAvatarUser");
            var fakeDialogs = Substitute.For<IDialogService>();
            using var _ = AppServices.Override<IDialogService>(fakeDialogs);

            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "csrf-1"))
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "csrf-2"))
                .Enqueue(HttpStatusCode.OK, "{\"invalidAssets\":[{\"id\":555}]}");

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            string avatarPayload = "{\"playerAvatarType\": \"R6\", \"assets\": [{\"id\": 555}]}";

            await RobloxSettingsApiService.Instance.SetAvatarAsync(acc, avatarPayload);

            Assert.Equal(3, handler.Requests.Count);
            var retryReq = handler.Requests[2];
            Assert.Equal("PATCH", retryReq.Method.Method);
            Assert.True(retryReq.Headers.TryGetValues("X-CSRF-TOKEN", out var retryCsrfVals));
            Assert.Equal("csrf-2", retryCsrfVals.FirstOrDefault());

            fakeDialogs.Received(1).OpenMissingAssetsDialog(acc, Arg.Is<long[]>(arr => arr.SequenceEqual(new long[] { 555 })));
        }

        [Fact]
        public async Task SetAvatarAsync_Http400WithAndWithoutInvalidAssets()
        {
            var acc = CreateUnlockedAccount("Http400AvatarUser");
            var fakeDialogs = Substitute.For<IDialogService>();
            using var _ = AppServices.Override<IDialogService>(fakeDialogs);

            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "csrf-1"))
                .Enqueue(HttpStatusCode.BadRequest, "{\"invalidAssetIds\":[777]}")
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "csrf-1"))
                .Enqueue(HttpStatusCode.BadRequest, "{\"errors\":[{\"message\":\"Unknown error\"}]}");

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            string avatarPayload = "{\"playerAvatarType\": \"R15\"}";

            await RobloxSettingsApiService.Instance.SetAvatarAsync(acc, avatarPayload);
            fakeDialogs.Received(1).OpenMissingAssetsDialog(acc, Arg.Is<long[]>(arr => arr.SequenceEqual(new long[] { 777 })));

            await RobloxSettingsApiService.Instance.SetAvatarAsync(acc, avatarPayload);
            fakeDialogs.Received(1).OpenMissingAssetsDialog(acc, Arg.Any<long[]>());
            Assert.Equal(4, handler.Requests.Count);
        }

        [Fact]
        public async Task SetAvatarAsync_403WithoutRefreshedCsrfOrNullResponse_HandledSafely()
        {
            var acc = CreateUnlockedAccount("NoRetryAvatarUser");
            var fakeDialogs = Substitute.For<IDialogService>();
            using var _ = AppServices.Override<IDialogService>(fakeDialogs);

            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "csrf-1"))
                .Enqueue(HttpStatusCode.Forbidden, "{}");

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            string avatarPayload = "{\"playerAvatarType\": \"R6\"}";

            Assert.False(await RobloxSettingsApiService.Instance.SetAvatarAsync(acc, avatarPayload));
            Assert.Equal(2, handler.Requests.Count);
            fakeDialogs.DidNotReceiveWithAnyArgs().OpenMissingAssetsDialog(default!, default!);

            var nullHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "csrf-1"))
                .Enqueue((HttpRequestMessage _) => (HttpResponseMessage)null!);
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(nullHandler);

            Assert.False(await RobloxSettingsApiService.Instance.SetAvatarAsync(acc, avatarPayload));
            Assert.Equal(2, nullHandler.Requests.Count);
        }

        [Fact]
        public async Task SettingsEndpoints_NegativeSessionAndCsrfBranches_HandledCleanly()
        {
            var customSvc = new RobloxSettingsApiService(AppServices.Logger, AppServices.GeneralSettings);
            Assert.NotNull(customSvc);

            var emptyAcc = new Account { Username = "EmptyTokenUser", SecurityToken = "" };
            Assert.False(await RobloxSettingsApiService.Instance.ChangePasswordAsync(emptyAcc, "old", "new"));
            Assert.False(await RobloxSettingsApiService.Instance.ChangeEmailAsync(emptyAcc, "old", "new@a.com"));
            await RobloxSettingsApiService.Instance.SetDisplayNameAsync(emptyAcc, "name");

            var failCsrfHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Unauthorized, "{}")
                .Enqueue(HttpStatusCode.Unauthorized, "{}");
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(failCsrfHandler);

            var acc = CreateUnlockedAccount("NoCsrfUser");
            Assert.False(await RobloxSettingsApiService.Instance.SetFollowPrivacyAsync(acc, 0));
            Assert.False(await RobloxSettingsApiService.Instance.ChangePasswordAsync(acc, "old", "new"));
            Assert.False(await RobloxSettingsApiService.Instance.ChangeEmailAsync(acc, "old", "new@a.com"));
            Assert.False(await RobloxSettingsApiService.Instance.LogOutOfOtherSessionsAsync(acc));
            Assert.False((await RobloxSettingsApiService.Instance.SetServerAsync(acc, 1818, "job")).Successful);
            await Assert.ThrowsAsync<InvalidOperationException>(() => RobloxSettingsApiService.Instance.SetDisplayNameAsync(acc, "name"));
            Assert.False(await RobloxSettingsApiService.Instance.SetAvatarAsync(acc, "{\"playerAvatarType\":\"R15\"}"));
        }
    }
}
