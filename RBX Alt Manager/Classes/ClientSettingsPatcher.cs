using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// Safety guardian for Roblox ClientSettings.
    /// Byfron / Hyperion detects ClientAppSettings.json and FastFlag overrides (especially FFlagDebug*)
    /// as "modified version of Roblox", resulting in 7-day account suspensions.
    /// This patcher actively PURGES and removes any ClientSettings folders to ensure Roblox stays
    /// 100% vanilla, clean, and safe from anti-cheat bans.
    /// </summary>
    public static class ClientSettingsPatcher
    {
        private static readonly System.Threading.Lock PatchLock = new();

        public static void PatchSettings()
        {
            lock (PatchLock)
            {
                PurgeSettings();
            }
        }

        /// <summary>
        /// Actively purges any ClientSettings directory from all detected Roblox versions to prevent bans.
        /// </summary>
        public static void PurgeSettings()
        {
            var targetFolders = FindRobloxVersionFolders();
            foreach (var folder in targetFolders)
            {
                try
                {
                    string settingsDir = Path.Combine(folder.FullName, "ClientSettings");
                    if (Directory.Exists(settingsDir))
                    {
                        Directory.Delete(settingsDir, recursive: true);
                        Program.Logger.Info($"[ClientSettingsPatcher] Safely purged ClientSettings in {folder.Name} to protect against Byfron bans.");
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger.Warn($"[ClientSettingsPatcher] Could not purge ClientSettings in {folder.FullName}: {ex.Message}");
                }
            }
        }

        public static List<DirectoryInfo> FindRobloxVersionFolders()
        {
            var folders = new List<DirectoryInfo>();
            var seenFullNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Scan standard %LOCALAPPDATA%\Roblox\Versions, Program Files, and Program Files (x86)
            var scanRoots = new List<string>(3)
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions")
            };

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
                scanRoots.Add(Path.Combine(programFiles, "Roblox", "Versions"));

            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(programFilesX86) && !string.Equals(programFilesX86, programFiles, StringComparison.OrdinalIgnoreCase))
                scanRoots.Add(Path.Combine(programFilesX86, "Roblox", "Versions"));

            foreach (string versionsRoot in scanRoots)
            {
                try
                {
                    if (Directory.Exists(versionsRoot))
                    {
                        foreach (var dir in new DirectoryInfo(versionsRoot).EnumerateDirectories())
                        {
                            if (File.Exists(Path.Combine(dir.FullName, "RobloxPlayerBeta.exe")) ||
                                File.Exists(Path.Combine(dir.FullName, "RobloxPlayerLauncher.exe")))
                            {
                                if (seenFullNames.Add(dir.FullName))
                                    folders.Add(dir);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Scan Roblox version directory '{versionsRoot}' failed: {ex.Message}");
                }
            }

            // 2. Scan Registry as fallback
            try
            {
                using (var subKey = Registry.ClassesRoot.OpenSubKey(@"roblox\DefaultIcon"))
                {
                    object regValue = subKey?.GetValue("");
                    if (regValue is string robloxPath && !string.IsNullOrWhiteSpace(robloxPath))
                    {
                        var regDir = Directory.GetParent(robloxPath);
                        if (regDir != null && regDir.Exists && seenFullNames.Add(regDir.FullName))
                        {
                            folders.Add(regDir);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Scan Roblox registry key failed: {ex.Message}");
            }

            return folders;
        }
    }
}
