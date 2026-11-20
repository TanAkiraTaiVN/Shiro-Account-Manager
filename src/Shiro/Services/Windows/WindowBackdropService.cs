#nullable enable
using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Shiro.Services
{
    /// <summary>
    /// Service to apply native Windows 11 Mica / Mica Alt / Acrylic backdrops
    /// and immersive dark titlebar styling to Avalonia Windows.
    /// </summary>
    public static partial class WindowBackdropService
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWA_MICA_EFFECT = 1029;
        private const uint LR_SHARED = 0x00008000;
        private const int IDI_APPLICATION_RESOURCE_ID = 32512;
        private const int FALLBACK_APP_ICON_RESOURCE_ID = 1;

        private static readonly System.Threading.Lock IconCacheLock = new();
        private static bool s_iconsResolved;
        private static IntPtr s_hIconBig;
        private static IntPtr s_hIconSmall;

        public enum WindowCornerPreference
        {
            Default = 0,
            DoNotRound = 1,
            Round = 2,
            RoundSmall = 3
        }

        public enum BackdropType
        {
            Auto = 0,
            None = 1,
            MainWindow = 2,    // Standard Mica
            TransientWindow = 3, // Acrylic
            TabbedWindow = 4   // Mica Alt (richer dark texture)
        }

        [LibraryImport("dwmapi.dll")]
        private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
        private static partial IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr GetModuleHandle(string? lpModuleName);

        [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
        private static partial IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true)]
        private static partial IntPtr LoadImage(IntPtr hInst, IntPtr name, uint type, int cx, int cy, uint fuLoad);

        public static void ApplyDarkAndBackdrop(Window window, BackdropType backdrop = BackdropType.None)
        {
            if (window is null) return;

            StreamerModeService.RegisterWindow(window);
            if (!OperatingSystem.IsWindows()) return;

            IntPtr hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
            {
                EventHandler? onOpened = null;
                onOpened = (s, e) =>
                {
                    window.Opened -= onOpened;
                    IntPtr openedHwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                    ApplyToHwnd(openedHwnd, backdrop);
                };
                window.Opened += onOpened;
                return;
            }

            ApplyToHwnd(hwnd, backdrop);
        }

        private static void ApplyToHwnd(IntPtr hwnd, BackdropType backdrop)
        {
            if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return;

            try
            {
                // Set Native Window Icon directly to DWM / Taskbar via WM_SETICON (cached shared handles)
                IntPtr hIconBig;
                IntPtr hIconSmall;
                lock (IconCacheLock)
                {
                    if (!s_iconsResolved)
                    {
                        s_iconsResolved = true;
                        IntPtr hModule = GetModuleHandle(null);
                        s_hIconBig = ResolvePreferredIconResourceId(
                            resId => LoadImage(hModule, resId, 1 /* IMAGE_ICON */, 32, 32, LR_SHARED));
                        s_hIconSmall = ResolvePreferredIconResourceId(
                            resId => LoadImage(hModule, resId, 1 /* IMAGE_ICON */, 16, 16, LR_SHARED));

                        if (s_hIconBig == IntPtr.Zero)
                            s_hIconBig = LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION_RESOURCE_ID); // IDI_APPLICATION
                        if (s_hIconSmall == IntPtr.Zero)
                            s_hIconSmall = s_hIconBig;
                    }
                    hIconBig = s_hIconBig;
                    hIconSmall = s_hIconSmall;
                }

                if (hIconBig != IntPtr.Zero)
                {
                    SendMessage(hwnd, 0x0080 /* WM_SETICON */, (IntPtr)1 /* ICON_BIG */, hIconBig);
                }
                if (hIconSmall != IntPtr.Zero)
                {
                    SendMessage(hwnd, 0x0080 /* WM_SETICON */, (IntPtr)0 /* ICON_SMALL */, hIconSmall);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Win32 window icon apply threw: {ex.Message}");
            }

            try
            {
                // Enable Immersive Dark Mode
                int darkMode = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                int build = Environment.OSVersion.Version.Build;
                if (Environment.OSVersion.Version.Major >= 10 && build >= 22000)
                {
                    int roundPreference = (int)WindowCornerPreference.Round;
                    DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundPreference, sizeof(int));
                }

                if (Environment.OSVersion.Version.Major >= 10 && build >= 22621)
                {
                    int backdropValue = (int)backdrop;
                    DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropValue, sizeof(int));
                }
                else if (ShouldApplyLegacyMica(backdrop, build))
                {
                    int micaTrue = 1;
                    DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref micaTrue, sizeof(int));
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"DwmSetWindowAttribute backdrop threw: {ex.Message}");
            }
        }

        internal static IntPtr ResolvePreferredIconResourceId(Func<IntPtr, IntPtr> loader)
        {
            if (loader == null) return IntPtr.Zero;

            IntPtr primary = loader((IntPtr)IDI_APPLICATION_RESOURCE_ID);
            if (primary != IntPtr.Zero)
                return primary;

            return loader((IntPtr)FALLBACK_APP_ICON_RESOURCE_ID);
        }

        internal static bool ShouldApplyLegacyMica(BackdropType backdrop, int osBuild)
            => backdrop != BackdropType.None && osBuild >= 22000 && osBuild < 22621;
    }
}
