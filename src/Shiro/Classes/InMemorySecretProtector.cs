#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Shiro.Classes
{
    /// <summary>
    /// Protects sensitive <see cref="Account"/> secrets (<c>SecurityToken</c> and <c>Password</c>)
    /// at rest in process RAM using authenticated AES-256-GCM under a per-process ephemeral key,
    /// ensuring plaintext credentials are not retained in long-lived heap string fields.
    /// </summary>
    internal static class InMemorySecretProtector
    {
        private const int KeySize = 32;
        private const int NonceSize = 12;
        private const int TagSize = 16;

        private static readonly byte[] ProcessKey = RandomNumberGenerator.GetBytes(KeySize);

        public static byte[]? Protect(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext))
                return null;

            int byteCount = Encoding.UTF8.GetByteCount(plaintext);
            byte[] plainBytes = new byte[byteCount];
            try
            {
                Encoding.UTF8.GetBytes(plaintext, plainBytes);
                byte[] blob = new byte[NonceSize + TagSize + byteCount];
                Span<byte> blobSpan = blob;
                Span<byte> nonce = blobSpan.Slice(0, NonceSize);
                Span<byte> tag = blobSpan.Slice(NonceSize, TagSize);
                Span<byte> ciphertext = blobSpan.Slice(NonceSize + TagSize, byteCount);

                RandomNumberGenerator.Fill(nonce);
                using var aesGcm = new AesGcm(ProcessKey, TagSize);
                aesGcm.Encrypt(nonce, plainBytes, ciphertext, tag);
                return blob;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }

        public static string Unprotect(ReadOnlySpan<byte> protectedBlob)
        {
            if (protectedBlob.IsEmpty)
                return string.Empty;

            if (protectedBlob.Length < NonceSize + TagSize)
                throw new CryptographicException("Protected secret blob is truncated.");

            int cipherLength = protectedBlob.Length - NonceSize - TagSize;
            ReadOnlySpan<byte> nonce = protectedBlob.Slice(0, NonceSize);
            ReadOnlySpan<byte> tag = protectedBlob.Slice(NonceSize, TagSize);
            ReadOnlySpan<byte> ciphertext = protectedBlob.Slice(NonceSize + TagSize, cipherLength);

            byte[] plainBytes = new byte[cipherLength];
            try
            {
                using var aesGcm = new AesGcm(ProcessKey, TagSize);
                aesGcm.Decrypt(nonce, ciphertext, tag, plainBytes);
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }

        public static void ZeroAndClear(ref byte[]? protectedBlob)
        {
            byte[]? existing = Interlocked.Exchange(ref protectedBlob, null);
            if (existing != null)
            {
                CryptographicOperations.ZeroMemory(existing);
            }
        }
    }
}
