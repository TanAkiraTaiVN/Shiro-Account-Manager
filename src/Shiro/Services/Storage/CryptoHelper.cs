#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Shiro.Services
{
    /// <summary>
    /// Modern cryptographic and hashing utilities for Shiro Account Manager.
    /// Utilizes high-performance .NET 10 System.Security.Cryptography APIs (SHA-256).
    /// </summary>
    public static class CryptoHelper
    {
        /// <summary>
        /// Computes the SHA-256 hash of a string, returning a hexadecimal string representation.
        /// Uses stackalloc buffers for small strings to eliminate intermediate byte[] heap allocations.
        /// </summary>
        public static string SHA256(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            Span<byte> hashSpan = stackalloc byte[32];
            int maxByteCount = Encoding.UTF8.GetMaxByteCount(input.Length);
            if (maxByteCount <= 512)
            {
                Span<byte> utf8Buffer = stackalloc byte[maxByteCount];
                int written = Encoding.UTF8.GetBytes(input.AsSpan(), utf8Buffer);
                System.Security.Cryptography.SHA256.HashData(utf8Buffer.Slice(0, written), hashSpan);
            }
            else
            {
                byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maxByteCount);
                try
                {
                    int written = Encoding.UTF8.GetBytes(input.AsSpan(), rented);
                    System.Security.Cryptography.SHA256.HashData(rented.AsSpan(0, written), hashSpan);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                }
            }

            return Convert.ToHexString(hashSpan);
        }

        /// <summary>
        /// Computes the SHA-256 hash of a file on disk. Returns standard empty-hash marker if the file does not exist.
        /// </summary>
        public static string FileSHA256(this string fileName)
        {
            if (!File.Exists(fileName)) return "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";

            using FileStream fileStream = File.OpenRead(fileName);
            byte[] hash = System.Security.Cryptography.SHA256.HashData(fileStream);
            return Convert.ToHexString(hash);
        }

        /// <summary>
        /// Performs a constant-time equality check between two secrets by hashing both with SHA-256
        /// before evaluating FixedTimeEquals to prevent timing side-channel attacks on secret length and content.
        /// </summary>
        public static bool FixedTimeEqualsSecret(string provided, string expected)
        {
            if (provided == null || expected == null) return false;
            int maxByteA = Encoding.UTF8.GetMaxByteCount(provided.Length);
            int maxByteB = Encoding.UTF8.GetMaxByteCount(expected.Length);
            if (maxByteA <= 256 && maxByteB <= 256)
            {
                Span<byte> bufA = stackalloc byte[maxByteA];
                Span<byte> bufB = stackalloc byte[maxByteB];
                int lenA = Encoding.UTF8.GetBytes(provided.AsSpan(), bufA);
                int lenB = Encoding.UTF8.GetBytes(expected.AsSpan(), bufB);
                Span<byte> digestA = stackalloc byte[32];
                Span<byte> digestB = stackalloc byte[32];
                System.Security.Cryptography.SHA256.HashData(bufA[..lenA], digestA);
                System.Security.Cryptography.SHA256.HashData(bufB[..lenB], digestB);
                bool digestsMatch = CryptographicOperations.FixedTimeEquals(digestA, digestB);
                return digestsMatch & (lenA == lenB);
            }

            byte[] a = Encoding.UTF8.GetBytes(provided);
            byte[] b = Encoding.UTF8.GetBytes(expected);
            Span<byte> hashA = stackalloc byte[32];
            Span<byte> hashB = stackalloc byte[32];
            System.Security.Cryptography.SHA256.HashData(a, hashA);
            System.Security.Cryptography.SHA256.HashData(b, hashB);
            bool match = CryptographicOperations.FixedTimeEquals(hashA, hashB);
            return match & (a.Length == b.Length);
        }

        internal static Func<byte[], byte[]>? TestProtectBytesOverride;

        /// <summary>
        /// Encrypts a sensitive string bound to the current Windows user profile using DPAPI.
        /// Prepends a "dpapi:" prefix to differentiate protected secrets from legacy plaintext.
        /// </summary>
        public static string ProtectSecretString(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;

            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            try
            {
                byte[] enc = TestProtectBytesOverride != null
                    ? TestProtectBytesOverride(plainBytes)
                    : ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                return "dpapi:" + Convert.ToBase64String(enc);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }

        /// <summary>
        /// Decrypts a DPAPI-protected secret string. Falls back to plaintext for legacy unprefixed values,
        /// and fails closed to <see cref="string.Empty"/> when a "dpapi:" payload cannot be unprotected.
        /// </summary>
        public static string UnprotectSecretString(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;
            if (!cipherText.StartsWith("dpapi:", StringComparison.Ordinal)) return cipherText;

            byte[]? plain = null;
            try
            {
                string b64 = cipherText.Substring("dpapi:".Length);
                byte[] enc = Convert.FromBase64String(b64);
                plain = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                AppServices.Logger.Debug($"[CryptoHelper] Failed to unprotect DPAPI secret: {ex.Message}");
                return string.Empty;
            }
            finally
            {
                if (plain != null)
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
        }
    }
}
