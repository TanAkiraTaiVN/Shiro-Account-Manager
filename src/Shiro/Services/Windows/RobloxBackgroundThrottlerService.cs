#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Shiro.Services
{
    /// <summary>
    /// Background Dynamic FPS & Memory Limiter (Resource Throttler) for multi-instance Roblox.
    /// Reduces CPU usage and trims physical working set memory for unfocused/background clients,
    /// restoring normal priority and responsiveness immediately when a client gains user focus.
    /// </summary>
    public partial class RobloxBackgroundThrottlerService
    {
        public const uint IDLE_PRIORITY_CLASS = 0x00000040;
        public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
        public const uint NORMAL_PRIORITY_CLASS = 0x00000020;

        private const uint PROCESS_SET_INFORMATION = 0x0200;
        private const uint PROCESS_SET_QUOTA = 0x0100;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private static readonly Lazy<RobloxBackgroundThrottlerService> _lazyInstance =
            new(() => new RobloxBackgroundThrottlerService());

        public static RobloxBackgroundThrottlerService Instance => _lazyInstance.Value;

        private readonly ConcurrentDictionary<int, bool> _throttledPids = new();
        private readonly System.Threading.Lock _gate = new();
        private bool _isEnabled;
        private int _targetBackgroundFps = 10;

        public bool IsEnabled
        {
            get
            {
                lock (_gate)
                {
                    return _isEnabled;
                }
            }
            set
            {
                bool changed = false;
                lock (_gate)
                {
                    if (_isEnabled != value)
                    {
                        _isEnabled = value;
                        changed = true;
                    }
                }

                if (changed && !value)
                {
                    RestoreAllThrottledProcesses();
                }
            }
        }

        public int TargetBackgroundFps
        {
            get
            {
                lock (_gate)
                {
                    return _targetBackgroundFps;
                }
            }
            set
            {
                lock (_gate)
                {
                    _targetBackgroundFps = Math.Clamp(value, 1, 60);
                }
            }
        }

        public RobloxBackgroundThrottlerService()
        {
        }

        /// <summary>
        /// Returns whether a given process ID is currently tracked as throttled.
        /// </summary>
        public bool IsProcessThrottled(int processId)
        {
            return _throttledPids.TryGetValue(processId, out bool throttled) && throttled;
        }

        /// <summary>
        /// Applies throttling or foreground priority restoration to a target Roblox process.
        /// </summary>
        public void ApplyThrottleState(int processId, bool isForeground, int backgroundFps = 10)
        {
            if (processId <= 0) return;

            if (isForeground)
            {
                _throttledPids.TryRemove(processId, out _);
            }
            else
            {
                _throttledPids[processId] = true;
            }

            if (!OperatingSystem.IsWindows()) return;

            IntPtr hProcess = Win32NativeHelper.OpenProcess(PROCESS_SET_INFORMATION, false, processId);
            if (hProcess == IntPtr.Zero) return;

            try
            {
                uint priority = MemoryOptimizer.ComputeTargetPriorityClass(isForeground, throttlerEnabled: true, backgroundFps);
                Win32NativeHelper.SetPriorityClass(hProcess, priority);
                if (isForeground)
                {
                    MemoryOptimizer.DisableEcoQoS(hProcess);
                    MemoryOptimizer.SetProcessMemoryPriorityNative(hProcess, isForeground: true);
                }
                else
                {
                    MemoryOptimizer.EnableEcoQoS(hProcess);
                    MemoryOptimizer.SetProcessMemoryPriorityNative(hProcess, isForeground: false);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[RobloxBackgroundThrottlerService] ApplyThrottleState failed for PID {processId}: {ex.Message}");
            }
            finally
            {
                Win32NativeHelper.CloseHandle(hProcess);
            }
        }

        /// <summary>
        /// Trims the physical memory working set for a process using psapi EmptyWorkingSet.
        /// </summary>
        public bool TrimWorkingSet(int processId)
        {
            if (processId <= 0 || !OperatingSystem.IsWindows()) return false;

            IntPtr hProcess = Win32NativeHelper.OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (hProcess == IntPtr.Zero) return false;

            try
            {
                return Win32NativeHelper.EmptyWorkingSet(hProcess);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[RobloxBackgroundThrottlerService] TrimWorkingSet failed for PID {processId}: {ex.Message}");
                return false;
            }
            finally
            {
                Win32NativeHelper.CloseHandle(hProcess);
            }
        }

        /// <summary>
        /// Called when the active foreground window changes. Adjusts priority and memory for all running Roblox clients.
        /// </summary>
        public void OnForegroundWindowChanged(int activeProcessId, IReadOnlyCollection<int>? runningRobloxPids)
        {
            if (!IsEnabled || runningRobloxPids == null || runningRobloxPids.Count == 0)
                return;

            int targetFps = TargetBackgroundFps;

            foreach (int pid in runningRobloxPids)
            {
                if (pid <= 0) continue;

                if (pid == activeProcessId)
                {
                    ApplyThrottleState(pid, isForeground: true);
                }
                else
                {
                    bool shouldTrim = !_throttledPids.ContainsKey(pid);
                    ApplyThrottleState(pid, isForeground: false, backgroundFps: targetFps);
                    if (shouldTrim && MemoryOptimizer.IsAggressiveWorkingSetTrimEnabled())
                    {
                        TrimWorkingSet(pid);
                    }
                }
            }
        }

        /// <summary>Removes exited PIDs from throttle tracking.</summary>
        public void PruneExitedProcesses(IReadOnlyCollection<int>? runningPids)
        {
            if (_throttledPids.IsEmpty) return;
            var live = runningPids is null ? null : new HashSet<int>(runningPids);
            foreach (int pid in _throttledPids.Keys)
            {
                if (live == null || !live.Contains(pid))
                    _throttledPids.TryRemove(pid, out _);
            }
        }

        /// <summary>
        /// Restores normal priority to all currently throttled processes.
        /// </summary>
        public void RestoreAllThrottledProcesses()
        {
            foreach (var kvp in _throttledPids)
            {
                int pid = kvp.Key;
                ApplyThrottleState(pid, isForeground: true);
            }
            _throttledPids.Clear();
        }
    }
}
