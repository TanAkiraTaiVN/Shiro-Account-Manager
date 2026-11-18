#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
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
            _previousHttpClientFactory = Account.TestHttpClientFactory;
            RobloxHbaService.ClearKeyCache();
        }

        public void Dispose()
        {
            Account.TestHttpClientFactory = _previousHttpClientFactory;
            AppServices.SelectionState.LastValidAccount = _previousLastValidAccount;
            RobloxHbaService.ClearKeyCache();
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

            Account.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(req =>
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

            Assert.True(cache.TryGetValue(keyA, out ECParameters cachedA));
            Assert.True(cache.TryGetValue(keyB, out ECParameters cachedB));
            byte[] scalarRefA = cachedA.D!;
            byte[] scalarRefB = cachedB.D!;
            Assert.Contains(scalarRefA, b => b != 0);
            Assert.Contains(scalarRefB, b => b != 0);

            accA.ClearSensitiveMemory();

            Assert.False(cache.ContainsKey(keyA));
            Assert.All(scalarRefA, b => Assert.Equal(0, b));
            Assert.True(cache.ContainsKey(keyB));
            Assert.Contains(scalarRefB, b => b != 0);
            Assert.Equal(string.Empty, accA.HbaPrivateKey);
            Assert.Equal(string.Empty, accA.AccountStatusIssue);
            Assert.False(RobloxHbaService.ClearCachedKey(keyA));
            Assert.False(RobloxHbaService.ClearCachedKey(null));
        }
    }
}
