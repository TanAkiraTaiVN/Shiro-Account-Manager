#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    /// <summary>
    /// Intelligent Anti-AFK Service for Roblox.
    /// Prevents Roblox's 20-minute idle kick (Error 268 / Disconnect).
    /// Tracks all active Roblox instances and periodically sends a harmless virtual key tap (Space/Jump).
    /// Preserves user productivity by remembering the user's active window and instantly restoring focus.
    /// </summary>
    public static partial class AntiAfkService
    {
        private static readonly TimeSpan TickPeriod = TimeSpan.FromSeconds(15);
        private static Timer? _timer;
        private static readonly System.Threading.Lock _gate = new();
        private static int _running;

        private sealed class ClientState
        {
            public DateTime NextDueUtc;
            public DateTime? LastSentUtc;
            public int Failures;
        }

        private static readonly ConcurrentDictionary<int, ClientState> _clients = new ConcurrentDictionary<int, ClientState>();

        static AntiAfkService()
        {
            RobloxJobObjectService.ProcessExitedFromJob += static (pid, _) => RemoveClientState(pid);
        }

        internal static bool RemoveClientState(int pid)
        {
            return pid > 0 && _clients.TryRemove(pid, out _);
        }

        private static int _focuslessWarningLogged = 0;

        internal static bool HasLoggedFocuslessAdvisory => Volatile.Read(ref _focuslessWarningLogged) != 0;

        internal static void ResetFocuslessAdvisory()
        {
            Interlocked.Exchange(ref _focuslessWarningLogged, 0);
        }

        internal static bool MaybeLogFocuslessAdvisory(bool allowForegroundFallback)
        {
            if (allowForegroundFallback) return false;
            if (Interlocked.CompareExchange(ref _focuslessWarningLogged, 1, 0) == 0)
            {
                AppServices.Logger?.Warn("[AntiAfk] Notice: Modern Roblox RawInput/DirectInput ignores background PostMessage synthetic input when minimized or unfocused. Enable 'AntiAfkForegroundFallback' if idle disconnects occur.");
                return true;
            }
            return false;
        }

        private static volatile bool _enabled = false;
        private static int _intervalMinutes = 8;
        private static int _tapsSent = 0;
        private static volatile string _status = "Anti-AFK Inactive";

        internal readonly record struct ClientPulseOutcome(bool Succeeded, int Failures, DateTime NextDueUtc);

        internal static DateTime ComputeFailureBackoffUtc(DateTime nowUtc, int failures)
            => nowUtc.AddSeconds(Math.Clamp(120 * (1 << Math.Min(Math.Max(0, failures - 1), 3)), 120, 600));

        internal static ClientPulseOutcome ProcessClientPulseForTest(
            IntPtr hWnd,
            bool isHidden,
            bool isForegroundWindow,
            bool allowFocusSteal,
            DateTime nowUtc,
            int intervalMinutes,
            Func<IntPtr, bool> postPulse,
            Func<IntPtr, bool> sendForegroundSpace,
            int previousFailures)
        {
            int jitterSec = Random.Shared.Next(-60, 120);
            if (isHidden)
            {
                MaybeLogFocuslessAdvisory(false);
                if (postPulse(hWnd))
                    return new(true, 0, ComputeNextDueUtc(nowUtc, intervalMinutes, jitterSec));

                int nextFail = previousFailures + 1;
                return new(false, nextFail, ComputeFailureBackoffUtc(nowUtc, nextFail));
            }

            if (!isForegroundWindow)
            {
                MaybeLogFocuslessAdvisory(allowFocusSteal);
                bool ok = allowFocusSteal ? sendForegroundSpace(hWnd) : postPulse(hWnd);
                if (ok)
                    return new(true, 0, ComputeNextDueUtc(nowUtc, intervalMinutes, jitterSec));

                int nextFail = previousFailures + 1;
                return new(false, nextFail, ComputeFailureBackoffUtc(nowUtc, nextFail));
            }

            if (postPulse(hWnd) || (allowFocusSteal && sendForegroundSpace(hWnd)))
                return new(true, 0, ComputeNextDueUtc(nowUtc, intervalMinutes, jitterSec));

            int failCount = previousFailures + 1;
            return new(false, failCount, ComputeFailureBackoffUtc(nowUtc, failCount));
        }

        public static event Action? StatusChanged;

        public static bool IsEnabled => _enabled;
        public static int TapsSent => _tapsSent;
        public static string StatusText => _status;

        public static void SetConfig(bool enabled, int intervalMinutes = 8)
        {
            ResetFocuslessAdvisory();
            _enabled = enabled;
            _intervalMinutes = Math.Clamp(intervalMinutes, 1, 18);

            foreach (var st in _clients.Values)
                st.NextDueUtc = DateTime.MinValue;

            if (_enabled)
            {
                Start();
                SetStatus($"Anti-AFK Active (Every ~{_intervalMinutes}m)");
            }
            else
            {
                Stop();
                SetStatus("Anti-AFK Inactive");
            }
        }

        public static void Start()
        {
            lock (_gate)
            {
                ResetFocuslessAdvisory();
                _enabled = true;
                if (_timer == null)
                    _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TickPeriod);
                SetStatus($"Anti-AFK Active (Every ~{_intervalMinutes}m)");
            }
        }

        public static void Stop()
        {
            lock (_gate)
            {
                ResetFocuslessAdvisory();
                _enabled = false;
                _timer?.Dispose();
                _timer = null;
                SetStatus("Anti-AFK Inactive");
            }
        }

        public static void Toggle()
        {
            if (_enabled) Stop();
            else Start();
        }

        internal static DateTime ComputeNextDueUtc(DateTime nowUtc, int intervalMinutes, int jitterSeconds)
        {
            int clampedMinutes = Math.Clamp(intervalMinutes, 1, 18);
            int totalSeconds = Math.Max(30, (clampedMinutes * 60) + jitterSeconds);
            return nowUtc.AddSeconds(totalSeconds);
        }

        private static void Tick()
        {
            try
            {
                if (!_enabled) return;

                var now = DateTime.UtcNow;
                var pids = GetRobloxPids();
                var activePidSet = new HashSet<int>(pids);

                // Drop exited clients without allocating LINQ Except/Keys lists
                foreach (var kvp in _clients)
                {
                    if (!activePidSet.Contains(kvp.Key))
                        _clients.TryRemove(kvp.Key, out _);
                }

                var duePids = new List<int>();
                var rand = Random.Shared;

                foreach (int pid in pids)
                {
                    var st = _clients.GetOrAdd(pid, _ => new ClientState { NextDueUtc = DateTime.MinValue });
                    if (st.NextDueUtc == DateTime.MinValue)
                    {
                        // Jitter: interval with -1 to +2 minutes randomness, minimum 30s floor
                        int jitterSec = rand.Next(-60, 120);
                        st.NextDueUtc = ComputeNextDueUtc(now, _intervalMinutes, jitterSec);
                    }
                    else if (st.NextDueUtc <= now)
                    {
                        duePids.Add(pid);
                    }
                }

                if (duePids.Count > 0)
                    _ = ExecutePassAsync(duePids);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Error($"[AntiAfk] Tick error: {ex.Message}");
            }
        }

        internal static async Task<int> ExecutePassAsync(IReadOnlyList<int> pids)
        {
            if (!OperatingSystem.IsWindows())
                return 0;

            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                return 0;

            int sent = 0;
            try
            {
                // 1. Dual-channel WebSocket Keepalive pulse to all connected Lua executors (RAMAccount.lua)
                try
                {
                    KestrelWebServer.Broadcast("anti_afk_pulse", new PulseBroadcastPayload(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"Kestrel anti-afk pulse broadcast failed: {ex.Message}");
                }

                var rand = Random.Shared;
                var pidSet = new HashSet<uint>(pids.Count);
                for (int i = 0; i < pids.Count; i++)
                {
                    int p = pids[i];
                    if (p > 0) pidSet.Add((uint)p);
                }
                var windowMap = RobloxWindowService.FindWindowMapForPids(pidSet);

                // Silent non-disruptive background message injection:
                // Computes the hardware scan code once per pass so DirectInput and Win32 message loops recognize the pulse.
                uint scanCode = MapVirtualKey((uint)VK_F15, 0);
                IntPtr lParamDown = (IntPtr)(1 | (scanCode << 16));
                IntPtr lParamUp = (IntPtr)(1 | (scanCode << 16) | (1 << 30) | (1 << 31));

                foreach (int pid in pids)
                {
                    var st = _clients.GetOrAdd(pid, _ => new ClientState());
                    windowMap.TryGetValue((uint)pid, out IntPtr hWnd);

                    if (hWnd == IntPtr.Zero)
                    {
                        st.NextDueUtc = DateTime.UtcNow.AddMinutes(2);
                        continue;
                    }

                    if (!_enabled) break;
                    bool isHidden = RobloxWindowService.AreWindowsHidden || !IsWindowVisible(hWnd);
                    bool isForegroundWindow = hWnd == GetForegroundWindow();
                    bool allowFocusSteal = AppServices.GeneralSettings.Get<bool>("AntiAfkForegroundFallback", false);

                    bool PulseViaPost(IntPtr target)
                    {
                        bool ok = PostMessage(target, WM_KEYDOWN, (IntPtr)VK_F15, lParamDown) &&
                                  PostMessage(target, WM_KEYUP, (IntPtr)VK_F15, lParamUp);
                        PostMessage(target, WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((10 << 16) | 10));
                        PostMessage(target, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                        return ok;
                    }

                    if (isHidden || (!isForegroundWindow && !allowFocusSteal) || isForegroundWindow)
                    {
                        var outcome = ProcessClientPulseForTest(hWnd, isHidden, isForegroundWindow, allowFocusSteal, DateTime.UtcNow, _intervalMinutes, PulseViaPost, _ => false, st.Failures);
                        if (outcome.Succeeded)
                        {
                            st.LastSentUtc = DateTime.UtcNow;
                            st.Failures = 0;
                            st.NextDueUtc = outcome.NextDueUtc;
                            sent++;
                            Interlocked.Increment(ref _tapsSent);
                            continue;
                        }
                        if (isHidden || !allowFocusSteal)
                        {
                            st.Failures = outcome.Failures;
                            st.NextDueUtc = outcome.NextDueUtc;
                            continue;
                        }
                    }
                    else
                    {
                        MaybeLogFocuslessAdvisory(true);
                    }

                    {
                        IntPtr userWindow = GetForegroundWindow();
                        bool wasMinimized = IsIconic(hWnd);
                        if (wasMinimized) ShowWindow(hWnd, SW_RESTORE);
                        SetForegroundWindow(hWnd);
                        await Task.Delay(80).ConfigureAwait(false);
                        if (GetForegroundWindow() == hWnd)
                        {
                            SendKey(VK_SPACE, keyUp: false);
                            await Task.Delay(40).ConfigureAwait(false);
                            SendKey(VK_SPACE, keyUp: true);
                            st.LastSentUtc = DateTime.UtcNow;
                            st.Failures = 0;
                            st.NextDueUtc = ComputeNextDueUtc(DateTime.UtcNow, _intervalMinutes, rand.Next(-60, 120));
                            sent++;
                            Interlocked.Increment(ref _tapsSent);
                        }
                        else
                        {
                            st.Failures++;
                            st.NextDueUtc = ComputeFailureBackoffUtc(DateTime.UtcNow, st.Failures);
                        }
                        if (wasMinimized) ShowWindow(hWnd, SW_MINIMIZE);
                        if (userWindow != IntPtr.Zero && userWindow != GetForegroundWindow()) SetForegroundWindow(userWindow);
                    }
                }

                if (sent > 0 && _enabled)
                {
                    AppServices.Logger?.Info($"[AntiAfk] Kept alive {sent} Roblox client(s) via silent background pulse. Total keepalives: {_tapsSent}.");
                    SetStatus($"Anti-AFK Active ({pids.Count} clients - {_tapsSent} keepalives sent)");
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Error($"[AntiAfk] Pass error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
            return sent;
        }

        private static List<int> GetRobloxPids()
        {
            return RobloxProcessService.GetRunningRobloxPids();
        }

        private static void SetStatus(string text)
        {
            if (_status == text) return;
            _status = text;
            try { StatusChanged?.Invoke(); }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[StatusChanged] {ex.Message}");
            }
        }

        // Win32 Interop
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const int VK_F15 = 0x7E;
        private const ushort VK_SPACE = 0x20;
        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYDOWN = 0x0000;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const int SW_RESTORE = 9;
        private const int SW_MINIMIZE = 6;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT
        {
            [FieldOffset(0)]
            public uint type;
            [FieldOffset(8)]
            public MOUSEINPUT mi;
            [FieldOffset(8)]
            public KEYBDINPUT ki;
            [FieldOffset(8)]
            public HARDWAREINPUT hi;
        }

        [LibraryImport("user32.dll", SetLastError = true)]
        private static unsafe partial uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

        private static unsafe void SendKey(ushort virtualKey, bool keyUp)
        {
            if (!OperatingSystem.IsWindows()) return;

            ushort scanCode = (ushort)MapVirtualKey(virtualKey, 0);
            INPUT input = default;
            input.type = INPUT_KEYBOARD;
            input.ki.wVk = virtualKey;
            input.ki.wScan = scanCode;
            input.ki.dwFlags = keyUp ? KEYEVENTF_KEYUP : KEYEVENTF_KEYDOWN;
            input.ki.time = 0;
            input.ki.dwExtraInfo = UIntPtr.Zero;

            SendInput(1, &input, sizeof(INPUT));
        }

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsIconic(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const uint WM_NULL = 0x0000;

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(IntPtr hWnd);

        [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
        private static partial uint MapVirtualKey(uint uCode, uint uMapType);
    }
}
