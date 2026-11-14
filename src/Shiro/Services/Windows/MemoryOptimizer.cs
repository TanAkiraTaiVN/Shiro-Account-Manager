#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Shiro.Services
{
    /// <summary>
    /// Memory Optimizer for Multi-Roblox Farming.
    /// Roblox clients easily accumulate 1.5GB - 2.5GB of RAM each.
    /// By calling EmptyWorkingSet via psapi.dll on background/idle clients,
    /// physical memory consumption drops to 150MB - 300MB per client.
    /// This allows running 3x - 5x more Roblox instances on the same PC.
    /// </summary>
    public static partial class MemoryOptimizer
    {
        private static Timer? _autoTrimTimer;
        private static readonly System.Threading.Lock _gate = new();
        private static bool _autoTrimEnabled = false;
        private static int _trimIntervalMinutes = 3;
        private static bool _dynamicThresholdEnabled = true;
        private static long _maxMemoryThresholdBytes = 350L * 1024 * 1024; // 350 MB threshold for background clients
        public const long ForegroundMaxMemoryThresholdBytes = 1200L * 1024 * 1024; // 1.2 GB threshold for active foreground client
        private static DateTime _lastFullTrim = DateTime.MinValue;
        private static long _totalBytesSaved = 0;
        private static int _lastForegroundRobloxPid = 0;

        public static bool AutoTrimEnabled => _autoTrimEnabled;
        public static long TotalBytesSaved => _totalBytesSaved;

        /// <summary>
        /// Immediately restores Normal CPU priority and disables EcoQoS on the newly focused Roblox client,
        /// while demoting the previously focused Roblox client to BelowNormal + EcoQoS in real time on Alt+Tab.
        /// </summary>
        public static bool IsAutoLowerPriorityActive()
        {
            if (!OperatingSystem.IsWindows()) return false;
            return !AppServices.GeneralSettings.Exists("AutoLowerProcessPriority") ||
                   AppServices.GeneralSettings.Get<bool>("AutoLowerProcessPriority");
        }

        public static void EvaluateWithPids(uint foregroundPid, List<int> runningPids)
        {
            if (!OperatingSystem.IsWindows() || !IsAutoLowerPriorityActive()) return;

            try
            {
                if (runningPids == null || runningPids.Count == 0)
                {
                    Interlocked.Exchange(ref _lastForegroundRobloxPid, 0);
                    return;
                }

                int newRobloxPid = 0;
                if (foregroundPid > 0)
                {
                    for (int i = 0; i < runningPids.Count; i++)
                    {
                        if (runningPids[i] == (int)foregroundPid)
                        {
                            newRobloxPid = (int)foregroundPid;
                            break;
                        }
                    }
                }

                int prevRobloxPid = Interlocked.Exchange(ref _lastForegroundRobloxPid, newRobloxPid);
                if (prevRobloxPid == newRobloxPid)
                    return;

                if (newRobloxPid > 0)
                {
                    ApplyProcessFocusProfile(newRobloxPid, isForeground: true);
                }

                if (prevRobloxPid > 0 && prevRobloxPid != newRobloxPid)
                {
                    ApplyProcessFocusProfile(prevRobloxPid, isForeground: false);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[MemoryOptimizer.EvaluateWithPids] {ex.Message}");
            }
        }

        internal static void ApplyProcessFocusProfile(int pid, bool isForeground)
        {
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION | PROCESS_SET_QUOTA, false, pid);
            if (hProcess == IntPtr.Zero) return;
            try
            {
                var throttler = RobloxBackgroundThrottlerService.Instance;
                uint targetPrio = ComputeTargetPriorityClass(isForeground, throttler.IsEnabled, throttler.TargetBackgroundFps);
                SetPriorityClass(hProcess, targetPrio);

                if (isForeground)
                {
                    DisableEcoQoS(hProcess);
                    SetProcessMemoryPriorityNative(hProcess, isForeground: true);
                }
                else
                {
                    EnableEcoQoS(hProcess);
                    SetProcessMemoryPriorityNative(hProcess, isForeground: false);
                    SoftRelaxWorkingSetNative(hProcess);
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        internal static bool ShouldTriggerPeriodicTrim(
            long workingSetBytes, ProcessPriorityClass currentPriority, bool isForeground,
            bool lowerPriorityEnabled, long backgroundThresholdBytes = 350L * 1024 * 1024, bool aggressiveTrimEnabled = true)
        {
            if (isForeground)
            {
                // Foreground client intentionally runs at Normal priority; only trim if its RAM is extreme
                return aggressiveTrimEnabled && workingSetBytes > ForegroundMaxMemoryThresholdBytes;
            }

            return (aggressiveTrimEnabled && workingSetBytes > backgroundThresholdBytes) ||
                   (lowerPriorityEnabled && currentPriority != ProcessPriorityClass.BelowNormal);
        }

        public static void ConfigureAutoTrim(bool enabled, int intervalMinutes = 3, bool dynamicThreshold = true)
        {
            lock (_gate)
            {
                _autoTrimEnabled = enabled;
                _trimIntervalMinutes = Math.Clamp(intervalMinutes, 1, 60);
                _dynamicThresholdEnabled = dynamicThreshold;

                if (_autoTrimEnabled)
                {
                    // Run continuous smart anti-lag check every 30 seconds, starting in 5 seconds
                    var checkInterval = TimeSpan.FromSeconds(30);
                    if (_autoTrimTimer == null)
                        _autoTrimTimer = new Timer(OnPeriodicCheck, null, TimeSpan.FromSeconds(5), checkInterval);
                    else
                        _autoTrimTimer.Change(TimeSpan.FromSeconds(5), checkInterval);
                }
                else
                {
                    _autoTrimTimer?.Dispose();
                    _autoTrimTimer = null;
                }
            }
        }

        private static void OnPeriodicCheck(object? state)
        {
            try
            {
                if (!RobloxProcessService.IsAnyRobloxRunning())
                    return;

                bool lowerPriority = !AppServices.GeneralSettings.Exists("AutoLowerProcessPriority") || AppServices.GeneralSettings.Get<bool>("AutoLowerProcessPriority");
                bool aggressiveTrim = IsAggressiveWorkingSetTrimEnabled();
                
                // If enough time has passed for full interval trim, or if any client exceeds 350MB, optimize!
                bool fullIntervalDue = (DateTime.UtcNow - _lastFullTrim).TotalMinutes >= _trimIntervalMinutes;
                bool needsThresholdTrim = false;

                if (!fullIntervalDue && _dynamicThresholdEnabled)
                {
                    var pids = RobloxProcessService.GetRunningRobloxPids();
                    if (pids.Count > 0)
                    {
                        uint fgPid = 0;
                        try
                        {
                            IntPtr hForeground = GetForegroundWindow();
                            if (hForeground != IntPtr.Zero)
                                GetWindowThreadProcessId(hForeground, out fgPid);
                        }
                        catch (Exception ex)
                        {
                            AppServices.Logger?.Debug($"GetForegroundWindow thread pid lookup threw: {ex.Message}");
                        }

                        foreach (int pid in pids)
                        {
                            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                            if (hProcess == IntPtr.Zero) continue;
                            try
                            {
                                long ws = GetWorkingSetBytesNative(hProcess);
                                ProcessPriorityClass prio = GetProcessPriorityClassNative(hProcess);
                                bool isFg = fgPid > 0 && pid == (int)fgPid;
                                if (ShouldTriggerPeriodicTrim(ws, prio, isFg, lowerPriority, _maxMemoryThresholdBytes, aggressiveTrim))
                                {
                                    needsThresholdTrim = true;
                                    break;
                                }
                            }
                            finally
                            {
                                CloseHandle(hProcess);
                            }
                        }
                    }
                }

                if (fullIntervalDue || needsThresholdTrim)
                {
                    _lastFullTrim = DateTime.UtcNow;
                    OptimizeRobloxClients(lowerPriority);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[MemoryOptimizer.Check] {ex.Message}");
            }
        }

        /// <summary>
        /// Trims memory on all running Roblox processes right now.
        /// </summary>
        /// <param name="lowerPriority">Whether to lower process priority to BelowNormal to prevent system lag.</param>
        /// <returns>Bytes saved and count of clients trimmed.</returns>
        public static (long bytesSaved, int clientsCount) TrimRobloxClients(bool lowerPriority = true)
        {
            var res = OptimizeRobloxClients(lowerPriority, forceWorkingSetTrim: true);
            return (res.bytesSaved, res.clientsCount);
        }

        /// <summary>
        /// Comprehensive anti-lag optimization: trims RAM and sets BelowNormal CPU priority.
        /// </summary>
        public static (long bytesSaved, int clientsCount, int priorityChanged) OptimizeRobloxClients(bool lowerPriority = true, bool forceWorkingSetTrim = false)
        {
            if (!RobloxProcessService.IsAnyRobloxRunning())
                return (0, 0, 0);

            var pids = RobloxProcessService.GetRunningRobloxPids();
            if (pids.Count == 0)
                return (0, 0, 0);

            long beforeTotal = 0;
            long afterTotal = 0;
            int count = 0;
            int prioCount = 0;

            uint fgPid = 0;
            try
            {
                IntPtr hForeground = GetForegroundWindow();
                if (hForeground != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hForeground, out fgPid);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"GetForegroundWindow thread pid lookup threw: {ex.Message}");
            }

            bool allowEmptyWorkingSet = forceWorkingSetTrim || IsAggressiveWorkingSetTrimEnabled();
            foreach (int pid in pids)
            {
                IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA | PROCESS_SET_INFORMATION, false, pid);
                if (hProcess == IntPtr.Zero) continue;

                try
                {
                    bool isForeground = fgPid > 0 && pid == (int)fgPid;

                    if (lowerPriority)
                    {
                        uint targetRawPrio = isForeground ? RobloxProcessOptimizer.NORMAL_PRIORITY_CLASS : RobloxProcessOptimizer.BELOW_NORMAL_PRIORITY_CLASS;
                        uint currentRawPrio = GetPriorityClass(hProcess);
                        if (currentRawPrio != 0 && currentRawPrio != targetRawPrio)
                        {
                            if (SetPriorityClass(hProcess, targetRawPrio))
                                prioCount++;
                        }
                    }

                    long before = GetWorkingSetBytesNative(hProcess);
                    beforeTotal += before;

                    // Avoid forcing the working set to disk with EmptyWorkingSet unless specifically opted into,
                    // preventing page-fault micro-stutters and unnecessary paging overhead.
                    if (allowEmptyWorkingSet && (!isForeground || before > ForegroundMaxMemoryThresholdBytes))
                    {
                        EmptyWorkingSet(hProcess);
                        count++;
                    }
                    else if (!isForeground)
                    {
                        SetProcessMemoryPriorityNative(hProcess, isForeground: false);
                        if (SoftRelaxWorkingSetNative(hProcess)) count++;
                    }

                    if (lowerPriority)
                    {
                        if (isForeground)
                        {
                            DisableEcoQoS(hProcess);
                        }
                        else
                        {
                            EnableEcoQoS(hProcess);
                        }
                    }

                    long after = GetWorkingSetBytesNative(hProcess);
                    afterTotal += after;
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"[MemoryOptimizer.TrimClient] {pid}: {ex.Message}");
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }

            long saved = Math.Max(0, beforeTotal - afterTotal);
            Interlocked.Add(ref _totalBytesSaved, saved);

            if (count > 0 || prioCount > 0)
            {
                double savedMb = saved / (1024.0 * 1024.0);
                AppServices.Logger?.Info($"[MemoryOptimizer] Anti-Lag optimization: Freed ~{savedMb:F1} MB across {count} client(s), set BelowNormal priority on {prioCount} client(s).");
            }

            return (saved, count, prioCount);
        }

        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_SET_QUOTA = 0x0100;
        private const uint PROCESS_SET_INFORMATION = 0x0200;

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

        private static unsafe long GetWorkingSetBytesNative(IntPtr hProcess)
        {
            PROCESS_MEMORY_COUNTERS counters = default;
            counters.cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS);
            if (K32GetProcessMemoryInfo(hProcess, out counters, counters.cb))
                return (long)counters.WorkingSetSize;
            return 0;
        }

        private static ProcessPriorityClass GetProcessPriorityClassNative(IntPtr hProcess)
        {
            uint raw = GetPriorityClass(hProcess);
            return raw switch
            {
                RobloxProcessOptimizer.IDLE_PRIORITY_CLASS => ProcessPriorityClass.Idle,
                RobloxProcessOptimizer.BELOW_NORMAL_PRIORITY_CLASS => ProcessPriorityClass.BelowNormal,
                RobloxProcessOptimizer.NORMAL_PRIORITY_CLASS => ProcessPriorityClass.Normal,
                RobloxProcessOptimizer.ABOVE_NORMAL_PRIORITY_CLASS => ProcessPriorityClass.AboveNormal,
                RobloxProcessOptimizer.HIGH_PRIORITY_CLASS => ProcessPriorityClass.High,
                _ => ProcessPriorityClass.Normal
            };
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool K32GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS ppsmemCounters, uint cb);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial uint GetPriorityClass(IntPtr hProcess);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
        private const int ProcessPowerThrottling = 4;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetProcessInformation(IntPtr hProcess, int processInformationClass, ref PROCESS_POWER_THROTTLING_STATE processInformation, uint processInformationSize);

        internal static bool EnableEcoQoS(IntPtr hProcess)
        {
            try
            {
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                };
                return SetProcessInformation(hProcess, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                return false;
            }
        }

        internal static bool DisableEcoQoS(IntPtr hProcess)
        {
            try
            {
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = 0
                };
                return SetProcessInformation(hProcess, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                return false;
            }
        }

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [LibraryImport("psapi.dll")]
        private static partial int EmptyWorkingSet(IntPtr hwProc);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr hObject);
    }
}
