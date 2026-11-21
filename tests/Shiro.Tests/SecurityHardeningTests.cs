using System;
using Shiro;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
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
            Assert.Equal(expected, WebAuthPolicy.IsLoopbackHostName(host));
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
            Assert.Equal(expected, WebAuthPolicy.IsTrustedLocalOrigin(origin));
        }

        [Fact]
        public void FixedTimeEqualsPassword_ComparesCorrectly()
        {
            Assert.True(CryptoHelper.FixedTimeEqualsSecret("secret123", "secret123"));
            Assert.False(CryptoHelper.FixedTimeEqualsSecret("secret123", "secret124"));
            Assert.False(CryptoHelper.FixedTimeEqualsSecret("short", "longer-password"));
            Assert.False(CryptoHelper.FixedTimeEqualsSecret(null!, "x"));
            Assert.False(CryptoHelper.FixedTimeEqualsSecret("x", null!));
        }

        [Theory]
        [InlineData("secret123", "secret123", true)]
        [InlineData("secret123", "secret124", false)]
        [InlineData("short", "muchlongersecret", false)]
        [InlineData("", "secret123", false)]
        public void FixedTimeEqualsSecret_ComparesByValue(string provided, string expected, bool match)
        {
            Assert.Equal(match, CryptoHelper.FixedTimeEqualsSecret(provided, expected));
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
        public void LogRedactor_RedactsProxyCredentials()
        {
            string raw = "Proxy error on http://admin:superSecret123@192.168.1.50:8080";
            string redacted = LogRedactor.Redact(raw);

            Assert.DoesNotContain("superSecret123", redacted);
            Assert.Contains("[REDACTED_PROXY_AUTH]", redacted);

            string legacyBlox = "api key BLOX-abcdef1234567890-xyz";
            string legacyRedacted = LogRedactor.Redact(legacyBlox);
            Assert.DoesNotContain("BLOX-[REDACTED_KEY]", legacyRedacted);
            Assert.Contains("BLOX-abcdef1234567890-xyz", legacyRedacted);
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
            // Seed a test vault key and inspect the cached buffer directly
            _ = StoragePaths.GetOrCreateVaultKey();
            byte[] key = StoragePaths.PeekCachedVaultKeyBufferForTesting()!;
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
            string oldPass = AppSettings.WebSocketPassword ?? string.Empty;
            try
            {
                AppSettings.WebSocketPassword = "";
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
            finally
            {
                AppSettings.WebSocketPassword = oldPass;
            }
        }

        [Fact]
        public void ExtractRequestPassword_PrefersXShiroAuthAndSupportsFallbackHeaders()
        {
            string fromShiro = WebAuthPolicy.ExtractRequestPassword(
                k => k == "X-Shiro-Auth" ? "shiro-secret" : (k == "X-RAM-Auth" ? "legacy-secret" : null),
                _ => null);
            Assert.Equal("shiro-secret", fromShiro);

            string fromLegacy = WebAuthPolicy.ExtractRequestPassword(
                k => k == "X-RAM-Auth" ? "legacy-secret" : null,
                _ => null);
            Assert.Equal("legacy-secret", fromLegacy);

            string fromBearer = WebAuthPolicy.ExtractRequestPassword(
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

        [Fact]
        public void Account_DoesNotHoldPlaintextSecurityTokenOrPassword_InInstanceStringFields()
        {
            using var suppressSave = Account.BeginSuppressDeferredSaveScope();

            string shortPayload = new string('A', 790);
            string rawCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_eyJ." + shortPayload + ".sig123";
            const string rawPassword = "Sup3r$ecretP@ssw0rd!Unicode-Привет";

            var account = new Account
            {
                Username = "RamProtectedUser",
                SecurityToken = rawCookie,
                Password = rawPassword,
                Valid = true
            };

            Assert.Equal(rawCookie, account.SecurityToken);
            Assert.Equal(rawPassword, account.Password);
            Assert.True(account.IsShortLivedSession);
            Assert.False(account.HasExpiredShortLivedCookie());
            Assert.Equal(SessionHealth.ShortLived, account.SessionHealthStatus);

            FieldInfo[] allFields = typeof(Account).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (FieldInfo field in allFields)
            {
                if (field.GetValue(account) is string strVal && !string.IsNullOrEmpty(strVal))
                {
                    Assert.DoesNotContain("sig123", strVal, StringComparison.Ordinal);
                    Assert.DoesNotContain(rawCookie, strVal, StringComparison.Ordinal);
                    Assert.DoesNotContain(rawPassword, strVal, StringComparison.Ordinal);
                }
            }

            FieldInfo? tokenBlobField = typeof(Account).GetField("_protectedSecurityToken", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? passwordBlobField = typeof(Account).GetField("_protectedPassword", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(tokenBlobField);
            Assert.NotNull(passwordBlobField);

            byte[]? initialTokenBlob = (byte[]?)tokenBlobField!.GetValue(account);
            byte[]? initialPasswordBlob = (byte[]?)passwordBlobField!.GetValue(account);
            Assert.NotNull(initialTokenBlob);
            Assert.NotNull(initialPasswordBlob);
            Assert.False(initialTokenBlob!.AsSpan().IndexOf(Encoding.UTF8.GetBytes("sig123")) >= 0);
            Assert.False(initialPasswordBlob!.AsSpan().IndexOf(Encoding.UTF8.GetBytes(rawPassword)) >= 0);

            // Prove SessionHealthStatus, IsShortLivedSession, and HasExpiredShortLivedCookie use cached metadata without decrypting
            initialTokenBlob[initialTokenBlob.Length - 1] ^= 0xFF;
            Assert.True(account.IsShortLivedSession);
            Assert.False(account.HasExpiredShortLivedCookie());
            Assert.Equal(SessionHealth.ShortLived, account.SessionHealthStatus);
            Assert.ThrowsAny<CryptographicException>(() => _ = account.SecurityToken);
            initialTokenBlob[initialTokenBlob.Length - 1] ^= 0xFF;

            // Idempotent re-assignment must keep existing buffer intact
            account.SecurityToken = rawCookie;
            account.Password = rawPassword;
            Assert.Same(initialTokenBlob, tokenBlobField.GetValue(account));
            Assert.Same(initialPasswordBlob, passwordBlobField.GetValue(account));

            string midCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_eyJ." + shortPayload + ".sig456";
            const string nextPassword = "UpdatedP@ssw0rd#2";
            account.SecurityToken = midCookie;
            account.Password = nextPassword;

            Assert.All(initialTokenBlob, b => Assert.Equal(0, b));
            Assert.All(initialPasswordBlob, b => Assert.Equal(0, b));
            Assert.Equal(midCookie, account.SecurityToken);
            Assert.Equal(nextPassword, account.Password);

            byte[]? midTokenBlob = (byte[]?)tokenBlobField.GetValue(account);
            Assert.NotNull(midTokenBlob);

            string fullPayload = new string('B', 939);
            string rotatedCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_eyJ." + fullPayload + ".sig999";
            Assert.True(account.ApplyRotatedSecurityToken(rotatedCookie, "unit-test"));

            Assert.All(midTokenBlob!, b => Assert.Equal(0, b));
            Assert.Equal(rotatedCookie, account.SecurityToken);
            Assert.False(account.IsShortLivedSession);
            Assert.Equal(SessionHealth.Healthy, account.SessionHealthStatus);

            byte[]? activeTokenBlob = (byte[]?)tokenBlobField.GetValue(account);
            byte[]? activePasswordBlob = (byte[]?)passwordBlobField.GetValue(account);
            Assert.NotNull(activeTokenBlob);
            Assert.NotNull(activePasswordBlob);

            account.ClearSensitiveMemory();

            Assert.All(activeTokenBlob!, b => Assert.Equal(0, b));
            Assert.All(activePasswordBlob!, b => Assert.Equal(0, b));
            Assert.Equal(string.Empty, account.SecurityToken);
            Assert.Equal(string.Empty, account.Password);
            Assert.True(account.HasExpiredShortLivedCookie());
            Assert.Equal(SessionHealth.Expired, account.SessionHealthStatus);
        }

        [Fact]
        public void InMemorySecretProtector_RoundTripsUnicode_AndZeroesBufferOnClear()
        {
            Assert.Null(InMemorySecretProtector.Protect(null));
            Assert.Null(InMemorySecretProtector.Protect(string.Empty));
            Assert.Equal(string.Empty, InMemorySecretProtector.Unprotect(ReadOnlySpan<byte>.Empty));

            const string secret = "Xin chào Shiro 🔒 Пароль 密碼 12345!";
            byte[]? blob1 = InMemorySecretProtector.Protect(secret);
            byte[]? blob2 = InMemorySecretProtector.Protect(secret);

            Assert.NotNull(blob1);
            Assert.NotNull(blob2);
            Assert.NotEqual(blob1, blob2);
            Assert.Equal(secret, InMemorySecretProtector.Unprotect(blob1));
            Assert.Equal(secret, InMemorySecretProtector.Unprotect(blob2));

            blob2![blob2.Length - 1] ^= 0xFF;
            Assert.ThrowsAny<CryptographicException>(() => InMemorySecretProtector.Unprotect(blob2));
            Assert.ThrowsAny<CryptographicException>(() => InMemorySecretProtector.Unprotect(new byte[] { 1, 2, 3 }));

            byte[] retainedReference = blob1!;
            InMemorySecretProtector.ZeroAndClear(ref blob1);

            Assert.Null(blob1);
            Assert.All(retainedReference, b => Assert.Equal(0, b));

            InMemorySecretProtector.ZeroAndClear(ref blob1);
            Assert.Null(blob1);
        }
    }
}
