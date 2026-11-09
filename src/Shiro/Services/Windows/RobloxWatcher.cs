#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro.Services
{
    internal partial class RobloxWatcher
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_TERMINATE = 0x0001;
        private const int GW_OWNER = 4;

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
        private static unsafe partial int GetWindowTextW(IntPtr hWnd, char* lpString, int nMaxCount);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetProcessTimes(
            IntPtr hProcess,
            out long lpCreationTime,
            out long lpExitTime,
            out long lpKernelTime,
            out long lpUserTime);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool K32GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS ppsmemCounters, uint cb);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public nuint PeakWorkingSetSize;
            public nuint WorkingSetSize;
            public nuint QuotaPeakPagedPoolUsage;
            public nuint QuotaPagedPoolUsage;
            public nuint QuotaPeakNonPagedPoolUsage;
            public nuint QuotaNonPagedPoolUsage;
            public nuint PagefileUsage;
            public nuint PeakPagefileUsage;
        }

        public static HashSet<int> Seen = [];
        public static List<RobloxProcess> Instances = [];
        private static readonly Dictionary<int, (string TrackerId, bool IsGameClient, RECT LastSavedRect)> _pidMetadataCache = new();
        internal static readonly System.Threading.Lock InstanceRegistryLock = new();
        public static bool VerifyDataModel = true;
        public static bool IgnoreExistingProcesses = true;
        public static bool CloseIfMemoryLow = false;
        public static bool CloseIfWindowTitle = false;
        public static bool RememberWindowPositions = false;
        public static int MemoryLowValue = 200;
        public static string? ExpectedWindowTitle = "Roblox";

        private static ModernPeriodicTask? _watcherLoop;
        private static ModernPeriodicTask? _logReadLoop;

        public static event EventHandler<EventArgs>? LogFileRead;

        public static void Start()
        {
            if (_watcherLoop == null)
            {
                _watcherLoop = new ModernPeriodicTask(
                    TimeSpan.FromSeconds(2),
                    async token => await Task.Run(CheckProcesses, token).ConfigureAwait(false),
                    enabled: true);
            }

            if (_logReadLoop == null)
            {
                _logReadLoop = new ModernPeriodicTask(
                    TimeSpan.FromSeconds(1),
                    _ => { LogFileRead?.Invoke(null, EventArgs.Empty); return Task.CompletedTask; },
                    enabled: true);
            }

            StartLogDirectoryWatcher();
        }

        public static void Stop()
        {
            StopLogDirectoryWatcher();

            _watcherLoop?.Stop();
            _watcherLoop?.Dispose();
            _watcherLoop = null;

            _logReadLoop?.Stop();
            _logReadLoop?.Dispose();
            _logReadLoop = null;
        }

        public static void CheckProcesses()
        {
            var runningPids = RobloxProcessService.GetRunningRobloxPids();
            if (runningPids.Count == 0)
            {
                lock (InstanceRegistryLock)
                {
                    if (Seen.Count > 0) Seen.Clear();
                    if (Instances.Count > 0) Instances.Clear();
                    if (_pidMetadataCache.Count > 0) _pidMetadataCache.Clear();
                }
                return;
            }

            if (!CloseIfMemoryLow && !CloseIfWindowTitle && !RememberWindowPositions)
            {
                bool allKnown = true;
                lock (InstanceRegistryLock)
                {
                    for (int i = 0; i < runningPids.Count; i++)
                    {
                        if (!_pidMetadataCache.ContainsKey(runningPids[i]))
                        {
                            allKnown = false;
                            break;
                        }
                    }

                    if (allKnown)
                    {
                        PruneExitedMetadataUnderLock(runningPids);
                        return;
                    }
                }
            }

            IntPtr focusedHwnd = GetForegroundWindow();
            uint focusedPid = 0;
            if (focusedHwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(focusedHwnd, out focusedPid);
            }

            Dictionary<string, Account>? accountByTracker = null;
            if (RememberWindowPositions)
            {
                accountByTracker = new Dictionary<string, Account>(StringComparer.Ordinal);
                foreach (Account acc in AppServices.AccountRepository.GetAll())
                {
                    if (acc != null && !string.IsNullOrEmpty(acc.BrowserTrackerID))
                        accountByTracker[acc.BrowserTrackerID] = acc;
                }
            }

            Dictionary<int, IntPtr>? mainWindowByPid = null;
            if (CloseIfWindowTitle || RememberWindowPositions)
            {
                var activePids = new HashSet<int>(runningPids.Count);
                for (int i = 0; i < runningPids.Count; i++)
                    activePids.Add(runningPids[i]);

                mainWindowByPid = BuildMainWindowMap(activePids);
            }

            long nowFileTimeUtc = DateTime.UtcNow.ToFileTimeUtc();

            for (int idx = 0; idx < runningPids.Count; idx++)
            {
                int pid = runningPids[idx];
                bool isFocusedProcess = focusedPid > 0 && pid == (int)focusedPid;

                string trackerId = string.Empty;
                bool isGameClient = false;
                RECT lastRect = default;
                bool hasCachedMeta;

                lock (InstanceRegistryLock)
                {
                    hasCachedMeta = _pidMetadataCache.TryGetValue(pid, out var cachedMeta);
                    if (hasCachedMeta)
                    {
                        trackerId = cachedMeta.TrackerId;
                        isGameClient = cachedMeta.IsGameClient;
                        lastRect = cachedMeta.LastSavedRect;
                    }
                }

                if (!hasCachedMeta)
                {
                    string? commandLine = ProcessExtensions.GetCommandLineByPid(pid);
                    if (!string.IsNullOrEmpty(commandLine))
                    {
                        isGameClient = TryClassifyClientCommandLine(commandLine, out trackerId);

                        lock (InstanceRegistryLock)
                        {
                            _pidMetadataCache[pid] = (trackerId, isGameClient, default);
                        }
                    }
                }

                if (!isGameClient)
                    continue;

                bool needNativeStats = (!isFocusedProcess && (CloseIfMemoryLow || CloseIfWindowTitle)) ||
                                       (RememberWindowPositions && !string.IsNullOrEmpty(trackerId));
                if (needNativeStats && TryGetProcessAgeAndMemory(pid, nowFileTimeUtc, out double ageSeconds, out long workingSetBytes) && ageSeconds > 30)
                {
                    if (!isFocusedProcess)
                    {
                        long workingSetMb = workingSetBytes / (1024 * 1024);
                        if (CloseIfMemoryLow && workingSetMb > 0 && workingSetMb < MemoryLowValue)
                        {
                            KillNative(pid, $"Low Memory ({workingSetMb} < {MemoryLowValue})");
                            continue;
                        }

                        if (CloseIfWindowTitle && mainWindowByPid != null && mainWindowByPid.TryGetValue(pid, out IntPtr titleHwnd) && titleHwnd != IntPtr.Zero)
                        {
                            string title = GetWindowTitleNative(titleHwnd);
                            if (!string.Equals(title, ExpectedWindowTitle, StringComparison.Ordinal))
                            {
                                KillNative(pid, $"Window Title isn't {ExpectedWindowTitle}, got {title}");
                                continue;
                            }
                        }
                    }

                    if (RememberWindowPositions &&
                        !string.IsNullOrEmpty(trackerId) &&
                        accountByTracker != null &&
                        accountByTracker.TryGetValue(trackerId, out Account? account) &&
                        account != null &&
                        mainWindowByPid != null &&
                        mainWindowByPid.TryGetValue(pid, out IntPtr hWnd) &&
                        hWnd != IntPtr.Zero &&
                        GetWindowRect(hWnd, out RECT rect) &&
                        TryValidateWindowBoundsChange(rect, lastRect, out int width, out int height))
                    {
                        account.SetWindowBounds(rect.Left, rect.Top, width, height);

                        lock (InstanceRegistryLock)
                        {
                            _pidMetadataCache[pid] = (trackerId, isGameClient, rect);
                        }
                    }
                }

                lock (InstanceRegistryLock)
                {
                    if (Seen.Contains(pid))
                        continue;
                }

                Process? process = null;
                bool retained = false;
                try
                {
                    process = Process.GetProcessById(pid);
                    if (process.HasExited) continue;

                    lock (InstanceRegistryLock)
                    {
                        if (Seen.Contains(pid))
                            continue;

                        Instances.Add(new RobloxProcess(process, trackerId));
                        Seen.Add(pid);
                        retained = true;
                    }

                    RobloxJobObjectService.AssignProcessByPid(pid);
                }
                catch (ArgumentException)
                {
                    // Process exited between snapshot and GetProcessById
                }
                catch (Exception x)
                {
                    AppServices.Logger?.Debug($"Access to Process {pid} denied: {x.Message}");
                }
                finally
                {
                    if (!retained && process != null)
                    {
                        SafeExecuteHelper.Try(() => process.Dispose(), "RobloxWatcher.DisposeProcess");
                    }
                }
            }

            lock (InstanceRegistryLock)
            {
                PruneExitedMetadataUnderLock(runningPids);
            }
        }

        private static void PruneExitedMetadataUnderLock(IReadOnlyList<int> activePids)
        {
            if (_pidMetadataCache.Count == 0 && Seen.Count == 0)
                return;

            List<int>? exitedPids = null;
            foreach (int cachedPid in _pidMetadataCache.Keys)
            {
                bool stillRunning = false;
                for (int i = 0; i < activePids.Count; i++)
                {
                    if (activePids[i] == cachedPid)
                    {
                        stillRunning = true;
                        break;
                    }
                }

                if (!stillRunning)
                {
                    exitedPids ??= new List<int>(4);
                    exitedPids.Add(cachedPid);
                }
            }

            if (exitedPids != null)
            {
                foreach (int exitedPid in exitedPids)
                {
                    _pidMetadataCache.Remove(exitedPid);
                    Seen.Remove(exitedPid);
                }
            }
        }

        private static Dictionary<int, IntPtr> BuildMainWindowMap(HashSet<int> targetPids)
        {
            var map = new Dictionary<int, IntPtr>(targetPids.Count);
            if (!OperatingSystem.IsWindows() || targetPids.Count == 0)
                return map;

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd) || GetWindow(hWnd, GW_OWNER) != IntPtr.Zero)
                    return true;

                GetWindowThreadProcessId(hWnd, out uint wndPid);
                int pid = (int)wndPid;
                if (pid > 0 && targetPids.Contains(pid) && !map.ContainsKey(pid))
                {
                    map[pid] = hWnd;
                }
                return true;
            }, IntPtr.Zero);

            return map;
        }

        private static unsafe bool TryGetProcessAgeAndMemory(int pid, long nowFileTimeUtc, out double ageSeconds, out long workingSetBytes)
        {
            ageSeconds = 0;
            workingSetBytes = 0;
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero)
                return false;

            try
            {
                if (GetProcessTimes(hProcess, out long creationFileTime, out _, out _, out _) && creationFileTime > 0)
                {
                    ageSeconds = Math.Max(0, (nowFileTimeUtc - creationFileTime) / 10_000_000.0);
                }

                if (CloseIfMemoryLow)
                {
                    PROCESS_MEMORY_COUNTERS counters = default;
                    counters.cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS);
                    if (K32GetProcessMemoryInfo(hProcess, out counters, counters.cb))
                    {
                        workingSetBytes = (long)counters.WorkingSetSize;
                    }
                }

                return true;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        private static unsafe string GetWindowTitleNative(IntPtr hWnd)
        {
            char* buf = stackalloc char[256];
            int len = GetWindowTextW(hWnd, buf, 256);
            return len > 0 ? new string(buf, 0, len) : string.Empty;
        }

        private static void KillNative(int pid, string reason)
        {
            AppServices.Logger?.Info($"Attempting to kill process {pid}, reason: {reason}");
            IntPtr hProcess = OpenProcess(PROCESS_TERMINATE, false, pid);
            if (hProcess == IntPtr.Zero) return;
            try
            {
                TerminateProcess(hProcess, 1);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Kill process {pid} failed: {ex.Message}");
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        internal static void RemoveProcessMetadata(int pid)
        {
            lock (InstanceRegistryLock)
            {
                Seen.Remove(pid);
                _pidMetadataCache.Remove(pid);
            }
        }
    }
}
