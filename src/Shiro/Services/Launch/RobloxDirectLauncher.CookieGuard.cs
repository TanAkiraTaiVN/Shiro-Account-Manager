#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;

namespace Shiro.Services
{
    public static partial class RobloxDirectLauncher
    {
        private static readonly System.Threading.Lock _cookieLock = new();
        private static CancellationTokenSource? _restoreCts;
        private static string? _lastInjectedUsername;
        private static string? _lastInjectedToken;
        private static string? _lastInjectedTrackerId;
        private static int _activeGuardScopes;
        private static bool _hasPendingBackupRestore;

        /// <summary>
        /// Indicates whether an active batch or launch scope is currently holding the RobloxCookies.dat guard,
        /// preventing background timer restoration from clobbering cookies mid-batch.
        /// </summary>
        public static bool IsCookieGuardHeld
        {
            get
            {
                lock (_cookieLock)
                    return _activeGuardScopes > 0;
            }
        }

        public static int ActiveCookieGuardCount
        {
            get
            {
                lock (_cookieLock)
                    return _activeGuardScopes;
            }
        }

        public sealed class CookieGuardScope : IDisposable
        {
            private readonly int _restoreDelayOnDisposeMs;
            private int _disposed;

            internal CookieGuardScope(int restoreDelayOnDisposeMs)
            {
                _restoreDelayOnDisposeMs = Math.Max(0, restoreDelayOnDisposeMs);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                bool shouldScheduleRestore = false;
                lock (_cookieLock)
                {
                    if (_activeGuardScopes > 0)
                        _activeGuardScopes--;

                    if (_activeGuardScopes == 0 && _hasPendingBackupRestore)
                        shouldScheduleRestore = true;
                }

                if (shouldScheduleRestore)
                {
                    ScheduleOriginalCookieRestore(_restoreDelayOnDisposeMs);
                }
            }
        }

        /// <summary>
        /// Acquires a reference-counted guard scope around multi-account or slow launches so RobloxCookies.dat
        /// is never overwritten by the original-session restore timer until the batch completes.
        /// </summary>
        public static CookieGuardScope BeginCookieGuardScope(int restoreDelayOnDisposeMs = 10_000)
        {
            lock (_cookieLock)
            {
                _activeGuardScopes++;
                _restoreCts?.Cancel();
                _restoreCts?.Dispose();
                _restoreCts = null;
            }

            return new CookieGuardScope(restoreDelayOnDisposeMs);
        }

        /// <summary>
        /// Injects the account's .ROBLOSECURITY session directly into %LOCALAPPDATA%\Roblox\LocalStorage\RobloxCookies.dat
        /// encrypted with Windows DPAPI (CurrentUser). Automatically syncs any rotated token from the previous
        /// launched client before overwriting, backs up the user's original session, and schedules safe restoration.
        /// </summary>
        public static bool InjectAccountCookieToLocalStorage(
            string securityToken,
            string? browserTrackerId = null,
            DateTimeOffset? expiryUtc = null,
            string? localStorageDirectoryOverride = null,
            string? username = null)
        {
            if (string.IsNullOrWhiteSpace(securityToken)) return false;
            try
            {
                string token = securityToken.Trim().Trim('"').Trim();
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string localStorageDir = localStorageDirectoryOverride ?? Path.Combine(localAppData, "Roblox", "LocalStorage");
                Directory.CreateDirectory(localStorageDir);
                string cookiesDatPath = Path.Combine(localStorageDir, "RobloxCookies.dat");
                string backupPath = Path.Combine(localStorageDir, "RobloxCookies.dat.shiro-orig");

                // Validate existing CookiesVersion if file exists to prevent format corruption
                if (File.Exists(cookiesDatPath))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllBytes(cookiesDatPath));
                        if (doc.RootElement.TryGetProperty("CookiesVersion", out var verElement))
                        {
                            string? ver = verElement.GetString();
                            if (!string.IsNullOrEmpty(ver) && ver != "1")
                            {
                                Program.Logger?.Warn($"[RobloxDirectLauncher] RobloxCookies.dat has unsupported CookiesVersion '{ver}'. Direct injection skipped.");
                                return false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"[RobloxDirectLauncher] Could not inspect existing RobloxCookies.dat JSON: {ex.Message}");
                    }

                    // Preserve original user session backup if not already backed up
                    if (!File.Exists(backupPath))
                    {
                        try
                        {
                            File.Copy(cookiesDatPath, backupPath, overwrite: false);
                            Program.Logger?.Info("[RobloxDirectLauncher] Backed up original RobloxCookies.dat before injection.");
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"[RobloxDirectLauncher] Could not create original cookie backup: {ex.Message}");
                        }
                    }
                    else if (localStorageDirectoryOverride == null)
                    {
                        // Sync any rotated cookie written by the preceding account's Roblox client before overwriting
                        SyncRotatedClientCookieIfApplicable(cookiesDatPath, backupPath);
                    }
                }

                long expiry = (expiryUtc ?? DateTimeOffset.UtcNow.AddMonths(3)).ToUnixTimeSeconds();
                string btid = !string.IsNullOrWhiteSpace(browserTrackerId) ? browserTrackerId : GenerateRandomTrackerId();

                string netscapeCookies =
                    $".roblox.com\tTRUE\t/\tTRUE\t{expiry}\t.ROBLOSECURITY\t{token}\r\n" +
                    $".roblox.com\tTRUE\t/\tFALSE\t{expiry}\tRBXEventTrackerV2\tCreateDate={DateTime.UtcNow:MM/dd/yyyy HH:mm:ss}&rbxid=&rbxuid=&browserid={btid};\r\n";

                byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(netscapeCookies);
                byte[] encryptedBytes = System.Security.Cryptography.ProtectedData.Protect(
                    plainBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                string base64Data = Convert.ToBase64String(encryptedBytes);

                var jsonObject = new RobloxCookiesDatEnvelope("1", base64Data);
                string json = JsonSerializer.Serialize(jsonObject, ShiroAppJson.Default.RobloxCookiesDatEnvelope);

                StoragePaths.AtomicWriteText(cookiesDatPath, json, durable: true);
                Program.Logger?.Info("[RobloxDirectLauncher] Successfully injected .ROBLOSECURITY into RobloxCookies.dat (DPAPI, atomic).");

                if (localStorageDirectoryOverride == null)
                {
                    bool shouldSchedule;
                    lock (_cookieLock)
                    {
                        _lastInjectedUsername = username;
                        _lastInjectedToken = token;
                        _lastInjectedTrackerId = btid;
                        _hasPendingBackupRestore = true;
                        shouldSchedule = _activeGuardScopes == 0;
                    }

                    if (shouldSchedule)
                    {
                        ScheduleOriginalCookieRestore(12_000);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[RobloxDirectLauncher] Failed to inject RobloxCookies.dat: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Schedules restoration of the original user's RobloxCookies.dat after a delay once launched processes have initialized.
        /// Deferred automatically if an active <see cref="CookieGuardScope"/> is currently held.
        /// </summary>
        public static void ScheduleOriginalCookieRestore(int delayMs = 10_000)
        {
            lock (_cookieLock)
            {
                _restoreCts?.Cancel();
                _restoreCts?.Dispose();
                _restoreCts = null;

                if (_activeGuardScopes > 0)
                {
                    _hasPendingBackupRestore = true;
                    return;
                }

                _restoreCts = new CancellationTokenSource();
                var token = _restoreCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(Math.Max(0, delayMs), token).ConfigureAwait(false);
                        if (IsCookieGuardHeld) return;

                        // Never overwrite RobloxCookies.dat with .shiro-orig while any Roblox client is actively running:
                        // if a running client is displaying a WebView2 verification challenge (challengedByGcs) or LuaApp Home,
                        // restoring a previous account's cookie triggers "[FLog::NewWebView2Browser] Security cookie value changed"
                        // and hijacks the running client's session into the previous account!
                        if (RobloxProcessService.IsAnyRobloxRunning())
                        {
                            lock (_cookieLock)
                            {
                                _hasPendingBackupRestore = true;
                            }
                            return;
                        }

                        RestoreOriginalCookies();
                    }
                    catch (OperationCanceledException) { /* canceled */ }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"[RobloxDirectLauncher] Scheduled cookie restoration failed: {ex.Message}");
                    }
                }, token);
            }
        }

        /// <summary>
        /// Waits for a spawned Roblox client process to initialize its main window (up to 18s) plus a 2.5s settle window
        /// before syncing rotated cookies. Defers restoring the original RobloxCookies.dat backup while Roblox clients are running
        /// so an active WebView2 challenge (challengedByGcs) is never overwritten mid-session.
        /// </summary>
        internal static void SchedulePostSpawnCookieRestore(int pid)
        {
            if (pid <= 0) return;

            lock (_cookieLock)
            {
                if (_activeGuardScopes > 0)
                {
                    _hasPendingBackupRestore = true;
                    return;
                }

                _restoreCts?.Cancel();
                _restoreCts?.Dispose();
                _restoreCts = new CancellationTokenSource();
                var token = _restoreCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RobloxSingletonService.WaitForClientUnlockedAsync(
                            pid,
                            TimeSpan.FromSeconds(18),
                            token).ConfigureAwait(false);
                        await Task.Delay(2500, token).ConfigureAwait(false);

                        if (!IsCookieGuardHeld)
                        {
                            if (RobloxProcessService.IsAnyRobloxRunning())
                            {
                                lock (_cookieLock)
                                {
                                    _hasPendingBackupRestore = true;
                                }
                                return;
                            }

                            RestoreOriginalCookies();
                        }
                    }
                    catch (OperationCanceledException) { /* canceled */ }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"[RobloxDirectLauncher] Post-spawn cookie restore failed: {ex.Message}");
                    }
                }, token);
            }
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
                byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(
                    encrypted, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                string netscape = System.Text.Encoding.UTF8.GetString(plain);

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
                Program.Logger?.Debug($"[RobloxDirectLauncher] Could not extract cookie from {Path.GetFileName(filePath)}: {ex.Message}");
            }

            return null;
        }

        private static void SyncRotatedClientCookieIfApplicable(string cookiesDatPath, string backupPath)
        {
            string? expectedUser;
            string? injectedToken;
            string? expectedTrackerId;
            lock (_cookieLock)
            {
                expectedUser = _lastInjectedUsername;
                injectedToken = _lastInjectedToken;
                expectedTrackerId = _lastInjectedTrackerId;
                _lastInjectedUsername = null;
                _lastInjectedToken = null;
                _lastInjectedTrackerId = null;
            }

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
                Program.Logger?.Warn($"[RobloxDirectLauncher] Refused to sync rotated cookie from RobloxCookies.dat to '{target.Username}' (Account UserID={target.UserID}, Tracker rbxuid={trackerUserId}, Tracker browserid={trackerBrowserId}) to prevent cross-account contamination.");
                return;
            }

            // Also ensure the rotated cookie does not already belong to a different account in AccountManager
            bool belongsToAnotherAccount = AppServices.AccountRepository.GetAll().Any(a =>
                a != null &&
                !string.Equals(a.Username, target.Username, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.SecurityToken, currentClientCookie, StringComparison.Ordinal));
            if (belongsToAnotherAccount)
            {
                Program.Logger?.Warn($"[RobloxDirectLauncher] Refused to sync cookie from RobloxCookies.dat to '{target.Username}' because it belongs to another saved account.");
                return;
            }

            target.ApplyRotatedSecurityToken(currentClientCookie, "RobloxCookies.dat post-launch sync");
        }

        /// <summary>
        /// Restores the original RobloxCookies.dat backup if one exists and cleans up the backup file.
        /// </summary>
        public static bool RestoreOriginalCookies(string? localStorageDirectoryOverride = null)
        {
            if (localStorageDirectoryOverride == null)
            {
                lock (_cookieLock)
                {
                    if (_activeGuardScopes > 0)
                    {
                        _hasPendingBackupRestore = true;
                        return false;
                    }

                    _restoreCts?.Cancel();
                    _restoreCts?.Dispose();
                    _restoreCts = null;
                    _hasPendingBackupRestore = false;
                }
            }

            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string localStorageDir = localStorageDirectoryOverride ?? Path.Combine(localAppData, "Roblox", "LocalStorage");
                string cookiesDatPath = Path.Combine(localStorageDir, "RobloxCookies.dat");
                string backupPath = Path.Combine(localStorageDir, "RobloxCookies.dat.shiro-orig");

                if (localStorageDirectoryOverride == null)
                {
                    SyncRotatedClientCookieIfApplicable(cookiesDatPath, backupPath);
                }

                if (!File.Exists(backupPath)) return false;

                string backupJson = File.ReadAllText(backupPath, System.Text.Encoding.UTF8);
                StoragePaths.AtomicWriteText(cookiesDatPath, backupJson, durable: true);
                try
                {
                    File.Delete(backupPath);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[RobloxDirectLauncher] Failed to delete cookie backup file: {ex.Message}");
                }

                Program.Logger?.Info("[RobloxDirectLauncher] Successfully restored original RobloxCookies.dat from backup.");
                return true;
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[RobloxDirectLauncher] Failed to restore original RobloxCookies.dat: {ex.Message}");
                return false;
            }
        }
    }
}
