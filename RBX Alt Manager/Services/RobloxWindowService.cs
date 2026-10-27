using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RBX_Alt_Manager.Services
{
    /// <summary>
    /// Manages visibility and desktop layout of Roblox client windows (Headless / Zero GPU Mode & Smart Tiling).
    /// Accurately identifies primary game viewports (WINDOWSCLIENT class) while ignoring internal CEF,
    /// Direct3D, and IME utility windows to prevent ghost blank rectangles.
    /// </summary>
    public static partial class RobloxWindowService
    {
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private const uint GW_OWNER = 4;
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const long WS_CHILD = 0x40000000L;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool EnumWindows(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> lpEnumFunc, IntPtr lParam);

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindow(IntPtr hWnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static partial nint GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
        private static unsafe partial int GetClassNameW(IntPtr hWnd, char* lpClassName, int nMaxCount);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true)]
        private static unsafe partial int GetWindowTextW(IntPtr hWnd, char* lpString, int nMaxCount);

        [LibraryImport("user32.dll")]
        private static partial IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool GetMonitorInfo(IntPtr hMonitor, MONITORINFO* lpmi);

        private static readonly ConcurrentDictionary<IntPtr, bool> _hiddenWindows = new ConcurrentDictionary<IntPtr, bool>();

        public static bool AreWindowsHidden
        {
            get
            {
                if (_hiddenWindows.IsEmpty) return false;
                bool anyHidden = false;
                foreach (var kvp in _hiddenWindows)
                {
                    if (!IsWindow(kvp.Key))
                    {
                        _hiddenWindows.TryRemove(kvp.Key, out _);
                        continue;
                    }
                    if (kvp.Value)
                        anyHidden = true;
                }
                return anyHidden;
            }
        }

        public enum WindowMatchQuality
        {
            None = 0,
            TitleCandidate = 1,
            ExactMainClient = 2
        }

        public static unsafe string GetWindowClassName(IntPtr hWnd)
        {
            Span<char> buffer = stackalloc char[256];
            fixed (char* ptr = buffer)
            {
                int length = GetClassNameW(hWnd, ptr, buffer.Length);
                return length > 0 ? new string(buffer.Slice(0, length)) : string.Empty;
            }
        }

        public static unsafe string GetWindowTextString(IntPtr hWnd)
        {
            Span<char> buffer = stackalloc char[256];
            fixed (char* ptr = buffer)
            {
                int length = GetWindowTextW(hWnd, ptr, buffer.Length);
                return length > 0 ? new string(buffer.Slice(0, length)) : string.Empty;
            }
        }

        public static bool IsExcludedUtilityClass(string className)
        {
            if (string.IsNullOrEmpty(className)) return false;
            return className.StartsWith("Chrome", StringComparison.OrdinalIgnoreCase) ||
                   className.StartsWith("CEF", StringComparison.OrdinalIgnoreCase) ||
                   className.StartsWith("Cicero", StringComparison.OrdinalIgnoreCase) ||
                   className.IndexOf("Direct3D", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   className.IndexOf("GDI+", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   className.Equals("WorkerW", StringComparison.OrdinalIgnoreCase);
        }

        public static WindowMatchQuality EvaluateRobloxWindow(IntPtr hWnd)
        {
            if (!IsWindow(hWnd)) return WindowMatchQuality.None;

            // Must not be owned by another window (popups/dialogs/tooltips have owners)
            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero)
                return WindowMatchQuality.None;

            // Must not be a child window
            nint style = GetWindowLongPtr(hWnd, GWL_STYLE);
            if ((style & WS_CHILD) != 0)
                return WindowMatchQuality.None;

            // Must not be a tool window
            nint exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                return WindowMatchQuality.None;

            string className = GetWindowClassName(hWnd);
            if (string.Equals(className, "WINDOWSCLIENT", StringComparison.OrdinalIgnoreCase))
                return WindowMatchQuality.ExactMainClient;

            if (!IsExcludedUtilityClass(className))
            {
                string title = GetWindowTextString(hWnd);
                if (title.Contains("Roblox", StringComparison.OrdinalIgnoreCase))
                    return WindowMatchQuality.TitleCandidate;
            }

            return WindowMatchQuality.None;
        }

        /// <summary>
        /// Calculates the optimal grid dimensions (columns, rows) for tiling windows cleanly.
        /// </summary>
        public static (int cols, int rows) CalculateOptimalGrid(int windowCount, int customCols = 0, int customRows = 0)
        {
            if (windowCount <= 0) return (1, 1);

            int cols = customCols;
            int rows = customRows;

            if (cols <= 0 || rows <= 0)
            {
                if (windowCount == 1) { cols = 1; rows = 1; }
                else if (windowCount == 2) { cols = 2; rows = 1; }
                else if (windowCount <= 4) { cols = 2; rows = 2; }
                else if (windowCount <= 6) { cols = 3; rows = 2; }
                else if (windowCount <= 9) { cols = 3; rows = 3; }
                else if (windowCount <= 12) { cols = 4; rows = 3; }
                else if (windowCount <= 16) { cols = 4; rows = 4; }
                else
                {
                    cols = (int)Math.Ceiling(Math.Sqrt(windowCount));
                    rows = (int)Math.Ceiling((double)windowCount / cols);
                }
            }
            else
            {
                // Dynamic overflow protection: if preset grid has fewer slots than running windows,
                // expand rows so no windows are placed off-screen.
                if (cols * rows < windowCount)
                {
                    rows = (int)Math.Ceiling((double)windowCount / cols);
                }
            }

            return (cols, rows);
        }

        [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SystemParametersInfo(int uAction, int uParam, out RECT lpvParam, int fuWinIni);

        public readonly record struct DesktopRect(int Left, int Top, int Width, int Height);

        public static unsafe DesktopRect GetTargetWorkArea()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var mainWin = AvaloniaUI.Views.MainWindow.Instance;
                    IntPtr hwnd = mainWin?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                    if (hwnd != IntPtr.Zero)
                    {
                        IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                        if (hMonitor != IntPtr.Zero)
                        {
                            MONITORINFO mi = default;
                            mi.cbSize = Marshal.SizeOf<MONITORINFO>();
                            if (GetMonitorInfo(hMonitor, &mi))
                            {
                                int width = mi.rcWork.right - mi.rcWork.left;
                                int height = mi.rcWork.bottom - mi.rcWork.top;
                                if (width > 0 && height > 0)
                                {
                                    return new DesktopRect(mi.rcWork.left, mi.rcWork.top, width, height);
                                }
                            }
                        }
                    }

                    if (SystemParametersInfo(0x0030, 0, out RECT spRect, 0))
                    {
                        return new DesktopRect(spRect.left, spRect.top, spRect.right - spRect.left, spRect.bottom - spRect.top);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"GetTargetWorkArea calculation threw: {ex.Message}");
            }

            return new DesktopRect(0, 0, 1920, 1080);
        }

        /// <summary>
        /// Hides all currently running Roblox game windows (reduces GPU & VRAM load).
        /// </summary>
        public static (int hiddenCount, int totalClients) HideAllWindows()
        {
            var pids = GetRobloxPids();
            if (pids.Count == 0) return (0, 0);

            var windows = FindWindowsForPids(pids);
            int hidden = 0;

            foreach (var hWnd in windows)
            {
                if (ShowWindow(hWnd, SW_HIDE))
                {
                    _hiddenWindows[hWnd] = true;
                    hidden++;
                }
            }

            Program.Logger.Info($"[RobloxWindowService] Hidden {hidden} window(s) across {pids.Count} Roblox client(s).");

            // Automatically optimize background processes (lower scheduler priority and trim working set)
            try
            {
                if (AccountManager.General?.Get<bool>(AppSettingsKeys.General.AutoLowerProcessPriority, true) ?? true)
                {
                    RobloxProcessOptimizer.LowerPriorityForPids(pids.Select(p => (int)p));
                    MemoryOptimizer.TrimRobloxClients(lowerPriority: false);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Process priority adjustment on hide threw: {ex.Message}");
            }

            return (hidden, pids.Count);
        }

        /// <summary>
        /// Restores all previously hidden Roblox game windows back to screen.
        /// </summary>
        public static (int shownCount, int totalClients) ShowAllWindows()
        {
            var pids = GetRobloxPids();
            var windows = new HashSet<IntPtr>(_hiddenWindows.Keys.Where(IsWindow));

            // Also probe any active windows in case new ones appeared
            foreach (var w in FindWindowsForPids(pids))
                windows.Add(w);

            int shown = 0;
            foreach (var hWnd in windows)
            {
                if (ShowWindow(hWnd, SW_RESTORE))
                {
                    shown++;
                }
            }
            _hiddenWindows.Clear();

            Program.Logger.Info($"[RobloxWindowService] Restored {shown} Roblox window(s).");

            // Restore normal priority for unhidden clients
            try
            {
                if (AccountManager.General?.Get<bool>(AppSettingsKeys.General.AutoLowerProcessPriority, true) ?? true)
                {
                    RobloxProcessOptimizer.RestorePriorityForPids(pids.Select(p => (int)p));
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Process priority adjustment on show threw: {ex.Message}");
            }

            return (shown, pids.Count);
        }

        /// <summary>
        /// Toggles between hiding and restoring all Roblox windows.
        /// </summary>
        public static (bool nowHidden, int affectedCount) ToggleVisibility()
        {
            if (AreWindowsHidden)
            {
                var (shown, _) = ShowAllWindows();
                return (false, shown);
            }
            else
            {
                var (hidden, _) = HideAllWindows();
                return (true, hidden);
            }
        }

        /// <summary>
        /// Restores and neatly arranges all running Roblox game windows into an optimal grid layout.
        /// </summary>
        public static (int tiledCount, string layoutDesc) TileWindows(int customCols = 0, int customRows = 0)
        {
            // First ensure any hidden windows are restored
            if (AreWindowsHidden)
            {
                ShowAllWindows();
            }

            var pids = GetRobloxPids();
            if (pids.Count == 0)
                return (0, "Không tìm thấy client Roblox nào đang chạy.");

            var windows = FindWindowsForPids(pids).Where(IsWindow).Distinct().ToList();
            if (windows.Count == 0)
                return (0, "Không tìm thấy cửa sổ Roblox nào.");

            var workArea = GetTargetWorkArea();
            int count = windows.Count;

            var (cols, rows) = CalculateOptimalGrid(count, customCols, customRows);

            int tileWidth = Math.Max(100, (int)(workArea.Width / cols));
            int tileHeight = Math.Max(100, (int)(workArea.Height / rows));

            int tiled = 0;
            for (int i = 0; i < count; i++)
            {
                int col = i % cols;
                int row = i / cols;

                int x = (int)workArea.Left + (col * tileWidth);
                int y = (int)workArea.Top + (row * tileHeight);

                IntPtr hWnd = windows[i];
                ShowWindow(hWnd, SW_RESTORE);
                if (SetWindowPos(hWnd, IntPtr.Zero, x, y, tileWidth, tileHeight, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW))
                {
                    tiled++;
                }
            }

            string desc = $"{cols}x{rows} ({tiled} cửa sổ)";
            Program.Logger.Info($"[RobloxWindowService] Tiled {tiled} Roblox window(s) into {desc} grid.");
            return (tiled, desc);
        }

        private sealed class SinglePidContext
        {
            public uint TargetPid;
            public IntPtr FoundHwnd;
            public WindowMatchQuality FoundQuality;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int FindWindowForPidCallback(IntPtr hWnd, IntPtr lParam)
        {
            var ctx = (SinglePidContext)GCHandle.FromIntPtr(lParam).Target!;
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid != ctx.TargetPid)
                return 1;

            var quality = EvaluateRobloxWindow(hWnd);
            if (quality == WindowMatchQuality.ExactMainClient)
            {
                ctx.FoundHwnd = hWnd;
                ctx.FoundQuality = quality;
                return 0; // Found definitive Roblox client window, stop early
            }

            if (quality == WindowMatchQuality.TitleCandidate && ctx.FoundQuality < WindowMatchQuality.TitleCandidate)
            {
                ctx.FoundHwnd = hWnd;
                ctx.FoundQuality = quality;
            }

            return 1;
        }

        public static unsafe IntPtr FindWindowForPid(uint pid)
        {
            var ctx = new SinglePidContext { TargetPid = pid };
            GCHandle handle = GCHandle.Alloc(ctx);
            try
            {
                EnumWindows(&FindWindowForPidCallback, GCHandle.ToIntPtr(handle));
                return ctx.FoundHwnd;
            }
            finally
            {
                handle.Free();
            }
        }

        private static HashSet<uint> GetRobloxPids()
        {
            var pids = new HashSet<uint>();
            foreach (int pid in RobloxProcessService.GetRunningRobloxPids())
            {
                if (pid > 0)
                    pids.Add((uint)pid);
            }
            return pids;
        }

        private sealed class MultiPidContext
        {
            public required HashSet<uint> Pids;
            public required Dictionary<uint, (IntPtr Hwnd, WindowMatchQuality Quality)> WindowMap;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int FindWindowsForPidsCallback(IntPtr hWnd, IntPtr lParam)
        {
            var ctx = (MultiPidContext)GCHandle.FromIntPtr(lParam).Target!;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (!ctx.Pids.Contains(pid))
                return 1;

            var quality = EvaluateRobloxWindow(hWnd);
            if (quality == WindowMatchQuality.None)
                return 1;

            if (!ctx.WindowMap.TryGetValue(pid, out var existing))
            {
                ctx.WindowMap[pid] = (hWnd, quality);
            }
            else if (quality > existing.Quality)
            {
                // Upgrade from fallback title match to exact WINDOWSCLIENT match
                ctx.WindowMap[pid] = (hWnd, quality);
            }

            return 1;
        }

        public static unsafe List<IntPtr> FindWindowsForPids(HashSet<uint> pids)
        {
            var windows = new List<IntPtr>();
            if (pids.Count == 0) return windows;

            var windowMap = FindWindowMapForPids(pids);
            return windowMap.Values.ToList();
        }

        public static unsafe Dictionary<uint, IntPtr> FindWindowMapForPids(HashSet<uint> pids)
        {
            var result = new Dictionary<uint, IntPtr>();
            if (pids == null || pids.Count == 0) return result;

            var windowMap = new Dictionary<uint, (IntPtr Hwnd, WindowMatchQuality Quality)>();
            var ctx = new MultiPidContext { Pids = pids, WindowMap = windowMap };
            GCHandle handle = GCHandle.Alloc(ctx);
            try
            {
                EnumWindows(&FindWindowsForPidsCallback, GCHandle.ToIntPtr(handle));
                foreach (var kvp in windowMap)
                {
                    result[kvp.Key] = kvp.Value.Hwnd;
                }
                return result;
            }
            finally
            {
                handle.Free();
            }
        }
    }
}
