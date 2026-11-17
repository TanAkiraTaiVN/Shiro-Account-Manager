#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Shiro.Services
{
    public static partial class MemoryOptimizer
    {
        private const uint QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x00000002;
        private const int ProcessMemoryPriority = 39;
        private const uint MEMORY_PRIORITY_LOW = 2;
        private const uint MEMORY_PRIORITY_NORMAL = 5;

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_PRIORITY_INFORMATION
        {
            public uint MemoryPriority;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetProcessWorkingSetSizeEx(
            IntPtr hProcess,
            nint dwMinimumWorkingSetSize,
            nint dwMaximumWorkingSetSize,
            uint Flags);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetProcessInformation(
            IntPtr hProcess,
            int processInformationClass,
            ref MEMORY_PRIORITY_INFORMATION processInformation,
            uint processInformationSize);

        internal static bool ShouldApplyBackgroundFocusThrottle(int runningClientCount)
            => runningClientCount > 1;

        internal static uint TryGetForegroundWindowPid(Func<IntPtr> getForegroundWindow, Func<IntPtr, uint> getWindowPid)
        {
            try
            {
                IntPtr hForeground = getForegroundWindow();
                return hForeground != IntPtr.Zero ? getWindowPid(hForeground) : 0;
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"GetForegroundWindow thread pid lookup threw: {ex.Message}");
                return 0;
            }
        }

        internal static uint ComputeTargetPriorityClass(bool isForeground, bool throttlerEnabled, int targetBackgroundFps)
        {
            if (isForeground)
                return RobloxProcessOptimizer.NORMAL_PRIORITY_CLASS;

            if (throttlerEnabled && targetBackgroundFps <= 15)
                return RobloxBackgroundThrottlerService.IDLE_PRIORITY_CLASS;

            return RobloxProcessOptimizer.BELOW_NORMAL_PRIORITY_CLASS;
        }

        internal static bool SetProcessMemoryPriorityNative(IntPtr hProcess, bool isForeground)
        {
            try
            {
                var info = new MEMORY_PRIORITY_INFORMATION
                {
                    MemoryPriority = isForeground ? MEMORY_PRIORITY_NORMAL : MEMORY_PRIORITY_LOW
                };
                return SetProcessInformation(
                    hProcess,
                    ProcessMemoryPriority,
                    ref info,
                    (uint)Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>());
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[MemoryOptimizer] SetProcessMemoryPriorityNative failed: {ex.Message}");
                return false;
            }
        }

        internal static bool SoftRelaxWorkingSetNative(IntPtr hProcess)
        {
            try
            {
                return SetProcessWorkingSetSizeEx(hProcess, -1, -1, QUOTA_LIMITS_HARDWS_MIN_DISABLE);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[MemoryOptimizer] SoftRelaxWorkingSetNative failed: {ex.Message}");
                return false;
            }
        }

        internal static bool IsAggressiveWorkingSetTrimEnabled()
        {
            if (AppServices.GeneralSettings.Exists(AppSettingsKeys.General.AggressiveRamTrimming))
                return AppServices.GeneralSettings.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming, false);
            if (AppServices.GeneralSettings.Exists("AllowEmptyWorkingSetTrimming"))
                return AppServices.GeneralSettings.Get<bool>("AllowEmptyWorkingSetTrimming", false);
            return false;
        }

        internal static void ResetStatsForTests()
        {
            Interlocked.Exchange(ref _totalBytesSaved, 0);
            _lastFullTrim = DateTime.MinValue;
        }
    }
}
