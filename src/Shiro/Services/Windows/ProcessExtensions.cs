#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
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

        private static readonly AsyncLocal<Func<int, string?>?> _testCommandLineOverride = new();
        internal static Func<int, string?>? TestCommandLineOverride
        {
            get => _testCommandLineOverride.Value;
            set => _testCommandLineOverride.Value = value;
        }

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

            using SafeProcessHandle hProcess = Win32NativeHelper.OpenSafeProcess(PROCESS_QUERY_LIMITED_INFORMATION, pid);
            if (hProcess.IsInvalid)
            {
                _commandLineCache.TryRemove(pid, out _);
                return null;
            }

            IntPtr handle = hProcess.DangerousGetHandle();
            bool hasCreationTime = Win32NativeHelper.GetProcessTimes(handle, out long creationTime, out _, out _, out _) && creationTime > 0;

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

        internal static bool TryValidateUnicodeStringSlice(
            nint baseAddress,
            int bufferBytes,
            ushort lengthBytes,
            nint stringBufferPtr)
        {
            if (baseAddress == 0 || stringBufferPtr == 0 || bufferBytes <= 0 || lengthBytes == 0 || (lengthBytes & 1) != 0)
                return false;

            ulong start = unchecked((ulong)(nuint)baseAddress);
            ulong end = start + (uint)bufferBytes;
            if (end < start)
                return false;

            ulong strStart = unchecked((ulong)(nuint)stringBufferPtr);
            ulong strEnd = strStart + lengthBytes;
            if (strEnd < strStart)
                return false;

            return strStart >= start && strEnd <= end;
        }

        private static unsafe string? TryExtractCommandLine(nint bufPtr, int allocatedBytes, int returnLength)
        {
            if (returnLength < Marshal.SizeOf<UNICODE_STRING>())
                return null;

            UNICODE_STRING uStr = *(UNICODE_STRING*)bufPtr;
            if (!TryValidateUnicodeStringSlice(bufPtr, allocatedBytes, uStr.Length, uStr.Buffer))
                return null;

            return Marshal.PtrToStringUni(uStr.Buffer, uStr.Length / 2);
        }

        private static unsafe string? QueryCommandLineCore(IntPtr handle)
        {
            const int StackBufferSize = 4096;
            const int MaxHeapBufferSize = 1024 * 1024;
            ulong* stackWords = stackalloc ulong[StackBufferSize / 8];
            IntPtr stackBuffer = (IntPtr)stackWords;

            try
            {
                int status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, stackBuffer, StackBufferSize, out int returnLength);
                if (status == 0)
                {
                    return TryExtractCommandLine(stackBuffer, StackBufferSize, returnLength);
                }

                if (returnLength > StackBufferSize && returnLength <= MaxHeapBufferSize)
                {
                    int allocBytes = returnLength;
                    void* heapBuffer = NativeMemory.AlignedAlloc((nuint)allocBytes, 8);
                    try
                    {
                        status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, (IntPtr)heapBuffer, allocBytes, out int heapReturnLength);
                        if (status == 0)
                        {
                            return TryExtractCommandLine((nint)heapBuffer, allocBytes, heapReturnLength);
                        }
                    }
                    finally
                    {
                        NativeMemory.AlignedFree(heapBuffer);
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
