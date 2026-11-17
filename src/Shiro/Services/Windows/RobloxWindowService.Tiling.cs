#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Shiro.Services
{
    public static partial class RobloxWindowService
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial IntPtr BeginDeferWindowPos(int nNumWindows);

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial IntPtr DeferWindowPos(
            IntPtr hWinPosInfo,
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EndDeferWindowPos(IntPtr hWinPosInfo);

        public static (int Cols, int Rows, List<(IntPtr Hwnd, int X, int Y, int Width, int Height)> Placements) ComputeTilePlacements(
            IReadOnlyList<IntPtr> windows,
            DesktopRect workArea,
            int customCols = 0,
            int customRows = 0)
        {
            int count = windows?.Count ?? 0;
            var placements = new List<(IntPtr Hwnd, int X, int Y, int Width, int Height)>(count);
            if (count == 0 || windows == null)
                return (1, 1, placements);

            var (cols, rows) = CalculateOptimalGrid(count, customCols, customRows);
            int tileWidth = Math.Max(100, (int)(workArea.Width / cols));
            int tileHeight = Math.Max(100, (int)(workArea.Height / rows));

            for (int i = 0; i < count; i++)
            {
                int col = i % cols;
                int row = i / cols;
                int x = workArea.Left + (col * tileWidth);
                int y = workArea.Top + (row * tileHeight);
                placements.Add((windows[i], x, y, tileWidth, tileHeight));
            }

            return (cols, rows, placements);
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
                return (0, Shiro.Services.LocalizationService.Get("WindowArranger.NoClientsRunning"));

            var windows = FindWindowsForPids(pids).Where(IsWindow).Distinct().ToList();
            if (windows.Count == 0)
                return (0, Shiro.Services.LocalizationService.Get("WindowArranger.NoWindowsFound"));

            var workArea = GetTargetWorkArea();
            var (cols, rows, placements) = ComputeTilePlacements(windows, workArea, customCols, customRows);

            for (int i = 0; i < placements.Count; i++)
            {
                ShowWindow(placements[i].Hwnd, SW_RESTORE);
            }

            const uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW;
            int tiled = 0;
            bool deferredSuccess = false;

            IntPtr hdwp = BeginDeferWindowPos(placements.Count);
            if (hdwp != IntPtr.Zero)
            {
                deferredSuccess = true;
                for (int i = 0; i < placements.Count; i++)
                {
                    var p = placements[i];
                    hdwp = DeferWindowPos(hdwp, p.Hwnd, IntPtr.Zero, p.X, p.Y, p.Width, p.Height, flags);
                    if (hdwp == IntPtr.Zero)
                    {
                        deferredSuccess = false;
                        break;
                    }
                }

                if (deferredSuccess && EndDeferWindowPos(hdwp))
                {
                    tiled = placements.Count;
                }
                else
                {
                    deferredSuccess = false;
                }
            }

            if (!deferredSuccess)
            {
                for (int i = 0; i < placements.Count; i++)
                {
                    var p = placements[i];
                    if (SetWindowPos(p.Hwnd, IntPtr.Zero, p.X, p.Y, p.Width, p.Height, flags))
                    {
                        tiled++;
                    }
                }
            }

            string desc = Shiro.Services.LocalizationService.Format("WindowService.GridDimensionsFormat", cols, rows, tiled);
            AppServices.Logger.Info($"[RobloxWindowService] Tiled {tiled} Roblox window(s) into {desc} grid.");
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
            try
            {
                return FindWindowForPidCallbackCore(hWnd, lParam);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"FindWindowForPidCallback failed: {ex.Message}");
                return 1;
            }
        }

        internal static int FindWindowForPidCallbackCore(IntPtr hWnd, IntPtr lParam)
        {
            if (lParam == IntPtr.Zero) return 1;
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
            try
            {
                return FindWindowsForPidsCallbackCore(hWnd, lParam);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"FindWindowsForPidsCallback failed: {ex.Message}");
                return 1;
            }
        }

        internal static int FindWindowsForPidsCallbackCore(IntPtr hWnd, IntPtr lParam)
        {
            if (lParam == IntPtr.Zero) return 1;
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
