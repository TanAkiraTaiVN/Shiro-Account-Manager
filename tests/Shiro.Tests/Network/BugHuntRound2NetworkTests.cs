#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests.Network
{
    [Collection("StaticSeams")]
    public sealed class BugHuntRound2NetworkTests : IDisposable
    {
        private sealed class DelegateHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

            public DelegateHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                _responder(request, cancellationToken);
        }

        private readonly Func<HttpClient>? _origGameInfoClientFactory;
        private readonly Func<Account, HttpClient>? _origAccountClientFactory;

        public BugHuntRound2NetworkTests()
        {
            _origGameInfoClientFactory = GameInfoService.HttpClientFactoryForTests;
            _origAccountClientFactory = Account.TestHttpClientFactory;
        }

        public void Dispose()
        {
            GameInfoService.HttpClientFactoryForTests = _origGameInfoClientFactory;
            Account.TestHttpClientFactory = _origAccountClientFactory;
        }

        private static Account CreateAccountWithCachedCsrf(string username, string csrfToken) => new()
        {
            Username = username,
            UserID = 12345,
            SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.ROUND2COOKIE.S",
            CSRFToken = csrfToken,
            TokenSet = DateTime.Now,
            Valid = true
        };

        [Fact]
        public async Task GameInfoService_ResolvePlaceNameAsync_CancelledCallerDoesNotLeakCompletedNullQuery()
        {
            const long placeId = 9988001122L;
            var enteredFirstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int phase = 0;

            GameInfoService.HttpClientFactoryForTests = () => new HttpClient(new DelegateHttpHandler(async (req, _) =>
            {
                if (Volatile.Read(ref phase) == 0)
                {
                    enteredFirstCall.TrySetResult();
                    await releaseFirstCall.Task.ConfigureAwait(false);
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
                }

                string url = req.RequestUri?.ToString() ?? string.Empty;
                if (url.Contains("/universes/v1/places/", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"universeId\":778899}")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[{\"id\":778899,\"name\":\"Recovered Place Name\"}]}")
                };
            }));

            using var cts = new CancellationTokenSource();
            Task<string?> firstCall = GameInfoService.ResolvePlaceNameAsync(placeId, cts.Token);
            await enteredFirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstCall);

            Interlocked.Exchange(ref phase, 1);
            releaseFirstCall.TrySetResult();
            await Task.Delay(80);

            string? recovered = await GameInfoService.ResolvePlaceNameAsync(placeId, CancellationToken.None);
            Assert.Equal("Recovered Place Name", recovered);
        }

        [Fact]
        public async Task RobloxSettingsApiService_MutatingEndpoints_RetryOnceOn403CsrfChallenge()
        {
            using var suppressSave = Account.BeginSuppressDeferredSaveScope();
            const string rotatedCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H." + "ROTATED_ROUND2_COOKIE";
            string fullRotated = rotatedCookie + new string('X', 100);

            // 1. SetFollowPrivacyAsync (403 -> retry -> 200)
            var followAcc = CreateAccountWithCachedCsrf("FollowRetryUser", "stale-1");
            int followCalls = 0;
            Account.TestHttpClientFactory = _ => new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                followCalls++;
                string csrf = req.Headers.TryGetValues("X-CSRF-TOKEN", out var vals) ? vals.FirstOrDefault() ?? "" : "";
                if (csrf == "stale-1")
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
                    resp.Headers.TryAddWithoutValidation("x-csrf-token", "fresh-follow");
                    return Task.FromResult(resp);
                }
                Assert.Equal("fresh-follow", csrf);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));
            Assert.True(await RobloxSettingsApiService.Instance.SetFollowPrivacyAsync(followAcc, 0));
            Assert.Equal(2, followCalls);
            Assert.Equal("fresh-follow", followAcc.CSRFToken);

            // 1b. SetFollowPrivacyAsync fallback path with out-of-range privacy and 403 challenge
            var followFbAcc = CreateAccountWithCachedCsrf("FollowFbRetryUser", "stale-fb");
            int followFbCalls = 0;
            Account.TestHttpClientFactory = _ => new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                followFbCalls++;
                string url = req.RequestUri?.ToString() ?? string.Empty;
                if (url.Contains("user-settings", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") });

                string csrf = req.Headers.TryGetValues("X-CSRF-TOKEN", out var vals) ? vals.FirstOrDefault() ?? "" : "";
                if (csrf == "stale-fb")
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
                    resp.Headers.TryAddWithoutValidation("x-csrf-token", "fresh-fb");
                    return Task.FromResult(resp);
                }
                Assert.Equal("fresh-fb", csrf);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));
            Assert.True(await RobloxSettingsApiService.Instance.SetFollowPrivacyAsync(followFbAcc, 99));
            Assert.Equal(3, followFbCalls);

            // 2. ChangePasswordAsync (403 -> retry -> 200 with rotated cookie)
            var pwAcc = CreateAccountWithCachedCsrf("PwRetryUser", "stale-pw");
            int pwCalls = 0;
            Account.TestHttpClientFactory = _ => new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                pwCalls++;
                string csrf = req.Headers.TryGetValues("X-CSRF-TOKEN", out var vals) ? vals.FirstOrDefault() ?? "" : "";
                if (csrf == "stale-pw")
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
                    resp.Headers.TryAddWithoutValidation("x-csrf-token", "fresh-pw");
                    return Task.FromResult(resp);
                }
                Assert.Equal("fresh-pw", csrf);
                var okResp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                okResp.Headers.TryAddWithoutValidation("Set-Cookie", $".ROBLOSECURITY={fullRotated}; path=/; domain=.roblox.com; HttpOnly");
                return Task.FromResult(okResp);
            }));
            Assert.True(await RobloxSettingsApiService.Instance.ChangePasswordAsync(pwAcc, "old", "new"));
            Assert.Equal(2, pwCalls);

            // 3. ChangeEmailAsync
            var emailAcc = CreateAccountWithCachedCsrf("EmailRetryUser", "stale-em");
            int emailCalls = 0;
            Account.TestHttpClientFactory = _ => new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                emailCalls++;
                string csrf = req.Headers.TryGetValues("X-CSRF-TOKEN", out var vals) ? vals.FirstOrDefault() ?? "" : "";
                if (csrf == "stale-em")
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
                    resp.Headers.TryAddWithoutValidation("x-csrf-token", "fresh-em");
                    return Task.FromResult(resp);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));
            Assert.True(await RobloxSettingsApiService.Instance.ChangeEmailAsync(emailAcc, "pw", "a@b.com"));
            Assert.Equal(2, emailCalls);

            // 4. LogOutOfOtherSessionsAsync
            var logoutAcc = CreateAccountWithCachedCsrf("LogoutRetryUser", "stale-lo");
            int logoutCalls = 0;
            Account.TestHttpClientFactory = _ => new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                logoutCalls++;
                string csrf = req.Headers.TryGetValues("X-CSRF-TOKEN", out var vals) ? vals.FirstOrDefault() ?? "" : "";
                if (csrf == "stale-lo")
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
                    resp.Headers.TryAddWithoutValidation("x-csrf-token", "fresh-lo");
                    return Task.FromResult(resp);
                }
                var okResp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                okResp.Headers.TryAddWithoutValidation("Set-Cookie", $".ROBLOSECURITY={fullRotated}; path=/; domain=.roblox.com");
                return Task.FromResult(okResp);
            }));
            Assert.True(await RobloxSettingsApiService.Instance.LogOutOfOtherSessionsAsync(logoutAcc));
            Assert.Equal(2, logoutCalls);

            // 5. SetServerAsync
            var srvAcc = CreateAccountWithCachedCsrf("SrvRetryUser", "stale-srv");
            int srvCalls = 0;
            Account.TestHttpClientFactory = _ => new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                srvCalls++;
                string csrf = req.Headers.TryGetValues("X-CSRF-TOKEN", out var vals) ? vals.FirstOrDefault() ?? "" : "";
                if (csrf == "stale-srv")
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
                    resp.Headers.TryAddWithoutValidation("x-csrf-token", "fresh-srv");
                    return Task.FromResult(resp);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"joinScriptUrl\":\"https://gamejoin.roblox.com/join\"}")
                });
            }));
            var (srvOk, _) = await RobloxSettingsApiService.Instance.SetServerAsync(srvAcc, 1818, "job-retry");
            Assert.True(srvOk);
            Assert.Equal(2, srvCalls);

            // 6. SetDisplayNameAsync
            var dpAcc = CreateAccountWithCachedCsrf("DpRetryUser", "stale-dp");
            int dpCalls = 0;
            Account.TestHttpClientFactory = _ => new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                dpCalls++;
                string csrf = req.Headers.TryGetValues("X-CSRF-TOKEN", out var vals) ? vals.FirstOrDefault() ?? "" : "";
                if (csrf == "stale-dp")
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
                    resp.Headers.TryAddWithoutValidation("x-csrf-token", "fresh-dp");
                    return Task.FromResult(resp);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));
            await RobloxSettingsApiService.Instance.SetDisplayNameAsync(dpAcc, "CoolRetry");
            Assert.Equal(2, dpCalls);
        }

        [Fact]
        public async Task RobloxSocialApiService_MutatingEndpoints_RetryOnceOn403CsrfChallenge()
        {
            using var suppressSave = Account.BeginSuppressDeferredSaveScope();

            // 1. BlockUserIdAsync
            var blockAcc = CreateAccountWithCachedCsrf("BlockRetryUser", "stale-blk");
            int blockCalls = 0;
            Account.TestHttpClientFactory = _ => new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                blockCalls++;
                string csrf = req.Headers.TryGetValues("X-CSRF-TOKEN", out var vals) ? vals.FirstOrDefault() ?? "" : "";
                if (csrf == "stale-blk")
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
                    resp.Headers.TryAddWithoutValidation("x-csrf-token", "fresh-blk");
                    return Task.FromResult(resp);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));
            var blockResp = await RobloxSocialApiService.Instance.BlockUserIdAsync(blockAcc, "555", skipPinCheck: true);
            Assert.NotNull(blockResp);
            Assert.True(blockResp!.IsSuccessful);
            Assert.Equal(2, blockCalls);

            // 2. SendFriendRequestAsync
            var friendAcc = CreateAccountWithCachedCsrf("FriendRetryUser", "stale-fr");
            int friendCalls = 0;
            Account.TestHttpClientFactory = _ => new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                string url = req.RequestUri?.ToString() ?? string.Empty;
                if (url.Contains("usernames/users", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":[{\"requestedUsername\":\"TargetFriend\",\"id\":999}]}")
                    });
                }

                friendCalls++;
                string csrf = req.Headers.TryGetValues("X-CSRF-TOKEN", out var vals) ? vals.FirstOrDefault() ?? "" : "";
                if (csrf == "stale-fr")
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
                    resp.Headers.TryAddWithoutValidation("x-csrf-token", "fresh-fr");
                    return Task.FromResult(resp);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));
            Assert.True(await RobloxSocialApiService.Instance.SendFriendRequestAsync(friendAcc, "TargetFriend"));
            Assert.Equal(2, friendCalls);

            // 3. QuickLogInAsync (both enterCode and validateCode retry on 403)
            var fakeDialogs = Substitute.For<IDialogService>();
            fakeDialogs.ShowConfirmation(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
            using var dialogScope = AppServices.Override<IDialogService>(fakeDialogs);

            var qliAcc = CreateAccountWithCachedCsrf("QuickLoginRetryUser", "stale-qli");
            int qliPostCalls = 0;
            Account.TestHttpClientFactory = _ => new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                qliPostCalls++;
                string csrf = req.Headers.TryGetValues("X-CSRF-TOKEN", out var vals) ? vals.FirstOrDefault() ?? "" : "";
                if (csrf == "stale-qli")
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
                    resp.Headers.TryAddWithoutValidation("x-csrf-token", "fresh-qli-1");
                    return Task.FromResult(resp);
                }
                if (req.RequestUri?.AbsolutePath.Contains("enter", StringComparison.OrdinalIgnoreCase) == true && csrf == "fresh-qli-1")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"deviceInfo\":\"PC\",\"location\":\"VN\"}")
                    });
                }
                if (req.RequestUri?.AbsolutePath.Contains("validate", StringComparison.OrdinalIgnoreCase) == true && csrf == "fresh-qli-1")
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
                    resp.Headers.TryAddWithoutValidation("x-csrf-token", "fresh-qli-2");
                    return Task.FromResult(resp);
                }
                Assert.Equal("fresh-qli-2", csrf);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));
            Assert.True(await RobloxSocialApiService.Instance.QuickLogInAsync(qliAcc, "ABC123"));
            Assert.Equal(4, qliPostCalls);
        }
    }
}
