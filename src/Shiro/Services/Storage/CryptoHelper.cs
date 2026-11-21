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
                Span<byte> digestA = stackalloc byte[32];
                Span<byte> digestB = stackalloc byte[32];
                try
                {
                    int lenA = Encoding.UTF8.GetBytes(provided.AsSpan(), bufA);
                    int lenB = Encoding.UTF8.GetBytes(expected.AsSpan(), bufB);
                    System.Security.Cryptography.SHA256.HashData(bufA[..lenA], digestA);
                    System.Security.Cryptography.SHA256.HashData(bufB[..lenB], digestB);
                    bool digestsMatch = CryptographicOperations.FixedTimeEquals(digestA, digestB);
                    return digestsMatch & (lenA == lenB);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bufA);
                    CryptographicOperations.ZeroMemory(bufB);
                    CryptographicOperations.ZeroMemory(digestA);
                    CryptographicOperations.ZeroMemory(digestB);
                }
            }

            byte[]? a = null;
            byte[]? b = null;
            Span<byte> hashA = stackalloc byte[32];
            Span<byte> hashB = stackalloc byte[32];
            try
            {
                a = Encoding.UTF8.GetBytes(provided);
                b = Encoding.UTF8.GetBytes(expected);
                System.Security.Cryptography.SHA256.HashData(a, hashA);
                System.Security.Cryptography.SHA256.HashData(b, hashB);
                bool match = CryptographicOperations.FixedTimeEquals(hashA, hashB);
                return match & (a.Length == b.Length);
            }
            finally
            {
                if (a != null) CryptographicOperations.ZeroMemory(a);
                if (b != null) CryptographicOperations.ZeroMemory(b);
                CryptographicOperations.ZeroMemory(hashA);
                CryptographicOperations.ZeroMemory(hashB);
            }
        }

        private const string DpapiPrefix = "dpapi:";
        private const string VaultPrefix = "vault:";
        private const int VaultNonceSize = 12;
        private const int VaultTagSize = 16;

        private static readonly System.Threading.AsyncLocal<Func<byte[], byte[]>?> _testProtectBytesOverride = new();
        internal static Func<byte[], byte[]>? TestProtectBytesOverride
        {
            get => _testProtectBytesOverride.Value;
            set => _testProtectBytesOverride.Value = value;
        }

        /// <summary>
        /// Encrypts a sensitive string using the portable AES-256-GCM vault key ("vault:") when
        /// running in portable mode, or Windows DPAPI ("dpapi:") otherwise.
        /// </summary>
        public static string ProtectSecretString(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;

            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[]? enc = null;
            byte[]? vaultKey = null;
            try
            {
                if (TestProtectBytesOverride != null)
                {
                    enc = TestProtectBytesOverride(plainBytes);
                    return DpapiPrefix + Convert.ToBase64String(enc);
                }

                StoragePaths.EnsureInitialized();
                if (StoragePaths.IsPortable)
                {
                    vaultKey = StoragePaths.GetOrCreatePortableSecretKeyCopy();
                    enc = new byte[VaultNonceSize + VaultTagSize + plainBytes.Length];
                    Span<byte> encSpan = enc;
                    Span<byte> nonce = encSpan.Slice(0, VaultNonceSize);
                    Span<byte> tag = encSpan.Slice(VaultNonceSize, VaultTagSize);
                    Span<byte> ciphertext = encSpan.Slice(VaultNonceSize + VaultTagSize);
                    RandomNumberGenerator.Fill(nonce);
                    using var aesGcm = new AesGcm(vaultKey, VaultTagSize);
                    aesGcm.Encrypt(nonce, plainBytes, ciphertext, tag);
                    return VaultPrefix + Convert.ToBase64String(enc);
                }

                enc = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                return DpapiPrefix + Convert.ToBase64String(enc);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
                if (vaultKey != null) CryptographicOperations.ZeroMemory(vaultKey);
                if (enc != null) CryptographicOperations.ZeroMemory(enc);
            }
        }

        /// <summary>
        /// Decrypts a protected secret string ("vault:" AES-256-GCM or legacy "dpapi:"). Falls back to
        /// plaintext for legacy unprefixed values, and fails closed to <see cref="string.Empty"/> on error.
        /// </summary>
        public static string UnprotectSecretString(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;
            if (cipherText.StartsWith(VaultPrefix, StringComparison.Ordinal))
                return UnprotectPrefixedPayload(cipherText.Substring(VaultPrefix.Length), "vault", DecryptVaultPayload);
            if (cipherText.StartsWith(DpapiPrefix, StringComparison.Ordinal))
                return UnprotectPrefixedPayload(cipherText.Substring(DpapiPrefix.Length), "DPAPI", enc => ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser));
            return cipherText;
        }

        private static string UnprotectPrefixedPayload(string payloadBase64, string kindLabel, Func<byte[], byte[]?> decryptor)
        {
            byte[]? enc = null;
            byte[]? plain = null;
            try
            {
                enc = Convert.FromBase64String(payloadBase64);
                plain = decryptor(enc);
                return plain == null ? string.Empty : Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                AppServices.Logger.Debug($"[CryptoHelper] Failed to unprotect {kindLabel} secret: {ex.Message}");
                return string.Empty;
            }
            finally
            {
                if (enc != null) CryptographicOperations.ZeroMemory(enc);
                if (plain != null) CryptographicOperations.ZeroMemory(plain);
            }
        }

        private static byte[]? DecryptVaultPayload(byte[] enc)
        {
            if (enc.Length < VaultNonceSize + VaultTagSize)
                return null;

            int cipherLen = enc.Length - VaultNonceSize - VaultTagSize;
            ReadOnlySpan<byte> encSpan = enc;
            ReadOnlySpan<byte> nonce = encSpan.Slice(0, VaultNonceSize);
            ReadOnlySpan<byte> tag = encSpan.Slice(VaultNonceSize, VaultTagSize);
            ReadOnlySpan<byte> ciphertext = encSpan.Slice(VaultNonceSize + VaultTagSize, cipherLen);

            byte[] plain = new byte[cipherLen];
            byte[]? vaultKey = null;
            bool decrypted = false;
            try
            {
                vaultKey = StoragePaths.TryGetExistingVaultKeyCopy();
                if (vaultKey == null)
                    return null;

                using var aesGcm = new AesGcm(vaultKey, VaultTagSize);
                aesGcm.Decrypt(nonce, ciphertext, tag, plain);
                decrypted = true;
                return plain;
            }
            finally
            {
                if (!decrypted) CryptographicOperations.ZeroMemory(plain);
                if (vaultKey != null) CryptographicOperations.ZeroMemory(vaultKey);
            }
        }
    }
}
