#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Shiro.Services
{
    public static partial class RobloxDirectLauncher
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DuplicateTokenEx(
            IntPtr hExistingToken,
            uint dwDesiredAccess,
            IntPtr lpTokenAttributes,
            int ImpersonationLevel,
            int TokenType,
            out IntPtr phNewToken);

        [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessWithTokenW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CreateProcessWithToken(
            IntPtr hToken,
            uint dwLogonFlags,
            string? lpApplicationName,
            Span<char> lpCommandLine,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr hObject);

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetShellWindow();

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        private const uint TOKEN_DUPLICATE = 0x0002;
        private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint MAXIMUM_ALLOWED = 0x02000000;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const uint LOGON_WITH_PROFILE = 1;

        /// <summary>
        /// Checks whether Shiro is currently executing with elevated Administrator privileges.
        /// </summary>
        public static bool IsCurrentProcessElevated()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[RobloxDirectLauncher] Check IsCurrentProcessElevated failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Spawns a Roblox client process, automatically de-elevating to Medium Integrity (Standard User)
        /// via explorer.exe token duplication if Shiro is running with Administrator privileges.
        /// </summary>
        public static Process? SpawnRobloxProcess(string playerPath, string args, string username)
        {
            bool deElevateEnabled = AppSettings.General.Get<bool>(AppSettingsKeys.General.EnableTokenDeElevation, true);
            if (deElevateEnabled && IsCurrentProcessElevated())
            {
                Program.Logger?.Info($"[RobloxDirectLauncher] Shiro is running elevated; de-elevating Roblox launch for {username}...");
                if (TrySpawnDeElevated(playerPath, args, out Process? deElevated) && deElevated != null)
                {
                    return deElevated;
                }
                Program.Logger?.Warn($"[RobloxDirectLauncher] De-elevated launch failed; falling back to standard Process.Start.");
            }

            var startInfo = new ProcessStartInfo(playerPath, args)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(playerPath) ?? Environment.CurrentDirectory
            };
            return Process.Start(startInfo);
        }

        internal static bool TrySpawnDeElevated(string playerPath, string args, out Process? process)
        {
            process = null;
            try
            {
                int explorerPid = 0;
                IntPtr hShell = GetShellWindow();
                if (hShell != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hShell, out explorerPid);
                }

                if (explorerPid <= 0)
                {
                    var explorers = Process.GetProcessesByName("explorer");
                    try
                    {
                        var exp = explorers.FirstOrDefault();
                        if (exp != null)
                        {
                            explorerPid = exp.Id;
                        }
                    }
                    finally
                    {
                        foreach (var p in explorers)
                            p.Dispose();
                    }
                }

                if (explorerPid <= 0)
                {
                    Program.Logger?.Debug("[RobloxDirectLauncher] Explorer process not found for de-elevation.");
                    return false;
                }

                IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, explorerPid);
                if (hProcess == IntPtr.Zero)
                {
                    Program.Logger?.Debug($"[RobloxDirectLauncher] OpenProcess(explorer) failed (err: {Marshal.GetLastWin32Error()}).");
                    return false;
                }

                try
                {
                    if (!OpenProcessToken(hProcess, TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY, out IntPtr hToken))
                    {
                        Program.Logger?.Debug($"[RobloxDirectLauncher] OpenProcessToken failed (err: {Marshal.GetLastWin32Error()}).");
                        return false;
                    }

                    try
                    {
                        if (!DuplicateTokenEx(hToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out IntPtr hNewToken))
                        {
                            Program.Logger?.Debug($"[RobloxDirectLauncher] DuplicateTokenEx failed (err: {Marshal.GetLastWin32Error()}).");
                            return false;
                        }

                        try
                        {
                            var si = new STARTUPINFO
                            {
                                cb = Marshal.SizeOf<STARTUPINFO>()
                            };

                            char[] cmdBuffer = ($"\"{playerPath}\" {args}\0").ToCharArray();
                            string workDir = Path.GetDirectoryName(playerPath) ?? Environment.CurrentDirectory;

                            if (CreateProcessWithToken(
                                hNewToken,
                                LOGON_WITH_PROFILE,
                                lpApplicationName: playerPath,
                                lpCommandLine: cmdBuffer,
                                0,
                                IntPtr.Zero,
                                workDir,
                                ref si,
                                out PROCESS_INFORMATION pi))
                            {
                                try
                                {
                                    process = Process.GetProcessById((int)pi.dwProcessId);
                                    Program.Logger?.Info($"[RobloxDirectLauncher] Successfully spawned de-elevated Roblox process (PID: {pi.dwProcessId}).");
                                    return true;
                                }
                                finally
                                {
                                    if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                                    if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                                }
                            }
                            else
                            {
                                int win32Err = Marshal.GetLastWin32Error();
                                if (win32Err == 1058)
                                {
                                    Program.Logger?.Warn("[RobloxDirectLauncher] Secondary Logon (seclogon) service is disabled on this system. Falling back to standard process launch.");
                                }
                                else
                                {
                                    Program.Logger?.Debug($"[RobloxDirectLauncher] CreateProcessWithTokenW failed (err: {win32Err}).");
                                }
                            }
                        }
                        finally
                        {
                            CloseHandle(hNewToken);
                        }
                    }
                    finally
                    {
                        CloseHandle(hToken);
                    }
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[RobloxDirectLauncher] De-elevation exception: {ex.Message}");
            }

            return false;
        }
    }
}
