#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Controls;
using Shiro.Classes;

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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private unsafe struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public nuint th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            public fixed char szExeFile[260];
        }

        private static readonly System.Threading.Lock _lock = new();
        private static readonly List<WeakReference<Window>> _registeredWindows = new();
        private static Timer? _detectionTimer;
        private static bool _isEnabled;
        private static bool _autoDetectEnabled = false;
        private static bool _isStreamingAppDetected;
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

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool Process32FirstW(IntPtr hSnapshot, PROCESSENTRY32W* lppe);

        [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool Process32NextW(IntPtr hSnapshot, PROCESSENTRY32W* lppe);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr hObject);

        public static void Initialize()
        {
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;

                _isEnabled = AppServices.GeneralSettings.Get<bool>(AppSettingsKeys.General.StreamerModeEnabled, false);
                _autoDetectEnabled = AppServices.GeneralSettings.Get<bool>(AppSettingsKeys.General.StreamerModeAutoDetect, false);

                if (_autoDetectEnabled)
                {
                    _detectionTimer = new Timer(CheckStreamingProcesses, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
                }
            }
        }

        public static void RegisterWindow(Window window)
        {
            if (window is null) return;

            lock (_lock)
            {
                _registeredWindows.RemoveAll(wr => !wr.TryGetTarget(out _));
                _registeredWindows.Add(new WeakReference<Window>(window));
            }

            ApplyAffinityToWindow(window, IsActive);
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

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == INVALID_HANDLE_VALUE)
                return false;

            try
            {
                PROCESSENTRY32W entry = default;
                entry.dwSize = (uint)sizeof(PROCESSENTRY32W);

                if (!Process32FirstW(snapshot, &entry))
                    return false;

                do
                {
                    ReadOnlySpan<char> exeSpan = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(entry.szExeFile);
                    if (MatchesStreamingProcessName(exeSpan))
                        return true;
                }
                while (Process32NextW(snapshot, &entry));

                return false;
            }
            finally
            {
                CloseHandle(snapshot);
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
                EventHandler? openedHandler = null;
                openedHandler = (s, e) =>
                {
                    window.Opened -= openedHandler;
                    IntPtr openedHwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                    ApplyToHwnd(openedHwnd, active);
                };
                window.Opened += openedHandler;
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
    }
}
