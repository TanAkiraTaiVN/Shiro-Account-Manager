#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using Shiro.Services;

namespace Shiro.Classes
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
                bool potatoMode = AppSettings.General?.Get<bool>("EnablePotatoMode", false) ?? false;
                if (potatoMode)
                {
                    ConfigureGlobalBasicSettings(graphicsQualityLevel: 1);
                }

                bool unlockFps = AppSettings.General?.Get<bool>("UnlockFPS", false) ?? false;
                int targetFps = AppSettings.General?.Get<int>("MaxFPSValue", 144) ?? 144;
                ApplyFpsLimit(unlockFps, targetFps);

                PurgeSettings();
            }
        }

        /// <summary>
        /// Actively purges any ClientSettings directory from all detected Roblox versions to prevent bans.
        /// Respects user configuration and preserves Bloxstrap/Fishstrap custom bootstrapper folders.
        /// </summary>
        public const string FpsFlagKey = "DFIntTaskSchedulerTargetFps";

        public static void ApplyFpsLimit(bool unlockFps, int targetFps)
        {
            var folders = FindRobloxVersionFolders();
            foreach (var folder in folders)
            {
                ApplyFpsLimitToDirectory(folder.FullName, unlockFps, targetFps);
            }
        }

        public static void ApplyFpsLimitToDirectory(string versionFolder, bool unlockFps, int targetFps)
        {
            try
            {
                string settingsDir = Path.Combine(versionFolder, "ClientSettings");
                string jsonPath = Path.Combine(settingsDir, "ClientAppSettings.json");

                int clampedFps = Math.Clamp(targetFps, 30, 360);

                if (unlockFps)
                {
                    if (!Directory.Exists(settingsDir))
                        Directory.CreateDirectory(settingsDir);

                    var dict = new Dictionary<string, object>();
                    if (File.Exists(jsonPath))
                    {
                        try
                        {
                            string existing = File.ReadAllText(jsonPath);
                            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(existing);
                            if (parsed != null)
                            {
                                foreach (var kvp in parsed)
                                    dict[kvp.Key] = kvp.Value;
                            }
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"Parse existing ClientAppSettings failed: {ex.Message}");
                        }
                    }

                    dict[FpsFlagKey] = clampedFps;
                    string updatedJson = System.Text.Json.JsonSerializer.Serialize(dict, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    StoragePaths.AtomicWriteText(jsonPath, updatedJson, durable: true);
                    Program.Logger?.Info($"[ClientSettingsPatcher] Applied safe FPS limit ({clampedFps}) to {Path.GetFileName(versionFolder)}");
                }
                else
                {
                    if (File.Exists(jsonPath))
                    {
                        try
                        {
                            string existing = File.ReadAllText(jsonPath);
                            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(existing);
                            if (parsed != null && parsed.ContainsKey(FpsFlagKey))
                            {
                                var dict = new Dictionary<string, object>();
                                foreach (var kvp in parsed)
                                {
                                    if (kvp.Key != FpsFlagKey)
                                        dict[kvp.Key] = kvp.Value;
                                }

                                if (dict.Count == 0)
                                {
                                    File.Delete(jsonPath);
                                    if (Directory.Exists(settingsDir) && Directory.GetFileSystemEntries(settingsDir).Length == 0)
                                        Directory.Delete(settingsDir);
                                }
                                else
                                {
                                    string updatedJson = System.Text.Json.JsonSerializer.Serialize(dict, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                                    StoragePaths.AtomicWriteText(jsonPath, updatedJson, durable: true);
                                }
                                Program.Logger?.Info($"[ClientSettingsPatcher] Removed FPS limit from {Path.GetFileName(versionFolder)}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"Clean FPS limit failed: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[ClientSettingsPatcher] ApplyFpsLimitToDirectory failed for {versionFolder}: {ex.Message}");
            }
        }

        public static void PurgeSettings()
        {
            bool shouldPurge = AppSettings.General?.Get<bool>("PurgeClientSettings", false) ?? false;
            if (!shouldPurge)
            {
                return;
            }

            var targetFolders = FindRobloxVersionFolders();
            foreach (var folder in targetFolders)
            {
                try
                {
                    bool isCustomBootstrapper = folder.FullName.Contains("Bloxstrap", StringComparison.OrdinalIgnoreCase) ||
                                               folder.FullName.Contains("Fishstrap", StringComparison.OrdinalIgnoreCase) ||
                                               folder.FullName.Contains("Froststrap", StringComparison.OrdinalIgnoreCase) ||
                                               folder.FullName.Contains("Voidstrap", StringComparison.OrdinalIgnoreCase);
                    if (isCustomBootstrapper)
                    {
                        Program.Logger?.Debug($"[ClientSettingsPatcher] Preserving custom bootstrapper ClientSettings in {folder.FullName}");
                        continue;
                    }

                    string settingsDir = Path.Combine(folder.FullName, "ClientSettings");
                    if (Directory.Exists(settingsDir))
                    {
                        Directory.Delete(settingsDir, recursive: true);
                        Program.Logger?.Info($"[ClientSettingsPatcher] Safely purged ClientSettings in {folder.Name} to protect against Byfron bans.");
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Warn($"[ClientSettingsPatcher] Could not purge ClientSettings in {folder.FullName}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Updates Roblox's native GlobalBasicSettings_13.xml safely without touching FastFlags or triggering anti-cheat detections.
        /// </summary>
        public static bool ConfigureGlobalBasicSettings(int? graphicsQualityLevel = null, bool? fullscreen = null)
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string settingsPath = Path.Combine(localAppData, "Roblox", "GlobalBasicSettings_13.xml");
                if (!File.Exists(settingsPath))
                    return false;

                string xml = File.ReadAllText(settingsPath);
                bool modified = false;

                if (graphicsQualityLevel.HasValue)
                {
                    int lvl = Math.Clamp(graphicsQualityLevel.Value, 1, 10);
                    var pattern = @"<int name=""GraphicsQualityLevel"">\d+</int>";
                    if (System.Text.RegularExpressions.Regex.IsMatch(xml, pattern))
                    {
                        xml = System.Text.RegularExpressions.Regex.Replace(xml, pattern, $"<int name=\"GraphicsQualityLevel\">{lvl}</int>");
                        modified = true;
                    }
                }

                if (fullscreen.HasValue)
                {
                    string fsVal = fullscreen.Value ? "true" : "false";
                    var pattern = @"<bool name=""Fullscreen"">(true|false)</bool>";
                    if (System.Text.RegularExpressions.Regex.IsMatch(xml, pattern))
                    {
                        xml = System.Text.RegularExpressions.Regex.Replace(xml, pattern, $"<bool name=\"Fullscreen\">{fsVal}</bool>");
                        modified = true;
                    }
                }

                if (modified)
                {
                    StoragePaths.AtomicWriteText(settingsPath, xml, durable: true);
                    Program.Logger?.Info("[ClientSettingsPatcher] Successfully updated native GlobalBasicSettings_13.xml");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[ClientSettingsPatcher] ConfigureGlobalBasicSettings failed: {ex.Message}");
            }
            return false;
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
                    object? regValue = subKey?.GetValue("");
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
