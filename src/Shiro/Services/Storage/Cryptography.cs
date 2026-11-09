#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Shiro.Services
{
    /// <summary>
    /// Pure .NET 10 Cryptography engine for Shiro Account Manager.
    /// Utilizes authenticated AES-256-GCM encryption with PBKDF2 (SHA-256, 600,000 iterations - OWASP standard).
    /// Zero third-party dependencies, maximum performance and enterprise-grade security.
    /// </summary>
    internal static class Cryptography
    {
        // Modern Shiro Fast Vault Header (V4 - HKDF-SHA256 for 256-bit high-entropy random vault keys)
        public static readonly byte[] ShiroVaultHeaderV4 = Encoding.UTF8.GetBytes("SHIRO_SECURE_VAULT_V4_AES_GCM_HKDF_SHA256_AUTHENTICATED_STORE");
        private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("Shiro.Vault.AES256GCM.v4");

        // Modern Shiro Primary Vault Header (V3 - 600,000 PBKDF2-SHA256 iterations)
        public static readonly byte[] ShiroVaultHeaderV3 = Encoding.UTF8.GetBytes("SHIRO_SECURE_VAULT_V3_AES_GCM_PBKDF2_600K_SHA256_AUTHENTICATED_STORE");

        // Default active header points to V3
        public static readonly byte[] ShiroVaultHeader = ShiroVaultHeaderV3;

        // Backward-compatible V1 Shiro Vault Header (210,000 iterations)
        public static readonly byte[] ShiroVaultHeaderV1 = Encoding.UTF8.GetBytes("SHIRO_SECURE_VAULT_V1_AES_GCM_PBKDF2_210K_SHA256_AUTHENTICATED_STORE");

        // Backward-compatible V2 RAM Vault Header (210,000 iterations)
        public static readonly byte[] RAMHeaderV2 = Encoding.UTF8.GetBytes("RAM_SECURE_VAULT_V2_AES_GCM_PBKDF2_210K_SHA256_AUTHENTICATED_STORE");

        private const int SaltSize = 16;
        private const int NonceSize = 12; // 96-bit standard for AES-GCM
        private const int TagSize = 16;   // 128-bit authentication tag
        private const int KeySize = 32;   // 256-bit AES key
        public const int Pbkdf2Iterations = 600_000;
        public const int LegacyPbkdf2Iterations = 210_000;

        /// <summary>
        /// Checks whether the given byte stream begins with any known authenticated Shiro/RAM encryption header.
        /// </summary>
        public static bool IsEncrypted(ReadOnlySpan<byte> data)
        {
            if (data.Length >= ShiroVaultHeaderV4.Length && data.Slice(0, ShiroVaultHeaderV4.Length).SequenceEqual(ShiroVaultHeaderV4))
                return true;

            if (data.Length >= ShiroVaultHeaderV3.Length && data.Slice(0, ShiroVaultHeaderV3.Length).SequenceEqual(ShiroVaultHeaderV3))
                return true;

            if (data.Length >= ShiroVaultHeaderV1.Length && data.Slice(0, ShiroVaultHeaderV1.Length).SequenceEqual(ShiroVaultHeaderV1))
                return true;

            if (data.Length >= RAMHeaderV2.Length && data.Slice(0, RAMHeaderV2.Length).SequenceEqual(RAMHeaderV2))
                return true;

            return false;
        }

        /// <summary>
        /// Returns true if the encrypted payload uses a legacy 210k-iteration header (V1 or RAM V2)
        /// that should be transparently migrated to V3 (600k iterations) on next save.
        /// </summary>
        public static bool IsLegacyHeader(ReadOnlySpan<byte> data)
        {
            if (data.Length >= ShiroVaultHeaderV1.Length && data.Slice(0, ShiroVaultHeaderV1.Length).SequenceEqual(ShiroVaultHeaderV1))
                return true;

            if (data.Length >= RAMHeaderV2.Length && data.Slice(0, RAMHeaderV2.Length).SequenceEqual(RAMHeaderV2))
                return true;

            return false;
        }

        /// <summary>
        /// Encrypts byte content using high-grade AES-256-GCM with PBKDF2 key derivation.
        /// Built entirely on pure .NET 10 System.Security.Cryptography.
        /// </summary>
        public static byte[] Encrypt(ReadOnlySpan<byte> plaintextBytes, byte[] password)
        {
            if (plaintextBytes.IsEmpty) throw new ArgumentNullException(nameof(plaintextBytes));
            if (password == null || password.Length == 0) throw new ArgumentNullException(nameof(password));

            int headerLen = ShiroVaultHeader.Length;
            int totalSize = headerLen + SaltSize + NonceSize + TagSize + plaintextBytes.Length;
            byte[] output = new byte[totalSize];
            Span<byte> outputSpan = output.AsSpan();

            ShiroVaultHeader.AsSpan().CopyTo(outputSpan.Slice(0, headerLen));
            Span<byte> salt = outputSpan.Slice(headerLen, SaltSize);
            Span<byte> nonce = outputSpan.Slice(headerLen + SaltSize, NonceSize);
            Span<byte> tag = outputSpan.Slice(headerLen + SaltSize + NonceSize, TagSize);
            Span<byte> ciphertext = outputSpan.Slice(headerLen + SaltSize + NonceSize + TagSize, plaintextBytes.Length);

            RandomNumberGenerator.Fill(salt);
            RandomNumberGenerator.Fill(nonce);

            byte[] derivedKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
            try
            {
                using (var aesGcm = new AesGcm(derivedKey, TagSize))
                {
                    aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derivedKey);
            }

            return output;
        }

        /// <summary>
        /// Encrypts string content using high-grade AES-256-GCM with PBKDF2 key derivation.
        /// </summary>
        public static byte[] Encrypt(string content, byte[] password)
        {
            if (string.IsNullOrEmpty(content)) throw new ArgumentNullException(nameof(content));
            byte[] plaintextBytes = Encoding.UTF8.GetBytes(content);
            try
            {
                return Encrypt(plaintextBytes, password);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }

        /// <summary>
        /// Encrypts byte content using AES-256-GCM with RFC 5869 HKDF-SHA256 key derivation
        /// for an already high-entropy 256-bit (32-byte) random vault key.
        /// </summary>
        public static byte[] EncryptWithRandomKey(ReadOnlySpan<byte> plaintextBytes, ReadOnlySpan<byte> randomKey256)
        {
            if (plaintextBytes.IsEmpty) throw new ArgumentNullException(nameof(plaintextBytes));
            if (randomKey256.Length != KeySize)
                throw new ArgumentException($"Random vault key must be exactly {KeySize} bytes (256-bit).", nameof(randomKey256));

            int headerLen = ShiroVaultHeaderV4.Length;
            int totalSize = headerLen + SaltSize + NonceSize + TagSize + plaintextBytes.Length;
            byte[] output = new byte[totalSize];
            Span<byte> outputSpan = output.AsSpan();

            ShiroVaultHeaderV4.AsSpan().CopyTo(outputSpan.Slice(0, headerLen));
            Span<byte> salt = outputSpan.Slice(headerLen, SaltSize);
            Span<byte> nonce = outputSpan.Slice(headerLen + SaltSize, NonceSize);
            Span<byte> tag = outputSpan.Slice(headerLen + SaltSize + NonceSize, TagSize);
            Span<byte> ciphertext = outputSpan.Slice(headerLen + SaltSize + NonceSize + TagSize, plaintextBytes.Length);

            RandomNumberGenerator.Fill(salt);
            RandomNumberGenerator.Fill(nonce);

            byte[] derivedKey = new byte[KeySize];
            try
            {
                HKDF.DeriveKey(HashAlgorithmName.SHA256, randomKey256, derivedKey, salt, HkdfInfo);
                using (var aesGcm = new AesGcm(derivedKey, TagSize))
                {
                    aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derivedKey);
            }

            return output;
        }

        /// <summary>
        /// Decrypts data authenticated with AES-256-GCM (ShiroVaultHeaderV4, ShiroVaultHeaderV3, V1, or RAMHeaderV2).
        /// </summary>
        public static byte[] Decrypt(byte[] encrypted, byte[] password)
        {
            if (encrypted == null || encrypted.Length == 0) throw new ArgumentNullException(nameof(encrypted));
            if (password == null || password.Length == 0) throw new ArgumentNullException(nameof(password));

            var dataSpan = new ReadOnlySpan<byte>(encrypted);
            int headerLength;
            int iterations = 0;
            bool useHkdf = false;

            if (dataSpan.Length >= ShiroVaultHeaderV4.Length && dataSpan.Slice(0, ShiroVaultHeaderV4.Length).SequenceEqual(ShiroVaultHeaderV4))
            {
                headerLength = ShiroVaultHeaderV4.Length;
                useHkdf = true;
            }
            else if (dataSpan.Length >= ShiroVaultHeaderV3.Length && dataSpan.Slice(0, ShiroVaultHeaderV3.Length).SequenceEqual(ShiroVaultHeaderV3))
            {
                headerLength = ShiroVaultHeaderV3.Length;
                iterations = Pbkdf2Iterations;
            }
            else if (dataSpan.Length >= ShiroVaultHeaderV1.Length && dataSpan.Slice(0, ShiroVaultHeaderV1.Length).SequenceEqual(ShiroVaultHeaderV1))
            {
                headerLength = ShiroVaultHeaderV1.Length;
                iterations = LegacyPbkdf2Iterations;
            }
            else if (dataSpan.Length >= RAMHeaderV2.Length && dataSpan.Slice(0, RAMHeaderV2.Length).SequenceEqual(RAMHeaderV2))
            {
                headerLength = RAMHeaderV2.Length;
                iterations = LegacyPbkdf2Iterations;
            }
            else
            {
                throw new ArgumentException("The data does not contain a recognized Shiro encryption header.");
            }

            int offset = headerLength;
            if (encrypted.Length < offset + SaltSize + NonceSize + TagSize)
                throw new InvalidDataException("Corrupted encrypted data payload.");

            ReadOnlySpan<byte> salt = dataSpan.Slice(offset, SaltSize);
            offset += SaltSize;

            ReadOnlySpan<byte> nonce = dataSpan.Slice(offset, NonceSize);
            offset += NonceSize;

            ReadOnlySpan<byte> tag = dataSpan.Slice(offset, TagSize);
            offset += TagSize;

            int cipherLength = encrypted.Length - offset;
            ReadOnlySpan<byte> ciphertext = dataSpan.Slice(offset, cipherLength);

            byte[] derivedKey;
            if (useHkdf)
            {
                derivedKey = new byte[KeySize];
                HKDF.DeriveKey(HashAlgorithmName.SHA256, password, derivedKey, salt, HkdfInfo);
            }
            else
            {
                derivedKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);
            }

            byte[] plaintext = new byte[cipherLength];
            try
            {
                using (var aesGcm = new AesGcm(derivedKey, TagSize))
                {
                    aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derivedKey);
            }

            return plaintext;
        }
    }
}