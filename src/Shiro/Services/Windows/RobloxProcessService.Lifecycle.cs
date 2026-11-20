#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Shiro.Services
{
    public static partial class RobloxProcessService
    {
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
                            AppServices.Logger?.Debug($"[RobloxProcessService] Could not kill process {proc.Id} ({procName}): {ex.Message}");
                            try { proc.Dispose(); }
                            catch (Exception disposeEx) { AppServices.Logger?.Debug($"[RobloxProcessService] Dispose error: {disposeEx.Message}"); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"[RobloxProcessService] Error enumerating '{procName}' for kill: {ex.Message}");
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
                    AppServices.Logger?.Debug($"WaitForExit proc failed: {ex.Message}");
                }
                finally
                {
                    SafeExecuteHelper.Try(() => proc.Dispose(), "RobloxProcessService.Lifecycle.DisposeProc");
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
                AppServices.Logger?.Debug($"[RobloxProcessService] RefreshNotificationArea error: {ex.Message}");
            }

            return totalKilled;
        }

        /// <summary>
        /// Terminates the Roblox client process associated with a specific account.
        /// Matches by LastLaunchedProcessId or BrowserTrackerID.
        /// </summary>
        private static bool IsAliveRobloxClientPid(int pid)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                return !proc.HasExited && proc.ProcessName.Contains("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"PID {pid} lookup failed: {ex.Message}");
                return false;
            }
        }

        private static void TerminateProcessGracefully(int pid)
        {
            using var proc = Process.GetProcessById(pid);
            proc.CloseMainWindow();
            if (!proc.WaitForExit(600))
                proc.Kill();
        }

        public static bool CloseAccountRobloxProcess(Account account)
        {
            bool closed = CloseAccountRobloxProcessCore(
                account,
                IsAliveRobloxClientPid,
                ProcessExtensions.GetCommandLineByPid,
                () => GetRunningRobloxPids(),
                TerminateProcessGracefully,
                AutoReconnectService.Instance.SuppressReconnectForAccount);

            if (closed)
            {
                InvalidateSnapshotCache();
                Utilities.QueueRefreshNotificationArea(300);
            }
            return closed;
        }

        internal static bool CloseAccountRobloxProcessCore(
            Account? account,
            Func<int, bool> isPidAlive,
            Func<int, string?> getCommandLineByPid,
            Func<List<int>> getRunningPids,
            Action<int> killPid,
            Action<Account>? beforeTerminate = null)
        {
            if (account == null) return false;
            bool closed = false;

            if (account.LastLaunchedProcessId > 0)
            {
                try
                {
                    if (isPidAlive(account.LastLaunchedProcessId))
                    {
                        bool isMatchingProcess = string.IsNullOrWhiteSpace(account.BrowserTrackerID);
                        if (!isMatchingProcess)
                        {
                            string? cmdLine = getCommandLineByPid(account.LastLaunchedProcessId);
                            if (!string.IsNullOrEmpty(cmdLine))
                            {
                                var match = RobloxProcessIdentityHelper.BrowserTrackerArgRegex().Match(cmdLine);
                                isMatchingProcess = match.Success && match.Groups[1].Value == account.BrowserTrackerID;
                            }
                        }

                        if (isMatchingProcess)
                        {
                            beforeTerminate?.Invoke(account);
                            killPid(account.LastLaunchedProcessId);
                            account.LastLaunchedProcessId = 0;
                            closed = true;
                        }
                        else
                        {
                            account.LastLaunchedProcessId = 0;
                        }
                    }
                    else
                    {
                        account.LastLaunchedProcessId = 0;
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"Closing LastLaunchedProcessId failed: {ex.Message}");
                    account.LastLaunchedProcessId = 0;
                }
            }

            if (!closed && !string.IsNullOrWhiteSpace(account.BrowserTrackerID))
            {
                try
                {
                    foreach (int pid in getRunningPids())
                    {
                        try
                        {
                            string? cmdLine = getCommandLineByPid(pid);
                            if (string.IsNullOrEmpty(cmdLine)) continue;
                            var match = RobloxProcessIdentityHelper.BrowserTrackerArgRegex().Match(cmdLine);
                            if (match.Success && match.Groups[1].Value == account.BrowserTrackerID)
                            {
                                beforeTerminate?.Invoke(account);
                                killPid(pid);
                                account.LastLaunchedProcessId = 0;
                                closed = true;
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            AppServices.Logger?.Debug($"Checking PID {pid} for tracker failed: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"Scanning GetRunningRobloxPids for tracker failed: {ex.Message}");
                }
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
                            bool matches = string.IsNullOrWhiteSpace(account.BrowserTrackerID);
                            if (!matches)
                            {
                                string? cmdLine = proc.GetCommandLine();
                                if (!string.IsNullOrEmpty(cmdLine))
                                {
                                    var match = RobloxProcessIdentityHelper.BrowserTrackerArgRegex().Match(cmdLine);
                                    matches = match.Success && match.Groups[1].Value == account.BrowserTrackerID;
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
                                account.LastLaunchedProcessId = 0;
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
                        AppServices.Logger?.Debug($"Process lookup for LastLaunchedProcessId failed: {ex.Message}");
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
                                var match = RobloxProcessIdentityHelper.BrowserTrackerArgRegex().Match(cmdLine);
                                if (match.Success && !string.IsNullOrEmpty(match.Groups[1].Value))
                                    trackerToPid[match.Groups[1].Value] = pid;
                            }
                            catch (Exception ex)
                            {
                                AppServices.Logger?.Debug($"CommandLine lookup for pid {pid} failed: {ex.Message}");
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
                            AppServices.Logger?.Debug($"GetProcessById {matchedPid} failed: {ex.Message}");
                        }
                    }
                }
            }

            if (processesToTerminate.Count == 0)
                return 0;

            foreach (var item in processesToTerminate)
                AutoReconnectService.Instance.SuppressReconnectForAccount(item.Account);

            foreach (var item in processesToTerminate)
            {
                try { item.Process.CloseMainWindow(); }
                catch (Exception ex) { AppServices.Logger?.Debug($"CloseMainWindow failed: {ex.Message}"); }
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
                    closed++;
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"Terminating process failed: {ex.Message}");
                }
                finally
                {
                    SafeExecuteHelper.Try(() => proc.Dispose(), "RobloxProcessService.Lifecycle.DisposeProc");
                }
            }

            if (closed > 0)
            {
                InvalidateSnapshotCache();
                Utilities.QueueRefreshNotificationArea(300);
            }

            return closed;
        }

        internal static (List<int> TrayDaemonPids, List<int> ActiveClientPids) PartitionRobloxPidsByCommandLine(
            IReadOnlyList<int> allPids,
            Func<int, string?> commandLineResolver)
        {
            var trayDaemons = new List<int>();
            var activeClients = new List<int>(allPids?.Count ?? 0);
            if (allPids == null || commandLineResolver == null)
                return (trayDaemons, activeClients);

            for (int i = 0; i < allPids.Count; i++)
            {
                int pid = allPids[i];
                string? cmd = null;
                try { cmd = commandLineResolver(pid); }
                catch (Exception ex) { AppServices.Logger.Debug($"CommandLine check for PID {pid} threw: {ex.Message}"); }

                if (ProcessExtensions.IsLaunchToTrayCommandLine(cmd))
                    trayDaemons.Add(pid);
                else
                    activeClients.Add(pid);
            }

            return (trayDaemons, activeClients);
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
                var (trayDaemonPids, runningClientPids) = PartitionRobloxPidsByCommandLine(
                    GetRunningRobloxPids(includeTrayDaemons: true),
                    ProcessExtensions.GetCommandLineByPid);

                // Purge dead background tray daemons (--launch-to-tray) which have no window
                // and hijack single-instance mutex checks when a game client exits.
                foreach (int pid in trayDaemonPids)
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        p.Kill();
                        p.WaitForExit(300);
                        cleaned++;
                        AppServices.Logger?.Info($"[RobloxProcessService] Purged ghost tray daemon PID {pid} (--launch-to-tray).");
                    }
                    catch (Exception ex)
                    {
                        AppServices.Logger?.Debug($"Purging ghost tray daemon PID {pid} threw: {ex.Message}");
                    }
                }

                if (cleaned > 0)
                    InvalidateSnapshotCache();

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
                        AppServices.Logger?.Debug($"Killing zombie RobloxCrashHandler threw: {ex.Message}");
                    }
                    finally { SafeExecuteHelper.Try(() => p.Dispose(), "RobloxProcessService.CleanZombieProcesses.Dispose"); }
                }

                if (cleaned > 0)
                {
                    InvalidateSnapshotCache();
                    AppServices.Logger?.Info($"[RobloxProcessService] Purged {cleaned} orphaned Roblox crash / tray process(es) and refreshed system tray silently.");
                    Utilities.QueueRefreshNotificationArea(400);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[CleanZombieProcesses] {ex.Message}");
            }
            return cleaned;
        }
    }
}
