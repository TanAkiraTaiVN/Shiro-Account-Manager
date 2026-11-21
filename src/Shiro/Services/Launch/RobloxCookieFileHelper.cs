#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
                string netscape;
                try { netscape = Encoding.UTF8.GetString(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }

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
                string netscape;
                try { netscape = Encoding.UTF8.GetString(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }

                foreach (string rawLine in netscape.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                        continue;

                    string[] cols = line.Split('\t');
                    if (cols.Length >= 7 && string.Equals(cols[5], "RBXEventTrackerV2", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = cols[6].Trim().TrimEnd(';');
                        long rbxuidVal = 0;
                        long rbxidVal = 0;
                        foreach (string kv in val.Split('&', StringSplitOptions.RemoveEmptyEntries))
                        {
                            int eq = kv.IndexOf('=');
                            if (eq <= 0) continue;
                            string key = kv.Substring(0, eq).Trim();
                            string v = kv.Substring(eq + 1).Trim();
                            if (string.Equals(key, "rbxuid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v))
                            {
                                long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out rbxuidVal);
                            }
                            else if (string.Equals(key, "rbxid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v))
                            {
                                long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out rbxidVal);
                            }
                            else if (string.Equals(key, "browserid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v))
                            {
                                browserTrackerId = v;
                            }
                        }
                        userId = rbxuidVal > 0 ? rbxuidVal : rbxidVal;
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

        public static (bool Restored, bool StillHasPendingFiles) RestoreDirectoryCookies(
            string localStorageDir,
            Action<string>? deleteFileOverride = null)
        {
            Action<string> deleteFile = deleteFileOverride ?? File.Delete;
            string cookiesDatPath = Path.Combine(localStorageDir, "RobloxCookies.dat");
            string backupPath = Path.Combine(localStorageDir, "RobloxCookies.dat.shiro-orig");
            string noOrigSentinelPath = Path.Combine(localStorageDir, "RobloxCookies.dat.shiro-none");

            if (!File.Exists(backupPath))
            {
                if (!File.Exists(noOrigSentinelPath))
                    return (false, false);

                if (File.Exists(cookiesDatPath))
                {
                    try
                    {
                        deleteFile(cookiesDatPath);
                    }
                    catch (Exception ex)
                    {
                        AppServices.Logger.Debug($"[RobloxDirectLauncher] Failed to delete temporary RobloxCookies.dat: {ex.Message}");
                        return (false, true);
                    }

                    if (File.Exists(cookiesDatPath))
                        return (false, true);
                }

                try
                {
                    deleteFile(noOrigSentinelPath);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Debug($"[RobloxDirectLauncher] Failed to delete no-orig sentinel: {ex.Message}");
                    return (false, File.Exists(noOrigSentinelPath));
                }

                return (true, false);
            }

            string backupJson = File.ReadAllText(backupPath, Encoding.UTF8);
            StoragePaths.AtomicWriteText(cookiesDatPath, backupJson, durable: true);
            try
            {
                deleteFile(backupPath);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[RobloxDirectLauncher] Failed to delete cookie backup file: {ex.Message}");
            }
            try
            {
                if (File.Exists(noOrigSentinelPath))
                    deleteFile(noOrigSentinelPath);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[RobloxDirectLauncher] Failed to delete no-orig sentinel: {ex.Message}");
            }

            AppServices.Logger.Info("[RobloxDirectLauncher] Successfully restored original RobloxCookies.dat from backup.");
            return (true, File.Exists(backupPath) || File.Exists(noOrigSentinelPath));
        }

        public static bool EnsureVersionSupportedAndBackupExisting(
            string cookiesDatPath,
            string backupPath,
            string noOrigSentinelPath,
            out bool shouldSyncRotated)
        {
            shouldSyncRotated = false;
            if (!File.Exists(cookiesDatPath))
                return true;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(cookiesDatPath));
                if (doc.RootElement.TryGetProperty("CookiesVersion", out var verElement))
                {
                    string? ver = verElement.GetString();
                    if (!string.IsNullOrEmpty(ver) && ver != "1")
                    {
                        AppServices.Logger.Warn($"[RobloxDirectLauncher] RobloxCookies.dat has unsupported CookiesVersion '{ver}'. Direct injection skipped.");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[RobloxDirectLauncher] Could not inspect existing RobloxCookies.dat JSON: {ex.Message}");
            }

            if (!File.Exists(backupPath) && !File.Exists(noOrigSentinelPath))
            {
                try
                {
                    File.Copy(cookiesDatPath, backupPath, overwrite: false);
                    AppServices.Logger.Info("[RobloxDirectLauncher] Backed up original RobloxCookies.dat before injection.");
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Debug($"[RobloxDirectLauncher] Could not create original cookie backup: {ex.Message}");
                }
            }
            else
            {
                shouldSyncRotated = true;
            }

            return true;
        }

        public static void SyncRotatedClientCookie(
            string cookiesDatPath,
            string backupPath,
            string? expectedUser,
            string? injectedToken,
            string? expectedTrackerId)
        {
            if (string.IsNullOrWhiteSpace(expectedUser) || string.IsNullOrWhiteSpace(injectedToken))
                return;

            string? currentClientCookie = TryExtractCookieFromCookiesDatFile(cookiesDatPath);
            if (string.IsNullOrWhiteSpace(currentClientCookie) ||
                string.Equals(currentClientCookie, injectedToken, StringComparison.Ordinal))
            {
                return;
            }

            string? backupCookie = TryExtractCookieFromCookiesDatFile(backupPath);
            if (!string.IsNullOrWhiteSpace(backupCookie) &&
                string.Equals(currentClientCookie, backupCookie, StringComparison.Ordinal))
            {
                return;
            }

            var target = AppServices.AccountRepository.GetAll().FirstOrDefault(a =>
                a != null &&
                string.Equals(a.Username, expectedUser, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.SecurityToken, injectedToken, StringComparison.Ordinal));

            if (target == null)
                return;

            TryExtractTrackerMetadataFromCookiesDatFile(cookiesDatPath, out long trackerUserId, out string? trackerBrowserId);
            if (!CanSyncRotatedCookieForTarget(target, trackerUserId, trackerBrowserId, expectedTrackerId))
            {
                AppServices.Logger.Warn($"[RobloxDirectLauncher] Refused to sync rotated cookie from RobloxCookies.dat to '{target.Username}' (Account UserID={target.UserID}, Tracker rbxuid={trackerUserId}, Tracker browserid={trackerBrowserId}) to prevent cross-account contamination.");
                return;
            }

            bool belongsToAnotherAccount = AppServices.AccountRepository.GetAll().Any(a =>
                a != null &&
                !string.Equals(a.Username, target.Username, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.SecurityToken, currentClientCookie, StringComparison.Ordinal));
            if (belongsToAnotherAccount)
            {
                AppServices.Logger.Warn($"[RobloxDirectLauncher] Refused to sync cookie from RobloxCookies.dat to '{target.Username}' because it belongs to another saved account.");
                return;
            }

            target.ApplyRotatedSecurityToken(currentClientCookie, "RobloxCookies.dat post-launch sync");
        }
    }
}
