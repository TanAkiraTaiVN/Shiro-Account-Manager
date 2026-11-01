#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Shiro.Classes;

namespace Shiro.Services
{
    /// <summary>
    /// High-performance process optimizer for Roblox game instances.
    /// Manages CPU process priorities, core affinities, and background throttling to prevent PC stutter when multi-boxing.
    /// </summary>
    public static partial class RobloxProcessOptimizer
    {
        private const uint PROCESS_SET_INFORMATION = 0x0200;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        // Process Priority Classes
        public const uint IDLE_PRIORITY_CLASS = 0x00000040;
        public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
        public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
        public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
        public const uint HIGH_PRIORITY_CLASS = 0x00000080;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial uint GetPriorityClass(IntPtr hProcess);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetProcessAffinityMask(IntPtr hProcess, UIntPtr dwProcessAffinityMask);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// Adjusts the Windows scheduler execution priority class for a target Roblox process ID.
        /// </summary>
        public static bool SetProcessPriority(int pid, uint priorityClass)
        {
            if (pid <= 0) return false;
            IntPtr hProcess = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero) return false;

            try
            {
                return SetPriorityClass(hProcess, priorityClass);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[RobloxProcessOptimizer] Failed to set priority for PID {pid}: {ex.Message}");
                return false;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        /// <summary>
        /// Restricts the CPU core affinity mask of a process to specified logical CPU cores.
        /// </summary>
        public static bool SetProcessAffinity(int pid, ulong affinityMask)
        {
            if (pid <= 0 || affinityMask == 0) return false;
            IntPtr hProcess = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero) return false;

            try
            {
                return SetProcessAffinityMask(hProcess, new UIntPtr(affinityMask));
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[RobloxProcessOptimizer] Failed to set affinity for PID {pid}: {ex.Message}");
                return false;
            }
            finally
            {
                CloseHandle(hProcess);
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

        /// <summary>
        /// Applies baseline launch optimization to a newly spawned Roblox process.
        /// </summary>
        public static void OptimizeLaunchedProcess(int pid)
        {
            if (pid <= 0) return;
            try
            {
                if (AccountManager.General?.Get<bool>(AppSettingsKeys.General.AutoLowerProcessPriority, false) ?? false)
                {
                    SetProcessPriority(pid, BELOW_NORMAL_PRIORITY_CLASS);
                }

                int cpuCapPercent = AccountManager.General?.Get<int>(AppSettingsKeys.General.MaxTotalRobloxCpuPercent, 0) ?? 0;
                if (cpuCapPercent > 0 || RobloxJobObjectService.CurrentCpuRatePercent > 0)
                {
                    if (RobloxJobObjectService.CurrentCpuRatePercent != cpuCapPercent && cpuCapPercent > 0)
                    {
                        RobloxJobObjectService.SetCpuRateLimit(cpuCapPercent);
                    }
                    RobloxJobObjectService.AssignProcessByPid(pid);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Failed optimizing launched process {pid}: {ex.Message}");
            }
        }
    }
}
