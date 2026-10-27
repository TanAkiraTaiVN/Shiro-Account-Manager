using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.Services
{
    /// <summary>
    /// Centralized service for Roblox process lifecycle, discovery, and termination.
    /// Uses native toolhelp snapshot for zero-allocation sub-millisecond process discovery.
    /// </summary>
    public static partial class RobloxProcessService
    {
        public static readonly string[] RobloxProcessNames =
        {
            "RobloxPlayerBeta",
            "RobloxCrashHandler",
            "RobloxPlayerLauncher"
        };

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public nuint th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            public fixed char szExeFile[260];
        }

        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "Process32FirstW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool Process32FirstW(IntPtr hSnapshot, PROCESSENTRY32W* lppe);

        [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "Process32NextW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool Process32NextW(IntPtr hSnapshot, PROCESSENTRY32W* lppe);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint SYNCHRONIZE = 0x00100000;
        private const uint WAIT_TIMEOUT = 0x00000102;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        /// <summary>
        /// Checks whether a process is currently alive using zero-allocation Win32 kernel handle probing.
        /// </summary>
        public static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    return !p.HasExited;
                }
                catch { return false; }
            }

            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, (uint)pid);
            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    return WaitForSingleObject(hProcess, 0) == WAIT_TIMEOUT;
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }

            return GetRunningRobloxPids().Contains(pid);
        }

        private const long SnapshotCacheTtlMs = 350;
        private static readonly System.Threading.Lock _snapshotCacheLock = new();
        private static long _lastSnapshotTick = -SnapshotCacheTtlMs * 2;
        private static int _cachedTotalCount;
        private static int _cachedClientCount;
        private static bool _cachedInstallerRunning;
        private static int[] _cachedClientPids = Array.Empty<int>();

        public static void InvalidateSnapshotCache()
        {
            lock (_snapshotCacheLock)
            {
                _lastSnapshotTick = -SnapshotCacheTtlMs * 2;
            }
        }

        /// <summary>
        /// Zero-allocation native check for whether RobloxPlayerInstaller or RobloxPlayerLauncher is currently running.
        /// Eliminates expensive Process.GetProcessesByName calls on every normal game launch.
        /// </summary>
        public static bool IsRobloxInstallerRunning()
        {
            if (!OperatingSystem.IsWindows())
                return false;

            if (TryGetProcessCountsNative(out _, out _))
            {
                lock (_snapshotCacheLock)
                {
                    return _cachedInstallerRunning;
                }
            }

            return false;
        }

        private static bool TryGetProcessCountsNative(out int totalCount, out int clientCount, List<int> clientPids = null)
        {
            long now = Environment.TickCount64;
            lock (_snapshotCacheLock)
            {
                if (now - _lastSnapshotTick <= SnapshotCacheTtlMs)
                {
                    totalCount = _cachedTotalCount;
                    clientCount = _cachedClientCount;
                    if (clientPids != null && _cachedClientPids.Length > 0)
                        clientPids.AddRange(_cachedClientPids);
                    return true;
                }
            }

            var capturedPids = new List<int>(8);
            if (!TryGetProcessCountsNativeCore(out totalCount, out clientCount, out bool installerRunning, capturedPids))
                return false;

            lock (_snapshotCacheLock)
            {
                _lastSnapshotTick = Environment.TickCount64;
                _cachedTotalCount = totalCount;
                _cachedClientCount = clientCount;
                _cachedInstallerRunning = installerRunning;
                _cachedClientPids = capturedPids.Count == 0 ? Array.Empty<int>() : capturedPids.ToArray();
            }

            if (clientPids != null && capturedPids.Count > 0)
                clientPids.AddRange(capturedPids);

            return true;
        }

        private static unsafe bool TryGetProcessCountsNativeCore(out int totalCount, out int clientCount, out bool installerRunning, List<int> clientPids)
        {
            totalCount = 0;
            clientCount = 0;
            installerRunning = false;

            IntPtr hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (hSnapshot == INVALID_HANDLE_VALUE || hSnapshot == IntPtr.Zero)
                return false;

            try
            {
                PROCESSENTRY32W entry = default;
                entry.dwSize = (uint)sizeof(PROCESSENTRY32W);

                if (!Process32FirstW(hSnapshot, &entry))
                    return false;

                do
                {
                    ReadOnlySpan<char> exeSpan = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(entry.szExeFile);
                    if (exeSpan.Equals("RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        clientCount++;
                        totalCount++;
                        clientPids?.Add((int)entry.th32ProcessID);
                    }
                    else if (exeSpan.Equals("RobloxPlayerLauncher.exe", StringComparison.OrdinalIgnoreCase) ||
                             exeSpan.Equals("RobloxPlayerInstaller.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        installerRunning = true;
                        totalCount++;
                    }
                    else if (exeSpan.Equals("RobloxCrashHandler.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        totalCount++;
                    }
                } while (Process32NextW(hSnapshot, &entry));

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                CloseHandle(hSnapshot);
            }
        }

        /// <summary>
        /// Gets all active Roblox processes currently running on the system.
        /// </summary>
        public static IReadOnlyList<Process> GetRunningRobloxProcesses()
        {
            var result = new List<Process>();
            foreach (var name in RobloxProcessNames)
            {
                try
                {
                    var procs = Process.GetProcessesByName(name);
                    result.AddRange(procs);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[RobloxProcessService] Error querying process '{name}': {ex.Message}");
                }
            }
            return result;
        }

        /// <summary>
        /// Fast check whether any Roblox game client process is currently running.
        /// </summary>
        public static bool IsAnyRobloxRunning()
        {
            if (TryGetProcessCountsNative(out _, out int clientCount))
                return clientCount > 0;

            try
            {
                var procs = Process.GetProcessesByName("RobloxPlayerBeta");
                bool any = procs != null && procs.Length > 0;
                if (procs != null)
                {
                    foreach (var p in procs)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
                return any;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the total count of running Roblox game client processes.
        /// </summary>
        public static int GetRunningRobloxCount()
        {
            if (TryGetProcessCountsNative(out _, out int clientCount))
                return clientCount;

            try
            {
                var procs = Process.GetProcessesByName("RobloxPlayerBeta");
                int count = procs?.Length ?? 0;
                if (procs != null)
                {
                    foreach (var p in procs)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
                return count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Gets the total count of all running Roblox processes (clients, crash handlers, launchers).
        /// </summary>
        public static int GetTotalRunningRobloxCount()
        {
            if (TryGetProcessCountsNative(out int totalCount, out _))
                return totalCount;

            int count = 0;
            foreach (var name in RobloxProcessNames)
            {
                try
                {
                    var procs = Process.GetProcessesByName(name);
                    if (procs != null)
                    {
                        count += procs.Length;
                        foreach (var p in procs) { try { p.Dispose(); } catch { } }
                    }
                }
                catch { }
            }
            return count;
        }

        /// <summary>
        /// Gets all process IDs of running RobloxPlayerBeta game client instances safely.
        /// </summary>
        public static List<int> GetRunningRobloxPids()
        {
            var result = new List<int>();
            if (TryGetProcessCountsNative(out _, out _, result))
                return result;

            try
            {
                var procs = Process.GetProcessesByName("RobloxPlayerBeta");
                if (procs != null)
                {
                    foreach (var p in procs)
                    {
                        try
                        {
                            if (!p.HasExited)
                            {
                                string cmd = p.GetCommandLine();
                                if (!string.IsNullOrEmpty(cmd) && cmd.Contains("--launch-to-tray", StringComparison.OrdinalIgnoreCase))
                                    continue;
                                result.Add(p.Id);
                            }
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"[RobloxProcessService.GetRunningRobloxPids] {ex.Message}");
                        }
                        finally
                        {
                            try { p.Dispose(); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[RobloxProcessService.GetRunningRobloxPids] Enumerate error: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Forcibly terminates all running Roblox client, launcher, and crash handler processes.
        /// Refreshes the Windows notification area to clean up ghost tray icons left behind.
        /// </summary>
        /// <returns>The total number of processes terminated.</returns>
        public static int KillAllRobloxProcesses()
        {
            AutoReconnectService.Instance.SuppressAll();
            int totalKilled = 0;
            var processesToWait = new List<Process>();

            foreach (var procName in RobloxProcessNames)
            {
                try
                {
                    var procs = Process.GetProcessesByName(procName);
                    if (procs.Length == 0) continue;

                    foreach (var proc in procs)
                    {
                        try
                        {
                            proc.Kill();
                            totalKilled++;
                            processesToWait.Add(proc);
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"[RobloxProcessService] Could not kill process {proc.Id} ({procName}): {ex.Message}");
                            try { proc.Dispose(); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[RobloxProcessService] Error enumerating '{procName}' for kill: {ex.Message}");
                }
            }

            // Wait briefly for processes to exit within a shared 500ms budget and clean up handles
            var exitTimer = Stopwatch.StartNew();
            foreach (var proc in processesToWait)
            {
                try
                {
                    int remainingMs = (int)Math.Max(0, 500 - exitTimer.ElapsedMilliseconds);
                    if (remainingMs > 0 && !proc.HasExited)
                    {
                        proc.WaitForExit(remainingMs);
                    }
                }
                catch { }
                finally
                {
                    try { proc.Dispose(); } catch { }
                }
            }

            // Clean up ghost icons in Windows system tray left by terminated Roblox processes silently
            InvalidateSnapshotCache();
            try
            {
                Utilities.RefreshNotificationArea();
                Utilities.QueueRefreshNotificationArea(500);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[RobloxProcessService] RefreshNotificationArea error: {ex.Message}");
            }

            return totalKilled;
        }

        [GeneratedRegex(@"\-b (\d+)")]
        private static partial Regex TrackerRegex();

        /// <summary>
        /// Terminates the Roblox client process associated with a specific account.
        /// Matches by LastLaunchedProcessId or BrowserTrackerID.
        /// </summary>
        public static bool CloseAccountRobloxProcess(Account account)
        {
            if (account == null) return false;
            bool closed = false;

            // 1. Try closing by LastLaunchedProcessId if valid
            if (account.LastLaunchedProcessId > 0)
            {
                try
                {
                    using var proc = Process.GetProcessById(account.LastLaunchedProcessId);
                    if (proc != null && !proc.HasExited && proc.ProcessName.Contains("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase))
                    {
                        bool isMatchingProcess = true;
                        if (!string.IsNullOrWhiteSpace(account.BrowserTrackerID))
                        {
                            string cmdLine = proc.GetCommandLine();
                            if (!string.IsNullOrEmpty(cmdLine))
                            {
                                var match = TrackerRegex().Match(cmdLine);
                                if (match.Success && match.Groups[1].Value != account.BrowserTrackerID)
                                {
                                    // PID was recycled by Windows and belongs to another Roblox instance
                                    isMatchingProcess = false;
                                }
                            }
                        }

                        if (isMatchingProcess)
                        {
                            proc.CloseMainWindow();
                            if (!proc.WaitForExit(600))
                            {
                                proc.Kill();
                            }
                            account.LastLaunchedProcessId = 0;
                            closed = true;
                        }
                    }
                    else
                    {
                        // Process is already gone
                        account.LastLaunchedProcessId = 0;
                    }
                }
                catch
                {
                    account.LastLaunchedProcessId = 0;
                }
            }

            // 2. If not closed, find by matching BrowserTrackerID in command line via zero-allocation PID snapshot
            if (!closed && !string.IsNullOrWhiteSpace(account.BrowserTrackerID))
            {
                try
                {
                    foreach (int pid in GetRunningRobloxPids())
                    {
                        try
                        {
                            string cmdLine = ProcessExtensions.GetCommandLineByPid(pid);
                            if (string.IsNullOrEmpty(cmdLine)) continue;

                            var match = TrackerRegex().Match(cmdLine);
                            string tracker = match.Success ? match.Groups[1].Value : string.Empty;
                            if (tracker == account.BrowserTrackerID)
                            {
                                using var proc = Process.GetProcessById(pid);
                                proc.CloseMainWindow();
                                if (!proc.WaitForExit(600))
                                {
                                    proc.Kill();
                                }
                                account.LastLaunchedProcessId = 0;
                                closed = true;
                                break;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            if (closed)
            {
                InvalidateSnapshotCache();
                if (!string.IsNullOrWhiteSpace(account.Username))
                {
                    AutoReconnectService.Instance.SuppressReconnect(account.Username);
                }
                Utilities.QueueRefreshNotificationArea(300);
            }
            return closed;
        }

        public static int CloseAccountRobloxProcesses(IEnumerable<Account> accounts)
        {
            if (accounts == null) return 0;

            var accountList = accounts.Where(a => a != null).ToList();
            if (accountList.Count == 0) return 0;
            if (accountList.Count == 1) return CloseAccountRobloxProcess(accountList[0]) ? 1 : 0;

            Dictionary<string, int> trackerToPid = null;
            var processesToTerminate = new List<(Account Account, Process Process)>(accountList.Count);
            var targetedPids = new HashSet<int>();

            foreach (Account account in accountList)
            {
                int targetPid = 0;

                if (account.LastLaunchedProcessId > 0)
                {
                    try
                    {
                        var proc = Process.GetProcessById(account.LastLaunchedProcessId);
                        if (proc != null && !proc.HasExited && proc.ProcessName.Contains("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase))
                        {
                            bool matches = true;
                            if (!string.IsNullOrWhiteSpace(account.BrowserTrackerID))
                            {
                                string cmdLine = proc.GetCommandLine();
                                if (!string.IsNullOrEmpty(cmdLine))
                                {
                                    var match = TrackerRegex().Match(cmdLine);
                                    if (match.Success && match.Groups[1].Value != account.BrowserTrackerID)
                                        matches = false;
                                }
                            }

                            if (matches)
                            {
                                targetPid = account.LastLaunchedProcessId;
                                if (targetedPids.Add(targetPid))
                                    processesToTerminate.Add((account, proc));
                                else
                                    proc.Dispose();
                            }
                            else
                            {
                                proc.Dispose();
                            }
                        }
                        else
                        {
                            proc?.Dispose();
                            account.LastLaunchedProcessId = 0;
                        }
                    }
                    catch
                    {
                        account.LastLaunchedProcessId = 0;
                    }
                }

                if (targetPid == 0 && !string.IsNullOrWhiteSpace(account.BrowserTrackerID))
                {
                    if (trackerToPid == null)
                    {
                        trackerToPid = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (int pid in GetRunningRobloxPids())
                        {
                            try
                            {
                                string cmdLine = ProcessExtensions.GetCommandLineByPid(pid);
                                if (string.IsNullOrEmpty(cmdLine)) continue;
                                var match = TrackerRegex().Match(cmdLine);
                                if (match.Success && !string.IsNullOrEmpty(match.Groups[1].Value))
                                    trackerToPid[match.Groups[1].Value] = pid;
                            }
                            catch { }
                        }
                    }

                    if (trackerToPid.TryGetValue(account.BrowserTrackerID, out int matchedPid) && targetedPids.Add(matchedPid))
                    {
                        try
                        {
                            var proc = Process.GetProcessById(matchedPid);
                            processesToTerminate.Add((account, proc));
                        }
                        catch { }
                    }
                }
            }

            if (processesToTerminate.Count == 0)
                return 0;

            foreach (var item in processesToTerminate)
            {
                try { item.Process.CloseMainWindow(); } catch { }
            }

            var waitSw = Stopwatch.StartNew();
            int closed = 0;
            foreach (var (account, proc) in processesToTerminate)
            {
                try
                {
                    int remainingMs = (int)Math.Max(0, 600 - waitSw.ElapsedMilliseconds);
                    if (!proc.HasExited && (remainingMs <= 0 || !proc.WaitForExit(remainingMs)))
                    {
                        proc.Kill();
                    }
                    account.LastLaunchedProcessId = 0;
                    if (!string.IsNullOrWhiteSpace(account.Username))
                        AutoReconnectService.Instance.SuppressReconnect(account.Username);
                    closed++;
                }
                catch { }
                finally
                {
                    try { proc.Dispose(); } catch { }
                }
            }

            if (closed > 0)
            {
                InvalidateSnapshotCache();
                Utilities.QueueRefreshNotificationArea(300);
            }

            return closed;
        }

        /// <summary>
        /// Cleans up dead crash handlers (RobloxCrashHandler) that hang indefinitely after a game crash.
        /// Preserves all running RobloxPlayerBeta instances so user game windows are never unexpectedly terminated.
        /// </summary>
        /// <returns>The total number of zombie crash handler processes terminated.</returns>
        public static int CleanZombieProcesses()
        {
            int cleaned = 0;
            try
            {
                // Purge dead background tray daemons (--launch-to-tray) which have no window
                // and hijack single-instance mutex checks when a game client exits.
                foreach (int pid in GetRunningRobloxPids())
                {
                    try
                    {
                        string cmd = ProcessExtensions.GetCommandLineByPid(pid);
                        if (!string.IsNullOrEmpty(cmd) && cmd.Contains("--launch-to-tray", StringComparison.OrdinalIgnoreCase))
                        {
                            using var p = Process.GetProcessById(pid);
                            p.Kill();
                            p.WaitForExit(300);
                            cleaned++;
                            Program.Logger?.Info($"[RobloxProcessService] Purged ghost tray daemon PID {pid} (--launch-to-tray).");
                        }
                    }
                    catch { }
                }

                if (cleaned > 0)
                    InvalidateSnapshotCache();

                var runningClientPids = GetRunningRobloxPids();
                // If game clients are actively running, do not terminate their crash handler processes
                // as Hyperion / anti-tamper treats an aborted crash monitor as a corruption signal and drops game windows.
                if (runningClientPids.Count > 0)
                {
                    if (cleaned > 0)
                        Utilities.QueueRefreshNotificationArea(400);
                    return cleaned;
                }

                // When no game clients are running, any remaining crash handlers are true zombies
                foreach (var p in Process.GetProcessesByName("RobloxCrashHandler"))
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(300);
                        cleaned++;
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }

                if (cleaned > 0)
                {
                    InvalidateSnapshotCache();
                    Program.Logger?.Info($"[RobloxProcessService] Purged {cleaned} orphaned Roblox crash / tray process(es) and refreshed system tray silently.");
                    Utilities.QueueRefreshNotificationArea(400);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[CleanZombieProcesses] {ex.Message}");
            }
            return cleaned;
        }

        /// <summary>
        /// Clears the local Roblox Client session cookies and cached authentication data from %LOCALAPPDATA%\Roblox.
        /// This ensures RobloxPlayerBeta.exe starts logged out rather than auto-logging into a previously deleted account.
        /// </summary>
        public static bool ClearRobloxClientSession(bool killProcesses = true)
        {
            try
            {
                if (killProcesses)
                {
                    try { KillAllRobloxProcesses(); } catch { }
                }

                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string localStorageDir = System.IO.Path.Combine(localAppData, "Roblox", "LocalStorage");
                if (System.IO.Directory.Exists(localStorageDir))
                {
                    string cookiesDat = System.IO.Path.Combine(localStorageDir, "RobloxCookies.dat");
                    if (System.IO.File.Exists(cookiesDat))
                    {
                        try { System.IO.File.Delete(cookiesDat); } catch { }
                    }

                    string appStorage = System.IO.Path.Combine(localStorageDir, "appStorage.json");
                    if (System.IO.File.Exists(appStorage))
                    {
                        try { System.IO.File.Delete(appStorage); } catch { }
                    }
                }

                string rbxStorageDb = System.IO.Path.Combine(localAppData, "Roblox", "rbx-storage.db");
                if (System.IO.File.Exists(rbxStorageDb))
                {
                    try { System.IO.File.Delete(rbxStorageDb); } catch { }
                }

                Program.Logger?.Info("[RobloxProcessService] Successfully cleared Roblox Client local session storage.");
                return true;
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[RobloxProcessService] Failed to clear Roblox Client session: {ex.Message}");
                return false;
            }
        }
    }
}
