#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.Services
{
    public static partial class RobloxProcessService
    {
        [GeneratedRegex(@"\-b (\d+)")]
        private static partial Regex TrackerRegex();

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
                            try { proc.Dispose(); }
                            catch (Exception disposeEx) { Program.Logger?.Debug($"[RobloxProcessService] Dispose error: {disposeEx.Message}"); }
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
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"WaitForExit proc failed: {ex.Message}");
                }
                finally
                {
                    try { proc.Dispose(); } catch (Exception) { }
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
                            string? cmdLine = proc.GetCommandLine();
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
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Closing LastLaunchedProcessId failed: {ex.Message}");
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
                            string? cmdLine = ProcessExtensions.GetCommandLineByPid(pid);
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
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"Checking PID {pid} for tracker failed: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Scanning GetRunningRobloxPids for tracker failed: {ex.Message}");
                }
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

            Dictionary<string, int>? trackerToPid = null;
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
                                string? cmdLine = proc.GetCommandLine();
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
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Process lookup for LastLaunchedProcessId failed: {ex.Message}");
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
                                string? cmdLine = ProcessExtensions.GetCommandLineByPid(pid);
                                if (string.IsNullOrEmpty(cmdLine)) continue;
                                var match = TrackerRegex().Match(cmdLine);
                                if (match.Success && !string.IsNullOrEmpty(match.Groups[1].Value))
                                    trackerToPid[match.Groups[1].Value] = pid;
                            }
                            catch (Exception ex)
                            {
                                Program.Logger?.Debug($"CommandLine lookup for pid {pid} failed: {ex.Message}");
                            }
                        }
                    }

                    if (trackerToPid.TryGetValue(account.BrowserTrackerID, out int matchedPid) && targetedPids.Add(matchedPid))
                    {
                        try
                        {
                            var proc = Process.GetProcessById(matchedPid);
                            processesToTerminate.Add((account, proc));
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"GetProcessById {matchedPid} failed: {ex.Message}");
                        }
                    }
                }
            }

            if (processesToTerminate.Count == 0)
                return 0;

            foreach (var item in processesToTerminate)
            {
                try { item.Process.CloseMainWindow(); }
                catch (Exception ex) { Program.Logger?.Debug($"CloseMainWindow failed: {ex.Message}"); }
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
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Terminating process failed: {ex.Message}");
                }
                finally
                {
                    try { proc.Dispose(); } catch (Exception) { }
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
                        string? cmd = ProcessExtensions.GetCommandLineByPid(pid);
                        if (!string.IsNullOrEmpty(cmd) && cmd.Contains("--launch-to-tray", StringComparison.OrdinalIgnoreCase))
                        {
                            using var p = Process.GetProcessById(pid);
                            p.Kill();
                            p.WaitForExit(300);
                            cleaned++;
                            Program.Logger?.Info($"[RobloxProcessService] Purged ghost tray daemon PID {pid} (--launch-to-tray).");
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Purging ghost tray daemon PID {pid} threw: {ex.Message}");
                    }
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
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Killing zombie RobloxCrashHandler threw: {ex.Message}");
                    }
                    finally { try { p.Dispose(); } catch (Exception) { } }
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
                    try { KillAllRobloxProcesses(); }
                    catch (Exception ex) { Program.Logger?.Warn($"KillAllRobloxProcesses in ClearRobloxClientSession failed: {ex.Message}"); }
                }

                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string localStorageDir = Path.Combine(localAppData, "Roblox", "LocalStorage");
                if (Directory.Exists(localStorageDir))
                {
                    string cookiesDat = Path.Combine(localStorageDir, "RobloxCookies.dat");
                    if (File.Exists(cookiesDat))
                    {
                        try { File.Delete(cookiesDat); }
                        catch (Exception ex) { Program.Logger?.Debug($"Delete cookiesDat failed: {ex.Message}"); }
                    }

                    string appStorage = Path.Combine(localStorageDir, "appStorage.json");
                    if (File.Exists(appStorage))
                    {
                        try { File.Delete(appStorage); }
                        catch (Exception ex) { Program.Logger?.Debug($"Delete appStorage failed: {ex.Message}"); }
                    }
                }

                string rbxStorageDb = Path.Combine(localAppData, "Roblox", "rbx-storage.db");
                if (File.Exists(rbxStorageDb))
                {
                    try { File.Delete(rbxStorageDb); }
                    catch (Exception ex) { Program.Logger?.Debug($"Delete rbxStorageDb failed: {ex.Message}"); }
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
