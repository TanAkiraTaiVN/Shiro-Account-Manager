#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Shiro.Services
{
    internal static partial class StoragePaths
    {
        private static byte[]? _cachedVaultKey;
        private static readonly System.Threading.Lock _vaultKeyLock = new();
        private static readonly byte[] DpapiKeyHeader = Encoding.ASCII.GetBytes("SHIRO_DPAPI_KEY_V1");
        private static readonly byte[] DpapiKeyEntropy = Encoding.ASCII.GetBytes("Shiro.VaultKey.v1");

        private static byte[] WrapVaultKey(byte[] key)
        {
            byte[] protectedKey = ProtectedData.Protect(
                key, DpapiKeyEntropy, DataProtectionScope.CurrentUser);
            byte[] result = new byte[DpapiKeyHeader.Length + protectedKey.Length];
            Buffer.BlockCopy(DpapiKeyHeader, 0, result, 0, DpapiKeyHeader.Length);
            Buffer.BlockCopy(protectedKey, 0, result, DpapiKeyHeader.Length, protectedKey.Length);
            return result;
        }

        private static bool HasDpapiKeyHeader(byte[] raw)
        {
            return raw.Length > DpapiKeyHeader.Length &&
                   raw.AsSpan(0, DpapiKeyHeader.Length).SequenceEqual(DpapiKeyHeader);
        }

        internal static byte[] BuildProtectedVaultKeyPayload(byte[] rawKey)
        {
            return WrapVaultKey(rawKey);
        }

        internal static bool TryReadAndUnprotectKeyFile(string path, out byte[]? key, out bool wasRawLegacy)
        {
            key = null;
            wasRawLegacy = false;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    byte[] raw = File.ReadAllBytes(path);
                    if (HasDpapiKeyHeader(raw))
                    {
                        byte[] blob = raw.AsSpan(DpapiKeyHeader.Length).ToArray();
                        byte[] unprotected = ProtectedData.Unprotect(
                            blob, DpapiKeyEntropy, DataProtectionScope.CurrentUser);
                        if (unprotected.Length == 32)
                        {
                            key = unprotected;
                            return true;
                        }
                    }
                    else if (raw.Length == 32)
                    {
                        wasRawLegacy = true;
                        key = raw;
                        return true;
                    }
                    return false;
                }
                catch (IOException) when (attempt < 2)
                {
                    System.Threading.Thread.Sleep(15);
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"Failed to read existing vault key from {path}: {ex.Message}");
                    return false;
                }
            }

            return false;
        }

        internal static byte[] LoadOrCreateVaultKeyAtPath(string keyFile, bool hasExistingEncryptedVault = false)
        {
            string bakFile = keyFile + ".bak";

            if (TryReadAndUnprotectKeyFile(keyFile, out byte[]? primaryKey, out bool wasRawLegacy) && primaryKey != null)
            {
                if (wasRawLegacy && !IsPortable)
                {
                    // Upgrade both primary and backup key files to DPAPI-wrapped format so no raw 32-byte key remains on disk.
                    try
                    {
                        byte[] wrapped = WrapVaultKey(primaryKey);
                        AtomicWriteBytes(keyFile, wrapped, durable: true);
                        AtomicWriteBytes(bakFile, wrapped, durable: true);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn($"Failed to upgrade raw key to DPAPI format: {ex.Message}");
                    }
                }
                else if (!File.Exists(bakFile))
                {
                    try
                    {
                        byte[] payload = IsPortable ? primaryKey : WrapVaultKey(primaryKey);
                        AtomicWriteBytes(bakFile, payload, durable: true);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn($"Failed to populate missing backup vault key file: {ex.Message}");
                    }
                }

                return primaryKey;
            }

            if (TryReadAndUnprotectKeyFile(bakFile, out byte[]? backupKey, out _) && backupKey != null)
            {
                _logger?.Warn($"Primary vault key at {keyFile} was unreadable; restoring from {bakFile}.");
                try
                {
                    if (File.Exists(keyFile))
                    {
                        string corruptCopy = $"{keyFile}.corrupt-{DateTime.UtcNow:yyyyMMdd_HHmmss}.bak";
                        File.Copy(keyFile, corruptCopy, overwrite: true);
                    }
                    byte[] bakBytes = File.ReadAllBytes(bakFile);
                    AtomicWriteBytes(keyFile, bakBytes, durable: true);
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"Failed to restore primary vault key file from backup: {ex.Message}");
                }

                return backupKey;
            }

            if (File.Exists(keyFile))
            {
                // Never silently destroy an existing non-empty key file without preserving a backup copy.
                try
                {
                    string corruptCopy = $"{keyFile}.corrupt-{DateTime.UtcNow:yyyyMMdd_HHmmss}.bak";
                    File.Copy(keyFile, corruptCopy, overwrite: true);
                    if (!File.Exists(bakFile))
                        File.Copy(keyFile, bakFile, overwrite: false);
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"Failed to preserve backup of damaged vault key file: {ex.Message}");
                }
            }

            if (hasExistingEncryptedVault)
            {
                throw new CryptographicException(
                    $"Encrypted vault exists at '{AccountDataFile}', but vault key at '{keyFile}' is missing or unreadable; refusing to generate an orphan key.");
            }

            byte[] newKey = RandomNumberGenerator.GetBytes(32);
            byte[]? keyPayload = null;
            try
            {
                keyPayload = IsPortable ? newKey : WrapVaultKey(newKey);
                AtomicWriteBytes(keyFile, keyPayload, durable: true);
                if (!File.Exists(bakFile))
                {
                    AtomicWriteBytes(bakFile, keyPayload, durable: true);
                }
                return newKey;
            }
            catch (Exception ex)
            {
                CryptographicOperations.ZeroMemory(newKey);
                _logger?.Error($"Failed to persist newly generated vault key to {keyFile}: {ex.Message}");
                throw;
            }
            finally
            {
                if (keyPayload != null && !ReferenceEquals(keyPayload, newKey))
                {
                    CryptographicOperations.ZeroMemory(keyPayload);
                }
            }
        }

        public static void ReloadVaultKeyCache()
        {
            lock (_vaultKeyLock)
            {
                if (_cachedVaultKey != null)
                {
                    CryptographicOperations.ZeroMemory(_cachedVaultKey);
                    _cachedVaultKey = null;
                }
            }
        }

        public static void ClearCachedVaultKey()
        {
            ReloadVaultKeyCache();
        }

        public static void SaveRecoveredVaultKey(byte[] key)
        {
            if (key == null || key.Length != 32)
                throw new ArgumentException("Vault key must be 32 bytes.", nameof(key));

            lock (_vaultKeyLock)
            {
                EnsureInitialized();
                string keyFile = VaultKeyFile;
                byte[] copy = key.AsSpan().ToArray();

                byte[]? payload = null;
                try
                {
                    payload = IsPortable ? copy : WrapVaultKey(copy);
                    AtomicWriteBytes(keyFile, payload, durable: true);
                    AtomicWriteBytes(keyFile + ".bak", payload, durable: true);
                }
                catch (Exception ex)
                {
                    CryptographicOperations.ZeroMemory(copy);
                    _logger?.Warn($"Failed to save recovered vault key: {ex.Message}");
                    throw;
                }
                finally
                {
                    if (payload != null && !ReferenceEquals(payload, copy))
                    {
                        CryptographicOperations.ZeroMemory(payload);
                    }
                }

                if (_cachedVaultKey != null && !ReferenceEquals(_cachedVaultKey, copy))
                {
                    CryptographicOperations.ZeroMemory(_cachedVaultKey);
                }
                _cachedVaultKey = copy;
            }
        }

        /// <summary>
        /// Provisions or loads the DPAPI-wrapped vault key for an authenticated, unlocked session
        /// transitioning from Master Password encryption back to default vault-key encryption.
        /// Fails closed if the key file cannot be persisted to disk.
        /// </summary>
        internal static byte[] EnsureVaultKeyForUnlockedVaultCopy(string? customDataDir = null)
        {
            lock (_vaultKeyLock)
            {
                EnsureInitialized();
                bool useActiveDirectory = string.IsNullOrWhiteSpace(customDataDir) ||
                                          PathEquals(customDataDir, DataDirectory);

                if (useActiveDirectory)
                {
                    string keyFile = VaultKeyFile;
                    if (_cachedVaultKey != null && File.Exists(keyFile))
                        return _cachedVaultKey.AsSpan().ToArray();

                    byte[] resolvedKey = LoadOrCreateVaultKeyAtPath(keyFile, hasExistingEncryptedVault: false);
                    if (_cachedVaultKey != null && !ReferenceEquals(_cachedVaultKey, resolvedKey))
                    {
                        CryptographicOperations.ZeroMemory(_cachedVaultKey);
                    }
                    _cachedVaultKey = resolvedKey;
                    return resolvedKey.AsSpan().ToArray();
                }

                string customKeyFile = Path.Combine(customDataDir!, "vault.key");
                return LoadOrCreateVaultKeyAtPath(customKeyFile, hasExistingEncryptedVault: false);
            }
        }

        /// <summary>
        /// Returns an isolated copy of the 256-bit vault key so callers can safely wipe their local buffer with ZeroMemory.
        /// </summary>
        public static byte[] GetOrCreateVaultKeyCopy()
        {
            return GetOrCreateVaultKey().AsSpan().ToArray();
        }

        /// <summary>
        /// Retrieves or creates a secure 256-bit vault key for local/portable account data encryption.
        /// In installed mode the key file is wrapped with Windows DPAPI (CurrentUser); in portable
        /// mode the raw 32-byte key is kept so the folder can be moved between machines.
        /// Legacy raw keys are transparently upgraded when not portable.
        /// </summary>
        internal static bool HasExistingEncryptedVaultFile(string accountDataFile)
        {
            try
            {
                if (!File.Exists(accountDataFile))
                    return false;

                Span<byte> header = stackalloc byte[96];
                using var fs = new FileStream(accountDataFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int read = fs.Read(header);
                return read > 0 && Cryptography.IsEncrypted(header.Slice(0, read));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return File.Exists(accountDataFile);
            }
        }

        public static byte[] GetOrCreateVaultKey()
        {
            lock (_vaultKeyLock)
            {
                if (_cachedVaultKey != null)
                    return _cachedVaultKey;

                EnsureInitialized();
                string keyFile = VaultKeyFile;
                bool hasExistingEncryptedVault = HasExistingEncryptedVaultFile(AccountDataFile);
                byte[] resolvedKey = LoadOrCreateVaultKeyAtPath(keyFile, hasExistingEncryptedVault);
                _cachedVaultKey = resolvedKey;
                return resolvedKey;
            }
        }
    }
}
