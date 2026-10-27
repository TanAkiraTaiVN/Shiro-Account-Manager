#nullable enable
using System;
using System.IO;

namespace RBX_Alt_Manager.Services
{
    /// <summary>
    /// Centralizes mutable application data paths.  Read-only files continue to
    /// live beside the executable, while account data, settings and caches are
    /// kept in the user's local application data folder.
    /// </summary>
    internal static class StoragePaths
    {
        private static readonly System.Threading.Lock Sync = new();
        private static bool initialized;
        private static string dataDirectory = string.Empty;
        private static string? _testDataDirectoryOverride;

        public static string BaseDirectory { get; private set; } = AppContext.BaseDirectory;
        public static string DataDirectory
        {
            get
            {
                if (_testDataDirectoryOverride != null)
                    return _testDataDirectoryOverride;

                EnsureInitialized();
                return dataDirectory;
            }
        }

        public static bool IsPortable { get; private set; }
        public static string AccountDataFile => GetUserFile("AccountData.json");
        public static string RecentGamesFile => GetUserFile("RecentGames.json");
        public static string SettingsFile => GetUserFile("RAMSettings.json");
        internal static string LegacySettingsFile => GetUserFile("RAMSettings.ini");
        public static string VaultKeyFile => GetUserFile("vault.key");
        public static string CacheDirectory => GetUserDirectory("Cache");
        public static string BrowserSessionsDirectory => GetUserDirectory("BrowserSessions");
        public static string AccountDumpsDirectory => GetUserDirectory("AccountDumps");
        public static string BackupsDirectory => GetUserDirectory("Backups");
        public static string NexusOutputDirectory => GetUserDirectory("NexusOutput");
        public static string LogDirectory => GetUserDirectory("Logs");

        private static bool IsRunningUnderUnitTests()
        {
            try
            {
                string friendlyName = AppDomain.CurrentDomain.FriendlyName ?? string.Empty;
                if (friendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
                    friendlyName.Contains("xunit", StringComparison.OrdinalIgnoreCase))
                    return true;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = asm.GetName().Name ?? string.Empty;
                    if (name.Equals("RBX_Alt_Manager.Tests", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Shiro.Tests", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("xunit.", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"StoragePaths assembly probe encountered error: {ex.Message}");
            }
            return false;
        }

        public static void EnsureInitialized()
        {
            if (initialized)
                return;

            lock (Sync)
            {
                if (initialized)
                    return;

                BaseDirectory = EnsureTrailingSeparator(AppContext.BaseDirectory);
                IsPortable = File.Exists(Path.Combine(BaseDirectory, "portable.mode"));

                if (IsRunningUnderUnitTests())
                {
                    dataDirectory = Path.Combine(Path.GetTempPath(), "Shiro-UnitTests");
                    Directory.CreateDirectory(dataDirectory);
                    initialized = true;
                    return;
                }

                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string defaultDataDirectory = Path.Combine(localAppData, "Shiro");
                dataDirectory = IsPortable ? BaseDirectory : defaultDataDirectory;

                Directory.CreateDirectory(dataDirectory);

                if (!IsPortable && !PathEquals(dataDirectory, BaseDirectory))
                    MigrateLegacyFiles(BaseDirectory, dataDirectory);

                initialized = true;
            }
        }

        public static string ResolveUserFile(string path)
        {
            EnsureInitialized();
            if (Path.IsPathRooted(path))
                return path;

            return GetUserFile(path);
        }

        public static string ResolveAsset(string relativePath)
        {
            EnsureInitialized();
            return Path.Combine(BaseDirectory, relativePath ?? string.Empty);
        }

        public static string GetUserFile(string fileName)
        {
            return Path.Combine(DataDirectory, fileName ?? string.Empty);
        }

        private static byte[]? _cachedVaultKey;
        private static readonly System.Threading.Lock _vaultKeyLock = new();
        private static readonly byte[] DpapiKeyHeader = System.Text.Encoding.ASCII.GetBytes("SHIRO_DPAPI_KEY_V1");
        private static readonly byte[] DpapiKeyEntropy = System.Text.Encoding.ASCII.GetBytes("Shiro.VaultKey.v1");

        private static byte[] WrapVaultKey(byte[] key)
        {
            byte[] protectedKey = System.Security.Cryptography.ProtectedData.Protect(
                key, DpapiKeyEntropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
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
                            byte[] key = System.Security.Cryptography.ProtectedData.Unprotect(
                                blob, DpapiKeyEntropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
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

                byte[] newKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
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

        public static string GetUserDirectory(string directoryName)
        {
            string path = Path.Combine(DataDirectory, directoryName ?? string.Empty);
            Directory.CreateDirectory(path);
            return path;
        }

        internal static IDisposable OverrideDataDirectoryForTesting(string tempDir)
        {
            Directory.CreateDirectory(tempDir);
            string? previous = _testDataDirectoryOverride;
            _testDataDirectoryOverride = tempDir;
            return new DisposableAction(() =>
            {
                _testDataDirectoryOverride = previous;
                ReloadVaultKeyCache();
            });
        }

        private sealed class DisposableAction(Action onDispose) : IDisposable
        {
            private Action? _onDispose = onDispose;
            public void Dispose()
            {
                System.Threading.Interlocked.Exchange(ref _onDispose, null)?.Invoke();
            }
        }

        /// <summary>
        /// Removes abandoned browser profiles and atomic-write temporary files
        /// left behind by a previous crash.  Browser sessions are deliberately
        /// kept for several days so a long-running interactive CAPTCHA or sign
        /// in window is never removed just because Shiro was restarted.
        /// </summary>
        public static int CleanupStaleRuntimeArtifacts(TimeSpan maxAge)
        {
            EnsureInitialized();

            TimeSpan safeAge = maxAge <= TimeSpan.Zero ? TimeSpan.FromDays(7) : maxAge;
            DateTime cutoffUtc = DateTime.UtcNow - safeAge;
            int removed = 0;

            string browserRoot = BrowserSessionsDirectory;
            try
            {
                foreach (string directory in Directory.EnumerateDirectories(browserRoot))
                {
                    if (!IsWithinDirectory(directory, browserRoot))
                        continue;

                    DateTime lastWriteUtc;
                    try { lastWriteUtc = Directory.GetLastWriteTimeUtc(directory); }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Failed to get last write time for {directory}: {ex.Message}");
                        continue;
                    }

                    if (lastWriteUtc >= cutoffUtc)
                        continue;

                    try
                    {
                        Directory.Delete(directory, true);
                        removed++;
                    }
                    catch (Exception exception)
                    {
                        Program.Logger?.Debug($"Could not remove stale browser session: {exception.Message}");
                    }
                }
            }
            catch (Exception exception)
            {
                Program.Logger?.Debug($"Could not enumerate browser sessions: {exception.Message}");
            }

            // AtomicWriteBytes creates uniquely named siblings and removes
            // them in its finally block.  Only files older than the same safe
            // cutoff can be abandoned remnants; current writes are untouched.
            try
            {
                foreach (string temporary in Directory.EnumerateFiles(DataDirectory, "*.tmp-*", SearchOption.TopDirectoryOnly))
                {
                    DateTime lastWriteUtc;
                    try { lastWriteUtc = File.GetLastWriteTimeUtc(temporary); }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Failed to get last write time for {temporary}: {ex.Message}");
                        continue;
                    }

                    if (lastWriteUtc >= cutoffUtc)
                        continue;

                    try
                    {
                        File.Delete(temporary);
                        removed++;
                    }
                    catch (Exception exception)
                    {
                        Program.Logger?.Debug($"Could not remove stale temporary data file: {exception.Message}");
                    }
                }
            }
            catch (Exception exception)
            {
                Program.Logger?.Debug($"Could not enumerate temporary data files: {exception.Message}");
            }

            return removed;
        }

        /// <summary>
        /// Returns true when <paramref name="path"/> is the directory itself
        /// or a descendant of it.  Cleanup code uses this boundary before
        /// recursively deleting a browser profile, so a bad path can never
        /// escape the application data root.
        /// </summary>
        public static bool IsWithinDirectory(string path, string directory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
                    return false;

                string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static void AtomicWriteBytes(string path, byte[] contents, bool durable = false)
        {
            if (contents == null) throw new ArgumentNullException(nameof(contents));
            AtomicWriteBytes(path, contents.AsSpan(), durable);
        }

        public static void AtomicWriteBytes(string path, ReadOnlySpan<byte> contents, bool durable = false)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                // Critical account/settings files opt into WriteThrough and
                // Flush(true) so the temporary payload is durable before the
                // rename. Cache, avatar and recent-game writes still get the
                // same atomic replacement without forcing a disk flush on
                // every image or metadata request.
                using (FileStream stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    options: durable ? FileOptions.WriteThrough : FileOptions.None))
                {
                    stream.Write(contents);
                    stream.Flush(durable);
                }
                ReplaceFile(temporary, path);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Failed to clean up temporary write file {temporary}: {ex.Message}");
                }
            }
        }

        public static void AtomicWriteText(string path, string contents, bool durable = false)
        {
            if (string.IsNullOrEmpty(contents))
            {
                AtomicWriteBytes(path, ReadOnlySpan<byte>.Empty, durable);
                return;
            }

            int byteCount = System.Text.Encoding.UTF8.GetByteCount(contents);
            byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                int written = System.Text.Encoding.UTF8.GetBytes(contents.AsSpan(), rented);
                AtomicWriteBytes(path, rented.AsSpan(0, written), durable);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static void ReplaceFile(string temporary, string destination)
        {
            if (File.Exists(destination))
            {
                try
                {
                    // AccountData has its own explicit .backup created by
                    // AccountStore.  Creating a second ".previous" file
                    // for every INI, avatar and cache write only leaves
                    // mutable clutter beside otherwise atomic files.
                    File.Replace(temporary, destination, null, true);
                    return;
                }
                catch (Exception ex)
                {
                    // File.Replace is not available on every filesystem (for
                    // example, some removable drives). Fall back to a move.
                    Program.Logger?.Debug($"File.Replace failed, falling back to File.Move: {ex.Message}");
                }
            }

            File.Move(temporary, destination, true);
        }

        private static void MigrateLegacyFiles(string sourceDirectory, string destinationDirectory)
        {
            string[] files =
            {
                "AccountData.json", "RecentGames.json", "FavoriteGames.json",
                "AccountControlData.json", "Nexus.lua", "RAMSettings.json", "RAMSettings.ini", "RAMTheme.ini",
                "vault.key", "proxies.txt", "BrowserConfig.json", "CustomClientAppSettings.json",
                "no773fix.txt", "NoEncryption.IUnderstandTheRisks.iautamor"
            };

            foreach (string fileName in files)
            {
                string source = Path.Combine(sourceDirectory, fileName);
                string destination = Path.Combine(destinationDirectory, fileName);
                CopyIfMissing(source, destination);
                CopyIfMissing(source + ".backup", destination + ".backup");
                CopyIfMissing(source + ".bak", destination + ".bak");
                CopyIfMissing(source + ".old", destination + ".old");

                // A legacy installation may only have the primary account
                // file. Preserve a second recovery copy before the new
                // location starts writing migrated data.
                if (string.Equals(fileName, "AccountData.json", StringComparison.OrdinalIgnoreCase))
                    CopyIfMissing(source, destination + ".backup");
            }

            string legacyDumps = Path.Combine(sourceDirectory, "AccountDumps");
            CopyDirectoryIfMissing(legacyDumps, Path.Combine(destinationDirectory, "AccountDumps"));
            CopyDirectoryIfMissing(Path.Combine(sourceDirectory, "Output"), Path.Combine(destinationDirectory, "NexusOutput"));

            // Avatar/cache files and the previous log are safe to preserve,
            // but must follow the same AppData boundary as new writes. Browser
            // session folders are intentionally not migrated because they are
            // temporary profiles and may contain stale authentication state.
            CopyDirectoryIfMissing(Path.Combine(sourceDirectory, "Cache"), Path.Combine(destinationDirectory, "Cache"));
            CopyDirectoryIfMissing(Path.Combine(sourceDirectory, "Logs"), Path.Combine(destinationDirectory, "Logs"));
        }

        private static void CopyDirectoryIfMissing(string sourceDirectory, string destinationDirectory)
        {
            try
            {
                if (!Directory.Exists(sourceDirectory))
                    return;

                Directory.CreateDirectory(destinationDirectory);
                foreach (string source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(sourceDirectory, source);
                    CopyIfMissing(source, Path.Combine(destinationDirectory, relative));
                }
            }
            catch (Exception exception)
            {
                Program.Logger?.Warn($"Could not migrate user data directory {Path.GetFileName(sourceDirectory)}: {exception.Message}");
            }
        }

        private static void CopyIfMissing(string source, string destination)
        {
            try
            {
                if (File.Exists(source) && !File.Exists(destination))
                {
                    string? destDir = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(destDir))
                        Directory.CreateDirectory(destDir);
                    File.Copy(source, destination, false);
                }
            }
            catch (Exception exception)
            {
                Program.Logger?.Warn($"Could not migrate user data file {Path.GetFileName(source)}: {exception.Message}");
            }
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            char separator = Path.DirectorySeparatorChar;
            return path.EndsWith(separator.ToString(), StringComparison.Ordinal) ? path : path + separator;
        }

        private static bool PathEquals(string left, string right)
        {
            try
            {
                return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"PathEquals failed for '{left}' vs '{right}': {ex.Message}");
                return false;
            }
        }
    }
}
