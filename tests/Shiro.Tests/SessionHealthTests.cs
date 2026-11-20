using System;
using Shiro;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class SessionHealthTests
    {
        private static string MakeToken(int payloadLen) =>
            "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_" +
            "AAAAHEADER." + new string('B', payloadLen) + ".SIGNATURE";

        [Theory]
        [InlineData(939, 939)]
        [InlineData(790, 790)]
        public void GetSessionTokenPayloadLength_ReturnsMiddleSegmentLength(int len, int expected)
        {
            Assert.Equal(expected, Account.GetSessionTokenPayloadLength(MakeToken(len)));
        }

        [Fact]
        public void GetSessionTokenPayloadLength_EmptyOrMalformed_ReturnsZero()
        {
            Assert.Equal(0, Account.GetSessionTokenPayloadLength(null!));
            Assert.Equal(0, Account.GetSessionTokenPayloadLength(""));
            Assert.Equal(0, Account.GetSessionTokenPayloadLength("nodots"));
        }

        [Fact]
        public void IsShortLivedCookie_DistinguishesFallbackFromFull()
        {
            Assert.True(Account.IsShortLivedCookie(MakeToken(790)));
            Assert.False(Account.IsShortLivedCookie(MakeToken(939)));
        }

        [Fact]
        public void HasExpiredShortLivedCookie_OldShortToken_IsNotLocallyExpired()
        {
            var acc = new Account { SecurityToken = MakeToken(790), LastUse = DateTime.Now.AddMinutes(-45) };
            Assert.False(acc.HasExpiredShortLivedCookie());
        }

        [Fact]
        public void HasExpiredShortLivedCookie_FreshShortToken_IsNotExpired()
        {
            var acc = new Account { SecurityToken = MakeToken(790), LastUse = DateTime.Now.AddMinutes(-5) };
            Assert.False(acc.HasExpiredShortLivedCookie());
        }

        [Fact]
        public void HasExpiredShortLivedCookie_OldFullToken_IsNotExpired()
        {
            var acc = new Account { SecurityToken = MakeToken(939), LastUse = DateTime.Now.AddDays(-5) };
            Assert.False(acc.HasExpiredShortLivedCookie());
        }

        [Fact]
        public void HasExpiredShortLivedCookie_EmptyToken_IsExpired()
        {
            Assert.True(new Account().HasExpiredShortLivedCookie());
        }

        [Fact]
        public void SessionHealthStatus_ReflectsTokenKind()
        {
            var full = new Account { SecurityToken = MakeToken(939), Valid = true, LastUse = DateTime.Now };
            var shortLived = new Account { SecurityToken = MakeToken(790), Valid = true, LastUse = DateTime.Now };
            var expired = new Account { SecurityToken = MakeToken(939), Valid = false };

            Assert.Equal(SessionHealth.Healthy, full.SessionHealthStatus);
            Assert.Equal(SessionHealth.ShortLived, shortLived.SessionHealthStatus);
            Assert.Equal(SessionHealth.Expired, expired.SessionHealthStatus);
        }

        [Fact]
        public void TryUpdateRotatedCookie_AcceptsRotatedToken()
        {
            var initialToken = MakeToken(939);
            var rotatedToken = MakeToken(790);
            var acc = new Account { SecurityToken = initialToken, Valid = true };

            using var msg = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK);
            msg.Headers.TryAddWithoutValidation("Set-Cookie", $".ROBLOSECURITY={rotatedToken}; Path=/; Domain=.roblox.com; HttpOnly");

            bool updated = AppServices.AccountSessionClient.TryUpdateRotatedCookie(acc, msg);
            Assert.True(updated);
            Assert.Equal(rotatedToken, acc.SecurityToken);
        }

        [Fact]
        public void TryUpdateRotatedCookie_DoesNotForceValidTrue_WhenAccountInvalid()
        {
            var initialToken = MakeToken(939);
            var rotatedToken = MakeToken(790);
            var acc = new Account { SecurityToken = initialToken, Valid = false };

            using var msg = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
            msg.Headers.TryAddWithoutValidation("Set-Cookie", $".ROBLOSECURITY={rotatedToken}; Path=/; Domain=.roblox.com; HttpOnly");

            bool updated = AppServices.AccountSessionClient.TryUpdateRotatedCookie(acc, msg);
            Assert.True(updated);
            Assert.Equal(rotatedToken, acc.SecurityToken);
            Assert.False(acc.Valid);
        }

        [Fact]
        public void ExtractRoblosecurityFromSetCookie_HandlesEdgeCases()
        {
            var validToken = MakeToken(939);

            Assert.Equal(validToken, Account.ExtractRoblosecurityFromSetCookie($".ROBLOSECURITY=\"{validToken}\"; Path=/; HttpOnly"));
            Assert.Equal(validToken, Account.ExtractRoblosecurityFromSetCookie($".ROBLOSECURITY={validToken}"));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie(".ROBLOSECURITY=; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT"));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie("OTHERCOOKIE=12345; Path=/"));
        }

        [Fact]
        public async System.Threading.Tasks.Task ConcurrentCookieRotations_AreThreadSafe()
        {
            var acc = new Account { SecurityToken = MakeToken(939), Valid = true };
            int versionStart = acc.CookieRotationVersion;

            var tasks = new System.Threading.Tasks.Task[16];
            for (int i = 0; i < tasks.Length; i++)
            {
                string nextToken = MakeToken(700 + i);
                tasks[i] = System.Threading.Tasks.Task.Run(() =>
                {
                    using var msg = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK);
                    msg.Headers.TryAddWithoutValidation("Set-Cookie", $".ROBLOSECURITY={nextToken}; Path=/");
                    AppServices.AccountSessionClient.TryUpdateRotatedCookie(acc, msg);
                });
            }

            await System.Threading.Tasks.Task.WhenAll(tasks);

            Assert.True(acc.CookieRotationVersion > versionStart);
            Assert.False(string.IsNullOrWhiteSpace(acc.SecurityToken));
            Assert.Contains("_|WARNING:-DO-NOT-SHARE-THIS", acc.SecurityToken);
        }
    }
}
