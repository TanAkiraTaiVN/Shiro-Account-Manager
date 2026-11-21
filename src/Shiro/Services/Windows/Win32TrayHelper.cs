#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public static partial class Win32TrayHelper
    {
        private sealed class TrayEnumState
        {
            public required HashSet<IntPtr> Targets;
            public required HashSet<uint> ExplorerPids;
            public required bool IncludeHiddenOverflow;
        }

        public static bool PostMessage(IntPtr hWnd, uint Msg, nuint wParam, nint lParam) =>
            Win32NativeHelper.PostMessageW(hWnd, Msg, unchecked((IntPtr)(nint)wParam), lParam);

        [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        private const uint WM_MOUSEMOVE = 0x0200;

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool EnumChildWindows(IntPtr hWndParent, delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> lpEnumFunc, IntPtr lParam);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static unsafe int EnumTopWindowsCallback(IntPtr hWnd, IntPtr lParam)
        {
            try
            {
                return EnumTopWindowsCallbackCore(hWnd, lParam);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"EnumTopWindowsCallback failed: {ex.Message}");
                return 1;
            }
        }

        internal static bool IsAllowedTrayClass(ReadOnlySpan<char> className)
        {
            if (className.IsEmpty) return false;

            return className.Equals("Shell_TrayWnd".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                   className.Equals("Shell_SecondaryTrayWnd".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                   className.Equals("NotifyIconOverflowWindow".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                   className.Equals("TopLevelWindowForOverflowXamlIsland".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                   className.Equals("ToolbarWindow32".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                   className.Equals("Windows.UI.Composition.DesktopWindowContentBridge".AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOverflowTrayClass(ReadOnlySpan<char> className) =>
            className.Equals("NotifyIconOverflowWindow".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            className.Equals("TopLevelWindowForOverflowXamlIsland".AsSpan(), StringComparison.OrdinalIgnoreCase);

        private static bool IsExplorerWindow(IntPtr hWnd, HashSet<uint> explorerPids)
        {
            if (hWnd == IntPtr.Zero || explorerPids.Count == 0) return false;
            Win32NativeHelper.GetWindowThreadProcessId(hWnd, out uint pid);
            return pid != 0 && explorerPids.Contains(pid);
        }

        private static HashSet<uint> GetExplorerPids()
        {
            var result = new HashSet<uint>();
            if (!OperatingSystem.IsWindows()) return result;

            try
            {
                foreach (var proc in Process.GetProcessesByName("explorer"))
                {
                    using (proc)
                    {
                        if (proc.Id > 0)
                            result.Add((uint)proc.Id);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                AppServices.Logger?.Debug($"GetExplorerPids failed: {ex.Message}");
            }

            return result;
        }

        internal static unsafe int EnumTopWindowsCallbackCore(IntPtr hWnd, IntPtr lParam)
        {
            if (lParam == IntPtr.Zero) return 1;
            Span<char> buffer = stackalloc char[128];
            int length;
            fixed (char* ptr = buffer)
            {
                length = Win32NativeHelper.GetClassNameW(hWnd, ptr, buffer.Length);
            }
            if (length <= 0) return 1;

            ReadOnlySpan<char> cName = buffer.Slice(0, length);
            if (!IsAllowedTrayClass(cName))
                return 1;

            if (GCHandle.FromIntPtr(lParam).Target is TrayEnumState state)
            {
                if (!IsExplorerWindow(hWnd, state.ExplorerPids))
                    return 1;
                if (!Win32NativeHelper.IsWindowVisible(hWnd) && !(state.IncludeHiddenOverflow && IsOverflowTrayClass(cName)))
                    return 1;

                state.Targets.Add(hWnd);
                CollectToolbars(hWnd, state.Targets);
            }
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static unsafe int EnumChildWindowsCallback(IntPtr child, IntPtr lParam)
        {
            try
            {
                return EnumChildWindowsCallbackCore(child, lParam);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"EnumChildWindowsCallback failed: {ex.Message}");
                return 1;
            }
        }

        internal static unsafe int EnumChildWindowsCallbackCore(IntPtr child, IntPtr lParam)
        {
            if (lParam == IntPtr.Zero) return 1;
            Span<char> buffer = stackalloc char[128];
            int length;
            fixed (char* ptr = buffer)
            {
                length = Win32NativeHelper.GetClassNameW(child, ptr, buffer.Length);
            }
            if (length <= 0) return 1;

            ReadOnlySpan<char> cName = buffer.Slice(0, length);
            if (IsTrayNotificationChildClass(cName))
            {
                if (GCHandle.FromIntPtr(lParam).Target is HashSet<IntPtr> collection)
                {
                    collection.Add(child);
                }
            }
            return 1;
        }

        internal static bool IsTrayNotificationChildClass(ReadOnlySpan<char> className)
        {
            if (className.IsEmpty) return false;

            return className.Equals("ToolbarWindow32".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("SysPager".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("TrayNotifyWnd".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("DesktopWindowContentBridge".AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        private static readonly System.Threading.Lock _refreshTrayLock = new();
        private static CancellationTokenSource? _refreshTrayCts;

        /// <summary>
        /// Schedules a debounced refresh of the system notification area.
        /// If multiple processes exit within a short window, requests are coalesced into a single sweep,
        /// followed by a second sweep after 1.5 seconds to capture any delayed Explorer cleanup.
        /// </summary>
        public static void QueueRefreshNotificationArea(int delayMs = 350)
        {
            lock (_refreshTrayLock)
            {
                _refreshTrayCts?.Cancel();
                _refreshTrayCts?.Dispose();
                _refreshTrayCts = new CancellationTokenSource();
                var token = _refreshTrayCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(Math.Max(0, delayMs), token).ConfigureAwait(false);
                        RefreshNotificationArea();

                        // Optional second pass after 1.5s to ensure Explorer finished deregistering dead PIDs
                        await Task.Delay(1500, token).ConfigureAwait(false);
                        RefreshNotificationArea();
                    }
                    catch (OperationCanceledException) { /* canceled */ }
                    catch (Exception ex)
                    {
                        AppServices.Logger?.Debug($"[QueueRefreshNotificationArea] {ex.Message}");
                    }
                }, token);
            }
        }

        public static void CancelPendingRefresh()
        {
            lock (_refreshTrayLock)
            {
                if (_refreshTrayCts == null)
                    return;

                try { _refreshTrayCts.Cancel(); }
                catch (ObjectDisposedException) { /* already disposed */ }

                try { _refreshTrayCts.Dispose(); }
                catch (ObjectDisposedException) { /* already disposed */ }

                _refreshTrayCts = null;
            }
        }

        /// <summary>
        /// Cleans up ghost/dead tray icons left in Windows notification area after processes terminate.
        /// Thoroughly enumerates all ToolbarWindow32 in Shell_TrayWnd, Shell_SecondaryTrayWnd,
        /// NotifyIconOverflowWindow, and Windows 11 XAML Island trays.
        /// </summary>
        public static void RefreshNotificationArea() => RefreshNotificationArea(includeHiddenOverflow: false);

        /// <summary>
        /// Cleans up ghost/dead tray icons left in Windows notification area after processes terminate.
        /// When includeHiddenOverflow is true, sweeps hidden overflow flyout windows without popping open UI.
        /// </summary>
        public static void RefreshNotificationArea(bool includeHiddenOverflow)
        {
            try
            {
                var explorerPids = GetExplorerPids();
                if (explorerPids.Count == 0)
                    return;

                var targets = new HashSet<IntPtr>();

                // 1. Known top-level window classes that hold notification icons
                string[] topLevelClasses = new[]
                {
                    "Shell_TrayWnd",
                    "Shell_SecondaryTrayWnd",
                    "NotifyIconOverflowWindow",
                    "TopLevelWindowForOverflowXamlIsland"
                };

                foreach (var cls in topLevelClasses)
                {
                    IntPtr hTop = FindWindow(cls, null);
                    if (hTop != IntPtr.Zero && IsExplorerWindow(hTop, explorerPids))
                    {
                        bool isOverflow = IsOverflowTrayClass(cls.AsSpan());

                        if (Win32NativeHelper.IsWindowVisible(hTop) || (includeHiddenOverflow && isOverflow))
                        {
                            targets.Add(hTop);
                            CollectToolbars(hTop, targets);
                        }
                    }
                }

                // 2. Global enumeration fallback to discover any additional secondary tray or overflow containers
                try
                {
                    var state = new TrayEnumState
                    {
                        Targets = targets,
                        ExplorerPids = explorerPids,
                        IncludeHiddenOverflow = includeHiddenOverflow
                    };
                    GCHandle handle = GCHandle.Alloc(state);
                    try
                    {
                        unsafe { Win32NativeHelper.EnumWindows(&EnumTopWindowsCallback, GCHandle.ToIntPtr(handle)); }
                    }
                    finally
                    {
                        handle.Free();
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"EnumWindows tray sweep threw: {ex.Message}");
                }

                // 3. Post WM_MOUSEMOVE across every target toolbar / overflow window (100% silent in background)
                foreach (var target in targets)
                {
                    SweepWindowForDeadIcons(target, allowHidden: includeHiddenOverflow);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[RefreshNotificationArea] Error during tray sweep: {ex.Message}");
            }
        }

        private static void CollectToolbars(IntPtr parent, HashSet<IntPtr> collection)
        {
            if (parent == IntPtr.Zero) return;
            try
            {
                GCHandle handle = GCHandle.Alloc(collection);
                try
                {
                    unsafe { EnumChildWindows(parent, &EnumChildWindowsCallback, GCHandle.ToIntPtr(handle)); }
                }
                finally
                {
                    handle.Free();
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"CollectToolbars threw: {ex.Message}");
            }
        }

        private static void SweepWindowForDeadIcons(IntPtr hWnd, bool allowHidden = false)
        {
            if (hWnd == IntPtr.Zero) return;
            if (!allowHidden && !Win32NativeHelper.IsWindowVisible(hWnd)) return;

            try
            {
                if (!GetClientRect(hWnd, out RECT rect)) return;

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                if (width <= 0 || height <= 0) return;

                // Step size of 12px sweeps across icons without spamming excessive messages
                int step = 12;
                for (int y = 4; y < height; y += step)
                {
                    for (int x = 4; x < width; x += step)
                    {
                        nint lParam = (nint)(((uint)y << 16) | ((uint)x & 0xFFFFu));
                        PostMessage(hWnd, WM_MOUSEMOVE, 0, lParam);
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"SweepWindowForDeadIcons threw: {ex.Message}");
            }
        }

        public static void ThoroughCleanNotificationArea(bool forceOpenFlyout = false)
        {
            RefreshNotificationArea(includeHiddenOverflow: forceOpenFlyout);
        }
    }
}
