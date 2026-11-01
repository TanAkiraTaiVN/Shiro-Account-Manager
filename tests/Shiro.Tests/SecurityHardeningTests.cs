using System;
using Shiro;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
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
        public void LogRedactor_RedactsRealRoblosecurityWithPipeAndEncoded()
        {
            string raw = "Error: _|WARNING:-DO-NOT-SHARE-THIS.--realtokenwithpipe--end and %5F%7CWARNING:-DO-NOT-SHARE-THIS.--encodedtoken--";
            string redacted = LogRedactor.Redact(raw);

            Assert.DoesNotContain("realtokenwithpipe", redacted);
            Assert.DoesNotContain("encodedtoken", redacted);
            Assert.Contains("[REDACTED_ROBLOSECURITY]", redacted);
        }

        [Fact]
        public void LogRedactor_RedactsProxyCredentialsAndBloxApiKey()
        {
            string raw = "Proxy error on http://admin:superSecret123@192.168.1.50:8080 and api key BLOX-abcdef1234567890-xyz";
            string redacted = LogRedactor.Redact(raw);

            Assert.DoesNotContain("superSecret123", redacted);
            Assert.DoesNotContain("abcdef1234567890", redacted);
            Assert.Contains("[REDACTED_PROXY_AUTH]", redacted);
            Assert.Contains("BLOX-[REDACTED_KEY]", redacted);
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

        [Fact]
        public async System.Threading.Tasks.Task WebServer_ExportAccounts_RejectsUnauthenticatedOrDisallowedRequests()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            int statusCode = 200;
            var ctx = new AccountManager.ApiRequestContext
            {
                AbsolutePath = "/Accounts/Export",
                IsV2 = true,
                IsLocal = true,
                Host = "localhost",
                Origin = "",
                GetQuery = k => k == "Format" ? "CookieOnly" : null,
                GetHeader = _ => null,
                SetStatusCode = c => statusCode = c
            };

            string resp = await mgr.ProcessApiRequestAsync(ctx);
            Assert.Equal(401, statusCode);
            Assert.Contains("false", resp, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("/GetCookie")]
        [InlineData("/GetCSRFToken")]
        [InlineData("/GetField")]
        [InlineData("/SetField")]
        [InlineData("/SetAlias")]
        [InlineData("/AntiAfk/Toggle")]
        [InlineData("/MultiRoblox/Toggle")]
        [InlineData("/LaunchAccount")]
        [InlineData("/SetProxy")]
        [InlineData("/KillAllRoblox")]
        [InlineData("/BlockUser")]
        public async System.Threading.Tasks.Task WebServer_SensitiveEndpoints_RejectWhenPasswordNotConfigured(string path)
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            int statusCode = 200;
            var ctx = new AccountManager.ApiRequestContext
            {
                AbsolutePath = path,
                IsV2 = true,
                IsLocal = true,
                Host = "localhost",
                Origin = "",
                GetQuery = _ => null,
                GetHeader = _ => null,
                SetStatusCode = c => statusCode = c
            };

            string resp = await mgr.ProcessApiRequestAsync(ctx);
            Assert.Equal(401, statusCode);
            Assert.Contains("WebServer password must be configured", resp, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ExtractRequestPassword_PrefersXShiroAuthAndSupportsFallbackHeaders()
        {
            string fromShiro = AccountManager.ExtractRequestPassword(
                k => k == "X-Shiro-Auth" ? "shiro-secret" : (k == "X-RAM-Auth" ? "legacy-secret" : null),
                _ => null);
            Assert.Equal("shiro-secret", fromShiro);

            string fromLegacy = AccountManager.ExtractRequestPassword(
                k => k == "X-RAM-Auth" ? "legacy-secret" : null,
                _ => null);
            Assert.Equal("legacy-secret", fromLegacy);

            string fromBearer = AccountManager.ExtractRequestPassword(
                k => k == "Authorization" ? "Bearer bearer-token-123" : null,
                _ => null);
            Assert.Equal("bearer-token-123", fromBearer);
        }

        [Fact]
        public void GetOrCreateVaultKeyCopy_ReturnsIndependentBufferSafeToZero()
        {
            byte[] cached = StoragePaths.GetOrCreateVaultKey();
            byte[] copy = StoragePaths.GetOrCreateVaultKeyCopy();

            Assert.NotSame(cached, copy);
            Assert.Equal(cached, copy);

            System.Security.Cryptography.CryptographicOperations.ZeroMemory(copy);
            Assert.Contains(cached, b => b != 0);
        }
    }
}
