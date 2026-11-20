#nullable enable
using System;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Shiro.Services
{
    /// <summary>
    /// Shared Win32 process creation-time identity and main-window eligibility helpers
    /// used to guard process caches and window lookups against OS PID recycling.
    /// </summary>
    internal static partial class RobloxProcessIdentityHelper
    {
        [GeneratedRegex(@"\-b (\d+)")]
        internal static partial Regex BrowserTrackerArgRegex();

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const uint WS_CHILD = 0x40000000u;
        private const uint WS_EX_TOOLWINDOW = 0x00000080u;

        internal static bool IsCreationMatch(long expectedTicks, long actualTicks)
            => expectedTicks > 0 && actualTicks > 0 && expectedTicks == actualTicks;

        internal static bool TryGetProcessCreationTicks(int pid, out long creationTicks)
        {
            creationTicks = 0;
            if (pid <= 0 || !OperatingSystem.IsWindows())
                return false;

            using SafeProcessHandle hProcess = Win32NativeHelper.OpenSafeProcess(PROCESS_QUERY_LIMITED_INFORMATION, pid);
            if (hProcess.IsInvalid)
                return false;

            if (Win32NativeHelper.GetProcessTimes(hProcess.DangerousGetHandle(), out long created, out _, out _, out _) && created > 0)
            {
                creationTicks = created;
                return true;
            }

            return false;
        }

        internal static bool IsSameProcessInstance(int pid, long expectedCreationTicks, out long currentTicks)
        {
            if (!TryGetProcessCreationTicks(pid, out currentTicks))
                return false;

            return IsCreationMatch(expectedCreationTicks, currentTicks);
        }

        internal static bool IsExcludedUtilityWindowClass(ReadOnlySpan<char> className)
        {
            if (className.IsEmpty)
                return false;

            return className.Equals("IME", StringComparison.OrdinalIgnoreCase) ||
                   className.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase) ||
                   className.Equals("Default IME", StringComparison.OrdinalIgnoreCase) ||
                   className.StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ShouldSelectAsMainWindow(uint style, uint exStyle, ReadOnlySpan<char> className)
        {
            if ((style & WS_CHILD) != 0 || (exStyle & WS_EX_TOOLWINDOW) != 0)
                return false;

            return !IsExcludedUtilityWindowClass(className);
        }

        internal static unsafe bool IsSelectableMainWindow(IntPtr hWnd)
        {
            if (!OperatingSystem.IsWindows() || hWnd == IntPtr.Zero)
                return false;

            uint style = unchecked((uint)(long)Win32NativeHelper.GetWindowLongPtrW(hWnd, GWL_STYLE));
            uint exStyle = unchecked((uint)(long)Win32NativeHelper.GetWindowLongPtrW(hWnd, GWL_EXSTYLE));
            char* buf = stackalloc char[256];
            int len = Win32NativeHelper.GetClassNameW(hWnd, buf, 256);
            if (len <= 0)
                return false;

            return ShouldSelectAsMainWindow(style, exStyle, new ReadOnlySpan<char>(buf, len));
        }
    }
}
