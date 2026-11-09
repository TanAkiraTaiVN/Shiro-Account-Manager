#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Shiro.Services
{
    public static partial class ProcessExtensions
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int ProcessCommandLineInformation = 60;
        private const int MaxCacheEntries = 256;

        // Command lines on Windows are immutable for the lifetime of a process.
        // Keyed by PID and verified against Kernel CreationTime (FILETIME) to be 100% immune to OS PID recycling.
        private static readonly ConcurrentDictionary<int, (long CreationTime, string CommandLine)> _commandLineCache = new();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial SafeProcessHandle OpenProcess(
            uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
            int dwProcessId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetProcessTimes(
            IntPtr hProcess,
            out long lpCreationTime,
            out long lpExitTime,
            out long lpKernelTime,
            out long lpUserTime);

        [LibraryImport("ntdll.dll")]
        private static partial int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            IntPtr processInformation,
            int processInformationLength,
            out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        public static string? GetCommandLine(this Process? process)
        {
            if (process == null) return null;
            int pid;
            try
            {
                pid = process.Id;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                return null;
            }

            return GetCommandLineByPid(pid);
        }

        internal static Func<int, string?>? TestCommandLineOverride;

        public static bool IsLaunchToTrayCommandLine(string? commandLine) =>
            !string.IsNullOrEmpty(commandLine) &&
            commandLine.Contains("--launch-to-tray", StringComparison.OrdinalIgnoreCase);

        public static bool IsLaunchToTrayProcess(int pid) =>
            pid > 0 && IsLaunchToTrayCommandLine(GetCommandLineByPid(pid));

        public static unsafe string? GetCommandLineByPid(int pid)
        {
            if (TestCommandLineOverride != null)
                return TestCommandLineOverride(pid);
            if (pid <= 0)
                return null;

            using SafeProcessHandle hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == null || hProcess.IsInvalid)
            {
                _commandLineCache.TryRemove(pid, out _);
                return null;
            }

            IntPtr handle = hProcess.DangerousGetHandle();
            bool hasCreationTime = GetProcessTimes(handle, out long creationTime, out _, out _, out _) && creationTime > 0;

            if (hasCreationTime &&
                _commandLineCache.TryGetValue(pid, out var cached) &&
                cached.CreationTime == creationTime &&
                !string.IsNullOrEmpty(cached.CommandLine))
            {
                return cached.CommandLine;
            }

            string? commandLine = QueryCommandLineCore(handle);
            if (!string.IsNullOrEmpty(commandLine) && hasCreationTime)
            {
                if (_commandLineCache.Count > MaxCacheEntries)
                    _commandLineCache.Clear();

                _commandLineCache[pid] = (creationTime, commandLine);
            }

            return commandLine;
        }

        private static unsafe string? QueryCommandLineCore(IntPtr handle)
        {
            const int StackBufferSize = 4096;
            byte* stackBuffer = stackalloc byte[StackBufferSize];

            try
            {
                int status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, (IntPtr)stackBuffer, StackBufferSize, out int returnLength);
                if (status == 0)
                {
                    UNICODE_STRING uStr = *(UNICODE_STRING*)stackBuffer;
                    if (uStr.Length > 0 && uStr.Buffer != IntPtr.Zero)
                    {
                        return Marshal.PtrToStringUni(uStr.Buffer, uStr.Length / 2);
                    }
                }
                else if (returnLength > StackBufferSize)
                {
                    IntPtr heapBuffer = Marshal.AllocHGlobal(returnLength);
                    try
                    {
                        status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, heapBuffer, returnLength, out returnLength);
                        if (status == 0)
                        {
                            UNICODE_STRING uStr = Marshal.PtrToStructure<UNICODE_STRING>(heapBuffer);
                            if (uStr.Length > 0 && uStr.Buffer != IntPtr.Zero)
                            {
                                return Marshal.PtrToStringUni(uStr.Buffer, uStr.Length / 2);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(heapBuffer);
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"GetCommandLineNative threw: {ex.Message}");
            }

            return null;
        }
    }
}
