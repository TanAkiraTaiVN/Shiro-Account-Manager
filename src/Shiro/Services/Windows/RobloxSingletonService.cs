#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    /// <summary>
    /// High-stability Multi-Instance Engine for Roblox holding ROBLOX_singletonMutex and ROBLOX_singletonEvent.
    /// </summary>
    public static partial class RobloxSingletonService
    {
        private const string MutexName = "ROBLOX_singletonMutex";
        private const string EventName = "ROBLOX_singletonEvent";
        private const uint SECURITY_DESCRIPTOR_REVISION = 1;
        private const uint WAIT_OBJECT_0 = 0x00000000;
        private const uint WAIT_ABANDONED = 0x00000080;
        private const uint WAIT_TIMEOUT = 0x00000102;
        private const int ERROR_ACCESS_DENIED = 5;

        private static readonly System.Threading.Lock _gate = new();
        private static Timer? _watcher;
        private static int _watchSeconds = 3;
        private static int _sweeping;
        private static HashSet<int> _lastObservedPids = new();
        private static Thread? _holderThread;
        private static HolderHandle? _activeHolder;
        private static long _holderGeneration;
        private static volatile bool _handlesHeld;
        private static volatile string _status = "Multi-Instance Ready";
        private static int _totalClosed;
        private static volatile bool _accessDenied;
        private static bool _isEnabled = true;

        public static event Action? StatusChanged;
        public static bool IsEnabled => _isEnabled;
        public static string StatusText => _status;
        public static bool MutexHeld => _handlesHeld;
        public static int TotalClosed => Volatile.Read(ref _totalClosed);
        public static bool AccessDenied => _accessDenied;

        public static bool IsElevated
        {
            get
            {
                try
                {
                    using var id = WindowsIdentity.GetCurrent();
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch (Exception ex) { AppServices.Logger?.Debug($"IsAdministrator check threw: {ex.Message}"); return false; }
            }
        }

        public static void Apply(bool enabled)
        {
            _isEnabled = enabled;
            if (!enabled)
            {
                StopCore("Multi-Instance Disabled");
                return;
            }

            EnsureMutex(true);
            StartWatcher(_watchSeconds);
            Task.Run(() => { try { Sweep(force: true); } catch (Exception ex) { AppServices.Logger?.Debug($"Initial sweep threw: {ex.Message}"); } });
        }

        public static void Stop() => StopCore("Multi-Instance Ready");

        internal static void ResetForTest(bool isEnabled = true)
        {
            NativeOpsOverrideForTest = null;
            StopCore("Multi-Instance Ready");
            _isEnabled = isEnabled;
        }

        private static void StopCore(string status)
        {
            StopWatcher();
            EnsureMutex(false);
            lock (_gate)
            {
                _accessDenied = false;
            }
            SetStatus(status);
        }

        public static void EnsureMutex(bool enabled)
            => EnsureMutexCore(enabled, TimeSpan.FromSeconds(2), startHolderOverride: null);

        private static bool StartWatcher(int seconds)
        {
            lock (_gate)
            {
                RobloxJobObjectService.ProcessExitedFromJob -= OnJobProcessExited;
                RobloxJobObjectService.ProcessExitedFromJob += OnJobProcessExited;

                if (_watcher != null && _watchSeconds == seconds) return false;

                var period = TimeSpan.FromSeconds(Math.Max(2, seconds));
                _watchSeconds = seconds;
                if (_watcher == null)
                    _watcher = new Timer(_ => { try { Sweep(); } catch (Exception ex) { AppServices.Logger?.Debug($"Periodic sweep threw: {ex.Message}"); } }, null, TimeSpan.Zero, period);
                else
                    _watcher.Change(TimeSpan.Zero, period);
                return true;
            }
        }

        private static void StopWatcher()
        {
            lock (_gate)
            {
                RobloxJobObjectService.ProcessExitedFromJob -= OnJobProcessExited;
                _watcher?.Dispose();
                _watcher = null;
            }
        }

        private static void OnJobProcessExited(int pid, bool abnormal)
        {
            _ = abnormal;
            if (!_isEnabled || pid <= 0) return;
            lock (_gate) { _lastObservedPids.Remove(pid); }
            ThreadPool.QueueUserWorkItem(static _ =>
            {
                try { Sweep(force: true); }
                catch (Exception ex) { AppServices.Logger.Debug($"[RobloxSingletonService] Event-driven sweep failed: {ex.Message}"); }
            });
        }

        internal static bool NotifyProcessExitedFromJobForTest(int pid)
        {
            if (pid <= 0) return false;
            lock (_gate) { return _lastObservedPids.Remove(pid); }
        }

        public static int SweepNow(out string message)
        {
            EnsureMutex(true);
            int purged = Sweep(force: true);
            var pids = ClientPids();
            if (_handlesHeld)
                message = pids.Count > 0 ? $"Multi-Instance active. {pids.Count} client(s) running safely with persistent Singleton Guard." : "Multi-Instance active and ready. Singleton Guard is holding all mutex and event locks.";
            else if (_accessDenied)
                message = "Access denied: Run as Administrator to grant full access.";
            else
                message = "Multi-Instance guard is active.";
            return purged;
        }

        /// <summary>
        /// Adaptive check confirming that a launched Roblox client has successfully initialized
        /// its main process window and is ready. With persistent NULL-DACL singleton guard held,
        /// clients are unlocked from process startup without requiring intrusive handle destruction.
        /// </summary>
        public static Task<bool> WaitForClientUnlockedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
            => WaitForClientUnlockedAsync(0, timeout, cancellationToken);

        public static async Task<bool> WaitForClientUnlockedAsync(int targetPid, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (!_isEnabled) return true;

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (targetPid > 0)
                {
                    if (!RobloxProcessService.IsProcessAlive(targetPid))
                        return false;

                    // Confirm the client has created its main window and initialized its UI thread without allocating a managed Process object
                    if (sw.ElapsedMilliseconds >= 300 && RobloxWindowService.FindWindowForPid((uint)targetPid) != IntPtr.Zero)
                    {
                        return true;
                    }
                }
                else
                {
                    var pids = ClientPids();
                    if (pids.Count > 0 && sw.ElapsedMilliseconds >= 300)
                    {
                        return true;
                    }
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        public static int Sweep(bool force = false)
        {
            if (!_isEnabled) return 0;
            if (Interlocked.Exchange(ref _sweeping, 1) == 1) return 0;

            try
            {
                var pids = ClientPids();
                bool pidSetChanged;
                lock (_gate)
                {
                    pidSetChanged = !_lastObservedPids.SetEquals(pids);
                    if (pidSetChanged)
                    {
                        _lastObservedPids = new HashSet<int>(pids);
                    }
                }

                int purged = 0;
                // Only inspect process command lines when forced or when the active client set changes
                if (force || pidSetChanged)
                {
                    purged = RobloxProcessService.CleanZombieProcesses();
                    if (purged > 0)
                    {
                        Interlocked.Add(ref _totalClosed, purged);
                    }
                }

                if (pids.Count == 0)
                {
                    SetStatus(_handlesHeld ? "Multi-Instance Ready" : "Ready (Guard Offline)");
                    return purged;
                }
                SetStatus(_handlesHeld ? $"Multi-Instance Active ({pids.Count} clients running)" : $"Multi-Instance Active ({pids.Count} clients running • Guard Starting)");
                return purged;
            }
            finally { Volatile.Write(ref _sweeping, 0); }
        }

        private static List<int> ClientPids() => RobloxProcessService.GetRunningRobloxPids();

        public static (int closed, bool accessDenied, bool isPending) CloseEventsInPid(int pid) => (0, false, false);

        private static void SetStatus(string text)
        {
            bool changed;
            lock (_gate)
            {
                if (_status == text) return;
                _status = text;
                changed = true;
            }

            if (changed)
                RaiseStatusChanged();
        }

        private static void RaiseStatusChanged()
        {
            try { StatusChanged?.Invoke(); }
            catch (Exception ex) { AppServices.Logger?.Debug($"[RobloxSingletonService.StatusChanged] {ex.Message}"); }
        }
    }
}
