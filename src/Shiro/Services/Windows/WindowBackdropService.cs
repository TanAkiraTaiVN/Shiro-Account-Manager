#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Shiro.Services
{
    /// <summary>
    /// Service to apply native Windows 11 Mica / Mica Alt / Acrylic backdrops
    /// and immersive dark titlebar styling to Avalonia Windows.
    /// </summary>
    public static partial class WindowBackdropService
    {
        private const int DWMWA_CLOAK = 13;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWA_MICA_EFFECT = 1029;
        internal const int DarkWindowColorRef = 0x000F0B0B; // RGB #0B0B0F in Win32 COLORREF (0x00BBGGRR)
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

        public static void ApplyDarkAndBackdrop(Window window, BackdropType backdrop = BackdropType.None, bool cloakUntilFirstFrame = false)
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
            if (cloakUntilFirstFrame && !window.IsVisible && TrySetWindowCloaked(hwnd, cloaked: true))
            {
                ScheduleUncloakAfterFirstFrame(window, hwnd);
            }
        }

        private static bool TrySetWindowCloaked(IntPtr hwnd, bool cloaked)
        {
            if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
                return false;

            try
            {
                int value = cloaked ? 1 : 0;
                return DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int)) == 0;
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"DwmSetWindowAttribute(DWMWA_CLOAK={cloaked}) threw: {ex.Message}");
                return false;
            }
        }

        private static void ScheduleUncloakAfterFirstFrame(Window window, IntPtr hwnd)
        {
            DispatcherTimer? failsafeTimer = null;
            FirstFrameUncloakCoordinator? coordinator = null;
            EventHandler? onOpened = null;
            EventHandler? onClosed = null;

            coordinator = new FirstFrameUncloakCoordinator(
                requestFrame: cb => window.RequestAnimationFrame(cb),
                startFailsafeTimer: () =>
                {
                    failsafeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                    failsafeTimer.Tick += (_, _) => coordinator?.OnFailsafeElapsed();
                    failsafeTimer.Start();
                },
                stopFailsafeTimer: () => failsafeTimer?.Stop(),
                uncloakAction: () =>
                {
                    if (onOpened != null) window.Opened -= onOpened;
                    if (onClosed != null) window.Closed -= onClosed;
                    TrySetWindowCloaked(hwnd, cloaked: false);
                });

            onOpened = (_, _) => coordinator.OnOpened();
            onClosed = (_, _) => coordinator.OnClosed();
            window.Opened += onOpened;
            window.Closed += onClosed;
        }

        internal sealed class FirstFrameUncloakCoordinator
        {
            private readonly Action<Action<TimeSpan>> _requestFrame;
            private readonly Action _startFailsafeTimer;
            private readonly Action _stopFailsafeTimer;
            private readonly Action _uncloakAction;
            private int _completed;

            public FirstFrameUncloakCoordinator(
                Action<Action<TimeSpan>> requestFrame,
                Action startFailsafeTimer,
                Action stopFailsafeTimer,
                Action uncloakAction)
            {
                _requestFrame = requestFrame ?? throw new ArgumentNullException(nameof(requestFrame));
                _startFailsafeTimer = startFailsafeTimer ?? throw new ArgumentNullException(nameof(startFailsafeTimer));
                _stopFailsafeTimer = stopFailsafeTimer ?? throw new ArgumentNullException(nameof(stopFailsafeTimer));
                _uncloakAction = uncloakAction ?? throw new ArgumentNullException(nameof(uncloakAction));
            }

            public void OnOpened()
            {
                if (Volatile.Read(ref _completed) != 0)
                    return;

                _startFailsafeTimer();
                _requestFrame(_ =>
                {
                    if (Volatile.Read(ref _completed) != 0)
                        return;

                    _requestFrame(_ => Complete());
                });
            }

            public void OnFailsafeElapsed() => Complete();

            public void OnClosed() => Complete();

            private void Complete()
            {
                if (Interlocked.Exchange(ref _completed, 1) != 0)
                    return;

                _stopFailsafeTimer();
                _uncloakAction();
            }
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
                if (Environment.OSVersion.Version.Major >= 10 && ShouldApplyDarkCaptionColor(build))
                {
                    int captionColor = DarkWindowColorRef;
                    DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
                }

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

        internal static bool ShouldApplyDarkCaptionColor(int osBuild) => osBuild >= 22000;

        internal static bool ShouldApplyLegacyMica(BackdropType backdrop, int osBuild)
            => backdrop != BackdropType.None && osBuild >= 22000 && osBuild < 22621;
    }
}
