using System;
using RBX_Alt_Manager;
using Xunit;

namespace RBX_Alt_Manager.Tests
{
    public class SecurityHardeningTests
    {
        [Theory]
        [InlineData("localhost", true)]
        [InlineData("LOCALHOST", true)]
        [InlineData("127.0.0.1", true)]
        [InlineData("::1", true)]
        [InlineData("[::1]", true)]
        [InlineData("localhost.evil.com", false)]
        [InlineData("127.0.0.1.attacker.com", false)]
        [InlineData("", false)]
        public void IsLoopbackHostName_MatchesExactly(string host, bool expected)
        {
            Assert.Equal(expected, AccountManager.IsLoopbackHostName(host));
        }

        [Theory]
        [InlineData("", true)]
        [InlineData("http://localhost:3000", true)]
        [InlineData("http://127.0.0.1", true)]
        [InlineData("https://localhost", true)]
        [InlineData("http://localhost.evil.com", false)]
        [InlineData("http://127.0.0.1.attacker.com", false)]
        [InlineData("https://evil.com", false)]
        [InlineData("not a url", false)]
        public void IsTrustedLocalOrigin_RejectsSpoofedOrigins(string origin, bool expected)
        {
            Assert.Equal(expected, AccountManager.IsTrustedLocalOrigin(origin));
        }

        [Fact]
        public void FixedTimeEqualsPassword_ComparesCorrectly()
        {
            Assert.True(AccountManager.FixedTimeEqualsPassword("secret123", "secret123"));
            Assert.False(AccountManager.FixedTimeEqualsPassword("secret123", "secret124"));
            Assert.False(AccountManager.FixedTimeEqualsPassword("short", "longer-password"));
            Assert.False(AccountManager.FixedTimeEqualsPassword(null, "x"));
            Assert.False(AccountManager.FixedTimeEqualsPassword("x", null));
        }
    }
}
