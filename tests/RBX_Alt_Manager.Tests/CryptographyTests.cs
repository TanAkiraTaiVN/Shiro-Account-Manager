using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RBX_Alt_Manager.Classes;
using Xunit;

namespace RBX_Alt_Manager.Tests
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
            byte[] decrypted = Cryptography.Decrypt(v2Encrypted, password);
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
    }
}
