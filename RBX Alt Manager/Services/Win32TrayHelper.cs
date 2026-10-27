using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Services
{
    public static partial class Win32TrayHelper
    {
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PostMessage(IntPtr hWnd, uint Msg, int wParam, int lParam);

        [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial IntPtr FindWindow(string lpClassName, string lpWindowName);

        [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string windowTitle);

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
        private static partial bool IsWindowVisible(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        private const uint WM_MOUSEMOVE = 0x0200;

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool EnumWindows(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> lpEnumFunc, IntPtr lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool EnumChildWindows(IntPtr hWndParent, delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> lpEnumFunc, IntPtr lParam);

        [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
        private static unsafe partial int GetClassNameW(IntPtr hWnd, char* lpClassName, int nMaxCount);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static unsafe int EnumTopWindowsCallback(IntPtr hWnd, IntPtr lParam)
        {
            Span<char> buffer = stackalloc char[128];
            int length;
            fixed (char* ptr = buffer)
            {
                length = GetClassNameW(hWnd, ptr, buffer.Length);
            }
            if (length <= 0) return 1;

            ReadOnlySpan<char> cName = buffer.Slice(0, length);
            if (cName.Contains("Tray".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                cName.Contains("Overflow".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                cName.Contains("XamlIsland".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                var targets = (HashSet<IntPtr>)GCHandle.FromIntPtr(lParam).Target;
                targets.Add(hWnd);
                CollectToolbars(hWnd, targets);
            }
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static unsafe int EnumChildWindowsCallback(IntPtr child, IntPtr lParam)
        {
            Span<char> buffer = stackalloc char[128];
            int length;
            fixed (char* ptr = buffer)
            {
                length = GetClassNameW(child, ptr, buffer.Length);
            }
            if (length <= 0) return 1;

            ReadOnlySpan<char> cName = buffer.Slice(0, length);
            if (cName.Equals("ToolbarWindow32".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                cName.Contains("SysPager".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                cName.Contains("TrayNotifyWnd".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                var collection = (HashSet<IntPtr>)GCHandle.FromIntPtr(lParam).Target;
                collection.Add(child);
            }
            return 1;
        }

        private static readonly System.Threading.Lock _refreshTrayLock = new();
        private static CancellationTokenSource _refreshTrayCts;

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
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                        RefreshNotificationArea();

                        // Optional second pass after 1.5s to ensure Explorer finished deregistering dead PIDs
                        await Task.Delay(1500, token).ConfigureAwait(false);
                        RefreshNotificationArea();
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"[QueueRefreshNotificationArea] {ex.Message}");
                    }
                }, token);
            }
        }

        /// <summary>
        /// Cleans up ghost/dead tray icons left in Windows notification area after processes terminate.
        /// Thoroughly enumerates all ToolbarWindow32 in Shell_TrayWnd, Shell_SecondaryTrayWnd,
        /// NotifyIconOverflowWindow, and Windows 11 XAML Island trays, even when the flyout is closed.
        /// </summary>
        public static void RefreshNotificationArea()
        {
            try
            {
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
                    if (hTop != IntPtr.Zero && IsWindowVisible(hTop))
                    {
                        targets.Add(hTop);
                        CollectToolbars(hTop, targets);
                    }
                }

                // 2. Global enumeration fallback to discover any additional secondary tray or overflow containers
                try
                {
                    GCHandle handle = GCHandle.Alloc(targets);
                    try
                    {
                        unsafe { EnumWindows(&EnumTopWindowsCallback, GCHandle.ToIntPtr(handle)); }
                    }
                    finally
                    {
                        handle.Free();
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"EnumWindows tray sweep threw: {ex.Message}");
                }

                // 3. Post WM_MOUSEMOVE across every target toolbar / overflow window (100% silent in background)
                foreach (var target in targets)
                {
                    SweepWindowForDeadIcons(target);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[RefreshNotificationArea] Error during tray sweep: {ex.Message}");
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
                Program.Logger?.Debug($"CollectToolbars threw: {ex.Message}");
            }
        }

        private static void SweepWindowForDeadIcons(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd)) return;

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
                        int lParam = (y << 16) | (x & 0xFFFF);
                        PostMessage(hWnd, WM_MOUSEMOVE, 0, lParam);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"SweepWindowForDeadIcons threw: {ex.Message}");
            }
        }

        public static void ThoroughCleanNotificationArea(bool forceOpenFlyout = false)
        {
            RefreshNotificationArea();
        }
    }
}
