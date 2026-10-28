using System;
using RBX_Alt_Manager;
using RBX_Alt_Manager.Services;
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

        [Fact]
        public void LogRedactor_RedactsRoblosecurityToken()
        {
            string raw = "User logged in with _WARNING:-DO-NOT-SHARE-THIS.--testtoken12345--done";
            string redacted = LogRedactor.Redact(raw);

            Assert.DoesNotContain("testtoken12345", redacted);
            Assert.Contains("[REDACTED_ROBLOSECURITY]", redacted);
        }

        [Fact]
        public void LogRedactor_RedactsCookieHeader()
        {
            string raw = "Header: .ROBLOSECURITY=abc123xyz_secret; path=/";
            string redacted = LogRedactor.Redact(raw);

            Assert.DoesNotContain("abc123xyz_secret", redacted);
            Assert.Contains(".ROBLOSECURITY=[REDACTED_COOKIE]", redacted);
        }

        [Fact]
        public void LogRedactor_RedactsJsonSecrets()
        {
            string raw = "{\"username\": \"john\", \"password\": \"SuperSecretPwd!\", \"token\": \"tok999\"}";
            string redacted = LogRedactor.Redact(raw);

            Assert.DoesNotContain("SuperSecretPwd!", redacted);
            Assert.DoesNotContain("tok999", redacted);
            Assert.Contains("\"password\":\"[REDACTED]\"", redacted);
            Assert.Contains("\"token\":\"[REDACTED]\"", redacted);
        }

        [Fact]
        public void LogRedactor_RedactsBearerAndQuerySecrets()
        {
            string raw = "Request GET /api?password=plain123 Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.test";
            string redacted = LogRedactor.Redact(raw);

            Assert.DoesNotContain("plain123", redacted);
            Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", redacted);
            Assert.Contains("?password=[REDACTED]", redacted);
            Assert.Contains("Bearer [REDACTED_TOKEN]", redacted);
        }

        [Fact]
        public void StoragePaths_ClearCachedVaultKey_WipesMemoryZero()
        {
            // Seed a test vault key
            byte[] key = StoragePaths.GetOrCreateVaultKey();
            Assert.NotNull(key);
            Assert.Equal(32, key.Length);

            // Call ClearCachedVaultKey()
            StoragePaths.ClearCachedVaultKey();

            // All bytes in the original reference must be zeroed out
            for (int i = 0; i < key.Length; i++)
            {
                Assert.Equal(0, key[i]);
            }
        }

        [Fact]
        public void AppSettingsKeys_ContainsHasAcceptedRiskDisclaimer()
        {
            Assert.Equal("HasAcceptedRiskDisclaimer", AppSettingsKeys.General.HasAcceptedRiskDisclaimer);
        }
    }
}
