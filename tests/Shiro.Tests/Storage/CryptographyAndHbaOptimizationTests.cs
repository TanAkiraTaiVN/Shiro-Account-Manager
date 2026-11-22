using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class CryptographyAndHbaOptimizationTests
    {
        [Fact]
        public void EncryptWithRandomKey_ProducesV4Header_AndRoundTripsViaDecrypt()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            byte[] plain = Encoding.UTF8.GetBytes("{\"accounts\":[{\"u\":\"shiro\"}]}");

            byte[] encrypted = Cryptography.EncryptWithRandomKey(plain, key);

            Assert.True(Cryptography.IsEncrypted(encrypted));
            Assert.False(Cryptography.IsLegacyHeader(encrypted));
            Assert.True(encrypted.AsSpan(0, Cryptography.ShiroVaultHeaderV4.Length)
                .SequenceEqual(Cryptography.ShiroVaultHeaderV4));

            byte[] decrypted = Cryptography.Decrypt(encrypted, key);
            Assert.Equal(plain, decrypted);
        }

        [Fact]
        public void EncryptWithRandomKey_InvalidKeyLength_ThrowsArgumentException()
        {
            byte[] shortKey = new byte[16];
            byte[] plain = Encoding.UTF8.GetBytes("payload");

            var ex = Assert.Throws<ArgumentException>(() => Cryptography.EncryptWithRandomKey(plain, shortKey));
            Assert.Contains("32", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Decrypt_SupportsV3Pbkdf2_And_V4Hkdf_Payloads()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            byte[] plain = Encoding.UTF8.GetBytes("secret-account-vault");

            byte[] v3 = Cryptography.Encrypt(plain, key);
            byte[] v4 = Cryptography.EncryptWithRandomKey(plain, key);

            Assert.Equal(plain, Cryptography.Decrypt(v3, key));
            Assert.Equal(plain, Cryptography.Decrypt(v4, key));
        }

        [Fact]
        public void RobloxHbaService_CachesEcParameters_AndClearKeyCacheResetsCleanly()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pkcs8Base64 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());

            bool ok1 = RobloxHbaService.TryBuildBoundAuthToken(
                pkcs8Base64, "https://auth.roblox.com/v1/authentication-ticket/", "POST", "{}", null, 1700000000L, out string token1);
            bool ok2 = RobloxHbaService.TryBuildBoundAuthToken(
                pkcs8Base64, "https://auth.roblox.com/v1/authentication-ticket/", "POST", "{}", null, 1700000000L, out string token2);

            Assert.True(ok1);
            Assert.True(ok2);
            Assert.StartsWith("v1|", token1, StringComparison.Ordinal);
            Assert.StartsWith("v1|", token2, StringComparison.Ordinal);

            RobloxHbaService.ClearKeyCache();

            bool okAfterClear = RobloxHbaService.TryBuildBoundAuthToken(
                pkcs8Base64, "https://auth.roblox.com/v1/authentication-ticket/", "POST", "{}", null, 1700000000L, out string token3);
            Assert.True(okAfterClear);
            Assert.StartsWith("v1|", token3, StringComparison.Ordinal);
        }

        [Fact]
        public void CryptoHelper_ProtectSecretString_FailsClosedOnDpapiError_AndUnprotectRejectsCorruptDpapiPrefix()
        {
            string protectedVal = CryptoHelper.ProtectSecretString("super-secret-token");
            Assert.StartsWith("dpapi:", protectedVal, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-token", protectedVal, StringComparison.Ordinal);
            Assert.Equal("super-secret-token", CryptoHelper.UnprotectSecretString(protectedVal));

            // Corrupt dpapi: payload must fail closed to empty string, never leaking raw "dpapi:..." into headers/URLs
            Assert.Equal(string.Empty, CryptoHelper.UnprotectSecretString("dpapi:not-valid-base64!!!"));
            Assert.Equal(string.Empty, CryptoHelper.UnprotectSecretString("dpapi:AAAA"));

            // Legacy plaintext without prefix remains readable for backward compatibility
            Assert.Equal("legacy-plain", CryptoHelper.UnprotectSecretString("legacy-plain"));

            var prev = CryptoHelper.TestProtectBytesOverride;
            byte[]? capturedPlainBytes = null;
            try
            {
                CryptoHelper.TestProtectBytesOverride = bytes =>
                {
                    capturedPlainBytes = bytes;
                    throw new CryptographicException("DPAPI unavailable");
                };
                Assert.Throws<CryptographicException>(() => CryptoHelper.ProtectSecretString("must-not-leak-plaintext"));
                Assert.NotNull(capturedPlainBytes);
                Assert.All(capturedPlainBytes!, b => Assert.Equal(0, b));
            }
            finally
            {
                CryptoHelper.TestProtectBytesOverride = prev;
            }
        }

        [Fact]
        public void AccountFileDecoder_WipesUnprotectedDpapiBuffer_AfterDeserialization()
        {
            var sample = new List<Account> { new Account { Username = "ZeroMemUser", UserID = 101, SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--SecretCookiePayload12345" } };
            byte[] serialized = AccountDataSerializer.Serialize(sample, includePasswords: true);
            byte[]? capturedPlainBuffer = null;

            var keys = new AccountDecodeKeys(
                GetVaultKeyCopy: () => Array.Empty<byte>(),
                TryRecoverVaultKey: null,
                UnprotectDpapi: _ =>
                {
                    capturedPlainBuffer = (byte[])serialized.Clone();
                    return (capturedPlainBuffer, false);
                },
                PlaintextOptIn: false);

            byte[] callerInput = new byte[] { 0x01, 0x02, 0x03 };
            var result = AccountFileDecoder.Decode(callerInput, null, keys);
            Assert.Equal(AccountDecodeStatus.Loaded, result.Status);
            Assert.Single(result.Accounts);
            Assert.NotNull(capturedPlainBuffer);
            Assert.All(capturedPlainBuffer!, b => Assert.Equal(0, b));
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, callerInput);
        }

        [Fact]
        public void AccountManager_ShutdownBackend_ClearsRobloxHbaKeyCache()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pkcs8Base64 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());

            Assert.True(RobloxHbaService.TryBuildBoundAuthToken(
                pkcs8Base64, "https://auth.roblox.com/v1/authentication-ticket/", "POST", "{}", null, 1700000000L, out _));

            var cacheField = typeof(RobloxHbaService).GetField("CachedEcParameters", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(cacheField);
            var dict = (ConcurrentDictionary<string, ECParameters>)cacheField!.GetValue(null)!;
            string cacheId = RobloxHbaService.ComputeKeyCacheId(pkcs8Base64);
            Assert.False(dict.ContainsKey(pkcs8Base64));
            Assert.True(dict.TryGetValue(cacheId, out ECParameters cachedBefore));
            Assert.NotNull(cachedBefore.D);
            byte[] privateScalarRef = cachedBefore.D!;
            Assert.Contains(privateScalarRef, b => b != 0);

            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroShutdownHba_" + Guid.NewGuid().ToString("N"));
            bool prevSuppress = AccountManager.SuppressExitOnShutdown;
            var prevInstance = AccountManager.Instance;
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    AccountManager.SuppressExitOnShutdown = true;
                    var tempMgr = new AccountManager();
                    tempMgr.ShutdownBackend();
                }

                Assert.False(dict.ContainsKey(cacheId));
                Assert.All(privateScalarRef, b => Assert.Equal(0, b));
            }
            finally
            {
                AccountManager.SuppressExitOnShutdown = prevSuppress;
                AccountManager.Instance = prevInstance;
                RobloxHbaService.ClearKeyCache();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }
    }
}
