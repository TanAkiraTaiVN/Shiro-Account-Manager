#nullable enable
using System;
using System.IO;

namespace Shiro.Services
{
    internal static partial class StoragePaths
    {
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
                _logger?.Warn($"Could not migrate user data directory {Path.GetFileName(sourceDirectory)}: {exception.Message}");
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
                _logger?.Warn($"Could not migrate user data file {Path.GetFileName(source)}: {exception.Message}");
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
                _logger?.Debug($"PathEquals failed for '{left}' vs '{right}': {ex.Message}");
                return false;
            }
        }
    }
}
