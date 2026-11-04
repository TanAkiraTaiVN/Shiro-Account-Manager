#nullable enable
using System;
using System.Net;
using System.Net.Http;
using Shiro.Classes;
using Shiro.Http;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountCookieRotationTests
    {
        private static string CreateValidRoblosecurity(string suffix)
        {
            return $"_|WARNING:-DO-NOT-SHARE-THIS.--{suffix}." + new string('A', 120);
        }

        [Fact]
        public void ExtractRoblosecurityFromSetCookie_ValidatesAndExtracts()
        {
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie(null));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie(string.Empty));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie("session_id=12345; path=/"));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie(".ROBLOSECURITY=short; path=/"));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie(".ROBLOSECURITY=" + new string('B', 120) + "; path=/"));

            string validCookie = CreateValidRoblosecurity("rot1");
            string header = $".ROBLOSECURITY=\"{validCookie}\"; Domain=.roblox.com; Path=/; Secure; HttpOnly";
            string? extracted = Account.ExtractRoblosecurityFromSetCookie(header);
            Assert.Equal(validCookie, extracted);
        }

        [Fact]
        public void ApplyRotatedSecurityToken_UpdatesAccountSession()
        {
            var acc = new Account { Username = "RotateUser", SecurityToken = "old_token" };

            string validCookie1 = CreateValidRoblosecurity("rot1");
            bool updated = acc.ApplyRotatedSecurityToken(validCookie1, "test");
            Assert.True(updated);
            Assert.Equal(validCookie1, acc.SecurityToken);
            Assert.True(acc.IsDirty);

            // Same token returns false
            bool reUpdated = acc.ApplyRotatedSecurityToken(validCookie1, "test");
            Assert.False(reUpdated);

            // Invalid token returns false
            bool invalidUpdated = acc.ApplyRotatedSecurityToken("invalid_cookie", "test");
            Assert.False(invalidUpdated);
        }

        [Fact]
        public void TryUpdateRotatedCookie_HttpResponseMessage_ExtractsAndUpdates()
        {
            var acc = new Account { Username = "HttpUser" };

            // Null response
            Assert.False(acc.TryUpdateRotatedCookie((HttpResponseMessage)null!));

            // Response without Set-Cookie
            using var emptyResp = new HttpResponseMessage(HttpStatusCode.OK);
            Assert.False(acc.TryUpdateRotatedCookie(emptyResp));

            // Response with valid Set-Cookie
            string validCookie = CreateValidRoblosecurity("httpRot");
            using var validResp = new HttpResponseMessage(HttpStatusCode.OK);
            validResp.Headers.Add("Set-Cookie", $".ROBLOSECURITY={validCookie}; path=/");

            bool updated = acc.TryUpdateRotatedCookie(validResp);
            Assert.True(updated);
            Assert.Equal(validCookie, acc.SecurityToken);
        }

        [Fact]
        public void TryUpdateRotatedCookie_RestResponse_ExtractsAndUpdates()
        {
            var acc = new Account { Username = "RestUser" };

            // Null response
            Assert.False(acc.TryUpdateRotatedCookie((RestResponse)null!));

            // Valid RestResponse with Set-Cookie header
            string validCookie = CreateValidRoblosecurity("restRot");
            var restResp = new RestResponse
            {
                StatusCode = HttpStatusCode.OK
            };
            restResp.Headers.Add(new HeaderParameter("Set-Cookie", $".ROBLOSECURITY={validCookie}; path=/"));

            bool updated = acc.TryUpdateRotatedCookie(restResp);
            Assert.True(updated);
            Assert.Equal(validCookie, acc.SecurityToken);
        }
    }
}
