#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Shiro.Services
{
    /// <summary>
    /// High-performance process optimizer for Roblox game instances.
    /// Manages CPU process priorities, core affinities, and background throttling to prevent PC stutter when multi-boxing.
    /// </summary>
    public static partial class RobloxProcessOptimizer
    {
        private const uint PROCESS_SET_INFORMATION = 0x0200;

        // Process Priority Classes
        public const uint IDLE_PRIORITY_CLASS = 0x00000040;
        public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
        public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
        public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
        public const uint HIGH_PRIORITY_CLASS = 0x00000080;

        /// <summary>
        /// Adjusts the Windows scheduler execution priority class for a target Roblox process ID.
        /// </summary>
        public static bool SetProcessPriority(int pid, uint priorityClass)
        {
            if (pid <= 0) return false;
            using SafeProcessHandle hProcess = Win32NativeHelper.OpenSafeProcess(PROCESS_SET_INFORMATION, pid);
            if (hProcess.IsInvalid) return false;

            try
            {
                return Win32NativeHelper.SetPriorityClass(hProcess.DangerousGetHandle(), priorityClass);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[RobloxProcessOptimizer] Failed to set priority for PID {pid}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Lowers the execution priority for a collection of Roblox process IDs (e.g. when windows are hidden).
        /// </summary>
        public static int LowerPriorityForPids(IEnumerable<int> pids, uint priorityClass = IDLE_PRIORITY_CLASS)
        {
            if (pids == null) return 0;
            int count = 0;
            foreach (int pid in pids)
            {
                if (SetProcessPriority(pid, priorityClass))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Restores normal execution priority for a collection of Roblox process IDs (e.g. when windows are shown).
        /// </summary>
        public static int RestorePriorityForPids(IEnumerable<int> pids, uint priorityClass = NORMAL_PRIORITY_CLASS)
        {
            if (pids == null) return 0;
            int count = 0;
            foreach (int pid in pids)
            {
                if (SetProcessPriority(pid, priorityClass))
                    count++;
            }
            return count;
        }

        internal static bool ShouldAssignToJobObject(int pid) => pid > 0;

        private static readonly AsyncLocal<Action<int>?> _testOptimizeLaunchedProcessHook = new();
        internal static Action<int>? TestOptimizeLaunchedProcessHook
        {
            get => _testOptimizeLaunchedProcessHook.Value;
            set => _testOptimizeLaunchedProcessHook.Value = value;
        }

        /// <summary>
        /// Applies baseline launch optimization to a newly spawned Roblox process.
        /// Always enrolls the process into RobloxJobObjectService so kernel IOCP exit telemetry works even when CPU rate-limiting is 0%.
        /// </summary>
        public static void OptimizeLaunchedProcess(int pid)
        {
            if (pid <= 0) return;
            TestOptimizeLaunchedProcessHook?.Invoke(pid);
            try
            {
                if (AppServices.GeneralSettings.Get<bool>(AppSettingsKeys.General.AutoLowerProcessPriority, false))
                {
                    SetProcessPriority(pid, BELOW_NORMAL_PRIORITY_CLASS);
                }

                int cpuCapPercent = AppServices.GeneralSettings.Get<int>(AppSettingsKeys.General.MaxTotalRobloxCpuPercent, 0);
                if (cpuCapPercent > 0 && RobloxJobObjectService.CurrentCpuRatePercent != cpuCapPercent)
                {
                    RobloxJobObjectService.SetCpuRateLimit(cpuCapPercent);
                }

                if (ShouldAssignToJobObject(pid))
                {
                    RobloxJobObjectService.AssignProcessByPid(pid);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Failed optimizing launched process {pid}: {ex.Message}");
            }
        }
    }
}
