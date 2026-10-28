#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RBX_Alt_Manager.Services
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

        public static void ReloadVaultKeyCache()
        {
            lock (_vaultKeyLock)
            {
                _cachedVaultKey = null;
            }
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

                try
                {
                    byte[] payload = IsPortable ? copy : WrapVaultKey(copy);
                    AtomicWriteBytes(keyFile, payload, durable: true);
                    AtomicWriteBytes(keyFile + ".bak", payload, durable: true);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Warn($"Failed to save recovered vault key: {ex.Message}");
                }

                _cachedVaultKey = copy;
            }
        }

        /// <summary>
        /// Retrieves or creates a secure 256-bit vault key for local/portable account data encryption.
        /// In installed mode the key file is wrapped with Windows DPAPI (CurrentUser); in portable
        /// mode the raw 32-byte key is kept so the folder can be moved between machines.
        /// Legacy raw keys are transparently upgraded when not portable.
        /// </summary>
        public static byte[] GetOrCreateVaultKey()
        {
            lock (_vaultKeyLock)
            {
                if (_cachedVaultKey != null)
                    return _cachedVaultKey;

                EnsureInitialized();
                string keyFile = VaultKeyFile;

                if (File.Exists(keyFile))
                {
                    try
                    {
                        byte[] raw = File.ReadAllBytes(keyFile);
                        if (HasDpapiKeyHeader(raw))
                        {
                            byte[] blob = raw.AsSpan(DpapiKeyHeader.Length).ToArray();
                            byte[] key = ProtectedData.Unprotect(
                                blob, DpapiKeyEntropy, DataProtectionScope.CurrentUser);
                            if (key.Length == 32)
                            {
                                _cachedVaultKey = key;
                                return key;
                            }
                        }
                        else if (raw.Length == 32)
                        {
                            if (!IsPortable)
                            {
                                // Preserve a backup of the raw key before upgrading to DPAPI-wrapped format.
                                try
                                {
                                    if (!File.Exists(keyFile + ".bak"))
                                        File.Copy(keyFile, keyFile + ".bak", overwrite: false);
                                    AtomicWriteBytes(keyFile, WrapVaultKey(raw), durable: true);
                                }
                                catch (Exception ex)
                                {
                                    Program.Logger?.Warn($"Failed to upgrade raw key to DPAPI format: {ex.Message}");
                                }
                            }
                            _cachedVaultKey = raw;
                            return raw;
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Warn($"Failed to read existing vault key from {keyFile}: {ex.Message}");
                    }

                    // Never silently destroy an existing non-empty key file without preserving a backup copy.
                    try
                    {
                        if (!File.Exists(keyFile + ".bak"))
                            File.Copy(keyFile, keyFile + ".bak", overwrite: false);
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Warn($"Failed to preserve backup of damaged vault key file: {ex.Message}");
                    }
                }

                byte[] newKey = RandomNumberGenerator.GetBytes(32);
                try
                {
                    AtomicWriteBytes(keyFile, IsPortable ? newKey : WrapVaultKey(newKey), durable: true);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Error($"Failed to persist newly generated vault key to {keyFile}: {ex.Message}");
                }

                _cachedVaultKey = newKey;
                return newKey;
            }
        }
    }
}
