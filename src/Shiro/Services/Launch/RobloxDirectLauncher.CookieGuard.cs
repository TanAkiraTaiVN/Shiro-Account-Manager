#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
        private static bool _hasPendingInjectionRestore;

        internal static Func<bool>? TestIsAnyRobloxRunningOverride;
        internal static Func<int, TimeSpan, CancellationToken, Task<bool>>? TestCookieGuardWaitForClientUnlockedOverride;
        internal static int? TestCookieRestoreRetryDelayMsOverride;
        internal static int? TestPostSpawnSettleDelayMsOverride;
        internal static string? TestDefaultLocalStorageDirOverride;

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

        internal static bool HasPendingCookieBackup
        {
            get
            {
                lock (_cookieLock)
                    return HasPendingCookieBackupLocked();
            }
        }

        internal static bool TryConsumePostSpawnRestorePending()
        {
            lock (_cookieLock)
            {
                if (!_hasPendingInjectionRestore)
                    return false;

                _hasPendingInjectionRestore = false;
                return true;
            }
        }

        internal static void ResetCookieGuardStateForTest()
        {
            lock (_cookieLock)
            {
                _restoreCts?.Cancel();
                _restoreCts = null;
                _activeGuardScopes = 0;
                _hasPendingBackupRestore = false;
                _hasPendingInjectionRestore = false;
                _lastInjectedUsername = null;
                _lastInjectedToken = null;
                _lastInjectedTrackerId = null;
            }

            TestIsAnyRobloxRunningOverride = null;
            TestCookieGuardWaitForClientUnlockedOverride = null;
            TestCookieRestoreRetryDelayMsOverride = null;
            TestPostSpawnSettleDelayMsOverride = null;
            TestDefaultLocalStorageDirOverride = null;
        }

        private static string GetDefaultLocalStorageDir() =>
            TestDefaultLocalStorageDirOverride ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "LocalStorage");

        private static bool HasPendingCookieBackupLocked()
        {
            if (_hasPendingBackupRestore || _hasPendingInjectionRestore)
                return true;

            string localStorageDir = GetDefaultLocalStorageDir();
            return File.Exists(Path.Combine(localStorageDir, "RobloxCookies.dat.shiro-orig")) ||
                   File.Exists(Path.Combine(localStorageDir, "RobloxCookies.dat.shiro-none"));
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
                _restoreCts = null;
            }

            return new CookieGuardScope(restoreDelayOnDisposeMs);
        }

        internal static byte[] ProtectAndZeroPlaintext(byte[] plainBytes, Func<byte[], byte[]>? protectOverride = null)
        {
            ArgumentNullException.ThrowIfNull(plainBytes);
            try
            {
                return protectOverride != null
                    ? protectOverride(plainBytes)
                    : ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
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
                string localStorageDir = localStorageDirectoryOverride ?? GetDefaultLocalStorageDir();
                Directory.CreateDirectory(localStorageDir);
                string cookiesDatPath = Path.Combine(localStorageDir, "RobloxCookies.dat");
                string backupPath = Path.Combine(localStorageDir, "RobloxCookies.dat.shiro-orig");
                string noOrigSentinelPath = Path.Combine(localStorageDir, "RobloxCookies.dat.shiro-none");

                lock (_cookieLock)
                {
                    if (!File.Exists(cookiesDatPath) && !File.Exists(backupPath) && !File.Exists(noOrigSentinelPath))
                    {
                        try { File.WriteAllText(noOrigSentinelPath, "1"); }
                        catch (Exception ex) { AppServices.Logger.Debug($"[RobloxDirectLauncher] Could not write no-orig sentinel: {ex.Message}"); }
                    }

                    // Validate existing CookiesVersion if file exists to prevent format corruption and back up original session
                    if (!RobloxCookieFileHelper.EnsureVersionSupportedAndBackupExisting(
                            cookiesDatPath, backupPath, noOrigSentinelPath, out bool shouldSyncRotated))
                    {
                        return false;
                    }
                    if (shouldSyncRotated && localStorageDirectoryOverride == null)
                    {
                        SyncRotatedClientCookieIfApplicable(cookiesDatPath, backupPath);
                    }

                    long expiry = (expiryUtc ?? DateTimeOffset.UtcNow.AddMonths(3)).ToUnixTimeSeconds();
                    string btid = !string.IsNullOrWhiteSpace(browserTrackerId) ? browserTrackerId : GenerateRandomTrackerId();

                    string netscapeCookies =
                        $".roblox.com\tTRUE\t/\tTRUE\t{expiry}\t.ROBLOSECURITY\t{token}\r\n" +
                        $".roblox.com\tTRUE\t/\tFALSE\t{expiry}\tRBXEventTrackerV2\tCreateDate={DateTime.UtcNow:MM/dd/yyyy HH:mm:ss}&rbxid=&rbxuid=&browserid={btid}\r\n";

                    byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(netscapeCookies);
                    byte[] encryptedBytes = ProtectAndZeroPlaintext(plainBytes);
                    string base64Data = Convert.ToBase64String(encryptedBytes);

                    var jsonObject = new RobloxCookiesDatEnvelope("1", base64Data);
                    string json = JsonSerializer.Serialize(jsonObject, ShiroAppJson.Default.RobloxCookiesDatEnvelope);

                    StoragePaths.AtomicWriteText(cookiesDatPath, json, durable: true);
                    AppServices.Logger.Info("[RobloxDirectLauncher] Successfully injected .ROBLOSECURITY into RobloxCookies.dat (DPAPI, atomic).");

                    if (localStorageDirectoryOverride == null)
                    {
                        _lastInjectedUsername = username;
                        _lastInjectedToken = token;
                        _lastInjectedTrackerId = btid;
                        _hasPendingBackupRestore = true;
                        _hasPendingInjectionRestore = true;
                        if (_activeGuardScopes == 0)
                        {
                            ScheduleOriginalCookieRestore(12_000);
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                AppServices.Logger.Warn($"[RobloxDirectLauncher] Failed to inject RobloxCookies.dat: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Schedules restoration of the original user's RobloxCookies.dat after a delay once launched processes have initialized.
        /// Deferred automatically if an active <see cref="CookieGuardScope"/> is currently held.
        /// </summary>
        public static void ScheduleOriginalCookieRestore(int delayMs = 10_000)
            => ScheduleRestoreCore(initialDelayMs: Math.Max(0, delayMs), waitForPid: 0, requirePendingBackup: false);

        /// <summary>
        /// Waits for a spawned Roblox client process to initialize its main window (up to 18s) plus a 2.5s settle window
        /// before syncing rotated cookies. Defers restoring the original RobloxCookies.dat backup while Roblox clients are running
        /// so an active WebView2 challenge (challengedByGcs) is never overwritten mid-session.
        /// </summary>
        internal static void SchedulePostSpawnCookieRestore(int pid)
        {
            if (pid <= 0) return;
            int settleDelayMs = TestPostSpawnSettleDelayMsOverride ?? 2_500;
            ScheduleRestoreCore(initialDelayMs: Math.Max(0, settleDelayMs), waitForPid: pid, requirePendingBackup: true);
        }

        private static void ScheduleRestoreCore(int initialDelayMs, int waitForPid, bool requirePendingBackup)
        {
            CancellationTokenSource myCts;
            lock (_cookieLock)
            {
                if (requirePendingBackup && !HasPendingCookieBackupLocked())
                    return;

                if (_activeGuardScopes > 0)
                {
                    if (!requirePendingBackup || HasPendingCookieBackupLocked())
                        _hasPendingBackupRestore = true;
                    return;
                }

                _restoreCts?.Cancel();
                myCts = new CancellationTokenSource();
                _restoreCts = myCts;
            }

            _ = Task.Run(() => RunRestoreLoopAsync(myCts, initialDelayMs, waitForPid));
        }

        private static async Task RunRestoreLoopAsync(
            CancellationTokenSource myCts,
            int initialDelayMs,
            int waitForPid)
        {
            CancellationToken token = myCts.Token;
            try
            {
                if (token.IsCancellationRequested)
                    return;

                if (waitForPid > 0)
                {
                    Task<bool> waitTask = TestCookieGuardWaitForClientUnlockedOverride != null
                        ? TestCookieGuardWaitForClientUnlockedOverride(waitForPid, TimeSpan.FromSeconds(18), token)
                        : RobloxSingletonService.WaitForClientUnlockedAsync(waitForPid, TimeSpan.FromSeconds(18), token);
                    await waitTask.ConfigureAwait(false);
                }

                int nextDelayMs = Math.Max(0, initialDelayMs);
                while (!token.IsCancellationRequested)
                {
                    if (nextDelayMs > 0)
                        await Task.Delay(nextDelayMs, token).ConfigureAwait(false);

                    lock (_cookieLock)
                    {
                        if (!ReferenceEquals(_restoreCts, myCts) || token.IsCancellationRequested)
                            return;

                        if (_activeGuardScopes > 0)
                        {
                            _hasPendingBackupRestore = true;
                            return;
                        }
                    }

                    bool robloxRunning = TestIsAnyRobloxRunningOverride != null
                        ? TestIsAnyRobloxRunningOverride()
                        : RobloxProcessService.IsAnyRobloxRunning();
                    if (robloxRunning)
                    {
                        lock (_cookieLock)
                        {
                            if (!ReferenceEquals(_restoreCts, myCts) || token.IsCancellationRequested)
                                return;

                            _hasPendingBackupRestore = true;
                        }

                        nextDelayMs = Math.Max(1, TestCookieRestoreRetryDelayMsOverride ?? 5_000);
                        continue;
                    }

                    RestoreOriginalCookiesIfOwner(myCts);
                    return;
                }
            }
            catch (OperationCanceledException) { /* canceled */ }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[RobloxDirectLauncher] Scheduled cookie restoration failed: {ex.Message}");
            }
            finally
            {
                lock (_cookieLock)
                {
                    if (ReferenceEquals(_restoreCts, myCts))
                        _restoreCts = null;

                    SafeExecuteHelper.Try(myCts.Dispose, "RobloxDirectLauncher.DisposeRestoreCts");
                }
            }
        }

        private static void RestoreOriginalCookiesIfOwner(CancellationTokenSource myCts)
        {
            lock (_cookieLock)
            {
                if (!ReferenceEquals(_restoreCts, myCts) || myCts.IsCancellationRequested || _activeGuardScopes > 0)
                {
                    if (_activeGuardScopes > 0)
                        _hasPendingBackupRestore = true;
                    return;
                }

                _restoreCts = null;
                RestoreOriginalCookiesCoreLocked(localStorageDirectoryOverride: null, deleteFileOverride: null);
            }
        }

        public static string? TryExtractCookieFromCookiesDatFile(string filePath) =>
            RobloxCookieFileHelper.TryExtractCookieFromCookiesDatFile(filePath);

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

            RobloxCookieFileHelper.SyncRotatedClientCookie(
                cookiesDatPath, backupPath, expectedUser, injectedToken, expectedTrackerId);
        }

        /// <summary>
        /// Restores the original RobloxCookies.dat backup if one exists and cleans up the backup file.
        /// </summary>
        public static bool RestoreOriginalCookies(string? localStorageDirectoryOverride = null)
            => RestoreOriginalCookiesCore(localStorageDirectoryOverride, deleteFileOverride: null);

        internal static bool RestoreOriginalCookiesCore(
            string? localStorageDirectoryOverride = null,
            Action<string>? deleteFileOverride = null)
        {
            lock (_cookieLock)
            {
                if (localStorageDirectoryOverride == null)
                {
                    if (_activeGuardScopes > 0)
                    {
                        _hasPendingBackupRestore = true;
                        return false;
                    }

                    _restoreCts?.Cancel();
                    _restoreCts = null;
                }

                return RestoreOriginalCookiesCoreLocked(localStorageDirectoryOverride, deleteFileOverride);
            }
        }

        private static bool RestoreOriginalCookiesCoreLocked(
            string? localStorageDirectoryOverride,
            Action<string>? deleteFileOverride)
        {
            bool isDefault = localStorageDirectoryOverride == null;
            string localStorageDir = localStorageDirectoryOverride ?? GetDefaultLocalStorageDir();
            bool stillHasPendingFiles = false;

            try
            {
                if (isDefault)
                {
                    string cookiesDatPath = Path.Combine(localStorageDir, "RobloxCookies.dat");
                    string backupPath = Path.Combine(localStorageDir, "RobloxCookies.dat.shiro-orig");
                    SyncRotatedClientCookieIfApplicable(cookiesDatPath, backupPath);
                }

                bool restored;
                (restored, stillHasPendingFiles) = RobloxCookieFileHelper.RestoreDirectoryCookies(localStorageDir, deleteFileOverride);
                return restored;
            }
            catch (Exception ex)
            {
                stillHasPendingFiles =
                    File.Exists(Path.Combine(localStorageDir, "RobloxCookies.dat.shiro-orig")) ||
                    File.Exists(Path.Combine(localStorageDir, "RobloxCookies.dat.shiro-none"));
                AppServices.Logger.Warn($"[RobloxDirectLauncher] Failed to restore original RobloxCookies.dat: {ex.Message}");
                return false;
            }
            finally
            {
                if (isDefault)
                {
                    _hasPendingBackupRestore = stillHasPendingFiles;
                    if (!stillHasPendingFiles)
                        _hasPendingInjectionRestore = false;
                }
            }
        }
    }
}
