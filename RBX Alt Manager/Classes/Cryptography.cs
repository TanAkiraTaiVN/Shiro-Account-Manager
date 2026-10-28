#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Shiro.Classes
{
    /// <summary>
    /// Pure .NET 10 Cryptography engine for Shiro Account Manager.
    /// Utilizes authenticated AES-256-GCM encryption with PBKDF2 (SHA-256, 210,000 iterations).
    /// Zero third-party dependencies, maximum performance and enterprise-grade security.
    /// </summary>
    internal static class Cryptography
    {
        // Modern Shiro Primary Vault Header
        public static readonly byte[] ShiroVaultHeader = Encoding.UTF8.GetBytes("SHIRO_SECURE_VAULT_V1_AES_GCM_PBKDF2_210K_SHA256_AUTHENTICATED_STORE");

        // Backward-compatible V2 RAM Vault Header
        public static readonly byte[] RAMHeaderV2 = Encoding.UTF8.GetBytes("RAM_SECURE_VAULT_V2_AES_GCM_PBKDF2_210K_SHA256_AUTHENTICATED_STORE");

        private const int SaltSize = 16;
        private const int NonceSize = 12; // 96-bit standard for AES-GCM
        private const int TagSize = 16;   // 128-bit authentication tag
        private const int KeySize = 32;   // 256-bit AES key
        private const int Pbkdf2Iterations = 210000;

        /// <summary>
        /// Checks whether the given byte stream begins with any known authenticated Shiro/RAM encryption header.
        /// </summary>
        public static bool IsEncrypted(ReadOnlySpan<byte> data)
        {
            if (data.Length >= ShiroVaultHeader.Length && data.Slice(0, ShiroVaultHeader.Length).SequenceEqual(ShiroVaultHeader))
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
        /// Decrypts data authenticated with AES-256-GCM (ShiroVaultHeader or RAMHeaderV2).
        /// </summary>
        public static byte[] Decrypt(byte[] encrypted, byte[] password)
        {
            if (encrypted == null || encrypted.Length == 0) throw new ArgumentNullException(nameof(encrypted));
            if (password == null || password.Length == 0) throw new ArgumentNullException(nameof(password));

            var dataSpan = new ReadOnlySpan<byte>(encrypted);
            int headerLength;

            if (dataSpan.Length >= ShiroVaultHeader.Length && dataSpan.Slice(0, ShiroVaultHeader.Length).SequenceEqual(ShiroVaultHeader))
            {
                headerLength = ShiroVaultHeader.Length;
            }
            else if (dataSpan.Length >= RAMHeaderV2.Length && dataSpan.Slice(0, RAMHeaderV2.Length).SequenceEqual(RAMHeaderV2))
            {
                headerLength = RAMHeaderV2.Length;
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

            byte[] derivedKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
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