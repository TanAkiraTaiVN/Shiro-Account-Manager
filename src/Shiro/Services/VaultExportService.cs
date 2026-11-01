#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Shiro.Classes;

namespace Shiro.Services
{
    /// <summary>
    /// Service for cross-device encrypted vault transfer (export and import).
    /// Uses PBKDF2-SHA256 (300,000 iterations) key derivation, AES-256-GCM authenticated
    /// encryption with zero-memory key wiping, allowing secure transfer independent of DPAPI.
    /// </summary>
    public static class VaultExportService
    {
        private static readonly byte[] HeaderMagic = "SHIRO_VAULT_V1"u8.ToArray();
        private const int SaltSizeBytes = 16;
        private const int NonceSizeBytes = 12;
        private const int TagSizeBytes = 16;
        private const int KeySizeBytes = 32;
        private const int Pbkdf2Iterations = 300_000;
        private const int HeaderPrefixSizeBytes = 14 + SaltSizeBytes + NonceSizeBytes + TagSizeBytes; // 58 bytes

        /// <summary>
        /// Exports the given accounts to the destination stream encrypted with the user-supplied passphrase.
        /// </summary>
        public static void ExportVault(IReadOnlyList<Account> accounts, string passphrase, Stream destination)
        {
            ArgumentNullException.ThrowIfNull(accounts);
            if (string.IsNullOrEmpty(passphrase))
                throw new ArgumentException("Passphrase cannot be null or empty.", nameof(passphrase));
            ArgumentNullException.ThrowIfNull(destination);

            if (!destination.CanWrite)
                throw new ArgumentException("Destination stream must be writable.", nameof(destination));

            byte[] plaintext = AccountDataSerializer.Serialize(accounts, includePasswords: true);

            Span<byte> salt = stackalloc byte[SaltSizeBytes];
            RandomNumberGenerator.Fill(salt);

            Span<byte> nonce = stackalloc byte[NonceSizeBytes];
            RandomNumberGenerator.Fill(nonce);

            Span<byte> tag = stackalloc byte[TagSizeBytes];
            byte[] ciphertext = new byte[plaintext.Length];

            Span<byte> key = stackalloc byte[KeySizeBytes];
            try
            {
                Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, key, Pbkdf2Iterations, HashAlgorithmName.SHA256);
                using var aesGcm = new AesGcm(key, TagSizeBytes);
                aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            destination.Write(HeaderMagic);
            destination.Write(salt);
            destination.Write(nonce);
            destination.Write(tag);
            destination.Write(ciphertext);
            destination.Flush();
        }

        /// <summary>
        /// Imports and decrypts accounts from the source stream using the user-supplied passphrase.
        /// Throws <see cref="CryptographicException"/> if the passphrase is wrong or the ciphertext has been tampered with.
        /// Throws <see cref="InvalidDataException"/> if the header is corrupt or unsupported.
        /// </summary>
        public static IReadOnlyList<Account> ImportVault(Stream source, string passphrase)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (string.IsNullOrEmpty(passphrase))
                throw new ArgumentException("Passphrase cannot be null or empty.", nameof(passphrase));

            if (!source.CanRead)
                throw new ArgumentException("Source stream must be readable.", nameof(source));

            Span<byte> headerBuffer = stackalloc byte[HeaderPrefixSizeBytes];
            try
            {
                source.ReadExactly(headerBuffer);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException("Vault file is truncated or corrupted.");
            }

            ReadOnlySpan<byte> magic = headerBuffer[..HeaderMagic.Length];
            if (!magic.SequenceEqual(HeaderMagic))
            {
                throw new InvalidDataException("Invalid or unsupported vault header magic.");
            }

            ReadOnlySpan<byte> salt = headerBuffer.Slice(HeaderMagic.Length, SaltSizeBytes);
            ReadOnlySpan<byte> nonce = headerBuffer.Slice(HeaderMagic.Length + SaltSizeBytes, NonceSizeBytes);
            ReadOnlySpan<byte> tag = headerBuffer.Slice(HeaderMagic.Length + SaltSizeBytes + NonceSizeBytes, TagSizeBytes);

            byte[] ciphertext;
            if (source.CanSeek)
            {
                long remaining = source.Length - source.Position;
                if (remaining < 0 || remaining > int.MaxValue)
                    throw new InvalidDataException("Invalid ciphertext length in vault stream.");
                ciphertext = new byte[(int)remaining];
                source.ReadExactly(ciphertext);
            }
            else
            {
                using var ms = new MemoryStream();
                source.CopyTo(ms);
                ciphertext = ms.ToArray();
            }

            byte[] plaintext = new byte[ciphertext.Length];
            Span<byte> key = stackalloc byte[KeySizeBytes];
            try
            {
                Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, key, Pbkdf2Iterations, HashAlgorithmName.SHA256);
                using var aesGcm = new AesGcm(key, TagSizeBytes);
                try
                {
                    aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
                }
                catch (AuthenticationTagMismatchException ex)
                {
                    throw new CryptographicException("The computed authentication tag did not match the input authentication tag.", ex);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            return AccountDataSerializer.Deserialize(plaintext, out _);
        }
    }
}
