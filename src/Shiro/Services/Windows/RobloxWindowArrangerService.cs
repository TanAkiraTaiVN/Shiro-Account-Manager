#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Shiro.Services
{
    public enum RobloxWindowLayoutMode
    {
        AutoGrid,
        Grid2x2,
        Grid3x2,
        Grid4x3,
        CascadeCorner
    }

    /// <summary>
    /// High-speed Win32 window manager for per-account Roblox window focusing, running-PID attribution,
    /// and 1-click multi-window grid / corner-cascade arrangement.
    /// </summary>
    public static partial class RobloxWindowArrangerService
    {
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [GeneratedRegex(@"\-b (\d+)")]
        private static partial Regex BrowserTrackerRegex();

        // Amortized O(1) cache of live PID -> BrowserTrackerID so status ticks never re-read process PEB command lines.
        private static readonly ConcurrentDictionary<int, string> _pidTrackerCache = new();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsIconic(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool BringWindowToTop(IntPtr hWnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        public readonly record struct WindowPlacementSlot(int X, int Y, int Width, int Height);

        /// <summary>
        /// Pure deterministic layout calculator for unit testing and Win32 placement.
        /// </summary>
        public static IReadOnlyList<WindowPlacementSlot> CalculatePlacements(
            RobloxWindowService.DesktopRect workArea,
            int windowCount,
            RobloxWindowLayoutMode mode)
        {
            if (windowCount <= 0)
                return Array.Empty<WindowPlacementSlot>();

            var slots = new List<WindowPlacementSlot>(windowCount);

            if (mode == RobloxWindowLayoutMode.CascadeCorner)
            {
                int compactWidth = Math.Clamp((int)(workArea.Width * 0.36), 420, 760);
                int compactHeight = Math.Clamp((int)(workArea.Height * 0.40), 300, 520);
                int stepX = 28;
                int stepY = 28;
                int maxStepsX = Math.Max(1, (workArea.Width - compactWidth) / stepX);
                int maxStepsY = Math.Max(1, (workArea.Height - compactHeight) / stepY);
                int cycle = Math.Max(1, Math.Min(maxStepsX, maxStepsY));

                // Anchor compact stack near bottom-right corner so main desktop stays uncluttered
                int baseRightX = workArea.Left + Math.Max(0, workArea.Width - compactWidth - 16);
                int baseBottomY = workArea.Top + Math.Max(0, workArea.Height - compactHeight - 16);

                for (int i = 0; i < windowCount; i++)
                {
                    int offsetIndex = i % cycle;
                    int x = Math.Max(workArea.Left, baseRightX - (offsetIndex * stepX));
                    int y = Math.Max(workArea.Top, baseBottomY - (offsetIndex * stepY));
                    slots.Add(new WindowPlacementSlot(x, y, compactWidth, compactHeight));
                }

                return slots;
            }

            var (customCols, customRows) = mode switch
            {
                RobloxWindowLayoutMode.Grid2x2 => (2, 2),
                RobloxWindowLayoutMode.Grid3x2 => (3, 2),
                RobloxWindowLayoutMode.Grid4x3 => (4, 3),
                _ => (0, 0)
            };

            var (cols, rows) = RobloxWindowService.CalculateOptimalGrid(windowCount, customCols, customRows);
            int tileWidth = Math.Max(160, workArea.Width / Math.Max(1, cols));
            int tileHeight = Math.Max(120, workArea.Height / Math.Max(1, rows));

            for (int i = 0; i < windowCount; i++)
            {
                int col = i % cols;
                int row = i / cols;
                int x = workArea.Left + (col * tileWidth);
                int y = workArea.Top + (row * tileHeight);
                slots.Add(new WindowPlacementSlot(x, y, tileWidth, tileHeight));
            }

            return slots;
        }

        /// <summary>
        /// Arranges all currently running Roblox client windows using the specified layout mode.
        /// </summary>
        public static (int arrangedCount, string description) ArrangeWindows(RobloxWindowLayoutMode mode)
        {
            if (!OperatingSystem.IsWindows())
                return (0, LocalizationService.Get("WindowArranger.WindowsOnly"));

            if (mode != RobloxWindowLayoutMode.CascadeCorner)
            {
                return mode switch
                {
                    RobloxWindowLayoutMode.Grid2x2 => RobloxWindowService.TileWindows(2, 2),
                    RobloxWindowLayoutMode.Grid3x2 => RobloxWindowService.TileWindows(3, 2),
                    RobloxWindowLayoutMode.Grid4x3 => RobloxWindowService.TileWindows(4, 3),
                    _ => RobloxWindowService.TileWindows(0, 0)
                };
            }

            if (RobloxWindowService.AreWindowsHidden)
            {
                RobloxWindowService.ShowAllWindows();
            }

            var runningPids = RobloxProcessService.GetRunningRobloxPids();
            if (runningPids.Count == 0)
            {
                return (0, LocalizationService.Get("WindowArranger.NoClientsRunning"));
            }

            var pidSet = new HashSet<uint>(runningPids.Where(p => p > 0).Select(p => (uint)p));
            var windows = RobloxWindowService.FindWindowsForPids(pidSet).Where(IsWindow).Distinct().ToList();
            if (windows.Count == 0)
            {
                return (0, LocalizationService.Get("WindowArranger.NoWindowsFound"));
            }

            var workArea = RobloxWindowService.GetTargetWorkArea();
            var placements = CalculatePlacements(workArea, windows.Count, RobloxWindowLayoutMode.CascadeCorner);
            int arranged = 0;

            for (int i = 0; i < windows.Count; i++)
            {
                IntPtr hWnd = windows[i];
                var slot = placements[i];
                ShowWindow(hWnd, SW_RESTORE);
                if (SetWindowPos(hWnd, IntPtr.Zero, slot.X, slot.Y, slot.Width, slot.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW))
                {
                    arranged++;
                }
            }

            string desc = LocalizationService.Format("WindowArranger.MinimizeToCorner", arranged);
            return (arranged, desc);
        }

        /// <summary>
        /// Maps live running RobloxPlayerBeta PIDs back to their owning <see cref="Account"/> instances
        /// using cached BrowserTrackerID command-line inspection + LastLaunchedProcessId validation.
        /// </summary>
        public static Dictionary<Account, int> ResolveRunningAccountPids(IEnumerable<Account>? accounts)
        {
            var result = new Dictionary<Account, int>(ReferenceEqualityComparer.Instance);
            if (accounts == null)
                return result;

            var runningPids = RobloxProcessService.GetRunningRobloxPids();
            if (runningPids.Count == 0)
            {
                _pidTrackerCache.Clear();
                return result;
            }

            var liveSet = new HashSet<int>(runningPids);
            foreach (int cachedPid in _pidTrackerCache.Keys)
            {
                if (!liveSet.Contains(cachedPid))
                    _pidTrackerCache.TryRemove(cachedPid, out _);
            }

            var trackerToPid = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < runningPids.Count; i++)
            {
                int pid = runningPids[i];
                if (pid <= 0) continue;

                string tracker = ResolveTrackerForPidWithCacheForTest(pid, ProcessExtensions.GetCommandLineByPid);
                if (!string.IsNullOrEmpty(tracker))
                    trackerToPid.TryAdd(tracker, pid);
            }

            foreach (var acc in accounts)
            {
                if (acc == null) continue;

                int matchedPid = 0;
                if (!string.IsNullOrWhiteSpace(acc.BrowserTrackerID) &&
                    trackerToPid.TryGetValue(acc.BrowserTrackerID, out int byTracker))
                {
                    matchedPid = byTracker;
                    acc.LastLaunchedProcessId = byTracker;
                }
                else if (acc.LastLaunchedProcessId > 0 && liveSet.Contains(acc.LastLaunchedProcessId))
                {
                    // Ensure this PID wasn't claimed by a different BrowserTrackerID
                    if (!_pidTrackerCache.TryGetValue(acc.LastLaunchedProcessId, out string? pidTracker) ||
                        string.IsNullOrEmpty(pidTracker) ||
                        string.IsNullOrWhiteSpace(acc.BrowserTrackerID) ||
                        string.Equals(pidTracker, acc.BrowserTrackerID, StringComparison.Ordinal))
                    {
                        matchedPid = acc.LastLaunchedProcessId;
                    }
                }

                if (matchedPid > 0)
                {
                    result[acc] = matchedPid;
                }
            }

            return result;
        }

        internal static string ResolveTrackerForPidWithCacheForTest(int pid, Func<int, string?> getCommandLineByPid)
        {
            if (pid <= 0) return string.Empty;
            if (_pidTrackerCache.TryGetValue(pid, out string? cached) && !string.IsNullOrEmpty(cached))
                return cached;
            try
            {
                string? cmd = getCommandLineByPid(pid);
                if (!string.IsNullOrEmpty(cmd))
                {
                    var m = BrowserTrackerRegex().Match(cmd);
                    if (m.Success && !string.IsNullOrEmpty(m.Groups[1].Value))
                    {
                        _pidTrackerCache[pid] = m.Groups[1].Value;
                        return m.Groups[1].Value;
                    }
                }
            }
            catch (Exception ex) { AppServices.Logger.Debug("[RobloxWindowArrangerService] Resolve process tracker failed", ex); }
            return string.Empty;
        }

        /// <summary>
        /// Restores and brings the specific Roblox game window belonging to <paramref name="account"/> to the foreground.
        /// </summary>
        public static bool FocusAccountWindow(Account? account)
        {
            if (account == null || !OperatingSystem.IsWindows())
                return false;

            var pidMap = ResolveRunningAccountPids(new[] { account });
            if (!pidMap.TryGetValue(account, out int pid) || pid <= 0)
                return false;

            IntPtr hWnd = RobloxWindowService.FindWindowForPid((uint)pid);
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
                return false;

            if (IsIconic(hWnd) || RobloxWindowService.AreWindowsHidden)
            {
                ShowWindow(hWnd, SW_RESTORE);
            }
            else
            {
                ShowWindow(hWnd, SW_SHOW);
            }

            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
            return true;
        }
    }
}
