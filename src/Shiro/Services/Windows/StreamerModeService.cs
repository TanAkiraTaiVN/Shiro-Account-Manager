#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Controls;

namespace Shiro.Services
{
    /// <summary>
    /// Privacy and Streamer Mode Protection Service.
    /// Protects user accounts, cookies, Robux balances, and job IDs from being leaked on OBS, Discord, or Twitch streams.
    /// Utilizes Windows DWM SetWindowDisplayAffinity (WDA_EXCLUDEFROMCAPTURE) to make Shiro windows invisible to screen capture.
    /// </summary>
    public static partial class StreamerModeService
    {
        private const uint WDA_NONE = 0x00000000;
        private const uint WDA_MONITOR = 0x00000001;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        private static readonly System.Threading.Lock _lock = new();
        private static readonly List<WeakReference<Window>> _registeredWindows = new();
        private static readonly EventHandler s_onWindowOpened = OnRegisteredWindowOpened;
        private static readonly EventHandler s_onWindowClosed = OnRegisteredWindowClosed;
        private static Timer? _detectionTimer;
        private static volatile bool _isEnabled;
        private static volatile bool _autoDetectEnabled = false;
        private static volatile bool _isStreamingAppDetected;
        private static bool _initialized;

        private static readonly string[] StreamingProcessNames =
        [
            "obs64",
            "obs32",
            "Streamlabs OBS",
            "TwitchStudio",
            "XSplit.Core",
            "PrismLiveStudio",
            "Action",
            "bdcam"
        ];

        public static event Action<bool>? StateChanged;

        public static bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                lock (_lock)
                {
                    if (_isEnabled == value) return;
                    _isEnabled = value;
                    AppServices.GeneralSettings.Set(AppSettingsKeys.General.StreamerModeEnabled, value ? "true" : "false");
                    AppServices.GeneralSettings.Set(AppSettingsKeys.General.HideUsernames, value ? "true" : "false");
                }
                UpdateAffinityState();
            }
        }

        public static bool AutoDetectEnabled
        {
            get => _autoDetectEnabled;
            set
            {
                lock (_lock)
                {
                    if (_autoDetectEnabled == value) return;
                    _autoDetectEnabled = value;
                    AppServices.GeneralSettings.Set(AppSettingsKeys.General.StreamerModeAutoDetect, value ? "true" : "false");

                    if (value)
                    {
                        _detectionTimer ??= new Timer(CheckStreamingProcesses, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
                    }
                    else
                    {
                        _detectionTimer?.Dispose();
                        _detectionTimer = null;
                        _isStreamingAppDetected = false;
                    }
                }

                if (!value)
                {
                    UpdateAffinityState();
                }
            }
        }

        public static bool IsStreamingAppDetected => _isStreamingAppDetected;

        /// <summary>
        /// Indicates whether privacy protection is actively hiding window contents from screen capture.
        /// </summary>
        public static bool IsActive => _isEnabled || (_autoDetectEnabled && _isStreamingAppDetected);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        internal static void InitializeForTest()
        {
            lock (_lock)
            {
                _initialized = false;
            }
            Initialize();
        }

        internal static bool ShouldApplyActiveOnOpened(bool capturedActive) => IsActive;

        public static void Initialize()
        {
            bool shouldUpdateAffinity;
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;

                _isEnabled = AppServices.GeneralSettings.Get<bool>(AppSettingsKeys.General.StreamerModeEnabled, false);
                _autoDetectEnabled = AppServices.GeneralSettings.Get<bool>(AppSettingsKeys.General.StreamerModeAutoDetect, false);
                shouldUpdateAffinity = _isEnabled || _autoDetectEnabled;

                if (_autoDetectEnabled)
                {
                    _detectionTimer = new Timer(CheckStreamingProcesses, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
                }
            }
            if (shouldUpdateAffinity)
                UpdateAffinityState();
        }

        public static void RegisterWindow(Window window)
        {
            if (window is null) return;

            lock (_lock)
            {
                bool alreadyRegistered = false;
                for (int i = _registeredWindows.Count - 1; i >= 0; i--)
                {
                    if (!_registeredWindows[i].TryGetTarget(out Window? existing))
                        _registeredWindows.RemoveAt(i);
                    else if (ReferenceEquals(existing, window))
                        alreadyRegistered = true;
                }

                if (!alreadyRegistered)
                    _registeredWindows.Add(new WeakReference<Window>(window));
            }

            window.Closed -= s_onWindowClosed;
            window.Closed += s_onWindowClosed;
            ApplyAffinityToWindow(window, IsActive);
        }

        public static void UnregisterWindow(Window? window)
        {
            if (window is null) return;

            window.Opened -= s_onWindowOpened;
            window.Closed -= s_onWindowClosed;

            lock (_lock)
            {
                for (int i = _registeredWindows.Count - 1; i >= 0; i--)
                {
                    if (!_registeredWindows[i].TryGetTarget(out Window? existing) || ReferenceEquals(existing, window))
                        _registeredWindows.RemoveAt(i);
                }
            }
        }

        private static void OnRegisteredWindowOpened(object? sender, EventArgs e)
        {
            if (sender is Window window)
            {
                window.Opened -= s_onWindowOpened;
                IntPtr openedHwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                ApplyToHwnd(openedHwnd, ShouldApplyActiveOnOpened(IsActive));
            }
        }

        private static void OnRegisteredWindowClosed(object? sender, EventArgs e)
        {
            if (sender is Window window)
            {
                UnregisterWindow(window);
            }
        }

        internal static bool MatchesStreamingProcessName(ReadOnlySpan<char> exeFileOrProcessName)
        {
            ReadOnlySpan<char> name = exeFileOrProcessName.Trim();
            if (name.IsEmpty)
                return false;

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];

            if (name.IsEmpty)
                return false;

            foreach (string target in StreamingProcessNames)
            {
                if (name.Equals(target.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        internal static unsafe bool HasAnyStreamingProcessRunning()
        {
            if (!OperatingSystem.IsWindows())
                return false;

            IntPtr snapshot = Win32NativeHelper.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == INVALID_HANDLE_VALUE)
                return false;

            try
            {
                Win32NativeHelper.PROCESSENTRY32W entry = default;
                entry.dwSize = (uint)sizeof(Win32NativeHelper.PROCESSENTRY32W);

                if (!Win32NativeHelper.Process32FirstW(snapshot, &entry))
                    return false;

                do
                {
                    ReadOnlySpan<char> exeSpan = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(entry.szExeFile);
                    if (MatchesStreamingProcessName(exeSpan))
                        return true;
                }
                while (Win32NativeHelper.Process32NextW(snapshot, &entry));

                return false;
            }
            finally
            {
                Win32NativeHelper.CloseHandle(snapshot);
            }
        }

        private static void CheckStreamingProcesses(object? state)
        {
            if (!_autoDetectEnabled) return;

            bool detected = false;
            try
            {
                detected = HasAnyStreamingProcessRunning();
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[StreamerModeService] Process check failed: {ex.Message}");
            }

            if (_isStreamingAppDetected != detected)
            {
                _isStreamingAppDetected = detected;
                AppServices.Logger.Info($"[StreamerModeService] Streaming software active: {detected}");
                UpdateAffinityState();
            }
        }

        private static void UpdateAffinityState()
        {
            bool active = IsActive;
            List<Window> targets = new();

            lock (_lock)
            {
                _registeredWindows.RemoveAll(wr => !wr.TryGetTarget(out _));
                foreach (var wr in _registeredWindows)
                {
                    if (wr.TryGetTarget(out Window? win) && win != null)
                    {
                        targets.Add(win);
                    }
                }
            }

            AppDispatcher.Post(() =>
            {
                foreach (Window win in targets)
                {
                    ApplyAffinityToWindow(win, active);
                }

                try
                {
                    StateChanged?.Invoke(active);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Debug($"[StreamerModeService] StateChanged handler exception: {ex.Message}");
                }
            });
        }

        private static void ApplyAffinityToWindow(Window window, bool active)
        {
            if (!OperatingSystem.IsWindows()) return;

            IntPtr hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
            {
                window.Opened -= s_onWindowOpened;
                window.Opened += s_onWindowOpened;
                return;
            }

            ApplyToHwnd(hwnd, active);
        }

        private static void ApplyToHwnd(IntPtr hwnd, bool active)
        {
            if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return;

            try
            {
                uint affinity = active ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE;
                if (!SetWindowDisplayAffinity(hwnd, affinity) && active)
                {
                    // Fallback to WDA_MONITOR if WDA_EXCLUDEFROMCAPTURE is not supported
                    SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[StreamerModeService] Failed to set window affinity: {ex.Message}");
            }
        }

        /// <summary>
        /// Masks a username to protect identity when Streamer Mode is active (e.g. "RobloxMaster" -> "R**********r").
        /// </summary>
        public static string MaskUsername(string? username)
        {
            if (string.IsNullOrEmpty(username)) return string.Empty;
            if (username.Length <= 2) return "**";
            return string.Create(username.Length, username, static (span, src) =>
            {
                span[0] = src[0];
                span[1..^1].Fill('*');
                span[^1] = src[^1];
            });
        }

        /// <summary>
        /// Masks a Robux balance string when Streamer Mode is active.
        /// </summary>
        public static string MaskRobux(long robux) => "*** R$";

        /// <summary>
        /// Masks a Roblox Game Job ID.
        /// </summary>
        public static string MaskJobId(string? jobId) => "***-***";

        /// <summary>
        /// Masks a Roblox numeric User ID when Streamer Mode is active.
        /// </summary>
        public static string MaskUserId(long userId) => userId > 0 ? "***" : "0";

        public static bool ShouldMaskUsernames(ISettingsService? settings = null)
            => IsActive && ((settings ?? AppServices.GeneralSettings)?.Get<bool>(AppSettingsKeys.General.StreamerModeHideUsernames, true) ?? true);

        public static bool ShouldMaskRobux(ISettingsService? settings = null)
            => IsActive && ((settings ?? AppServices.GeneralSettings)?.Get<bool>(AppSettingsKeys.General.StreamerModeHideRobux, true) ?? true);

        public static string GetDisplayUsername(string? username, ISettingsService? settings = null)
            => ShouldMaskUsernames(settings) ? MaskUsername(username) : (username ?? string.Empty);

        public static string GetDisplayAlias(string? alias, ISettingsService? settings = null)
        {
            if (string.IsNullOrWhiteSpace(alias)) return string.Empty;
            return ShouldMaskUsernames(settings) ? MaskUsername(alias) : alias;
        }

        public static string GetDisplayUserId(long userId, ISettingsService? settings = null)
            => userId > 0 && ShouldMaskUsernames(settings)
                ? MaskUserId(userId)
                : userId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Stops background streaming-app detection and releases timer resources during shutdown.
        /// </summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                _detectionTimer?.Dispose();
                _detectionTimer = null;
                _initialized = false;
                _isStreamingAppDetected = false;
            }
        }
    }
}
