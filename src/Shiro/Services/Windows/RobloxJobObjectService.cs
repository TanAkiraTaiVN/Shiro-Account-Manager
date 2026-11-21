#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Shiro.Services
{
    /// <summary>
    /// Windows Job Object Service for Roblox processes.
    /// Manages client processes as an aggregated OS-level kernel job,
    /// enabling hardware CPU hard-caps across all running farm clients without killing them on Shiro exit.
    /// </summary>
    public static partial class RobloxJobObjectService
    {
        private static readonly System.Threading.Lock _gate = new();
        private static IntPtr _hJob = IntPtr.Zero;
        private static IntPtr _hIocp = IntPtr.Zero;
        private static Thread? _iocpThread;
        private static int _cpuRatePercent = 0;

        internal static bool IsIocpThreadAliveForTest
        {
            get
            {
                Thread? t;
                lock (_gate) t = _iocpThread;
                return t != null && t.IsAlive;
            }
        }

        private const int JobObjectAssociateCompletionPortInformation = 7;
        private const int JobObjectCpuRateControlInformation = 15;

        private const uint JOB_OBJECT_MSG_EXIT_PROCESS = 7;
        private const uint JOB_OBJECT_MSG_NEW_PROCESS = 6;
        private const uint JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS = 8;
        private const uint IOCP_QUIT_MESSAGE = 0;
        private const uint INFINITE_TIMEOUT = 0xFFFFFFFF;

        public static event Action<int, bool>? ProcessExitedFromJob;

        private const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
        private const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

        private const uint PROCESS_TERMINATE = 0x0001;
        private const uint PROCESS_SET_QUOTA = 0x0100;

        public static int CurrentCpuRatePercent
        {
            get
            {
                lock (_gate)
                    return _cpuRatePercent;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            public uint ControlFlags;
            public uint CpuRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_ASSOCIATE_COMPLETION_PORT
        {
            public IntPtr CompletionKey;
            public IntPtr CompletionPort;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr CreateIoCompletionPort(
            IntPtr FileHandle,
            IntPtr ExistingCompletionPort,
            nuint CompletionKey,
            uint NumberOfConcurrentThreads);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetQueuedCompletionStatus(
            IntPtr CompletionPort,
            out uint lpNumberOfBytesTransferred,
            out nuint lpCompletionKey,
            out nuint lpOverlapped,
            uint dwMilliseconds);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PostQueuedCompletionStatus(
            IntPtr CompletionPort,
            uint dwNumberOfBytesTransferred,
            nuint dwCompletionKey,
            IntPtr lpOverlapped);

        [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetInformationJobObject(IntPtr hJob, int JobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

        public static IntPtr EnsureJobHandle()
        {
            if (!OperatingSystem.IsWindows()) return IntPtr.Zero;

            lock (_gate)
            {
                if (_hJob == IntPtr.Zero || _hJob == (IntPtr)(-1))
                {
                    try
                    {
                        // Note: JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE is intentionally NOT set so closing Shiro leaves active games running.
                        _hJob = CreateJobObject(IntPtr.Zero, "Shiro_Roblox_Farming_Job_" + Environment.ProcessId);
                        if (_hJob != IntPtr.Zero)
                        {
                            AttachCompletionPortInternal(_hJob);
                            if (_cpuRatePercent > 0)
                            {
                                ApplyCpuRateLimitInternal(_cpuRatePercent);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppServices.Logger?.Debug($"[RobloxJobObjectService] CreateJobObject error: {ex.Message}");
                    }
                }
                return _hJob;
            }
        }

        public static bool AssignProcess(IntPtr hProcess)
        {
            if (!OperatingSystem.IsWindows() || hProcess == IntPtr.Zero) return false;
            try
            {
                lock (_gate)
                {
                    IntPtr hJob = EnsureJobHandle();
                    if (hJob != IntPtr.Zero && hJob != (IntPtr)(-1))
                    {
                        return AssignProcessToJobObject(hJob, hProcess);
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[RobloxJobObjectService] AssignProcess error: {ex.Message}");
            }
            return false;
        }

        public static bool AssignProcessByPid(int pid)
        {
            if (!OperatingSystem.IsWindows() || pid <= 0) return false;

            IntPtr hProcess = Win32NativeHelper.OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE, false, pid);
            if (hProcess == IntPtr.Zero) return false;

            try
            {
                return AssignProcess(hProcess);
            }
            finally
            {
                Win32NativeHelper.CloseHandle(hProcess);
            }
        }

        public static int AssignAllRunningRobloxProcesses(IReadOnlyList<int>? preloadedPids = null)
        {
            if (!OperatingSystem.IsWindows()) return 0;

            var pids = preloadedPids ?? RobloxProcessService.GetRunningRobloxPids();
            int assigned = 0;
            for (int i = 0; i < pids.Count; i++)
            {
                if (AssignProcessByPid(pids[i]))
                    assigned++;
            }
            return assigned;
        }

        internal static JOBOBJECT_CPU_RATE_CONTROL_INFORMATION BuildCpuRateControlInfo(int maxPercentage)
        {
            int clamped = Math.Clamp(maxPercentage, 0, 100);
            if (clamped <= 0)
            {
                return new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
                {
                    ControlFlags = 0,
                    CpuRate = 0
                };
            }

            return new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                CpuRate = (uint)(clamped * 100) // 10,000 = 100%
            };
        }

        /// <summary>
        /// Sets or disables (when 0) the aggregated CPU hard-cap across all Roblox processes in the job.
        /// When enabled (> 0), automatically assigns any currently running Roblox processes into the job.
        /// </summary>
        public static bool SetCpuRateLimit(int maxPercentage)
        {
            if (!OperatingSystem.IsWindows()) return false;

            int normalized = maxPercentage <= 0 ? 0 : Math.Clamp(maxPercentage, 5, 100);
            bool applied;
            lock (_gate)
            {
                _cpuRatePercent = normalized;
                if (normalized == 0 && (_hJob == IntPtr.Zero || _hJob == (IntPtr)(-1)))
                    return true;

                EnsureJobHandle();
                applied = ApplyCpuRateLimitInternal(normalized);
            }

            if (applied && normalized > 0)
            {
                AssignAllRunningRobloxProcesses();
            }

            return applied;
        }

        private static unsafe bool ApplyCpuRateLimitInternal(int percentage)
        {
            if (_hJob == IntPtr.Zero || _hJob == (IntPtr)(-1))
                return false;

            try
            {
                JOBOBJECT_CPU_RATE_CONTROL_INFORMATION info = BuildCpuRateControlInfo(percentage);
                return SetInformationJobObject(
                    _hJob,
                    JobObjectCpuRateControlInformation,
                    (IntPtr)(&info),
                    (uint)sizeof(JOBOBJECT_CPU_RATE_CONTROL_INFORMATION));
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[RobloxJobObjectService] ApplyCpuRateLimit error: {ex.Message}");
                return false;
            }
        }

        internal static bool DispatchCompletionMessage(uint messageId, nuint processId)
        {
            if (messageId == JOB_OBJECT_MSG_NEW_PROCESS)
            {
                RobloxProcessService.InvalidateSnapshotCache();
                return true;
            }

            if (messageId == JOB_OBJECT_MSG_EXIT_PROCESS || messageId == JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS)
            {
                RobloxProcessService.InvalidateSnapshotCache();
                int pid = (int)processId;
                if (pid > 0)
                {
                    bool abnormal = messageId == JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS;
                    var handlers = ProcessExitedFromJob;
                    if (handlers != null)
                    {
                        try
                        {
                            handlers(pid, abnormal);
                        }
                        catch (Exception ex)
                        {
                            AppServices.Logger.Debug($"[RobloxJobObjectService] ProcessExitedFromJob handler error: {ex.Message}");
                        }
                    }
                }
                return true;
            }

            return false;
        }

        private static unsafe void AttachCompletionPortInternal(IntPtr hJob)
        {
            if (_hIocp != IntPtr.Zero || hJob == IntPtr.Zero || hJob == (IntPtr)(-1))
                return;

            try
            {
                IntPtr iocp = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, 0, 1);
                if (iocp == IntPtr.Zero)
                    return;

                var assoc = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT
                {
                    CompletionKey = hJob,
                    CompletionPort = iocp
                };

                if (!SetInformationJobObject(
                        hJob,
                        JobObjectAssociateCompletionPortInformation,
                        (IntPtr)(&assoc),
                        (uint)sizeof(JOBOBJECT_ASSOCIATE_COMPLETION_PORT)))
                {
                    Win32NativeHelper.CloseHandle(iocp);
                    return;
                }

                _hIocp = iocp;
                _iocpThread = new Thread(() => PumpCompletionPort(iocp))
                {
                    IsBackground = true,
                    Name = "shiro-job-iocp"
                };
                _iocpThread.Start();
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[RobloxJobObjectService] AttachCompletionPortInternal error: {ex.Message}");
            }
        }

        private static void PumpCompletionPort(IntPtr iocp)
        {
            try
            {
                while (GetQueuedCompletionStatus(iocp, out uint messageId, out _, out nuint processId, INFINITE_TIMEOUT))
                {
                    if (messageId == IOCP_QUIT_MESSAGE)
                        break;

                    DispatchCompletionMessage(messageId, processId);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[RobloxJobObjectService] PumpCompletionPort stopped: {ex.Message}");
            }
        }

        public static void Close()
        {
            IntPtr hIocp;
            IntPtr hJob;
            Thread? threadToJoin;
            lock (_gate)
            {
                _cpuRatePercent = 0;
                hIocp = _hIocp;
                hJob = _hJob;
                threadToJoin = _iocpThread;
                _hIocp = IntPtr.Zero;
                _hJob = IntPtr.Zero;
                _iocpThread = null;
            }

            if (hIocp != IntPtr.Zero)
            {
                try { PostQueuedCompletionStatus(hIocp, IOCP_QUIT_MESSAGE, 0, IntPtr.Zero); }
                catch (Exception ex) { AppServices.Logger.Debug($"Failed posting quit to job IOCP: {ex.Message}"); }

                bool joined = threadToJoin == null || !threadToJoin.IsAlive || threadToJoin == Thread.CurrentThread || threadToJoin.Join(TimeSpan.FromSeconds(2));
                if (joined)
                {
                    try { Win32NativeHelper.CloseHandle(hIocp); }
                    catch (Exception ex) { AppServices.Logger.Debug($"Failed closing job IOCP handle: {ex.Message}"); }
                }
                else
                {
                    AppServices.Logger?.Debug("[RobloxJobObjectService] Skipping IOCP CloseHandle because completion thread did not exit within 2s.");
                }
            }

            if (hJob != IntPtr.Zero && hJob != (IntPtr)(-1))
            {
                try { Win32NativeHelper.CloseHandle(hJob); }
                catch (Exception ex) { AppServices.Logger?.Debug($"Failed closing job object handle: {ex.Message}"); }
            }
        }
    }
}
