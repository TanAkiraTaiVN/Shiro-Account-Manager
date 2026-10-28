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
    }
}
