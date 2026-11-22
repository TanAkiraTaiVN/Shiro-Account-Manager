#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P0AuthAndDomainRegressionTests : IDisposable
    {
        private const string WrongUserAccountJson = "{\"UserId\":90002,\"Name\":\"ActualWrongUserB\",\"DisplayName\":\"WrongB\"}";
        private const string WrongUserAuthResponseJson = "{\"id\":90002,\"name\":\"ActualWrongUserB\",\"displayName\":\"WrongB\"}";
        private const string MatchedUserAccountJson = "{\"UserId\":90001,\"Name\":\"ExpectedUserA\",\"DisplayName\":\"ExpectedA\"}";
        private const string EmptyJsonObject = "{}";

        private readonly Account? _previousLastValidAccount;
        private readonly Func<Account, HttpClient>? _previousHttpClientFactory;

        public P0AuthAndDomainRegressionTests()
        {
            _previousLastValidAccount = AppServices.SelectionState.LastValidAccount;
            _previousHttpClientFactory = RobloxAccountSessionClient.TestHttpClientFactory;
            RobloxHbaService.ClearKeyCache();
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
        }

        public void Dispose()
        {
            RobloxAccountSessionClient.TestHttpClientFactory = _previousHttpClientFactory;
            AppServices.SelectionState.LastValidAccount = _previousLastValidAccount;
            RobloxHbaService.ClearKeyCache();
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
        }

        private static string CreateValidPkcs8Key()
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return Convert.ToBase64String(ec.ExportPkcs8PrivateKey());
        }

        private static string MakeToken(char fill, int payloadLen)
        {
            return "_|WARNING:-DO-NOT-SHARE-THIS.--|_HDR." + new string(fill, payloadLen) + ".SIG";
        }

        [Fact]
        public void FastLoginScripts_RetryPolling_KeepsCookieEligibleAndPreservesPendingFp()
        {
            string initialCookie = MakeToken('A', 620);
            string upgradedCookie = MakeToken('B', 920);
            string fpInitial = FastLoginCoordinator.FingerprintCookie(initialCookie);
            string fpUpgraded = FastLoginCoordinator.FingerprintCookie(upgradedCookie);
            var ignoredCookies = new HashSet<string>(StringComparer.Ordinal);

            FastLoginScripts.ResolveSettledCookieResult settled = FastLoginScripts.ResolveSettledCookieCandidates(
                initialCookie,
                new[] { initialCookie, upgradedCookie },
                ignoredCookies);

            Assert.Equal(upgradedCookie, settled.CookieValue);
            Assert.Contains(fpInitial, settled.CandidateFingerprints);
            Assert.Contains(fpUpgraded, settled.CandidateFingerprints);
            Assert.DoesNotContain(fpInitial, ignoredCookies);
            Assert.DoesNotContain(fpUpgraded, ignoredCookies);

            string? pendingCapturedFp = settled.PendingCapturedFp;
            Assert.Equal(fpInitial, pendingCapturedFp);

            ignoredCookies.Add(fpInitial);
            bool keptForRetry = FastLoginScripts.ApplySettlementOutcome(
                FastLoginCaptureStatus.RetryPolling,
                ignoredCookies,
                ref pendingCapturedFp,
                fpInitial,
                fpUpgraded);

            Assert.True(keptForRetry);
            Assert.DoesNotContain(fpInitial, ignoredCookies);
            Assert.DoesNotContain(fpUpgraded, ignoredCookies);
            Assert.Equal(fpInitial, pendingCapturedFp);
            Assert.False(FastLoginScripts.ShouldDeleteIgnoredCookie(
                IgnoredCookieAction.DeleteCookie,
                fpInitial,
                pendingCapturedFp));

            bool committedAfterTerminalOutcome = !FastLoginScripts.ApplySettlementOutcome(
                FastLoginCaptureStatus.UsernameMismatch,
                ignoredCookies,
                ref pendingCapturedFp,
                fpInitial,
                fpUpgraded);

            Assert.True(committedAfterTerminalOutcome);
            Assert.Null(pendingCapturedFp);
            Assert.Contains(fpInitial, ignoredCookies);
            Assert.Contains(fpUpgraded, ignoredCookies);
            Assert.True(FastLoginScripts.ShouldDeleteIgnoredCookie(
                IgnoredCookieAction.DeleteCookie,
                fpInitial,
                pendingCapturedFp));
        }

        [Fact]
        public async Task AccountCreateAsync_WhenExpectedUsernameMismatches_PreservesPreviousLastValidAccount()
        {
            var previousValid = new Account { Username = "PreviousValidUser", UserID = 10001, Valid = true };
            AppServices.SelectionState.LastValidAccount = previousValid;

            string cookie = MakeToken('C', 920);
            Account mismatchedFromJson = await Account.CreateAsync(
                cookie,
                WrongUserAccountJson,
                CancellationToken.None,
                hbaCapture: null,
                expectedUsername: "ExpectedUserA");

            Assert.True(mismatchedFromJson.Valid);
            Assert.Equal("ActualWrongUserB", mismatchedFromJson.Username);
            Assert.Same(previousValid, AppServices.SelectionState.LastValidAccount);

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(req =>
            {
                string url = req.RequestUri?.ToString() ?? string.Empty;
                if (url.Contains("users/authenticated", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(WrongUserAuthResponseJson)
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(EmptyJsonObject) };
            }));

            Account mismatchedFromHttp = await Account.CreateAsync(
                cookie,
                null,
                CancellationToken.None,
                hbaCapture: null,
                expectedUsername: "ExpectedUserA");

            Assert.True(mismatchedFromHttp.Valid);
            Assert.Equal("ActualWrongUserB", mismatchedFromHttp.Username);
            Assert.Same(previousValid, AppServices.SelectionState.LastValidAccount);

            Account matchedFromJson = await Account.CreateAsync(
                cookie,
                MatchedUserAccountJson,
                CancellationToken.None,
                hbaCapture: null,
                expectedUsername: "expectedusera");

            Assert.True(matchedFromJson.Valid);
            Assert.Same(matchedFromJson, AppServices.SelectionState.LastValidAccount);
        }

        [Fact]
        public async Task FastLoginCoordinator_ResolveAndCommit_UsernameMismatch_PreservesLastValidAccount()
        {
            var previousValid = new Account { Username = "PreviousValidUser", UserID = 10002, Valid = true };
            AppServices.SelectionState.LastValidAccount = previousValid;

            var repo = Substitute.For<IAccountRepository>();
            string cookie = MakeToken('D', 920);

            FastLoginCaptureResult result = await FastLoginCoordinator.ResolveAndCommitCapturedSessionAsync(
                cookieValue: cookie,
                expectedUsername: "ExpectedUserA",
                password: "Password123",
                alias: "Alias",
                group: "Group",
                accountJson: WrongUserAccountJson,
                hbaCapture: null,
                queueCount: 1,
                currentQueueAccount: new Account { Username = "ExpectedUserA" },
                detectDomIssueAsync: null,
                repository: repo,
                createAccountAsync: null);

            Assert.Equal(FastLoginCaptureStatus.UsernameMismatch, result.Status);
            Assert.Same(previousValid, AppServices.SelectionState.LastValidAccount);
            repo.DidNotReceiveWithAnyArgs().RegisterValidated(default!, default!, default!, default);
        }

        [Fact]
        public void Account_IsProtectedInternalField_ProtectsAccountStatusIssue()
        {
            Assert.True(Account.IsProtectedInternalField("AccountStatusIssue"));
            Assert.True(Account.IsProtectedInternalField("  accountstatusissue  "));

            var account = new Account
            {
                Username = "ModeratedUser",
                UserID = 5050,
                Valid = false,
                AccountStatusIssue = "Banned 7 Days"
            };

            Assert.Equal("Banned 7 Days", account.AccountStatusIssue);
            Assert.Equal(string.Empty, account.GetField("AccountStatusIssue"));

            account.SetField("AccountStatusIssue", "Tampered");
            Assert.Equal("Banned 7 Days", account.AccountStatusIssue);

            account.RemoveField("AccountStatusIssue");
            Assert.Equal("Banned 7 Days", account.AccountStatusIssue);

            Assert.DoesNotContain("AccountStatusIssue", account.GetPublicFieldsSnapshot().Keys);
            Assert.Equal("Banned 7 Days", account.GetSerializedFieldsSnapshot()["AccountStatusIssue"]);
        }

        [Fact]
        public void Account_ClearSensitiveMemory_ClearsAndZerosOnlyOwnHbaCachedKey()
        {
            string keyA = CreateValidPkcs8Key();
            string keyB = CreateValidPkcs8Key();
            Assert.NotEqual(keyA, keyB);

            var accA = new Account
            {
                Username = "UserA",
                UserID = 111,
                SecurityToken = MakeToken('E', 920),
                HbaPrivateKey = keyA,
                AccountStatusIssue = "Warning"
            };
            var accB = new Account
            {
                Username = "UserB",
                UserID = 222,
                SecurityToken = MakeToken('F', 920),
                HbaPrivateKey = keyB
            };

            Assert.True(RobloxHbaService.TryBuildBoundAuthToken(
                keyA, "https://auth.roblox.com/v1/authentication-ticket/", "POST", EmptyJsonObject, null, 1700000000L, out _));
            Assert.True(RobloxHbaService.TryBuildBoundAuthToken(
                keyB, "https://auth.roblox.com/v1/authentication-ticket/", "POST", EmptyJsonObject, null, 1700000000L, out _));

            FieldInfo? cacheField = typeof(RobloxHbaService).GetField("CachedEcParameters", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(cacheField);
            var cache = (ConcurrentDictionary<string, ECParameters>)cacheField!.GetValue(null)!;

            string cacheIdA = RobloxHbaService.ComputeKeyCacheId(keyA);
            string cacheIdB = RobloxHbaService.ComputeKeyCacheId(keyB);
            Assert.False(cache.ContainsKey(keyA));
            Assert.False(cache.ContainsKey(keyB));
            Assert.True(cache.TryGetValue(cacheIdA, out ECParameters cachedA));
            Assert.True(cache.TryGetValue(cacheIdB, out ECParameters cachedB));
            byte[] scalarRefA = cachedA.D!;
            byte[] scalarRefB = cachedB.D!;
            Assert.Contains(scalarRefA, b => b != 0);
            Assert.Contains(scalarRefB, b => b != 0);

            accA.ClearSensitiveMemory();

            Assert.False(cache.ContainsKey(cacheIdA));
            Assert.All(scalarRefA, b => Assert.Equal(0, b));
            Assert.True(cache.ContainsKey(cacheIdB));
            Assert.Contains(scalarRefB, b => b != 0);
            Assert.Equal(string.Empty, accA.HbaPrivateKey);
            Assert.Equal(string.Empty, accA.AccountStatusIssue);
            Assert.False(RobloxHbaService.ClearCachedKey(keyA));
            Assert.False(RobloxHbaService.ClearCachedKey(null));
        }

        [Fact]
        public async Task AccountApi_WhenHttp429Returned_IncrementsRateLimitCoordinatorOncePerResponse()
        {
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
            string token = MakeToken('G', 920);

            var csrfHandler = new FakeHttpHandler(_ => new HttpResponseMessage((HttpStatusCode)429));
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(csrfHandler);

            var csrfAcc = new Account
            {
                Username = "Csrf429User",
                UserID = 7001,
                SecurityToken = token
            };

            var (csrfOk, _, _) = await csrfAcc.GetCSRFTokenAsync();

            Assert.False(csrfOk);
            Assert.True(csrfAcc.LastAuthTicketRateLimited);
            Assert.Equal(1, csrfHandler.Calls);
            Assert.Equal(1, RobloxRateLimitCoordinator.Instance.ConsecutiveRateLimitHits);

            RobloxRateLimitCoordinator.Instance.ResetBackoff();

            int ticketCalls = 0;
            var ticketHandler = new FakeHttpHandler(req =>
            {
                if (req.RequestUri != null &&
                    req.RequestUri.AbsolutePath.Contains("client-assertion", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(EmptyJsonObject) };
                }

                Interlocked.Increment(ref ticketCalls);
                var resp = new HttpResponseMessage((HttpStatusCode)429);
                resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return resp;
            });
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(ticketHandler);

            var ticketAcc = new Account
            {
                Username = "Ticket429User",
                UserID = 7002,
                SecurityToken = token
            };

            var (ticketOk, ticket) = await ticketAcc.GetAuthTicketAsync();

            Assert.False(ticketOk);
            Assert.Empty(ticket);
            Assert.True(ticketAcc.LastAuthTicketRateLimited);
            Assert.Equal(4, ticketCalls);
            Assert.Equal(4, RobloxRateLimitCoordinator.Instance.ConsecutiveRateLimitHits);
        }

        [Fact]
        public async Task CookieRotation_MaxAgeZeroPastExpiresAndBogusPrefix_DoNotOverwriteOrMatch()
        {
            string originalToken = MakeToken('H', 160);
            string deletedLikeLongToken = MakeToken('X', 160);
            string evilToken = MakeToken('Y', 160);

            Assert.Null(Account.ExtractRoblosecurityFromSetCookie($".ROBLOSECURITY={deletedLikeLongToken}; max-age=0; Path=/"));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie($".ROBLOSECURITY={deletedLikeLongToken}; Max-Age = 0; Path=/"));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie($".ROBLOSECURITY={deletedLikeLongToken}; max-age=-10; Path=/"));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie($".ROBLOSECURITY={deletedLikeLongToken}; expires=Mon, 01 Jan 2024 00:00:00 GMT; Path=/"));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie($"FAKE.ROBLOSECURITY={evilToken}; Path=/"));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie($"OTHER=abc .ROBLOSECURITY={evilToken}; Path=/"));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie($"foo=.ROBLOSECURITY={evilToken}; Path=/"));

            var httpAcc = new Account
            {
                Username = "HttpRotateGuardUser",
                SecurityToken = originalToken
            };

            using var httpResp = new HttpResponseMessage(HttpStatusCode.OK);
            httpResp.Headers.TryAddWithoutValidation("Set-Cookie", ".ROBLOSECURITY=deleted; max-age=0; Path=/");
            httpResp.Headers.TryAddWithoutValidation("Set-Cookie", $".ROBLOSECURITY={deletedLikeLongToken}; max-age=-5; Path=/");
            httpResp.Headers.TryAddWithoutValidation("Set-Cookie", $"FAKE.ROBLOSECURITY={evilToken}; Path=/");

            Assert.False(AppServices.AccountSessionClient.TryUpdateRotatedCookie(httpAcc, httpResp));
            Assert.Equal(originalToken, httpAcc.SecurityToken);

            var restAcc = new Account
            {
                Username = "RestRotateGuardUser",
                SecurityToken = originalToken
            };

            var fakeRestHandler = new FakeHttpHandler(_ =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(EmptyJsonObject)
                };
                resp.Headers.TryAddWithoutValidation("Set-Cookie", $".ROBLOSECURITY={deletedLikeLongToken}; max-age=0; Path=/");
                resp.Headers.TryAddWithoutValidation("Set-Cookie", $"FAKE.ROBLOSECURITY={evilToken}; Path=/");
                return resp;
            });

            using var httpClient = new HttpClient(fakeRestHandler);
            using var restClient = new RestClient(httpClient, "https://users.roblox.com/");
            RestResponse restResponse = await restClient.ExecuteAsync(new RestRequest("v1/users/authenticated", Method.Get));

            Assert.NotNull(restResponse.Cookies[".ROBLOSECURITY"]);
            Assert.False(AppServices.AccountApiService.TryUpdateRotatedCookie(restAcc, restResponse));
            Assert.Equal(originalToken, restAcc.SecurityToken);
        }

        [Fact]
        public void WebAuthPolicy_RecordAuthResult_EvictsStaleAndExcessNonLockedClientFailures()
        {
            WebAuthPolicy.ResetLockoutsForTesting();
            try
            {
                FieldInfo? clientFailuresField = typeof(WebAuthPolicy).GetField(
                    "ClientFailures",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(clientFailuresField);
                var clientFailures = (System.Collections.ICollection)clientFailuresField!.GetValue(null)!;

                DateTimeOffset t0 = DateTimeOffset.UtcNow;

                for (int i = 0; i < WebAuthPolicy.MaxFailedAttempts - 1; i++)
                {
                    Assert.False(WebAuthPolicy.RecordAuthResult("stale-client", AuthDecision.InvalidPassword, t0));
                }

                DateTimeOffset t1 = t0.AddMinutes(30);
                for (int i = 0; i < WebAuthPolicy.MaxFailedAttempts; i++)
                {
                    WebAuthPolicy.RecordAuthResult("locked-client", AuthDecision.InvalidPassword, t1);
                }
                Assert.True(WebAuthPolicy.IsClientLockedOut("locked-client", t1));

                for (int i = 0; i < 525; i++)
                {
                    bool locked = WebAuthPolicy.RecordAuthResult($"burst-client-{i}", AuthDecision.InvalidPassword, t1.AddMilliseconds(i));
                    Assert.False(locked);
                }

                Assert.True(clientFailures.Count <= 512, $"Expected bounded ClientFailures count <= 512, actual {clientFailures.Count}");
                Assert.True(WebAuthPolicy.IsClientLockedOut("locked-client", t1));
                Assert.False(WebAuthPolicy.RecordAuthResult("stale-client", AuthDecision.InvalidPassword, t1.AddSeconds(1)));
            }
            finally
            {
                WebAuthPolicy.ResetLockoutsForTesting();
            }
        }
    }
}
