#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Shiro.Services;

namespace Shiro.Services
{
    /// <summary>
    /// Safety guardian for Roblox ClientSettings.
    /// Byfron / Hyperion detects ClientAppSettings.json and FastFlag overrides (especially FFlagDebug*)
    /// as "modified version of Roblox", resulting in 7-day account suspensions.
    /// This patcher actively PURGES and removes any ClientSettings folders to ensure Roblox stays
    /// 100% vanilla, clean, and safe from anti-cheat bans.
    /// </summary>
    public static partial class ClientSettingsPatcher
    {
        private static readonly System.Threading.Lock PatchLock = new();
        internal static Action? TestOnFindRobloxVersionFoldersCalled;

        [GeneratedRegex(@"<int name=""GraphicsQualityLevel"">-?\d+</int>")]
        private static partial Regex GraphicsQualityXmlRegex();

        [GeneratedRegex(@"<bool name=""Fullscreen"">(true|false)</bool>")]
        private static partial Regex FullscreenXmlRegex();

        [GeneratedRegex(@"<int name=""FramerateCap"">-?\d+</int>")]
        private static partial Regex FramerateCapXmlRegex();

        [GeneratedRegex(@"<Item class=""UserGameSettings""[^>]*>\s*<Properties\s*/?>", RegexOptions.Singleline)]
        private static partial Regex UserGameSettingsPropertiesRegex();

        public static void PatchSettings()
        {
            PatchSettings(settingsPathOverride: null);
        }

        internal static void PatchSettings(string? settingsPathOverride)
        {
            lock (PatchLock)
            {
                bool potatoMode = AppServices.GeneralSettings?.Get<bool>("EnablePotatoMode", false) ?? false;
                bool unlockFps = AppServices.GeneralSettings?.Get<bool>("UnlockFPS", false) ?? false;
                int? graphicsQuality = potatoMode ? 1 : null;
                int? framerateCap = unlockFps
                    ? Math.Clamp(AppServices.GeneralSettings?.Get<int>("MaxFPSValue", 144) ?? 144, 30, 240)
                    : null;

                if (graphicsQuality.HasValue || framerateCap.HasValue)
                {
                    ConfigureGlobalBasicSettings(
                        graphicsQualityLevel: graphicsQuality,
                        fullscreen: null,
                        framerateCap: framerateCap,
                        settingsPathOverride: settingsPathOverride);
                }

                if (string.IsNullOrEmpty(settingsPathOverride))
                {
                    // Do not overwrite user's in-game Roblox FPS setting when UnlockFPS is off; only scrub legacy FastFlags.
                    foreach (var folder in FindRobloxVersionFolders())
                    {
                        ScrubLegacyFpsFastFlag(folder.FullName);
                    }

                    PurgeSettings();
                }
            }
        }

        /// <summary>
        /// Actively purges any ClientSettings directory from all detected Roblox versions to prevent bans.
        /// Respects user configuration and preserves Bloxstrap/Fishstrap custom bootstrapper folders.
        /// </summary>
        public const string FpsFlagKey = "DFIntTaskSchedulerTargetFps";

        public static void ApplyFpsLimit(bool unlockFps, int targetFps)
        {
            ApplyFpsLimit(unlockFps, targetFps, xmlPathOverride: null);
        }

        internal static void ApplyFpsLimit(bool unlockFps, int targetFps, string? xmlPathOverride = null)
        {
            int cap = unlockFps ? Math.Clamp(targetFps, 30, 240) : -1;
            ConfigureGlobalBasicSettings(graphicsQualityLevel: null, fullscreen: null, framerateCap: cap, settingsPathOverride: xmlPathOverride);

            if (string.IsNullOrEmpty(xmlPathOverride))
            {
                var folders = FindRobloxVersionFolders();
                foreach (var folder in folders)
                {
                    ApplyFpsLimitToDirectory(folder.FullName, unlockFps, targetFps);
                }
            }
        }

        public static void ApplyFpsLimitToDirectory(string versionFolder, bool unlockFps, int targetFps)
        {
            ScrubLegacyFpsFastFlag(versionFolder);
        }

        public static void ScrubLegacyFpsFastFlag(string versionFolder)
        {
            try
            {
                string settingsDir = Path.Combine(versionFolder, "ClientSettings");
                string jsonPath = Path.Combine(settingsDir, "ClientAppSettings.json");
                if (!File.Exists(jsonPath))
                    return;

                string existing = File.ReadAllText(jsonPath);
                var parsed = JsonSerializer.Deserialize(existing, ShiroAppJson.Default.DictionaryStringJsonElement);
                if (parsed != null && parsed.Remove(FpsFlagKey))
                {
                    if (parsed.Count == 0)
                    {
                        File.Delete(jsonPath);
                        if (Directory.Exists(settingsDir) && Directory.GetFileSystemEntries(settingsDir).Length == 0)
                            Directory.Delete(settingsDir);
                    }
                    else
                    {
                        string updatedJson = SerializeClientSettings(parsed);
                        StoragePaths.AtomicWriteText(jsonPath, updatedJson, durable: true);
                    }
                    AppServices.Logger?.Info($"[ClientSettingsPatcher] Scrubbed legacy FPS flag from {Path.GetFileName(versionFolder)}");
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[ClientSettingsPatcher] ScrubLegacyFpsFastFlag failed for {versionFolder}: {ex.Message}");
            }
        }

        private static string SerializeClientSettings(Dictionary<string, JsonElement> settings)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var kvp in settings)
                {
                    writer.WritePropertyName(kvp.Key);
                    kvp.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }

        public static void PurgeSettings()
        {
            bool shouldPurge = AppServices.GeneralSettings?.Get<bool>("PurgeClientSettings", false) ?? false;
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
                        AppServices.Logger?.Debug($"[ClientSettingsPatcher] Preserving custom bootstrapper ClientSettings in {folder.FullName}");
                        continue;
                    }

                    string settingsDir = Path.Combine(folder.FullName, "ClientSettings");
                    if (Directory.Exists(settingsDir))
                    {
                        Directory.Delete(settingsDir, recursive: true);
                        AppServices.Logger?.Info($"[ClientSettingsPatcher] Safely purged ClientSettings in {folder.Name} to protect against Byfron bans.");
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Warn($"[ClientSettingsPatcher] Could not purge ClientSettings in {folder.FullName}: {ex.Message}");
                }
            }
        }

        internal static string UpdateBasicSettingsXmlContent(
            string xml,
            int? graphicsQualityLevel,
            bool? fullscreen,
            out bool modified,
            int? framerateCap = null)
        {
            modified = false;
            if (string.IsNullOrEmpty(xml))
                return string.Empty;

            string current = xml;
            if (graphicsQualityLevel.HasValue)
            {
                int lvl = Math.Clamp(graphicsQualityLevel.Value, 1, 10);
                modified |= UpsertXmlProperty(
                    ref current,
                    GraphicsQualityXmlRegex(),
                    $"<int name=\"GraphicsQualityLevel\">{lvl}</int>",
                    insertIfMissing: true);
            }

            if (fullscreen.HasValue)
            {
                string fsVal = fullscreen.Value ? "true" : "false";
                modified |= UpsertXmlProperty(
                    ref current,
                    FullscreenXmlRegex(),
                    $"<bool name=\"Fullscreen\">{fsVal}</bool>",
                    insertIfMissing: true);
            }

            if (framerateCap.HasValue)
            {
                int cap = framerateCap.Value <= 0 ? -1 : Math.Clamp(framerateCap.Value, 30, 240);
                modified |= UpsertXmlProperty(
                    ref current,
                    FramerateCapXmlRegex(),
                    $"<int name=\"FramerateCap\">{cap}</int>",
                    insertIfMissing: cap > 0);
            }

            return current;
        }

        private static bool UpsertXmlProperty(
            ref string xml,
            Regex existingRegex,
            string replacementNode,
            bool insertIfMissing)
        {
            if (existingRegex.IsMatch(xml))
            {
                string next = existingRegex.Replace(xml, replacementNode);
                if (!ReferenceEquals(next, xml) && !string.Equals(next, xml, StringComparison.Ordinal))
                {
                    xml = next;
                    return true;
                }
                return false;
            }

            if (!insertIfMissing)
                return false;

            var match = UserGameSettingsPropertiesRegex().Match(xml);
            if (!match.Success)
                return false;

            if (match.Value.EndsWith("/>", StringComparison.Ordinal))
            {
                int slashIndex = match.Index + match.Value.LastIndexOf("/>", StringComparison.Ordinal);
                int openTagEnd = slashIndex;
                while (openTagEnd > match.Index && char.IsWhiteSpace(xml[openTagEnd - 1]))
                    openTagEnd--;

                xml = $"{xml[..openTagEnd]}>{replacementNode}</Properties>{xml[(slashIndex + 2)..]}";
                return true;
            }

            int insertIndex = match.Index + match.Length;
            xml = xml.Insert(insertIndex, replacementNode);
            return true;
        }

        /// <summary>
        /// Updates Roblox's native GlobalBasicSettings_13.xml safely without touching FastFlags or triggering anti-cheat detections.
        /// </summary>
        public static bool ConfigureGlobalBasicSettings(int? graphicsQualityLevel = null, bool? fullscreen = null)
        {
            return ConfigureGlobalBasicSettings(graphicsQualityLevel, fullscreen, framerateCap: null, settingsPathOverride: null);
        }

        internal static bool ConfigureGlobalBasicSettings(
            int? graphicsQualityLevel,
            bool? fullscreen,
            int? framerateCap,
            string? settingsPathOverride = null)
        {
            try
            {
                string settingsPath = settingsPathOverride ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Roblox",
                    "GlobalBasicSettings_13.xml");
                if (!File.Exists(settingsPath))
                    return false;

                string xml = File.ReadAllText(settingsPath);
                string updatedXml = UpdateBasicSettingsXmlContent(xml, graphicsQualityLevel, fullscreen, out bool modified, framerateCap);

                if (modified)
                {
                    StoragePaths.AtomicWriteText(settingsPath, updatedXml, durable: true);
                    AppServices.Logger?.Info("[ClientSettingsPatcher] Successfully updated native GlobalBasicSettings_13.xml");
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[ClientSettingsPatcher] ConfigureGlobalBasicSettings failed: {ex.Message}");
            }
            return false;
        }

        public static List<DirectoryInfo> FindRobloxVersionFolders()
        {
            TestOnFindRobloxVersionFoldersCalled?.Invoke();
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
                    AppServices.Logger?.Debug($"Scan Roblox version directory '{versionsRoot}' failed: {ex.Message}");
                }
            }

            // 2. Scan Registry as fallback
            try
            {
                using (var subKey = Registry.ClassesRoot.OpenSubKey(@"roblox\DefaultIcon"))
                {
                    object? regValue = subKey?.GetValue("");
                    string cleanPath = NormalizeRegistryExecutablePath(regValue as string);
                    if (!string.IsNullOrWhiteSpace(cleanPath))
                    {
                        var regDir = Directory.GetParent(cleanPath);
                        if (regDir != null && regDir.Exists && seenFullNames.Add(regDir.FullName))
                        {
                            folders.Add(regDir);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Scan Roblox registry key failed: {ex.Message}");
            }

            return folders;
        }

        internal static string NormalizeRegistryExecutablePath(string? rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
                return string.Empty;

            string cleaned = rawValue.Trim().Trim('"');
            int commaIdx = cleaned.IndexOf(',');
            if (commaIdx > 0)
                cleaned = cleaned.Substring(0, commaIdx).Trim().Trim('"');

            return cleaned;
        }
    }
}
