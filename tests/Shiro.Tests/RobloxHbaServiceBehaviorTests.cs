#nullable enable
using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class RobloxHbaServiceBehaviorTests
    {
        private static string GenerateTestP256Key()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
        }

        [Fact]
        public void IsValidPkcs8EcdsaKey_WithValidKey_ReturnsTrue()
        {
            string validKey = GenerateTestP256Key();
            bool result = RobloxHbaService.IsValidPkcs8EcdsaKey(validKey);
            Assert.True(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-a-valid-base64-key!!!")]
        [InlineData("AAAA")]
        public void IsValidPkcs8EcdsaKey_WithInvalidKey_ReturnsFalse(string? key)
        {
            bool result = RobloxHbaService.IsValidPkcs8EcdsaKey(key);
            Assert.False(result);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("no-browserid-here", null)]
        [InlineData("browserid=1234567890", "1234567890")]
        [InlineData(".ROBLOSECURITY=abc; browserid=987654321; path=/", "987654321")]
        [InlineData("&browserid=5551234&foo=bar", "5551234")]
        public void ExtractBrowserTrackerIdFromCookie_ExtractsCorrectId(string? cookie, string? expected)
        {
            string? result = RobloxHbaService.ExtractBrowserTrackerIdFromCookie(cookie);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void GetLocalNetworkSignature_ReturnsNonEmptyHexOrOffline()
        {
            string signature = RobloxHbaService.GetLocalNetworkSignature();
            Assert.False(string.IsNullOrWhiteSpace(signature));
            Assert.True(signature.Length >= 6);
        }

        [Fact]
        public void InteractiveLoginScope_IncrementsAndDecrementsCount()
        {
            bool initial = RobloxHbaService.IsInteractiveLoginActive;
            using (RobloxHbaService.BeginInteractiveLoginScope())
            {
                Assert.True(RobloxHbaService.IsInteractiveLoginActive);
            }
            Assert.Equal(initial, RobloxHbaService.IsInteractiveLoginActive);
        }

        [Fact]
        public void CaptureSnapshot_PropertiesRoundTrip()
        {
            var snapshot = new RobloxHbaService.CaptureSnapshot
            {
                PrivateKeyPkcs8Base64 = "pkcs8",
                PublicKeySpkiBase64 = "spki",
                TokenTemplate = "v1|b64|HB,TS,URL,M",
                UserAgent = "TestAgent/1.0",
                BrowserTrackerId = "998877"
            };

            Assert.Equal("pkcs8", snapshot.PrivateKeyPkcs8Base64);
            Assert.Equal("spki", snapshot.PublicKeySpkiBase64);
            Assert.Equal("v1|b64|HB,TS,URL,M", snapshot.TokenTemplate);
            Assert.Equal("TestAgent/1.0", snapshot.UserAgent);
            Assert.Equal("998877", snapshot.BrowserTrackerId);
            Assert.True(snapshot.HasPrivateKey);

            snapshot.PrivateKeyPkcs8Base64 = "";
            Assert.False(snapshot.HasPrivateKey);
        }

        [Fact]
        public void GetBrowserCaptureScript_ReturnsJavaScriptContainingExpectedHooks()
        {
            string script = RobloxHbaService.GetBrowserCaptureScript();
            Assert.Contains("SubtleCrypto", script);
            Assert.Contains("generateKey", script);
            Assert.Contains("extractable", script);
        }

        [Fact]
        public void GetPersistedKeyInjectionScript_WithNullOrEmpty_ReturnsEmptyString()
        {
            Assert.Equal(string.Empty, RobloxHbaService.GetPersistedKeyInjectionScript(null, null, null));
            Assert.Equal(string.Empty, RobloxHbaService.GetPersistedKeyInjectionScript("", "", ""));
        }

        [Fact]
        public void GetPersistedKeyInjectionScript_WithValidKey_ReturnsScriptInjectingKey()
        {
            string key = GenerateTestP256Key();
            string script = RobloxHbaService.GetPersistedKeyInjectionScript(key, "spki-sample", "v1|b64|HB,TS,URL,M");
            Assert.Contains("window.__shiroHbaState", script);
            Assert.Contains(key, script);
            Assert.Contains("spki-sample", script);
        }

        [Fact]
        public void TryBuildBoundAuthToken_WithNullKeyOrUrl_ReturnsFalse()
        {
            string key = GenerateTestP256Key();
            bool result1 = RobloxHbaService.TryBuildBoundAuthToken(null, "https://roblox.com", "GET", null, null, null, out string token1);
            Assert.False(result1);
            Assert.Equal(string.Empty, token1);

            bool result2 = RobloxHbaService.TryBuildBoundAuthToken(key, null, "GET", null, null, null, out string token2);
            Assert.False(result2);
            Assert.Equal(string.Empty, token2);
        }

        [Fact]
        public void TryBuildBoundAuthToken_WithValidKey_SignsDefaultTemplate()
        {
            string key = GenerateTestP256Key();
            string url = "https://auth.roblox.com/v1/authentication-ticket";
            long timestamp = 1700000000L;

            bool success = RobloxHbaService.TryBuildBoundAuthToken(
                key,
                url,
                "POST",
                "{\"sample\":\"body\"}",
                null,
                timestamp,
                out string token);

            Assert.True(success);
            Assert.NotEmpty(token);

            string[] parts = token.Split('|');
            Assert.True(parts.Length >= 4);
            Assert.Equal("v1", parts[0]);
            Assert.Equal("1700000000", parts[2]);
        }

        [Fact]
        public void TryBuildBoundAuthToken_WithHexTemplateAndCustomSpec_SignsCorrectly()
        {
            string key = GenerateTestP256Key();
            string url = "https://apis.roblox.com/user/info?id=123";
            string customTemplate = "v2|hex|HB,TS,PATH,M";
            long timestamp = 1711111111L;

            bool success = RobloxHbaService.TryBuildBoundAuthToken(
                key,
                url,
                "GET",
                null,
                customTemplate,
                timestamp,
                out string token);

            Assert.True(success);
            string[] parts = token.Split('|');
            Assert.Equal("v2", parts[0]);
            Assert.Equal("1711111111", parts[2]);
        }

        [Fact]
        public void GenerateBoundAuthToken_WithAccountMissingKey_ReturnsNull()
        {
            var acc = new Account { Username = "TestAcc" };
            string? token = RobloxHbaService.GenerateBoundAuthToken(acc, "https://roblox.com", "GET");
            Assert.Null(token);
        }

        [Fact]
        public void GenerateBoundAuthToken_WithAccountHavingKey_ReturnsSignedToken()
        {
            string key = GenerateTestP256Key();
            var acc = new Account { Username = "TestAcc", HbaPrivateKeyPkcs8 = key };

            string? token = RobloxHbaService.GenerateBoundAuthToken(acc, "https://roblox.com/test", "POST", "body");
            Assert.NotNull(token);
            Assert.StartsWith("v1|", token!);
        }

        [Fact]
        public void TryAttachHbaHeader_WithAccountMissingKey_ReturnsFalse()
        {
            var acc = new Account { Username = "TestAcc" };
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://roblox.com/v1/users");

            bool attached = RobloxHbaService.TryAttachHbaHeader(request, acc);
            Assert.False(attached);
            Assert.False(request.Headers.Contains(RobloxHbaService.BoundAuthHeaderName));
        }

        [Fact]
        public void TryAttachHbaHeader_WithAccountHavingKey_AttachesHeader()
        {
            string key = GenerateTestP256Key();
            var acc = new Account { Username = "TestAcc", HbaPrivateKeyPkcs8 = key };
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://roblox.com/v1/auth");

            bool attached = RobloxHbaService.TryAttachHbaHeader(request, acc, "{\"test\":true}");
            Assert.True(attached);
            Assert.True(request.Headers.Contains(RobloxHbaService.BoundAuthHeaderName));
        }

        [Fact]
        public async Task TryAttachHbaHeaderAsync_WithContent_AttachesHeader()
        {
            string key = GenerateTestP256Key();
            var acc = new Account { Username = "TestAcc", HbaPrivateKeyPkcs8 = key };
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://roblox.com/v1/login")
            {
                Content = new StringContent("{\"user\":\"test\"}", Encoding.UTF8, "application/json")
            };

            bool attached = await RobloxHbaService.TryAttachHbaHeaderAsync(request, acc);
            Assert.True(attached);
            Assert.True(request.Headers.Contains(RobloxHbaService.BoundAuthHeaderName));
        }
    }
}
