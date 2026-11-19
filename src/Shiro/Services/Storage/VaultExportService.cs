#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Shiro.Services
{
    /// <summary>
    /// Service for cross-device encrypted vault transfer (export and import).
    /// Uses PBKDF2-SHA256 (600,000 iterations in V2; 300,000 in legacy V1) key derivation, AES-256-GCM authenticated
    /// encryption with zero-memory key wiping, allowing secure transfer independent of DPAPI.
    /// </summary>
    public static class VaultExportService
    {
        private static ReadOnlySpan<byte> HeaderMagic => "SHIRO_VAULT_V2"u8;
        private static ReadOnlySpan<byte> LegacyHeaderMagicV1 => "SHIRO_VAULT_V1"u8;
        private const int SaltSizeBytes = 16;
        private const int NonceSizeBytes = 12;
        private const int TagSizeBytes = 16;
        private const int KeySizeBytes = 32;
        public const int Pbkdf2Iterations = 600_000;
        public const int LegacyV1Pbkdf2Iterations = 300_000;
        public const int MaxEncryptedVaultBytes = 32 * 1024 * 1024;
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
            try
            {
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
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
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
            int iterations;
            if (magic.SequenceEqual(HeaderMagic))
            {
                iterations = Pbkdf2Iterations;
            }
            else if (magic.SequenceEqual(LegacyHeaderMagicV1))
            {
                iterations = LegacyV1Pbkdf2Iterations;
            }
            else
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
                if (remaining < 0 || remaining > MaxEncryptedVaultBytes)
                    throw new InvalidDataException($"Encrypted vault payload exceeds maximum allowed size of {MaxEncryptedVaultBytes} bytes.");
                ciphertext = new byte[(int)remaining];
                source.ReadExactly(ciphertext);
            }
            else
            {
                ciphertext = ReadBoundedNonSeekablePayload(source);
            }

            byte[] plaintext = new byte[ciphertext.Length];
            try
            {
                Span<byte> key = stackalloc byte[KeySizeBytes];
                try
                {
                    Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, key, iterations, HashAlgorithmName.SHA256);
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
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        private static byte[] ReadBoundedNonSeekablePayload(Stream source)
        {
            using var ms = new MemoryStream();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                long totalRead = 0;
                while (true)
                {
                    int toRead = (int)Math.Min(buffer.Length, (MaxEncryptedVaultBytes - totalRead) + 1);
                    int read = source.Read(buffer, 0, toRead);
                    if (read == 0)
                        break;

                    totalRead += read;
                    if (totalRead > MaxEncryptedVaultBytes)
                        throw new InvalidDataException($"Encrypted vault payload exceeds maximum allowed size of {MaxEncryptedVaultBytes} bytes.");

                    ms.Write(buffer, 0, read);
                }

                return ms.ToArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
