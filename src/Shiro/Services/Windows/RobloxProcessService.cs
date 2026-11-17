#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Shiro.Services
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
                catch (ArgumentException) { return false; }
                catch (Exception ex)
                {
                    AppServices.Logger.Debug("[RobloxProcessService] IsProcessAlive check failed", ex);
                    return false;
                }
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

            if (Marshal.GetLastWin32Error() != 5) // ERROR_ACCESS_DENIED
                return false;

            InvalidateSnapshotCache();
            return GetRunningRobloxPids(includeTrayDaemons: true).Contains(pid);
        }

        private const long SnapshotCacheTtlMs = 350;
        private static readonly System.Threading.Lock _snapshotCacheLock = new();
        private static long _lastSnapshotTick = -SnapshotCacheTtlMs * 2;
        private static long _snapshotEpoch;
        private static int _cachedTotalCount;
        private static int _cachedClientCount;
        private static bool _cachedInstallerRunning;
        private static int[] _cachedClientPids = Array.Empty<int>();

        public static void InvalidateSnapshotCache()
        {
            lock (_snapshotCacheLock)
            {
                _snapshotEpoch++;
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

        private static bool TryGetProcessCountsNative(out int totalCount, out int clientCount, List<int>? clientPids = null)
        {
            long now = Environment.TickCount64;
            long entryEpoch;
            lock (_snapshotCacheLock)
            {
                entryEpoch = _snapshotEpoch;
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
                if (_snapshotEpoch == entryEpoch)
                {
                    _lastSnapshotTick = Environment.TickCount64;
                    _cachedTotalCount = totalCount;
                    _cachedClientCount = clientCount;
                    _cachedInstallerRunning = installerRunning;
                    _cachedClientPids = capturedPids.Count == 0 ? Array.Empty<int>() : capturedPids.ToArray();
                }
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
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"TryGetProcessCountsNative failed: {ex.Message}");
                return false;
            }
            finally
            {
                CloseHandle(hSnapshot);
            }
        }

        private static unsafe bool TryCollectRobloxPidsNative(List<int> pids)
        {
            if (!OperatingSystem.IsWindows())
                return false;

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
                    if (exeSpan.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        exeSpan = exeSpan[..^4];

                    for (int i = 0; i < RobloxProcessNames.Length; i++)
                    {
                        if (exeSpan.Equals(RobloxProcessNames[i].AsSpan(), StringComparison.OrdinalIgnoreCase))
                        {
                            pids.Add((int)entry.th32ProcessID);
                            break;
                        }
                    }
                } while (Process32NextW(hSnapshot, &entry));

                return true;
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"TryCollectRobloxPidsNative failed: {ex.Message}");
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
            var pids = new List<int>(8);
            if (TryCollectRobloxPidsNative(pids))
            {
                foreach (int pid in pids)
                {
                    try
                    {
                        result.Add(Process.GetProcessById(pid));
                    }
                    catch (Exception ex)
                    {
                        AppServices.Logger.Debug($"[RobloxProcessService] GetProcessById({pid}) skipped: {ex.Message}");
                    }
                }
                return result;
            }

            foreach (var name in RobloxProcessNames)
            {
                try
                {
                    var procs = Process.GetProcessesByName(name);
                    result.AddRange(procs);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Debug($"[RobloxProcessService] Error querying process '{name}': {ex.Message}");
                }
            }
            return result;
        }

        /// <summary>
        /// Fast check whether any Roblox game client process is currently running.
        /// </summary>
        public static bool IsAnyRobloxRunning() => GetRunningRobloxCount() > 0;

        /// <summary>
        /// Gets the total count of running Roblox game client processes.
        /// </summary>
        public static int GetRunningRobloxCount() => GetRunningRobloxPids().Count;

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
                        foreach (var p in procs) { SafeExecuteHelper.Try(() => p.Dispose(), "RobloxProcessService.DisposeProc"); }
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"GetTotalRunningRobloxCount fallback '{name}' failed: {ex.Message}");
                }
            }
            return count;
        }

        /// <summary>
        /// Gets all process IDs of running RobloxPlayerBeta game client instances safely.
        /// </summary>
        internal static Func<IReadOnlyList<int>>? TestGetRunningRobloxPidsOverride;

        public static List<int> GetRunningRobloxPids(bool includeTrayDaemons = false)
        {
            if (TestGetRunningRobloxPidsOverride != null)
            {
                var overridden = TestGetRunningRobloxPidsOverride().ToList();
                if (!includeTrayDaemons && ProcessExtensions.TestCommandLineOverride != null)
                    overridden.RemoveAll(ProcessExtensions.IsLaunchToTrayProcess);
                return overridden;
            }
            var result = new List<int>();
            if (TryGetProcessCountsNative(out _, out _, result))
            {
                if (!includeTrayDaemons && result.Count > 0)
                    result.RemoveAll(ProcessExtensions.IsLaunchToTrayProcess);
                return result;
            }

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
                                if (!includeTrayDaemons && ProcessExtensions.IsLaunchToTrayCommandLine(p.GetCommandLine()))
                                    continue;
                                result.Add(p.Id);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppServices.Logger?.Debug($"[RobloxProcessService.GetRunningRobloxPids] {ex.Message}");
                        }
                        finally
                        {
                            try { p.Dispose(); }
                            catch (Exception ex) { AppServices.Logger?.Debug($"[RobloxProcessService] Dispose error: {ex.Message}"); }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[RobloxProcessService.GetRunningRobloxPids] Enumerate error: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Clears the local Roblox Client session cookies and cached authentication data from %LOCALAPPDATA%\Roblox.
        /// This ensures RobloxPlayerBeta.exe starts logged out rather than auto-logging into a previously deleted account.
        /// </summary>
        public static bool ClearRobloxClientSession(bool killProcesses = true)
        {
            if (killProcesses)
            {
                SafeExecuteHelper.Try(() => KillAllRobloxProcesses(), "RobloxProcessService.ClearRobloxClientSession.KillAll");
            }

            return RobloxCookieFileHelper.ClearLocalClientSessionFiles();
        }
    }
}
