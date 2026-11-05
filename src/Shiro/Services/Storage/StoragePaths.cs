#nullable enable
using System;
using System.IO;

namespace Shiro.Services
{
    /// <summary>
    /// Centralizes mutable application data paths.  Read-only files continue to
    /// live beside the executable, while account data, settings and caches are
    /// kept in the user's local application data folder.
    /// </summary>
    internal static partial class StoragePaths
    {
        private static IAppLogger? _logger = AppServices.Logger;
        public static IAppLogger? Logger
        {
            get => _logger ?? AppServices.Logger;
            set => _logger = value;
        }

        private static readonly System.Threading.Lock Sync = new();
        private static bool initialized;
        private static string dataDirectory = string.Empty;
        private static string? _testDataDirectoryOverride;
        internal static bool? UnitTestOverride;
        internal static bool TestForceAssemblyProbe;

        internal static void ResetInitializedForTesting()
        {
            lock (Sync)
            {
                initialized = false;
            }
        }

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
        public static string AccountRuntimeStateFile => GetUserFile("AccountRuntimeState.json");
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
            if (UnitTestOverride.HasValue)
                return UnitTestOverride.Value;
            try
            {
                string friendlyName = AppDomain.CurrentDomain.FriendlyName ?? string.Empty;
                if (!TestForceAssemblyProbe &&
                    (friendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
                     friendlyName.Contains("xunit", StringComparison.OrdinalIgnoreCase)))
                    return true;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = asm.GetName().Name ?? string.Empty;
                    if (name.Equals("Shiro.Tests", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Shiro.Tests", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("xunit.", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"StoragePaths assembly probe encountered error: {ex.Message}");
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

                string localAppData = UnitTestOverride == false
                    ? Path.Combine(Path.GetTempPath(), "Shiro-ProdBranchTest")
                    : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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
                        _logger?.Debug($"Failed to get last write time for {directory}: {ex.Message}");
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
                        _logger?.Debug($"Could not remove stale browser session: {exception.Message}");
                    }
                }
            }
            catch (Exception exception)
            {
                _logger?.Debug($"Could not enumerate browser sessions: {exception.Message}");
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
                        _logger?.Debug($"Failed to get last write time for {temporary}: {ex.Message}");
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
                        _logger?.Debug($"Could not remove stale temporary data file: {exception.Message}");
                    }
                }
            }
            catch (Exception exception)
            {
                _logger?.Debug($"Could not enumerate temporary data files: {exception.Message}");
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
                    _logger?.Debug($"Failed to clean up temporary write file {temporary}: {ex.Message}");
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
                    _logger?.Debug($"File.Replace failed, falling back to File.Move: {ex.Message}");
                }
            }

            File.Move(temporary, destination, true);
        }
    }
}
