#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Shiro.Services
{
    /// <summary>
    /// Listens to OS foreground window transitions in real time via Win32 SetWinEventHook(EVENT_SYSTEM_FOREGROUND).
    /// Must be started on a thread with an active Windows message loop (e.g., the Avalonia UI thread).
    /// Eliminates polling latency on Alt+Tab for Smart Mute and foreground/background CPU priority switching.
    /// </summary>
    public static partial class ForegroundWindowService
    {
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        internal const uint WinEventHookFlags = WINEVENT_OUTOFCONTEXT;

        private delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime);

        // Rooted static delegate prevents garbage collection while the native WinEvent hook is installed.
        private static readonly WinEventDelegate _winEventProc = OnWinEvent;
        private static readonly System.Threading.Lock _sync = new();
        private static IntPtr _hookHandle = IntPtr.Zero;
        private static uint _lastForegroundPid;
        private static long _transitionSequence;

        public static bool IsHookActive => Volatile.Read(ref _hookHandle) != IntPtr.Zero;

        public static uint LastForegroundProcessId => Volatile.Read(ref _lastForegroundPid);

        public static event Action<IntPtr, uint>? ForegroundProcessChanged;

        [LibraryImport("user32.dll")]
        private static partial IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnhookWinEvent(IntPtr hWinEventHook);

        public static bool Start()
        {
            if (!OperatingSystem.IsWindows()) return false;

            lock (_sync)
            {
                if (_hookHandle != IntPtr.Zero)
                    return true;

                try
                {
                    _hookHandle = SetWinEventHook(
                        EVENT_SYSTEM_FOREGROUND,
                        EVENT_SYSTEM_FOREGROUND,
                        IntPtr.Zero,
                        _winEventProc,
                        0,
                        0,
                        WinEventHookFlags);

                    if (_hookHandle != IntPtr.Zero)
                    {
                        IntPtr currentFg = Win32NativeHelper.GetForegroundWindow();
                        if (currentFg != IntPtr.Zero)
                        {
                            Win32NativeHelper.GetWindowThreadProcessId(currentFg, out uint pid);
                            Volatile.Write(ref _lastForegroundPid, pid);
                        }

                        AppServices.Logger?.Info("[ForegroundWindowService] Real-time SetWinEventHook(EVENT_SYSTEM_FOREGROUND) active.");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"[ForegroundWindowService.Start] Failed to install WinEvent hook: {ex.Message}");
                }

                return false;
            }
        }

        public static void Stop()
        {
            if (!OperatingSystem.IsWindows()) return;

            lock (_sync)
            {
                if (_hookHandle == IntPtr.Zero)
                    return;

                try
                {
                    UnhookWinEvent(_hookHandle);
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"[ForegroundWindowService.Stop] {ex.Message}");
                }
                finally
                {
                    _hookHandle = IntPtr.Zero;
                }
            }
        }

        internal static bool ShouldCommitForegroundSequence(long capturedSequence, long currentSequence)
            => capturedSequence == currentSequence;

        internal static bool ShouldDispatchProcessChange(uint newPid, ref uint lastPid)
        {
            if (newPid == 0 || newPid == lastPid)
                return false;

            lastPid = newPid;
            return true;
        }

        private static void OnWinEvent(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime)
        {
            if (eventType != EVENT_SYSTEM_FOREGROUND || hwnd == IntPtr.Zero)
                return;

            try
            {
                Win32NativeHelper.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return;

                uint previous = Volatile.Read(ref _lastForegroundPid);
                if (previous == pid)
                    return;

                Volatile.Write(ref _lastForegroundPid, pid);
                long seq = Interlocked.Increment(ref _transitionSequence);

                // Single coalesced background worker avoiding duplicate Toolhelp32 process snapshotting
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (!ShouldCommitForegroundSequence(seq, Volatile.Read(ref _transitionSequence)))
                            return;

                        bool needAudio = RobloxAudioService.IsSmartMuteEnabled;
                        bool needMemory = MemoryOptimizer.IsAutoLowerPriorityActive();
                        bool needThrottler = RobloxBackgroundThrottlerService.Instance.IsEnabled;
                        if (!needAudio && !needMemory && !needThrottler)
                            return;

                        var runningPids = RobloxProcessService.GetRunningRobloxPids();

                        if (!ShouldCommitForegroundSequence(seq, Volatile.Read(ref _transitionSequence)))
                            return;

                        if (needAudio)
                            RobloxAudioService.EvaluateWithPids(pid, runningPids);
                        if (needThrottler)
                            RobloxBackgroundThrottlerService.Instance.OnForegroundWindowChanged((int)pid, runningPids);
                        else if (needMemory)
                            MemoryOptimizer.EvaluateWithPids(pid, runningPids);
                    }
                    catch (Exception ex)
                    {
                        AppServices.Logger?.Debug($"[ForegroundWindowService.CoalescedFocus] {ex.Message}");
                    }
                });

                var handlers = ForegroundProcessChanged;
                if (handlers != null)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { handlers(hwnd, pid); }
                        catch (Exception ex) { AppServices.Logger?.Debug($"[ForegroundWindowService] Handler error: {ex.Message}"); }
                    });
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[ForegroundWindowService.OnWinEvent] {ex.Message}");
            }
        }
    }
}
