#nullable enable
using System;
using System.Runtime.InteropServices;

namespace Shiro.Services
{
    /// <summary>
    /// Windows Job Object Service for Roblox processes.
    /// Manages client processes as an aggregated OS-level kernel job,
    /// enabling hardware CPU hard-caps and clean multi-instance lifecycle control.
    /// </summary>
    public static partial class RobloxJobObjectService
    {
        private static readonly System.Threading.Lock _gate = new();
        private static IntPtr _hJob = IntPtr.Zero;
        private static int _cpuRatePercent = 0;

        private const int JobObjectCpuRateControlInformation = 15;

        private const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
        private const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            public uint ControlFlags;
            public uint CpuRate;
        }

        [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetInformationJobObject(IntPtr hJob, int JobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr hObject);

        public static IntPtr EnsureJobHandle()
        {
            lock (_gate)
            {
                if (_hJob == IntPtr.Zero || _hJob == (IntPtr)(-1))
                {
                    try
                    {
                        _hJob = CreateJobObject(IntPtr.Zero, "Shiro_Roblox_Farming_Job_" + Environment.ProcessId);
                        if (_hJob != IntPtr.Zero && _cpuRatePercent > 0)
                        {
                            ApplyCpuRateLimitInternal(_cpuRatePercent);
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"[RobloxJobObjectService] CreateJobObject error: {ex.Message}");
                    }
                }
                return _hJob;
            }
        }

        public static bool AssignProcess(IntPtr hProcess)
        {
            if (hProcess == IntPtr.Zero) return false;
            try
            {
                IntPtr hJob = EnsureJobHandle();
                if (hJob != IntPtr.Zero && hJob != (IntPtr)(-1))
                {
                    return AssignProcessToJobObject(hJob, hProcess);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[RobloxJobObjectService] AssignProcess error: {ex.Message}");
            }
            return false;
        }

        public static bool SetCpuRateLimit(int maxPercentage)
        {
            lock (_gate)
            {
                EnsureJobHandle();
                _cpuRatePercent = Math.Clamp(maxPercentage, 0, 100);
                return ApplyCpuRateLimitInternal(_cpuRatePercent);
            }
        }

        private static unsafe bool ApplyCpuRateLimitInternal(int percentage)
        {
            if (_hJob == IntPtr.Zero || _hJob == (IntPtr)(-1))
                return false;

            try
            {
                if (percentage > 0)
                {
                    var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
                    {
                        ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                        CpuRate = (uint)(percentage * 100) // 10,000 = 100%
                    };
                    return SetInformationJobObject(_hJob, JobObjectCpuRateControlInformation, (IntPtr)(&info), (uint)sizeof(JOBOBJECT_CPU_RATE_CONTROL_INFORMATION));
                }
                return true;
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[RobloxJobObjectService] ApplyCpuRateLimit error: {ex.Message}");
                return false;
            }
        }

        public static void Close()
        {
            lock (_gate)
            {
                if (_hJob != IntPtr.Zero && _hJob != (IntPtr)(-1))
                {
                    try { CloseHandle(_hJob); }
                    catch (Exception ex) { Program.Logger?.Debug($"Failed closing job object handle: {ex.Message}"); }
                    _hJob = IntPtr.Zero;
                }
            }
        }
    }
}
