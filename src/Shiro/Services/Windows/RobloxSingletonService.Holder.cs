#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Shiro.Services
{
    public static partial class RobloxSingletonService
    {
        // Include a Low Mandatory Integrity Level SACL (S:(ML;;NW;;;LW)) alongside the protected DACL
        // so non-elevated and Low-integrity Roblox processes can open the singleton kernel objects
        // even when Shiro is running elevated.
        internal const string RestrictedInteractiveSddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;IU)S:(ML;;NW;;;LW)";

        internal sealed class HolderHandle
        {
            public ManualResetEventSlim Stop { get; } = new(false);
            public ManualResetEventSlim Ready { get; } = new(false);
            private int _refCount;
            private int _disposed;

            public HolderHandle(int initialRefs)
            {
                _refCount = initialRefs;
            }

            public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            public bool TryAcquire()
            {
                while (true)
                {
                    int current = Volatile.Read(ref _refCount);
                    if (current <= 0)
                        return false;
                    if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                        return true;
                }
            }

            public void SignalStop()
            {
                if (Volatile.Read(ref _disposed) == 0)
                    Stop.Set();
            }

            public void Release()
            {
                if (Interlocked.Decrement(ref _refCount) == 0 && Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Stop.Dispose();
                    Ready.Dispose();
                }
            }
        }

        internal readonly record struct HolderNativeOps(
            Func<IntPtr, bool, string, (IntPtr Handle, int Error)> CreateMutex,
            Func<IntPtr, bool, bool, string, (IntPtr Handle, int Error)> CreateEvent,
            Func<IntPtr, uint, uint> WaitForSingleObject,
            Action<IntPtr> ReleaseMutex,
            Action<IntPtr> CloseHandle);

        private static readonly HolderNativeOps DefaultNativeOps = new(
            CreateMutexNative,
            CreateEventNative,
            static (h, ms) => Win32NativeHelper.WaitForSingleObject(h, ms),
            static h => SafeExecuteHelper.Try(() => ReleaseMutex(h)),
            static h => SafeExecuteHelper.Try(() => Win32NativeHelper.CloseHandle(h)));

        internal static HolderNativeOps? NativeOpsOverrideForTest { get; set; }

        internal static HolderNativeOps CreateSuffixedNativeOpsForTest(string suffix) => DefaultNativeOps with
        {
            CreateMutex = (pSd, init, name) => CreateMutexNative(pSd, init, name + suffix),
            CreateEvent = (pSd, manual, initState, name) => CreateEventNative(pSd, manual, initState, name + suffix)
        };

        internal static bool ShouldAbortHoldOnZeroEvent(IntPtr hEvent) => hEvent == IntPtr.Zero;

        internal static long ReserveGenerationForTest() => Interlocked.Increment(ref _holderGeneration);

        internal static void EnsureMutexCore(
            bool enabled,
            TimeSpan joinTimeout,
            Action<HolderHandle, long>? startHolderOverride)
        {
            Thread? oldThread = null;
            HolderHandle? oldHolder = null;
            lock (_gate)
            {
                if (!enabled || (_holderThread != null && !_holderThread.IsAlive))
                {
                    Interlocked.Increment(ref _holderGeneration);
                    oldThread = _holderThread;
                    oldHolder = _activeHolder;
                    oldHolder?.SignalStop();
                    _holderThread = null;
                    _activeHolder = null;
                    if (!enabled) _handlesHeld = false;
                }
            }

            if (oldThread != null && oldThread.IsAlive && oldThread != Thread.CurrentThread)
            {
                oldThread.Join(joinTimeout);
            }
            oldHolder?.Release();

            HolderHandle? waitHolder = null;
            HolderHandle? staleHolder = null;

            if (enabled)
            {
                lock (_gate)
                {
                    if (_holderThread != null && _holderThread.IsAlive && _activeHolder != null)
                    {
                        if (_activeHolder.TryAcquire())
                            waitHolder = _activeHolder;
                    }
                    else
                    {
                        if (_activeHolder != null)
                        {
                            staleHolder = _activeHolder;
                            _activeHolder = null;
                            _holderThread = null;
                        }

                        var holder = new HolderHandle(initialRefs: 2);
                        long gen = Interlocked.Increment(ref _holderGeneration);
                        _handlesHeld = false;
                        _accessDenied = false;
                        if (holder.TryAcquire())
                            waitHolder = holder;

                        var entry = startHolderOverride ?? HoldHandles;
                        var thread = new Thread(() => entry(holder, gen))
                        {
                            IsBackground = true,
                            Name = "roblox-singleton-guard"
                        };

                        _activeHolder = holder;
                        _holderThread = thread;
                        try
                        {
                            thread.Start();
                        }
                        catch (Exception ex)
                        {
                            AppServices.Logger?.Error($"[MultiRoblox] Failed to start guard thread: {ex.Message}");
                            _activeHolder = null;
                            _holderThread = null;
                            waitHolder?.Release();
                            waitHolder = null;
                            holder.Release();
                            holder.Release();
                            throw;
                        }
                    }
                }
            }

            if (staleHolder != null)
            {
                staleHolder.SignalStop();
                staleHolder.Release();
            }

            if (waitHolder != null)
            {
                try
                {
                    int readyTimeoutMs = startHolderOverride == null ? 2000 : 500;
                    waitHolder.Ready.Wait(readyTimeoutMs);
                }
                finally
                {
                    waitHolder.Release();
                }
            }
        }

        internal static IntPtr BuildSecurityDescriptor(string sddl, out bool allocatedWithLocalAlloc)
        {
            if (ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    sddl,
                    SECURITY_DESCRIPTOR_REVISION,
                    out IntPtr pSd,
                    IntPtr.Zero))
            {
                allocatedWithLocalAlloc = true;
                return pSd;
            }

            allocatedWithLocalAlloc = false;
            pSd = Marshal.AllocHGlobal(64);
            if (!InitializeSecurityDescriptor(pSd, SECURITY_DESCRIPTOR_REVISION) ||
                !SetSecurityDescriptorDacl(pSd, true, IntPtr.Zero, false))
            {
                Marshal.FreeHGlobal(pSd);
                return IntPtr.Zero;
            }

            return pSd;
        }

        internal static void FreeSecurityDescriptor(IntPtr pSd, bool allocatedWithLocalAlloc)
        {
            if (pSd == IntPtr.Zero) return;
            if (allocatedWithLocalAlloc)
                LocalFree(pSd);
            else
                Marshal.FreeHGlobal(pSd);
        }

        private static SECURITY_ATTRIBUTES CreateSecurityAttributes(IntPtr pSd) => new()
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = pSd,
            bInheritHandle = 0
        };

        private static (IntPtr Handle, int Error) CreateMutexNative(IntPtr pSd, bool initialOwner, string name)
        {
            unsafe
            {
                var sa = CreateSecurityAttributes(pSd);
                IntPtr pSa = pSd != IntPtr.Zero ? (IntPtr)(&sa) : IntPtr.Zero;
                IntPtr handle = CreateMutexW(pSa, initialOwner, name);
                return (handle, Marshal.GetLastWin32Error());
            }
        }

        private static (IntPtr Handle, int Error) CreateEventNative(IntPtr pSd, bool manualReset, bool initialState, string name)
        {
            unsafe
            {
                var sa = CreateSecurityAttributes(pSd);
                IntPtr pSa = pSd != IntPtr.Zero ? (IntPtr)(&sa) : IntPtr.Zero;
                IntPtr handle = CreateEventW(pSa, manualReset, initialState, name);
                return (handle, Marshal.GetLastWin32Error());
            }
        }

        private static void HoldHandles(HolderHandle holder, long generation)
            => HoldHandlesCore(holder, generation, NativeOpsOverrideForTest ?? DefaultNativeOps, buildSecurityDescriptor: true);

        internal static void HoldHandlesCore(
            HolderHandle holder,
            long generation,
            HolderNativeOps ops,
            bool buildSecurityDescriptor = true)
        {
            IntPtr pSd = IntPtr.Zero;
            bool allocatedWithLocalAlloc = false;
            IntPtr hMutex = IntPtr.Zero;
            IntPtr hEvent = IntPtr.Zero;
            bool mutexOwned = false;

            try
            {
                if (buildSecurityDescriptor)
                    pSd = BuildSecurityDescriptor(RestrictedInteractiveSddl, out allocatedWithLocalAlloc);

                // Create and claim initial ownership of ROBLOX_singletonMutex
                (hMutex, int mutexErr) = ops.CreateMutex(pSd, true, MutexName);

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
                            uint wait = ops.WaitForSingleObject(hMutex, 0);
                            if (wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED)
                            {
                                mutexOwned = true;
                                AppServices.Logger?.Info("[MultiRoblox] Claimed active singleton mutex ownership.");
                            }
                        }
                        catch (Exception ex)
                        {
                            AppServices.Logger?.Debug($"WaitForSingleObject on hMutex threw: {ex.Message}");
                        }
                    }
                }

                // Create persistent ROBLOX_singletonEvent (manual-reset, initially non-signaled)
                (hEvent, int eventErr) = ops.CreateEvent(pSd, true, false, EventName);

                if (ShouldAbortHoldOnZeroEvent(hEvent))
                {
                    bool accessDenied = (mutexErr == ERROR_ACCESS_DENIED || eventErr == ERROR_ACCESS_DENIED);
                    TryPublishHolderState(
                        generation,
                        handlesHeld: false,
                        accessDenied: accessDenied,
                        statusText: $"Singleton Guard Warning: MutexErr={mutexErr}, EventErr={eventErr}");
                    AppServices.Logger?.Error(
                        $"[MultiRoblox] Failed to create singleton event (MutexErr={mutexErr}, EventErr={eventErr}); releasing mutex and aborting hold.");
                    holder.Ready.Set();
                    return;
                }

                if (hMutex != IntPtr.Zero && mutexOwned)
                {
                    TryPublishHolderState(
                        generation,
                        handlesHeld: true,
                        accessDenied: false,
                        statusText: "Mutex Active • Multi-Instance Running");
                    AppServices.Logger?.Info($"[MultiRoblox] Singleton Guard active (Mutex: 0x{hMutex:X}, Event: 0x{hEvent:X}).");
                }
                else if (hMutex != IntPtr.Zero && !mutexOwned)
                {
                    TryPublishHolderState(
                        generation,
                        handlesHeld: false,
                        accessDenied: false,
                        statusText: "Waiting for existing Roblox to release singleton mutex...");
                    AppServices.Logger?.Warn("[MultiRoblox] Singleton mutex is currently held by an existing Roblox process.");
                }
                else
                {
                    TryPublishHolderState(
                        generation,
                        handlesHeld: false,
                        accessDenied: mutexErr == ERROR_ACCESS_DENIED || eventErr == ERROR_ACCESS_DENIED,
                        statusText: $"Singleton Guard Warning: MutexErr={mutexErr}, EventErr={eventErr}");
                    AppServices.Logger?.Warn($"[MultiRoblox] Guard handle warning: MutexErr={mutexErr}, EventErr={eventErr}");
                }

                holder.Ready.Set();

                // If an existing Roblox process held the mutex when we started, wait until it closes and claim ownership immediately
                while (!holder.Stop.IsSet && hMutex != IntPtr.Zero && !mutexOwned)
                {
                    uint wait = ops.WaitForSingleObject(hMutex, 250);
                    if (wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED)
                    {
                        mutexOwned = true;
                        TryPublishHolderState(
                            generation,
                            handlesHeld: !ShouldAbortHoldOnZeroEvent(hEvent),
                            accessDenied: false,
                            statusText: "Mutex Active • Multi-Instance Running");
                        AppServices.Logger?.Info("[MultiRoblox] Claimed singleton mutex after existing Roblox process exited.");
                        break;
                    }
                }

                holder.Stop.Wait();
            }
            catch (Exception ex)
            {
                TryPublishHolderState(
                    generation,
                    handlesHeld: false,
                    accessDenied: null,
                    statusText: $"Singleton Guard Error: {ex.Message}");
                holder.Ready.Set();
                AppServices.Logger?.Error($"[MultiRoblox] Exception in singleton guard thread: {ex.Message}");
            }
            finally
            {
                ClearHandlesHeldIfCurrentGen(generation);
                holder.Ready.Set();
                if (hMutex != IntPtr.Zero)
                {
                    if (mutexOwned)
                        ops.ReleaseMutex(hMutex);
                    ops.CloseHandle(hMutex);
                }
                if (hEvent != IntPtr.Zero)
                    ops.CloseHandle(hEvent);
                FreeSecurityDescriptor(pSd, allocatedWithLocalAlloc);
                holder.Release();
            }
        }

        private static bool TryPublishHolderState(long generation, bool handlesHeld, bool? accessDenied, string statusText)
        {
            bool statusChanged;
            lock (_gate)
            {
                if (Volatile.Read(ref _holderGeneration) != generation)
                    return false;

                _handlesHeld = handlesHeld;
                if (accessDenied.HasValue)
                    _accessDenied = accessDenied.Value;

                statusChanged = !string.Equals(_status, statusText, StringComparison.Ordinal);
                if (statusChanged)
                    _status = statusText;
            }

            if (statusChanged)
                RaiseStatusChanged();
            return true;
        }

        private static void ClearHandlesHeldIfCurrentGen(long generation)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _holderGeneration) == generation)
                    _handlesHeld = false;
            }
        }
    }
}
