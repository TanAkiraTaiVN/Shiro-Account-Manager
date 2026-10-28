#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
        private static readonly System.Threading.Lock _gate = new();
        private static Timer? _smartMuteTimer;
        private static bool _smartMuteEnabled = false;
        private static int _lastActiveRobloxPid = -1;
        private static int _lastRunningRobloxCount = -1;
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
                    if (_smartMuteTimer == null)
                    {
                        _smartMuteTimer = new Timer(OnSmartMuteCheck, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(1500));
                    }
                    else
                    {
                        _smartMuteTimer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(1500));
                    }
                    SetStatus("Smart Mute Active (Background clients muted)");
                }
                else
                {
                    _smartMuteTimer?.Dispose();
                    _smartMuteTimer = null;
                    _lastActiveRobloxPid = -1;
                    _lastRunningRobloxCount = -1;
                    shouldUnmuteAll = wasEnabled;
                    SetStatus("Audio Manager Ready");
                }
            }

            if (shouldUnmuteAll)
            {
                try { MuteAllRoblox(false, updateStatus: false); }
                catch (Exception ex) { Program.Logger?.Debug($"Failed to unmute all Roblox sessions: {ex.Message}"); }
            }
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
            int lastRunningRobloxCount)
        {
            if (runningRobloxCount <= 0)
                return false;

            if (activePid == lastActivePid && runningRobloxCount == lastRunningRobloxCount)
                return false;

            return true;
        }

        private static void OnSmartMuteCheck(object? state)
        {
            if (!_smartMuteEnabled) return;

            try
            {
                var runningPids = RobloxProcessService.GetRunningRobloxPids();
                int runningRobloxCount = runningPids.Count;
                if (runningRobloxCount <= 0)
                {
                    _lastActiveRobloxPid = 0;
                    _lastRunningRobloxCount = 0;
                    return;
                }

                IntPtr hForeground = GetForegroundWindow();
                uint fgPid = 0;
                if (hForeground != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hForeground, out fgPid);
                }

                bool isRobloxActive = fgPid > 0 && runningPids.Contains((int)fgPid);

                int activePid = isRobloxActive ? (int)fgPid : 0;

                // If active foreground state and running Roblox process count haven't changed, skip COM enumeration to save CPU
                if (!ShouldApplySmartMuteState(activePid, _lastActiveRobloxPid, runningRobloxCount, _lastRunningRobloxCount))
                {
                    return;
                }

                _lastActiveRobloxPid = activePid;
                _lastRunningRobloxCount = runningRobloxCount;

                ApplyFocusAudioState(activePid, runningPids);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[RobloxAudioService.SmartMute] {ex.Message}");
            }
        }

        /// <summary>
        /// Enumerates all active Windows Core Audio sessions belonging to running Roblox processes.
        /// Returning false from <paramref name="onSession"/> stops enumeration early.
        /// </summary>
        private static void ForEachRobloxAudioSession(Func<uint, ISimpleAudioVolume, bool> onSession, IReadOnlyList<int>? preloadedPids = null)
        {
            if (!OperatingSystem.IsWindows() || onSession == null) return;

            var robloxPids = preloadedPids ?? RobloxProcessService.GetRunningRobloxPids();
            if (robloxPids.Count == 0) return;

            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            IAudioSessionManager2? sessionManager = null;
            IAudioSessionEnumerator? sessionEnumerator = null;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device) != 0 || device == null)
                    return;

                var iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out object sessionMgrObj) != 0 || sessionMgrObj == null)
                    return;

                sessionManager = (IAudioSessionManager2)sessionMgrObj;
                if (sessionManager.GetSessionEnumerator(out sessionEnumerator) != 0 || sessionEnumerator == null)
                    return;

                if (sessionEnumerator.GetCount(out int count) != 0 || count <= 0)
                    return;

                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl? session = null;
                    try
                    {
                        if (sessionEnumerator.GetSession(i, out session) != 0 || session == null)
                            continue;

                        if (session is IAudioSessionControl2 session2)
                        {
                            session2.GetProcessId(out uint pid);
                            if (pid == 0 || !ContainsPid(robloxPids, (int)pid))
                                continue;

                            if (session is ISimpleAudioVolume volume)
                            {
                                if (!onSession(pid, volume))
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Failed inspecting audio session: {ex.Message}");
                    }
                    finally
                    {
                        if (session != null) Marshal.ReleaseComObject(session);
                    }
                }
            }
            finally
            {
                if (sessionEnumerator != null) Marshal.ReleaseComObject(sessionEnumerator);
                if (sessionManager != null) Marshal.ReleaseComObject(sessionManager);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
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
        public static (int muted, int unmuted) ApplyFocusAudioState(int activePid, IReadOnlyList<int>? preloadedPids = null)
        {
            int muted = 0;
            int unmuted = 0;

            if (!OperatingSystem.IsWindows()) return (0, 0);

            try
            {
                var emptyGuid = Guid.Empty;
                ForEachRobloxAudioSession((pid, volume) =>
                {
                    bool targetMute = activePid <= 0 || pid != (uint)activePid;
                    volume.GetMute(out bool currentMute);

                    if (currentMute != targetMute)
                    {
                        volume.SetMute(targetMute, ref emptyGuid);
                        if (targetMute) muted++;
                        else unmuted++;
                    }
                    return true;
                }, preloadedPids);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[RobloxAudioService.ApplyFocusAudioState] {ex.Message}");
            }

            return (muted, unmuted);
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
                var emptyGuid = Guid.Empty;
                ForEachRobloxAudioSession((_, volume) =>
                {
                    volume.GetMute(out bool currentMute);
                    if (currentMute != mute)
                    {
                        volume.SetMute(mute, ref emptyGuid);
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
                Program.Logger?.Error($"[RobloxAudioService.MuteAllRoblox] {ex.Message}");
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
                    volume.GetMute(out bool isMuted);
                    if (!isMuted)
                    {
                        anyUnmuted = true;
                        return false; // Stop enumeration immediately
                    }
                    return true;
                });
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Failed checking Roblox mute status: {ex.Message}");
            }

            return anyUnmuted;
        }

        private static void SetStatus(string text)
        {
            if (_status == text) return;
            _status = text;
            try { StatusChanged?.Invoke(); }
            catch (Exception ex) { Program.Logger?.Warn($"StatusChanged handler error: {ex.Message}"); }
        }
    }
}
