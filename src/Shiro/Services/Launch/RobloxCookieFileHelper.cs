#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shiro.Classes;

namespace Shiro.Services
{
    /// <summary>
    /// Pure DPAPI cookie file parsing, target validation, and local storage cleanup helpers for RobloxCookies.dat.
    /// </summary>
    internal static class RobloxCookieFileHelper
    {
        public static bool CanSyncRotatedCookieForTarget(
            Account? target,
            long trackerUserId,
            string? trackerBrowserId,
            string? expectedBrowserId)
        {
            if (target == null || target.HasHbaBinding)
                return false;

            if (trackerUserId > 0 && target.UserID > 0 && trackerUserId != target.UserID)
                return false;

            if (!string.IsNullOrWhiteSpace(trackerBrowserId))
            {
                bool matchesAccountTracker = !string.IsNullOrWhiteSpace(target.BrowserTrackerID) &&
                    string.Equals(trackerBrowserId, target.BrowserTrackerID, StringComparison.Ordinal);
                bool matchesInjectedTracker = !string.IsNullOrWhiteSpace(expectedBrowserId) &&
                    string.Equals(trackerBrowserId, expectedBrowserId, StringComparison.Ordinal);

                if (!matchesAccountTracker && !matchesInjectedTracker && (trackerUserId <= 0 || target.UserID <= 0))
                    return false;
            }

            return true;
        }

        public static string? TryExtractCookieFromCookiesDatFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(filePath));
                if (!doc.RootElement.TryGetProperty("CookiesData", out var dataProp))
                    return null;

                string? base64 = dataProp.GetString();
                if (string.IsNullOrWhiteSpace(base64))
                    return null;

                byte[] encrypted = Convert.FromBase64String(base64);
                byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                string netscape = Encoding.UTF8.GetString(plain);

                foreach (string rawLine in netscape.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                        continue;

                    string[] cols = line.Split('\t');
                    if (cols.Length >= 7 && string.Equals(cols[5], ".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = Account.SanitizeCookie(cols[6]);
                        if (!string.IsNullOrWhiteSpace(val) && val.Contains("_|WARNING:-DO-NOT-SHARE-THIS", StringComparison.Ordinal))
                            return val;
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[RobloxDirectLauncher] Could not extract cookie from {Path.GetFileName(filePath)}: {ex.Message}");
            }

            return null;
        }

        public static bool TryExtractTrackerMetadataFromCookiesDatFile(string filePath, out long userId, out string? browserTrackerId)
        {
            userId = 0;
            browserTrackerId = null;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(filePath));
                if (!doc.RootElement.TryGetProperty("CookiesData", out var dataProp))
                    return false;

                string? base64 = dataProp.GetString();
                if (string.IsNullOrWhiteSpace(base64))
                    return false;

                byte[] encrypted = Convert.FromBase64String(base64);
                byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                string netscape = Encoding.UTF8.GetString(plain);

                foreach (string rawLine in netscape.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                        continue;

                    string[] cols = line.Split('\t');
                    if (cols.Length >= 7 && string.Equals(cols[5], "RBXEventTrackerV2", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = cols[6].Trim().TrimEnd(';');
                        foreach (string kv in val.Split('&', StringSplitOptions.RemoveEmptyEntries))
                        {
                            int eq = kv.IndexOf('=');
                            if (eq <= 0) continue;
                            string key = kv.Substring(0, eq).Trim();
                            string v = kv.Substring(eq + 1).Trim();
                            if (string.Equals(key, "rbxuid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v))
                            {
                                long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId);
                            }
                            else if (string.Equals(key, "browserid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v))
                            {
                                browserTrackerId = v;
                            }
                        }
                        return userId > 0 || !string.IsNullOrEmpty(browserTrackerId);
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[RobloxDirectLauncher] Could not extract tracker metadata from {Path.GetFileName(filePath)}: {ex.Message}");
            }

            return false;
        }

        public static bool ClearLocalClientSessionFiles()
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string localStorageDir = Path.Combine(localAppData, "Roblox", "LocalStorage");
                if (Directory.Exists(localStorageDir))
                {
                    string cookiesDat = Path.Combine(localStorageDir, "RobloxCookies.dat");
                    if (File.Exists(cookiesDat))
                    {
                        try { File.Delete(cookiesDat); }
                        catch (Exception ex) { AppServices.Logger.Debug($"Delete cookiesDat failed: {ex.Message}"); }
                    }

                    string appStorage = Path.Combine(localStorageDir, "appStorage.json");
                    if (File.Exists(appStorage))
                    {
                        try { File.Delete(appStorage); }
                        catch (Exception ex) { AppServices.Logger.Debug($"Delete appStorage failed: {ex.Message}"); }
                    }
                }

                string rbxStorageDb = Path.Combine(localAppData, "Roblox", "rbx-storage.db");
                if (File.Exists(rbxStorageDb))
                {
                    try { File.Delete(rbxStorageDb); }
                    catch (Exception ex) { AppServices.Logger.Debug($"Delete rbxStorageDb failed: {ex.Message}"); }
                }

                AppServices.Logger.Info("[RobloxProcessService] Successfully cleared Roblox Client local session storage.");
                return true;
            }
            catch (Exception ex)
            {
                AppServices.Logger.Warn($"[RobloxProcessService] Failed to clear Roblox Client session: {ex.Message}");
                return false;
            }
        }
    }
}
