using System;
using System.Security.Cryptography;
using System.Text;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
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
    }
}
