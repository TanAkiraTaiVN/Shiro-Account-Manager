#nullable enable
using System;
using System.Runtime.InteropServices;

namespace Shiro.Services
{
    /// <summary>
    /// Centralized source-generated Win32 P/Invoke declarations and native structs shared across Windows services.
    /// </summary>
    internal static unsafe partial class Win32NativeHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PROCESSENTRY32W
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

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_MEMORY_COUNTERS
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

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(IntPtr hObject);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint GetPriorityClass(IntPtr hProcess);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool K32GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS ppsmemCounters, uint cb);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool Process32FirstW(IntPtr hSnapshot, PROCESSENTRY32W* lppe);

        [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool Process32NextW(IntPtr hSnapshot, PROCESSENTRY32W* lppe);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessTimes(
            IntPtr hProcess,
            out long lpCreationTime,
            out long lpExitTime,
            out long lpKernelTime,
            out long lpUserTime);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [LibraryImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool EmptyWorkingSet(IntPtr hProcess);

        [LibraryImport("user32.dll")]
        internal static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindowVisible(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsIconic(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetForegroundWindow(IntPtr hWnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll")]
        internal static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true)]
        internal static partial int GetWindowTextW(IntPtr hWnd, char* lpString, int nMaxCount);

        [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
        internal static partial int GetClassNameW(IntPtr hWnd, char* lpClassName, int nMaxCount);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool EnumWindows(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> lpEnumFunc, IntPtr lParam);

        [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    }
}
