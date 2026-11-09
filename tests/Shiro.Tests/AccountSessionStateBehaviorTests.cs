#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Shiro.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class AccountSessionStateBehaviorTests : IDisposable
    {
        private const string InitialToken = "_|WARNING:-DO-NOT-SHARE-THIS.INITIALTOKEN123";

        public AccountSessionStateBehaviorTests()
        {
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
        }

        public void Dispose()
        {
            Account.TestHttpClientFactory = null;
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
        }

        [Fact]
        public async Task GetCSRFTokenAsync_WhenTokenIsFresh_ReturnsCachedWithoutNetwork()
        {
            var acc = new Account
            {
                Username = "CachedUser",
                CSRFToken = "cached-csrf-token",
                TokenSet = DateTime.Now
            };

            // Network should not be hit at all
            Account.TestHttpClientFactory = _ => throw new InvalidOperationException("Network should not be called");

            var (success, token, err) = await acc.GetCSRFTokenAsync();

            Assert.True(success);
            Assert.Equal("cached-csrf-token", token);
            Assert.Empty(err);
        }

        [Fact]
        public async Task GetCSRFTokenAsync_WhenTokenExpired_FetchesFromAuthEndpoint()
        {
            var acc = new Account
            {
                Username = "FetchUser",
                CSRFToken = "old-csrf",
                TokenSet = DateTime.Now.AddMinutes(-10),
                SecurityToken = InitialToken
            };

            var handler = new FakeHttpHandler(req =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
                resp.Headers.TryAddWithoutValidation("x-csrf-token", "newly-issued-token-99");
                return resp;
            });
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            var (success, token, err) = await acc.GetCSRFTokenAsync();

            Assert.True(success);
            Assert.Equal("newly-issued-token-99", token);
            Assert.Equal("newly-issued-token-99", acc.CSRFToken);
            Assert.Empty(err);
            Assert.True(handler.Calls >= 1);
        }

        [Fact]
        public async Task GetCSRFTokenAsync_WhenEndpointsFail_ReturnsFailure()
        {
            var acc = new Account
            {
                Username = "FailUser",
                SecurityToken = InitialToken
            };

            var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            var (success, token, err) = await acc.GetCSRFTokenAsync();

            Assert.False(success);
            Assert.Empty(token);
            Assert.NotEmpty(err);
        }

        [Fact]
        public async Task GetAuthTicketAsync_WhenTicketHeaderPresent_ReturnsTicket()
        {
            var acc = new Account
            {
                Username = "TicketUser",
                SecurityToken = InitialToken
            };

            var handler = new FakeHttpHandler(req =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                resp.Headers.TryAddWithoutValidation("rbx-authentication-ticket", "sample-ticket-value-12345");
                return resp;
            });
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            var (success, ticket) = await acc.GetAuthTicketAsync();

            Assert.True(success);
            Assert.Equal("sample-ticket-value-12345", ticket);
        }

        [Fact]
        public async Task GetAuthTicketAsync_WhenCsrfMismatched_RetriesWithUpdatedToken()
        {
            var acc = new Account
            {
                Username = "RetryTicketUser",
                SecurityToken = InitialToken
            };

            int callCount = 0;
            var handler = new FakeHttpHandler(req =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden);
                    forbidden.Headers.TryAddWithoutValidation("x-csrf-token", "updated-csrf-777");
                    return forbidden;
                }

                var successResp = new HttpResponseMessage(HttpStatusCode.OK);
                successResp.Headers.TryAddWithoutValidation("rbx-authentication-ticket", "ticket-after-retry");
                return successResp;
            });
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            var (success, ticket) = await acc.GetAuthTicketAsync();

            Assert.True(success);
            Assert.Equal("ticket-after-retry", ticket);
            Assert.Equal(2, callCount);
        }

        [Fact]
        public async Task ValidateSessionAsync_EmptySecurityToken_SetsInvalidAndReturnsFalse()
        {
            var acc = new Account
            {
                Username = "NoTokenUser",
                SecurityToken = "",
                Valid = true
            };

            bool isValid = await acc.ValidateSessionAsync();

            Assert.False(isValid);
            Assert.False(acc.Valid);
        }

        [Fact]
        public async Task ValidateSessionAsync_CaptivePortalHtml_PreservesExistingValidity()
        {
            var acc = new Account
            {
                Username = "CaptiveUser",
                SecurityToken = InitialToken,
                Valid = true
            };

            var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<!DOCTYPE html><html><body>Captive Wi-Fi Login</body></html>")
            });
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            bool isValid = await acc.ValidateSessionAsync();

            Assert.True(isValid);
            Assert.True(acc.Valid);
        }

        [Fact]
        public async Task ValidateSessionAsync_UserIdMismatch_MarksInvalid()
        {
            var acc = new Account
            {
                Username = "MismatchUser",
                UserID = 4242,
                SecurityToken = InitialToken,
                Valid = true
            };

            var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":99999,\"name\":\"DifferentUser\"}")
            });
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            bool isValid = await acc.ValidateSessionAsync();

            Assert.False(isValid);
            Assert.False(acc.Valid);
        }

        [Fact]
        public void TryUpdateRotatedCookie_UpdatesTokenAndIncrementsVersion()
        {
            var acc = new Account
            {
                Username = "RotatedUser",
                SecurityToken = InitialToken
            };

            int initialVersion = acc.CookieRotationVersion;

            string longRotatedCookie = "_|WARNING:-DO-NOT-SHARE-THIS." + new string('A', 120);

            using var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.TryAddWithoutValidation("Set-Cookie", $".ROBLOSECURITY={longRotatedCookie}; path=/; domain=.roblox.com; HttpOnly");

            bool updated = acc.TryUpdateRotatedCookie(resp);

            Assert.True(updated);
            Assert.Contains(longRotatedCookie, acc.SecurityToken);
            Assert.True(acc.CookieRotationVersion > initialVersion);
        }

        [Fact]
        public void GetRateLimitRecoverySeconds_ParsesHeadersCorrectly()
        {
            var method = typeof(Account).GetMethod("GetRateLimitRecoverySeconds", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            // Null response returns default (90)
            int dflt = (int)method!.Invoke(null, new object?[] { null })!;
            Assert.Equal(90, dflt);

            // Response with numeric Retry-After
            var resp1 = new RestResponse { StatusCode = HttpStatusCode.TooManyRequests };
            resp1.Headers.Add(new HeaderParameter("Retry-After", "45"));
            int delay1 = (int)method.Invoke(null, new object?[] { resp1 })!;
            Assert.Equal(45, delay1);

            // Response with oversized Retry-After is clamped to 180
            var resp2 = new RestResponse { StatusCode = HttpStatusCode.TooManyRequests };
            resp2.Headers.Add(new HeaderParameter("Retry-After", "999"));
            int delay2 = (int)method.Invoke(null, new object?[] { resp2 })!;
            Assert.Equal(180, delay2);

            // Response with X-RateLimit-Reset unix timestamp
            long futureUnix = DateTimeOffset.UtcNow.AddSeconds(20).ToUnixTimeSeconds();
            var resp3 = new RestResponse { StatusCode = HttpStatusCode.TooManyRequests };
            resp3.Headers.Add(new HeaderParameter("X-RateLimit-Reset", futureUnix.ToString()));
            int delay3 = (int)method.Invoke(null, new object?[] { resp3 })!;
            Assert.InRange(delay3, 10, 30);
        }

        [Fact]
        public void MarkNativeAuthRateLimited_DoesNotThrowAndCoordinatesRateLimit()
        {
            var ex = Record.Exception(() =>
            {
                Account.MarkNativeAuthRateLimited(25, "TestRateLimitUser", "unit-test");
            });

            Assert.Null(ex);
        }
    }
}
