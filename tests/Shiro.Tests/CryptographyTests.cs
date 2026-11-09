using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class CryptographyTests
    {
        [Fact]
        public void EncryptAndDecrypt_RoundTrip_ReturnsOriginalPlainText()
        {
            // Arrange
            string originalData = "{\"Username\":\"RobloxPro123\",\"UserID\":12345678,\"SecurityToken\":\"_|WARNING:-DO-NOT-SHARE-THIS...\"}";
            byte[] masterKey = Encoding.UTF8.GetBytes("SuperSecretMasterPassword2026!");

            // Act
            byte[] encrypted = Cryptography.Encrypt(originalData, masterKey);
            byte[] decryptedBytes = Cryptography.Decrypt(encrypted, masterKey);
            string decryptedText = Encoding.UTF8.GetString(decryptedBytes);

            // Assert
            Assert.NotNull(encrypted);
            Assert.True(encrypted.Length > originalData.Length);
            Assert.Equal(originalData, decryptedText);
        }

        [Fact]
        public void IsEncrypted_ReturnsTrue_ForEncryptedPayload()
        {
            // Arrange
            string content = "SampleContent";
            byte[] password = Encoding.UTF8.GetBytes("password123");

            // Act
            byte[] encrypted = Cryptography.Encrypt(content, password);

            // Assert
            Assert.True(Cryptography.IsEncrypted(encrypted));
        }

        [Fact]
        public void IsEncrypted_ReturnsFalse_ForPlaintextContent()
        {
            // Arrange
            byte[] plain = Encoding.UTF8.GetBytes("{\"Accounts\": []}");

            // Assert
            Assert.False(Cryptography.IsEncrypted(plain));
        }

        [Fact]
        public void Decrypt_WithWrongPassword_ThrowsCryptographicException()
        {
            // Arrange
            string originalData = "ImportantPayload";
            byte[] correctPassword = Encoding.UTF8.GetBytes("CorrectPassword123");
            byte[] wrongPassword = Encoding.UTF8.GetBytes("WrongPassword999");

            byte[] encrypted = Cryptography.Encrypt(originalData, correctPassword);

            // Act & Assert
            Assert.ThrowsAny<CryptographicException>(() => Cryptography.Decrypt(encrypted, wrongPassword));
        }

        [Fact]
        public void Encrypt_Produces_ShiroVaultHeader()
        {
            byte[] password = Encoding.UTF8.GetBytes("TestPass");
            byte[] encrypted = Cryptography.Encrypt("{\"accounts\":[]}", password);

            Assert.True(Cryptography.IsEncrypted(encrypted));
            Assert.StartsWith(Encoding.UTF8.GetString(Cryptography.ShiroVaultHeader), Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);
        }

        [Fact]
        public void Decrypt_RAMHeaderV2_WorksForBackwardCompatibility()
        {
            // Simulate legacy RAMHeaderV2 payload
            string original = "{\"Legacy\":\"Data\"}";
            byte[] password = Encoding.UTF8.GetBytes("Password123");
            byte[] plaintextBytes = Encoding.UTF8.GetBytes(original);

            byte[] salt = new byte[16];
            byte[] nonce = new byte[12];
            RandomNumberGenerator.Fill(salt);
            RandomNumberGenerator.Fill(nonce);

            byte[] derivedKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32);
            byte[] ciphertext = new byte[plaintextBytes.Length];
            byte[] tag = new byte[16];

            using (var aesGcm = new AesGcm(derivedKey, 16))
            {
                aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);
            }

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(Cryptography.RAMHeaderV2);
            writer.Write(salt);
            writer.Write(nonce);
            writer.Write(tag);
            writer.Write(ciphertext);
            byte[] v2Encrypted = ms.ToArray();

            Assert.True(Cryptography.IsEncrypted(v2Encrypted));
            Assert.True(Cryptography.IsLegacyHeader(v2Encrypted));
            byte[] decrypted = Cryptography.Decrypt(v2Encrypted, password);
            Assert.Equal(original, Encoding.UTF8.GetString(decrypted));
        }

        [Fact]
        public void Decrypt_ShiroVaultHeaderV1_WorksForBackwardCompatibilityAndFlagsLegacy()
        {
            string original = "{\"LegacyV1\":\"Data210K\"}";
            byte[] password = Encoding.UTF8.GetBytes("PasswordV1_210K!");
            byte[] plaintextBytes = Encoding.UTF8.GetBytes(original);

            byte[] salt = new byte[16];
            byte[] nonce = new byte[12];
            RandomNumberGenerator.Fill(salt);
            RandomNumberGenerator.Fill(nonce);

            byte[] derivedKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, Cryptography.LegacyPbkdf2Iterations, HashAlgorithmName.SHA256, 32);
            byte[] ciphertext = new byte[plaintextBytes.Length];
            byte[] tag = new byte[16];

            using (var aesGcm = new AesGcm(derivedKey, 16))
            {
                aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);
            }

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(Cryptography.ShiroVaultHeaderV1);
            writer.Write(salt);
            writer.Write(nonce);
            writer.Write(tag);
            writer.Write(ciphertext);
            byte[] v1Encrypted = ms.ToArray();

            Assert.True(Cryptography.IsEncrypted(v1Encrypted));
            Assert.True(Cryptography.IsLegacyHeader(v1Encrypted));

            byte[] v3Encrypted = Cryptography.Encrypt(original, password);
            Assert.False(Cryptography.IsLegacyHeader(v3Encrypted));

            byte[] decrypted = Cryptography.Decrypt(v1Encrypted, password);
            Assert.Equal(original, Encoding.UTF8.GetString(decrypted));
        }

        [Fact]
        public void Encrypt_SpanOverload_RoundtripsSuccessfully()
        {
            byte[] rawBytes = Encoding.UTF8.GetBytes("{\"Accounts\":[{\"Username\":\"Player1\"}]}");
            byte[] password = Encoding.UTF8.GetBytes("SpanPassword2026!");

            byte[] encrypted = Cryptography.Encrypt(rawBytes.AsSpan(), password);
            Assert.True(Cryptography.IsEncrypted(encrypted));

            byte[] decrypted = Cryptography.Decrypt(encrypted, password);
            Assert.Equal(rawBytes, decrypted);
        }

        [Fact]
        public void EncryptAndDecrypt_EdgeCasesAndValidation_ThrowsExpectedExceptions()
        {
            byte[] validKey = Encoding.UTF8.GetBytes("ValidPassword123!");
            byte[] validData = Encoding.UTF8.GetBytes("ValidPayloadData");

            Assert.Throws<ArgumentNullException>(() => Cryptography.Encrypt(ReadOnlySpan<byte>.Empty, validKey));
            Assert.Throws<ArgumentNullException>(() => Cryptography.Encrypt((string)null!, validKey));
            Assert.Throws<ArgumentNullException>(() => Cryptography.Encrypt(string.Empty, validKey));
            Assert.Throws<ArgumentNullException>(() => Cryptography.Encrypt(validData.AsSpan(), null!));
            Assert.Throws<ArgumentNullException>(() => Cryptography.Encrypt(validData.AsSpan(), Array.Empty<byte>()));

            Assert.Throws<ArgumentNullException>(() => Cryptography.Decrypt(null!, validKey));
            Assert.Throws<ArgumentNullException>(() => Cryptography.Decrypt(Array.Empty<byte>(), validKey));
            Assert.Throws<ArgumentNullException>(() => Cryptography.Decrypt(validData, null!));
            Assert.Throws<ArgumentNullException>(() => Cryptography.Decrypt(validData, Array.Empty<byte>()));

            // Invalid header
            byte[] badHeader = Encoding.UTF8.GetBytes("INVALID_VAULT_HEADER_DATA_12345678901234567890");
            Assert.Throws<ArgumentException>(() => Cryptography.Decrypt(badHeader, validKey));

            // Truncated payload with valid header but shorter than Salt + Nonce + Tag
            byte[] truncated = new byte[Cryptography.ShiroVaultHeader.Length + 10];
            Buffer.BlockCopy(Cryptography.ShiroVaultHeader, 0, truncated, 0, Cryptography.ShiroVaultHeader.Length);
            Assert.Throws<InvalidDataException>(() => Cryptography.Decrypt(truncated, validKey));
        }

        [Fact]
        public void Encrypt_ProducesUniqueSaltAndNonce_ForIdenticalPlaintext()
        {
            byte[] payload = Encoding.UTF8.GetBytes("IdenticalPayloadToTestNonceUniqueness");
            byte[] password = Encoding.UTF8.GetBytes("SharedPassword123!");

            byte[] encrypted1 = Cryptography.Encrypt(payload.AsSpan(), password);
            byte[] encrypted2 = Cryptography.Encrypt(payload.AsSpan(), password);

            Assert.NotEqual(encrypted1, encrypted2);

            byte[] decrypted1 = Cryptography.Decrypt(encrypted1, password);
            byte[] decrypted2 = Cryptography.Decrypt(encrypted2, password);
            Assert.Equal(payload, decrypted1);
            Assert.Equal(payload, decrypted2);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(16)]
        [InlineData(64)]
        [InlineData(1024)]
        public void EncryptDecrypt_VariousPayloadSizes_RoundtripsSuccessfully(int size)
        {
            byte[] data = new byte[size];
            RandomNumberGenerator.Fill(data);
            byte[] password = Encoding.UTF8.GetBytes("VariedSizeKey2026!");

            byte[] encrypted = Cryptography.Encrypt(data.AsSpan(), password);
            byte[] decrypted = Cryptography.Decrypt(encrypted, password);

            Assert.Equal(data, decrypted);
        }

        [Fact]
        public void Decrypt_TamperedCiphertextOrTag_ThrowsCryptographicException()
        {
            byte[] data = Encoding.UTF8.GetBytes("TamperTestingPayload");
            byte[] password = Encoding.UTF8.GetBytes("TamperPassword!");

            byte[] encrypted = Cryptography.Encrypt(data.AsSpan(), password);

            // Tamper with the last byte (part of ciphertext)
            byte[] tamperedCiphertext = (byte[])encrypted.Clone();
            tamperedCiphertext[^1] ^= 0xFF;
            Assert.ThrowsAny<CryptographicException>(() => Cryptography.Decrypt(tamperedCiphertext, password));

            // Tamper with authentication tag
            byte[] tamperedTag = (byte[])encrypted.Clone();
            int tagOffset = Cryptography.ShiroVaultHeader.Length + 16 + 12; // Header + Salt + Nonce
            tamperedTag[tagOffset] ^= 0x01;
            Assert.ThrowsAny<CryptographicException>(() => Cryptography.Decrypt(tamperedTag, password));
        }

        [Fact]
        public void CryptoHelper_SHA256_ComputesCorrectlyForSmallAndLargeStrings()
        {
            // Empty
            Assert.Equal(string.Empty, Services.CryptoHelper.SHA256(string.Empty));
            Assert.Equal(string.Empty, Services.CryptoHelper.SHA256(null!));

            // Small string (stackalloc path <= 512 bytes)
            string small = "ShiroAccountManagerSmall";
            string hashSmall = Services.CryptoHelper.SHA256(small);
            Assert.NotEmpty(hashSmall);
            Assert.Equal(64, hashSmall.Length);

            // Large string (> 512 UTF8 bytes, ArrayPool path)
            string large = new string('A', 1000);
            string hashLarge = Services.CryptoHelper.SHA256(large);
            Assert.NotEmpty(hashLarge);
            Assert.Equal(64, hashLarge.Length);

            // Verify with standard framework hash
            byte[] expectedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(large));
            string expectedHex = Convert.ToHexString(expectedBytes);
            Assert.Equal(expectedHex, hashLarge);
        }

        [Fact]
        public void CryptoHelper_FileSHA256_ComputesCorrectHashOrReturnsMarker()
        {
            // Non-existent file
            string nonExistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tmp");
            string missingHash = Services.CryptoHelper.FileSHA256(nonExistent);
            Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", missingHash);

            // Real file
            string tempFile = Path.Combine(Path.GetTempPath(), "crypto_test_" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(tempFile, "Testing File SHA256 with Shiro", Encoding.UTF8);
                string fileHash = Services.CryptoHelper.FileSHA256(tempFile);
                byte[] expectedBytes = SHA256.HashData(File.ReadAllBytes(tempFile));
                string expectedHex = Convert.ToHexString(expectedBytes);
                Assert.Equal(expectedHex, fileHash);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }
    }
}
