#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;

namespace Shiro.Services
{
    /// <summary>
    /// Windows Core Audio (WASAPI) Service for Roblox Multi-Client Management.
    /// Provides master mute, per-PID mute, and intelligent background audio suppression.
    /// In Smart Mute mode, audio is automatically muted on all background Roblox clients
    /// and unmuted only for the currently active/focused client window.
    /// </summary>
    public static partial class RobloxAudioService
    {
        private const long ComFailureBackoffMs = 10_000;

        private static readonly System.Threading.Lock _gate = new();
        private static Timer? _smartMuteTimer;
        private static bool _smartMuteEnabled = false;
        private static int _lastActiveRobloxPid = -1;
        private static int _lastRunningRobloxCount = -1;
        private static long _lastComFailureTick = 0;
        private static volatile string _status = "Audio Manager Ready";

        public static event Action? StatusChanged;

        public static bool IsSmartMuteEnabled => _smartMuteEnabled;
        public static string StatusText => _status;

        public static void ConfigureSmartMute(bool enabled)
        {
            bool shouldUnmuteAll = false;
            lock (_gate)
            {
                bool wasEnabled = _smartMuteEnabled || _smartMuteTimer != null;
                _smartMuteEnabled = enabled;
                if (_smartMuteEnabled)
                {
                    _lastActiveRobloxPid = -1;
                    _lastRunningRobloxCount = -1;
                    _lastComFailureTick = 0;
                    TimeSpan pollInterval = ForegroundWindowService.IsHookActive
                        ? TimeSpan.FromMilliseconds(4000)
                        : TimeSpan.FromMilliseconds(1500);

                    if (_smartMuteTimer == null)
                    {
                        _smartMuteTimer = new Timer(OnSmartMuteCheck, null, TimeSpan.Zero, pollInterval);
                    }
                    else
                    {
                        _smartMuteTimer.Change(TimeSpan.Zero, pollInterval);
                    }
                    SetStatus("Smart Mute Active (Background clients muted)");
                }
                else
                {
                    _smartMuteTimer?.Dispose();
                    _smartMuteTimer = null;
                    _lastActiveRobloxPid = -1;
                    _lastRunningRobloxCount = -1;
                    _lastComFailureTick = 0;
                    shouldUnmuteAll = wasEnabled;
                    SetStatus("Audio Manager Ready");
                }
            }

            if (shouldUnmuteAll)
            {
                try { MuteAllRoblox(false, updateStatus: false); }
                catch (Exception ex) { AppServices.Logger?.Debug($"Failed to unmute all Roblox sessions: {ex.Message}"); }
                ClearAudioSessionCache();
            }
        }

        /// <summary>
        /// Legacy foreground notification hook. Preferred entry point is coalesced evaluation via <see cref="EvaluateWithPids"/>.
        /// </summary>
        [Obsolete("Use ForegroundWindowService coalesced focus evaluation or EvaluateWithPids.")]
        public static void NotifyForegroundChanged(IntPtr hwnd, uint foregroundPid)
        {
            if (!_smartMuteEnabled) return;

            // Dispatch onto ThreadPool so the WinEvent callback returns instantaneously to the OS message pump
            ThreadPool.QueueUserWorkItem(_ => EvaluateSmartMuteForForegroundPid(foregroundPid));
        }

        /// <summary>
        /// Pure decision helper determining whether Smart Mute needs to enumerate WASAPI audio sessions on this tick.
        /// Skips COM enumeration when no Roblox processes are running or when neither the focused Roblox PID nor
        /// the running Roblox process count has changed since the last tick.
        /// </summary>
        internal static bool ShouldApplySmartMuteState(
            int activePid,
            int lastActivePid,
            int runningRobloxCount,
            int lastRunningRobloxCount,
            long nowTick = 0,
            long lastFailureTick = 0)
        {
            if (runningRobloxCount <= 0)
                return false;

            if (lastFailureTick > 0 && nowTick > 0 && (nowTick - lastFailureTick) < ComFailureBackoffMs)
                return false;

            if (activePid == lastActivePid && runningRobloxCount == lastRunningRobloxCount)
                return false;

            return true;
        }

        private static void OnSmartMuteCheck(object? state)
        {
            if (!_smartMuteEnabled) return;

            IntPtr hForeground = GetForegroundWindow();
            uint fgPid = 0;
            if (hForeground != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hForeground, out fgPid);
            }

            EvaluateSmartMuteForForegroundPid(fgPid);
        }

        public static void EvaluateWithPids(uint fgPid, List<int> runningPids)
        {
            if (!_smartMuteEnabled || runningPids == null) return;
            EvaluateSmartMuteForForegroundPidCore(fgPid, runningPids);
        }

        private static void EvaluateSmartMuteForForegroundPid(uint fgPid)
        {
            if (!_smartMuteEnabled) return;
            var runningPids = RobloxProcessService.GetRunningRobloxPids();
            EvaluateSmartMuteForForegroundPidCore(fgPid, runningPids);
        }

        private static void EvaluateSmartMuteForForegroundPidCore(uint fgPid, List<int> runningPids)
        {
            if (!_smartMuteEnabled) return;

            try
            {
                int runningRobloxCount = runningPids.Count;
                if (runningRobloxCount <= 0)
                {
                    _lastActiveRobloxPid = 0;
                    _lastRunningRobloxCount = 0;
                    return;
                }

                bool isRobloxActive = fgPid > 0 && ContainsPid(runningPids, (int)fgPid);
                int activePid = isRobloxActive ? (int)fgPid : 0;
                long nowTick = Environment.TickCount64;

                if (!ShouldApplySmartMuteState(
                        activePid,
                        _lastActiveRobloxPid,
                        runningRobloxCount,
                        _lastRunningRobloxCount,
                        nowTick,
                        Interlocked.Read(ref _lastComFailureTick)))
                {
                    return;
                }

                _lastActiveRobloxPid = activePid;

                bool succeeded = TryApplyFocusAudioState(activePid, runningPids, out _, out _, out int totalSessions);
                if (!succeeded)
                {
                    Interlocked.Exchange(ref _lastComFailureTick, Environment.TickCount64);
                    return;
                }

                Interlocked.Exchange(ref _lastComFailureTick, 0);

                // If not all running processes have registered audio sessions yet (e.g. game still launching),
                // do not lock _lastRunningRobloxCount so next tick will re-check and mute the new client.
                if (totalSessions < runningRobloxCount)
                {
                    _lastRunningRobloxCount = -1;
                }
                else
                {
                    _lastRunningRobloxCount = runningRobloxCount;
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _lastComFailureTick, Environment.TickCount64);
                AppServices.Logger?.Debug($"[RobloxAudioService.SmartMute] {ex.Message}");
            }
        }

        /// <summary>
        /// Enumerates all active Windows Core Audio sessions belonging to running Roblox processes using
        /// raw unmanaged COM pointers (zero CLR RCW apartment marshaling).
        /// Returning false from <paramref name="onSession"/> stops enumeration early.
        /// </summary>
        private static unsafe bool ForEachRobloxAudioSession(
            Func<uint, NativeSimpleAudioVolume, bool> onSession,
            IReadOnlyList<int>? preloadedPids = null)
        {
            if (!OperatingSystem.IsWindows() || onSession == null) return false;

            var robloxPids = preloadedPids ?? RobloxProcessService.GetRunningRobloxPids();
            if (robloxPids.Count == 0)
            {
                ClearAudioSessionCache();
                return true;
            }

            lock (_gate)
            {
                if (TryForEachCachedAudioSession(robloxPids, onSession))
                {
                    return true;
                }

                PruneExitedCachedAudioSessions(robloxPids);

                int hrCoInit = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
                bool shouldUninit = hrCoInit >= 0; // S_OK (0) or S_FALSE (1)
                if (hrCoInit < 0 && hrCoInit != RPC_E_CHANGED_MODE)
                {
                    return false;
                }

                IntPtr pEnumerator = IntPtr.Zero;
                IntPtr pDevice = IntPtr.Zero;
                IntPtr pSessionManager2 = IntPtr.Zero;
                IntPtr pSessionEnum = IntPtr.Zero;

                try
                {
                    Guid clsid = CLSID_MMDeviceEnumerator;
                    Guid iidEnum = IID_IMMDeviceEnumerator;
                    IntPtr createdEnumerator = IntPtr.Zero;
                    int hr = CoCreateInstance(&clsid, IntPtr.Zero, CLSCTX_ALL, &iidEnum, &createdEnumerator);
                    if (hr < 0 || createdEnumerator == IntPtr.Zero)
                        return false;

                    pEnumerator = createdEnumerator;

                    // eRender = 0, eMultimedia = 1
                    if (DeviceEnumeratorGetDefaultEndpoint(pEnumerator, 0, 1, out pDevice) < 0 || pDevice == IntPtr.Zero)
                    {
                        // No default audio render device attached (e.g. headless/unplugged speakers) -> treat as non-fatal
                        return false;
                    }

                    if (DeviceActivateSessionManager2(pDevice, out pSessionManager2) < 0 || pSessionManager2 == IntPtr.Zero)
                        return false;

                    if (SessionManager2GetEnumerator(pSessionManager2, out pSessionEnum) < 0 || pSessionEnum == IntPtr.Zero)
                        return false;

                    if (SessionEnumeratorGetCount(pSessionEnum, out int count) < 0 || count <= 0)
                        return true;

                    for (int i = 0; i < count; i++)
                    {
                        IntPtr pSession = IntPtr.Zero;
                        IntPtr pSession2 = IntPtr.Zero;
                        IntPtr pVolume = IntPtr.Zero;

                        try
                        {
                            if (SessionEnumeratorGetSession(pSessionEnum, i, out pSession) < 0 || pSession == IntPtr.Zero)
                                continue;

                            if (ComQueryInterface(pSession, in IID_IAudioSessionControl2, out pSession2) < 0 || pSession2 == IntPtr.Zero)
                                continue;

                            if (SessionControl2GetProcessId(pSession2, out uint pid) < 0 || pid == 0 || !ContainsPid(robloxPids, (int)pid))
                                continue;

                            if (ComQueryInterface(pSession, in IID_ISimpleAudioVolume, out pVolume) < 0 || pVolume == IntPtr.Zero)
                                continue;

                            var nativeVolume = new NativeSimpleAudioVolume(pVolume);
                            if (_cachedVolumePointers.TryRemove((int)pid, out IntPtr oldPtr) && oldPtr != IntPtr.Zero)
                            {
                                ComRelease(ref oldPtr);
                            }
                            _cachedVolumePointers[(int)pid] = pVolume;
                            // Ownership of pVolume reference transferred to _cachedVolumePointers
                            pVolume = IntPtr.Zero;

                            if (!onSession(pid, nativeVolume))
                                break;
                        }
                        catch (Exception ex)
                        {
                            AppServices.Logger?.Debug($"Failed inspecting audio session: {ex.Message}");
                        }
                        finally
                        {
                            ComRelease(ref pVolume);
                            ComRelease(ref pSession2);
                            ComRelease(ref pSession);
                        }
                    }

                    return true;
                }
                finally
                {
                    ComRelease(ref pSessionEnum);
                    ComRelease(ref pSessionManager2);
                    ComRelease(ref pDevice);
                    ComRelease(ref pEnumerator);
                    if (shouldUninit)
                    {
                        CoUninitialize();
                    }
                }
            }
        }

        private static bool ContainsPid(IReadOnlyList<int> pids, int targetPid)
        {
            for (int i = 0; i < pids.Count; i++)
            {
                if (pids[i] == targetPid)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Mutes all background Roblox processes, keeping only activePid unmuted (or muting all if activePid is 0).
        /// </summary>
        public static (int muted, int unmuted, int totalSessions) ApplyFocusAudioState(int activePid, IReadOnlyList<int>? preloadedPids = null)
        {
            TryApplyFocusAudioState(activePid, preloadedPids, out int muted, out int unmuted, out int totalSessions);
            return (muted, unmuted, totalSessions);
        }

        internal static bool TryApplyFocusAudioState(
            int activePid,
            IReadOnlyList<int>? preloadedPids,
            out int muted,
            out int unmuted,
            out int totalSessions)
        {
            int localMuted = 0;
            int localUnmuted = 0;
            int localTotal = 0;

            if (!OperatingSystem.IsWindows())
            {
                muted = 0;
                unmuted = 0;
                totalSessions = 0;
                return false;
            }

            try
            {
                bool ok = ForEachRobloxAudioSession((pid, volume) =>
                {
                    localTotal++;
                    bool targetMute = activePid <= 0 || pid != (uint)activePid;
                    if (volume.TryGetMute(out bool currentMute) && currentMute != targetMute)
                    {
                        if (volume.TrySetMute(targetMute))
                        {
                            if (targetMute) localMuted++;
                            else localUnmuted++;
                        }
                    }
                    return true;
                }, preloadedPids);

                muted = localMuted;
                unmuted = localUnmuted;
                totalSessions = localTotal;
                return ok;
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[RobloxAudioService.ApplyFocusAudioState] {ex.Message}");
                muted = localMuted;
                unmuted = localUnmuted;
                totalSessions = localTotal;
                return false;
            }
        }

        /// <summary>
        /// Explicitly sets the mute state for all running Roblox instances.
        /// </summary>
        public static int MuteAllRoblox(bool mute, bool updateStatus = true)
        {
            if (!OperatingSystem.IsWindows()) return 0;

            int changed = 0;
            try
            {
                ForEachRobloxAudioSession((_, volume) =>
                {
                    if (volume.TryGetMute(out bool currentMute) && currentMute != mute)
                    {
                        if (volume.TrySetMute(mute))
                            changed++;
                    }
                    return true;
                });

                if (updateStatus)
                {
                    SetStatus(mute ? $"Muted {changed} Roblox instance(s)" : $"Unmuted {changed} Roblox instance(s)");
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Error($"[RobloxAudioService.MuteAllRoblox] {ex.Message}");
            }

            return changed;
        }

        /// <summary>
        /// Toggles between mute all and unmute all.
        /// </summary>
        public static bool ToggleMuteAll()
        {
            bool anyUnmuted = IsAnyRobloxUnmuted();
            MuteAllRoblox(anyUnmuted);
            return anyUnmuted;
        }

        /// <summary>
        /// Checks whether any Roblox instance is currently unmuted.
        /// </summary>
        public static bool IsAnyRobloxUnmuted()
        {
            if (!OperatingSystem.IsWindows()) return false;

            bool anyUnmuted = false;
            try
            {
                ForEachRobloxAudioSession((_, volume) =>
                {
                    if (volume.TryGetMute(out bool isMuted) && !isMuted)
                    {
                        anyUnmuted = true;
                        return false; // Stop enumeration immediately
                    }
                    return true;
                });
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Failed checking Roblox mute status: {ex.Message}");
            }

            return anyUnmuted;
        }

        private static void SetStatus(string text)
        {
            if (_status == text) return;
            _status = text;
            try { StatusChanged?.Invoke(); }
            catch (Exception ex) { AppServices.Logger?.Warn($"StatusChanged handler error: {ex.Message}"); }
        }
    }
}
