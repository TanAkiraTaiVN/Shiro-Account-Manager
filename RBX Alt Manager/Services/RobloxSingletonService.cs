using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.Services
{
    /// <summary>
    /// High-stability Multi-Instance Engine for Roblox.
    /// Defeats both of Roblox's single-instance barriers without destructive handle manipulation:
    ///  1. ROBLOX_singletonMutex - Persistent Win32 Mutex held with a permissive NULL DACL.
    ///     Because Shiro maintains continuous ownership on a dedicated background thread,
    ///     the mutex is never abandoned when an individual Roblox window closes.
    ///  2. ROBLOX_singletonEvent - Persistent Win32 Event held with a permissive NULL DACL.
    ///     All Roblox clients can safely access and wait on this event without getting Access Denied,
    ///     and their internal handle tables are never corrupted by intrusive DuplicateHandle calls.
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
        private static Timer _watcher;
        private static int _watchSeconds = 3;
        private static int _sweeping;
        private static HashSet<int> _lastObservedPids = new();

        // Singleton holder thread
        private static Thread _holderThread;
        private static ManualResetEventSlim _holderStop;
        private static ManualResetEventSlim _holderReady;
        private static volatile bool _handlesHeld;

        // Observable state
        private static volatile string _status = "Multi-Instance Ready";
        private static int _totalClosed;
        private static volatile bool _accessDenied;
        private static bool _isEnabled = true;

        public static event Action StatusChanged;

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
                    using (var id = WindowsIdentity.GetCurrent())
                        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        public static void Apply(bool enabled)
        {
            _isEnabled = enabled;
            if (!enabled)
            {
                Stop();
                SetStatus("Multi-Instance Disabled");
                return;
            }

            EnsureMutex(true);
            StartWatcher(_watchSeconds);
            Task.Run(() => { try { Sweep(force: true); } catch { } });
        }

        public static void Stop()
        {
            StopWatcher();
            EnsureMutex(false);
        }

        public static void EnsureMutex(bool enabled)
        {
            ManualResetEventSlim readyToWait = null;
            lock (_gate)
            {
                if (enabled)
                {
                    if (_holderThread != null && _holderThread.IsAlive)
                    {
                        readyToWait = _holderReady;
                    }
                    else
                    {
                        var stop = new ManualResetEventSlim(false);
                        var ready = new ManualResetEventSlim(false);
                        _holderStop = stop;
                        _holderReady = ready;
                        readyToWait = ready;
                        _holderThread = new Thread(() => HoldHandles(stop, ready))
                        {
                            IsBackground = true,
                            Name = "roblox-singleton-guard"
                        };
                        _holderThread.Start();
                    }
                }
                else
                {
                    _holderStop?.Set();
                    _holderStop = null;
                    _holderReady = null;
                    _holderThread = null;
                    _handlesHeld = false;
                }
            }

            if (enabled && readyToWait != null)
            {
                try { readyToWait.Wait(500); } catch { }
            }
        }

        private static void HoldHandles(ManualResetEventSlim stop, ManualResetEventSlim ready)
        {
            IntPtr pSd = IntPtr.Zero;
            IntPtr hMutex = IntPtr.Zero;
            IntPtr hEvent = IntPtr.Zero;
            bool mutexOwned = false;

            try
            {
                // Create a Security Descriptor with a NULL DACL (permits Everyone / All Integrity Levels unrestricted access).
                // This ensures Roblox processes never encounter ERROR_ACCESS_DENIED even if Shiro is run as Administrator.
                pSd = Marshal.AllocHGlobal(64);
                if (!InitializeSecurityDescriptor(pSd, SECURITY_DESCRIPTOR_REVISION) ||
                    !SetSecurityDescriptorDacl(pSd, true, IntPtr.Zero, false))
                {
                    Marshal.FreeHGlobal(pSd);
                    pSd = IntPtr.Zero;
                }

                var sa = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                    lpSecurityDescriptor = pSd,
                    bInheritHandle = 0
                };

                // Create and claim initial ownership of ROBLOX_singletonMutex
                unsafe
                {
                    IntPtr pSa = pSd != IntPtr.Zero ? (IntPtr)(&sa) : IntPtr.Zero;
                    hMutex = CreateMutexW(pSa, true, MutexName);
                }

                int mutexErr = Marshal.GetLastWin32Error();

                if (hMutex != IntPtr.Zero)
                {
                    if (mutexErr == 0)
                    {
                        mutexOwned = true;
                    }
                    else
                    {
                        try
                        {
                            uint wait = WaitForSingleObject(hMutex, 0);
                            if (wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED)
                            {
                                mutexOwned = true;
                                Program.Logger?.Info("[MultiRoblox] Claimed active singleton mutex ownership.");
                            }
                        }
                        catch { }
                    }
                }

                // Create persistent ROBLOX_singletonEvent (manual-reset, initially non-signaled)
                unsafe
                {
                    IntPtr pSa = pSd != IntPtr.Zero ? (IntPtr)(&sa) : IntPtr.Zero;
                    hEvent = CreateEventW(pSa, true, false, EventName);
                }

                int eventErr = Marshal.GetLastWin32Error();

                if (hMutex != IntPtr.Zero && hEvent != IntPtr.Zero && mutexOwned)
                {
                    _handlesHeld = true;
                    _accessDenied = false;
                    SetStatus("Mutex Active • Multi-Instance Running");
                    Program.Logger?.Info($"[MultiRoblox] Singleton Guard active (Mutex: 0x{hMutex:X}, Event: 0x{hEvent:X}).");
                }
                else if (hMutex != IntPtr.Zero && !mutexOwned)
                {
                    _handlesHeld = false;
                    _accessDenied = false;
                    SetStatus("Waiting for existing Roblox to release singleton mutex...");
                    Program.Logger?.Warn("[MultiRoblox] Singleton mutex is currently held by an existing Roblox process.");
                }
                else
                {
                    _handlesHeld = false;
                    _accessDenied = (mutexErr == ERROR_ACCESS_DENIED || eventErr == ERROR_ACCESS_DENIED);
                    SetStatus($"Singleton Guard Warning: MutexErr={mutexErr}, EventErr={eventErr}");
                    Program.Logger?.Warn($"[MultiRoblox] Guard handle warning: MutexErr={mutexErr}, EventErr={eventErr}");
                }

                ready?.Set();

                // If an existing Roblox process held the mutex when we started, wait until it closes and claim ownership immediately
                while (!stop.IsSet && hMutex != IntPtr.Zero && !mutexOwned)
                {
                    uint wait = WaitForSingleObject(hMutex, 250);
                    if (wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED)
                    {
                        mutexOwned = true;
                        _handlesHeld = hEvent != IntPtr.Zero;
                        _accessDenied = false;
                        SetStatus("Mutex Active • Multi-Instance Running");
                        Program.Logger?.Info("[MultiRoblox] Claimed singleton mutex after existing Roblox process exited.");
                        break;
                    }
                }

                stop.Wait();
            }
            catch (Exception ex)
            {
                _handlesHeld = false;
                ready?.Set();
                SetStatus($"Singleton Guard Error: {ex.Message}");
                Program.Logger?.Error($"[MultiRoblox] Exception in singleton guard thread: {ex.Message}");
            }
            finally
            {
                _handlesHeld = false;
                ready?.Set();
                if (hMutex != IntPtr.Zero)
                {
                    if (mutexOwned)
                    {
                        try { ReleaseMutex(hMutex); } catch { }
                    }
                    try { CloseHandle(hMutex); } catch { }
                }
                if (hEvent != IntPtr.Zero)
                {
                    try { CloseHandle(hEvent); } catch { }
                }
                if (pSd != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(pSd);
                }
            }
        }

        private static bool StartWatcher(int seconds)
        {
            lock (_gate)
            {
                if (_watcher != null && _watchSeconds == seconds) return false;

                var period = TimeSpan.FromSeconds(Math.Max(2, seconds));
                _watchSeconds = seconds;
                if (_watcher == null)
                    _watcher = new Timer(_ => { try { Sweep(); } catch { } }, null, TimeSpan.Zero, period);
                else
                    _watcher.Change(TimeSpan.Zero, period);
                return true;
            }
        }

        private static void StopWatcher()
        {
            lock (_gate)
            {
                _watcher?.Dispose();
                _watcher = null;
            }
        }

        public static int SweepNow(out string message)
        {
            EnsureMutex(true);
            int purged = Sweep(force: true);
            var pids = ClientPids();

            if (_handlesHeld)
            {
                message = pids.Count > 0
                    ? $"Multi-Instance active. {pids.Count} client(s) running safely with persistent Singleton Guard."
                    : "Multi-Instance active and ready. Singleton Guard is holding all mutex and event locks.";
            }
            else if (_accessDenied)
            {
                message = "Access denied: Run as Administrator to grant full access.";
            }
            else
            {
                message = "Multi-Instance guard is active.";
            }

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

                SetStatus(_handlesHeld
                    ? $"Multi-Instance Active ({pids.Count} clients running)"
                    : $"Multi-Instance Active ({pids.Count} clients running • Guard Starting)");

                return purged;
            }
            finally
            {
                Volatile.Write(ref _sweeping, 0);
            }
        }

        private static List<int> ClientPids()
        {
            return RobloxProcessService.GetRunningRobloxPids();
        }

        public static (int closed, bool accessDenied, bool isPending) CloseEventsInPid(int pid)
        {
            // Intrusive handle destruction has been eliminated to prevent Byfron/WndProcessCheck
            // from throwing "Failure waiting on named event: Access is denied" and terminating all tabs.
            return (0, false, false);
        }

        private static void SetStatus(string text)
        {
            if (_status == text) return;
            _status = text;
            try { StatusChanged?.Invoke(); }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[RobloxSingletonService.StatusChanged] {ex.Message}");
            }
        }

        // Native Win32 Interop
        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool InitializeSecurityDescriptor(IntPtr pSecurityDescriptor, uint dwRevision);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetSecurityDescriptorDacl(IntPtr pSecurityDescriptor,
                                                              [MarshalAs(UnmanagedType.Bool)] bool bDaclPresent,
                                                              IntPtr pDacl,
                                                              [MarshalAs(UnmanagedType.Bool)] bool bDaclDefaulted);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CreateMutexW(IntPtr lpMutexAttributes,
                                                   [MarshalAs(UnmanagedType.Bool)] bool bInitialOwner,
                                                   string lpName);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CreateEventW(IntPtr lpEventAttributes,
                                                   [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
                                                   [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
                                                   string lpName);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ReleaseMutex(IntPtr hMutex);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);
    }
}
