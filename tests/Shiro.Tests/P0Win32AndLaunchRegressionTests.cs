#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P0Win32AndLaunchRegressionTests
    {
        [Fact]
        public void MemoryOptimizer_SetProcessMemoryPriorityNative_UsesProcessMemoryPriorityZero_AndSucceedsOnWindows()
        {
            Assert.Equal(0, MemoryOptimizer.ProcessMemoryPriorityInfoClass);

            if (!OperatingSystem.IsWindows())
                return;

            using var self = Process.GetCurrentProcess();
            try
            {
                bool fgOk = MemoryOptimizer.SetProcessMemoryPriorityNative(self.Handle, isForeground: true);
                Assert.True(fgOk);

                bool bgOk = MemoryOptimizer.SetProcessMemoryPriorityNative(self.Handle, isForeground: false);
                Assert.True(bgOk);
            }
            finally
            {
                MemoryOptimizer.SetProcessMemoryPriorityNative(self.Handle, isForeground: true);
            }
        }

        [Fact]
        public void MemoryOptimizer_SoftRelaxWorkingSet_DoesNotTrimWithMinusOne_WhenAggressiveTrimDisabled()
        {
            Assert.False(MemoryOptimizer.ShouldTrimWorkingSetOnSoftRelax(aggressiveEnabled: false));
            Assert.True(MemoryOptimizer.ShouldTrimWorkingSetOnSoftRelax(aggressiveEnabled: true));

            var calls = new List<(nint min, nint max, uint flags)>();
            bool nonAggressiveOk = MemoryOptimizer.SoftRelaxWorkingSetCore(
                new IntPtr(1234),
                aggressiveTrimEnabled: false,
                _ => (true, 204800, 1413120, 0x00000001u),
                (_, min, max, flags) =>
                {
                    calls.Add((min, max, flags));
                    return true;
                });

            Assert.True(nonAggressiveOk);
            Assert.Single(calls);
            Assert.Equal((nint)204800, calls[0].min);
            Assert.Equal((nint)1413120, calls[0].max);
            Assert.Equal(0x00000002u, calls[0].flags);
            Assert.DoesNotContain(calls, c => c.min == -1 && c.max == -1);

            calls.Clear();
            bool queryFailedOk = MemoryOptimizer.SoftRelaxWorkingSetCore(
                new IntPtr(1234),
                aggressiveTrimEnabled: false,
                _ => (false, 0, 0, 0u),
                (_, min, max, flags) =>
                {
                    calls.Add((min, max, flags));
                    return true;
                });

            Assert.False(queryFailedOk);
            Assert.Empty(calls);

            bool aggressiveOk = MemoryOptimizer.SoftRelaxWorkingSetCore(
                new IntPtr(1234),
                aggressiveTrimEnabled: true,
                _ => (true, 204800, 1413120, 0u),
                (_, min, max, flags) =>
                {
                    calls.Add((min, max, flags));
                    return true;
                });

            Assert.True(aggressiveOk);
            Assert.Single(calls);
            Assert.Equal((-1, -1, 0x00000002u), ((long)calls[0].min, (long)calls[0].max, calls[0].flags));

            if (!OperatingSystem.IsWindows())
                return;

            var settings = Substitute.For<ISettingsService>();
            settings.Exists(AppSettingsKeys.General.AggressiveRamTrimming).Returns(true);
            settings.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming, false).Returns(false);
            using var scope = AppServices.Override<ISettingsService>(settings);
            using var self = Process.GetCurrentProcess();
            Assert.True(MemoryOptimizer.SoftRelaxWorkingSetNative(self.Handle));
        }

        [Fact]
        public void WindowBackdropService_ResolvePreferredIconResourceId_Prefers32512_AndFallsBackTo1()
        {
            var probed = new List<IntPtr>();
            IntPtr expectedPrimary = new IntPtr(0x1111);

            IntPtr resolvedPrimary = WindowBackdropService.ResolvePreferredIconResourceId(resId =>
            {
                probed.Add(resId);
                return resId == (IntPtr)32512 ? expectedPrimary : IntPtr.Zero;
            });

            Assert.Equal(expectedPrimary, resolvedPrimary);
            Assert.Equal(new[] { (IntPtr)32512 }, probed);

            probed.Clear();
            IntPtr expectedFallback = new IntPtr(0x2222);

            IntPtr resolvedFallback = WindowBackdropService.ResolvePreferredIconResourceId(resId =>
            {
                probed.Add(resId);
                return resId == (IntPtr)1 ? expectedFallback : IntPtr.Zero;
            });

            Assert.Equal(expectedFallback, resolvedFallback);
            Assert.Equal(new[] { (IntPtr)32512, (IntPtr)1 }, probed);

            Assert.Equal(IntPtr.Zero, WindowBackdropService.ResolvePreferredIconResourceId(_ => IntPtr.Zero));
        }

        [Fact]
        public void AntiAfkService_BuildKeyUpLParam_AvoidsSignExtension_AndForegroundPulseIsEligible()
        {
            Assert.Equal((nuint)0xC0000001u, AntiAfkService.BuildKeyUpLParam(0u));

            const uint f15ScanCode = 0x66u;
            nuint lParamUp = AntiAfkService.BuildKeyUpLParam(f15ScanCode);
            nuint expected = (nuint)(1u | (f15ScanCode << 16) | (1u << 30) | (1u << 31));
            Assert.Equal(expected, lParamUp);
            Assert.Equal(0xC0660001UL, (ulong)lParamUp);

            IntPtr nativeLParam = unchecked((IntPtr)(nint)lParamUp);
            if (IntPtr.Size == 8)
            {
                Assert.Equal(0x00000000C0660001L, nativeLParam.ToInt64());
            }

            var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
            int silentCalls = 0;

            var fgSilentOutcome = AntiAfkService.EvaluateSilentFirstPulse(
                hWnd: new IntPtr(501),
                isHidden: false,
                isForegroundWindow: true,
                allowFocusSteal: false,
                nowUtc: now,
                intervalMinutes: 8,
                postPulse: _ =>
                {
                    silentCalls++;
                    return true;
                },
                previousFailures: 0);

            Assert.True(fgSilentOutcome.Succeeded);
            Assert.Equal(0, fgSilentOutcome.Failures);
            Assert.Equal(1, silentCalls);

            int fgFallbackCalls = 0;
            var fgFallbackOutcome = AntiAfkService.ProcessClientPulseForTest(
                hWnd: new IntPtr(502),
                isHidden: false,
                isForegroundWindow: true,
                allowFocusSteal: true,
                nowUtc: now,
                intervalMinutes: 8,
                postPulse: _ => false,
                sendForegroundSpace: _ =>
                {
                    fgFallbackCalls++;
                    return true;
                },
                previousFailures: 0);

            Assert.True(fgFallbackOutcome.Succeeded);
            Assert.Equal(0, fgFallbackOutcome.Failures);
            Assert.Equal(1, fgFallbackCalls);
        }

        [Fact]
        public async Task RobloxJobObjectService_DispatchCompletionMessage7_DoesNotBlockOnWasapiMtaWaiter()
        {
            const int testPid = 987654;
            RobloxAudioService.SeedDummyCachedAudioSessionForTest(testPid);

            using var mtaEntered = new ManualResetEventSlim(false);
            using var releaseMta = new ManualResetEventSlim(false);
            var mtaBlockerTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mtaBlockerThread = new Thread(() =>
            {
                try
                {
                    RobloxAudioService.RunOnWasapiMtaThread(() =>
                    {
                        mtaEntered.Set();
                        releaseMta.Wait(TimeSpan.FromSeconds(5));
                        return true;
                    });
                    mtaBlockerTcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    mtaBlockerTcs.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "test-wasapi-mta-blocker"
            };
            mtaBlockerThread.Start();

            try
            {
                Assert.True(mtaEntered.Wait(TimeSpan.FromSeconds(5)));

                var dispatchTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var iocpSimThread = new Thread(() =>
                {
                    try
                    {
                        dispatchTcs.TrySetResult(RobloxJobObjectService.DispatchCompletionMessage(7, (nuint)testPid));
                    }
                    catch (Exception ex)
                    {
                        dispatchTcs.TrySetException(ex);
                    }
                })
                {
                    IsBackground = true,
                    Name = "test-iocp-dispatcher"
                };
                iocpSimThread.Start();

                Task completed = await Task.WhenAny(dispatchTcs.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
                Assert.Same(dispatchTcs.Task, completed);
                Assert.True(await dispatchTcs.Task);
            }
            finally
            {
                releaseMta.Set();
                await mtaBlockerTcs.Task;
            }

            bool drained = SpinWait.SpinUntil(() => !RobloxAudioService.HasCachedAudioSessionForTest(testPid), TimeSpan.FromSeconds(2));
            Assert.True(drained);
            RobloxAudioService.ClearAudioSessionCache();
        }

        [Fact]
        public void RobloxSingletonService_RestrictedInteractiveSddl_IncludesLowIntegritySacl_AndCompilesOnWindows()
        {
            Assert.Contains("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;IU)", RobloxSingletonService.RestrictedInteractiveSddl, StringComparison.Ordinal);
            Assert.Contains("S:(ML;;NW;;;LW)", RobloxSingletonService.RestrictedInteractiveSddl, StringComparison.Ordinal);
            Assert.Equal("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;IU)S:(ML;;NW;;;LW)", RobloxSingletonService.RestrictedInteractiveSddl);

            if (!OperatingSystem.IsWindows())
                return;

            IntPtr pSd = IntPtr.Zero;
            bool allocatedWithLocalAlloc = false;
            try
            {
                RobloxSingletonService.Stop();
                pSd = RobloxSingletonService.BuildSecurityDescriptor(
                    RobloxSingletonService.RestrictedInteractiveSddl,
                    out allocatedWithLocalAlloc);
                Assert.NotEqual(IntPtr.Zero, pSd);
                Assert.True(allocatedWithLocalAlloc);

                RobloxSingletonService.EnsureMutex(true);
                Assert.False(RobloxSingletonService.AccessDenied);
                Assert.DoesNotContain("Warning", RobloxSingletonService.StatusText, StringComparison.Ordinal);
                Assert.DoesNotContain("Error", RobloxSingletonService.StatusText, StringComparison.Ordinal);
                if (Process.GetProcessesByName("RobloxPlayerBeta").Length == 0)
                {
                    Assert.True(RobloxSingletonService.MutexHeld);
                }
            }
            finally
            {
                RobloxSingletonService.FreeSecurityDescriptor(pSd, allocatedWithLocalAlloc);
                RobloxSingletonService.Stop();
            }

            Assert.False(RobloxSingletonService.MutexHeld);
        }

        [Fact]
        public void RobloxSingletonService_EnsureMutex_JoinTimeoutRace_DoesNotDisposeHandlesWhileHolderActive()
        {
            using var holderEntered = new ManualResetEventSlim(false);
            using var allowHolderExit = new ManualResetEventSlim(false);
            RobloxSingletonService.HolderHandle? capturedHolder = null;
            Exception? holderException = null;
            bool observedStopSet = false;

            try
            {
                RobloxSingletonService.Stop();
                RobloxSingletonService.EnsureMutexCore(
                    enabled: true,
                    joinTimeout: TimeSpan.FromMilliseconds(20),
                    startHolderOverride: (holder, _) =>
                    {
                        try
                        {
                            capturedHolder = holder;
                            holderEntered.Set();
                            allowHolderExit.Wait(TimeSpan.FromSeconds(5));
                            observedStopSet = holder.Stop.IsSet;
                            holder.Ready.Set();
                            holder.Stop.Wait(10);
                        }
                        catch (Exception ex)
                        {
                            holderException = ex;
                        }
                        finally
                        {
                            holder.Release();
                        }
                    });

                Assert.True(holderEntered.Wait(TimeSpan.FromSeconds(5)));
                Assert.NotNull(capturedHolder);

                // Teardown times out waiting on the holder thread (20ms), yet must not dispose active handles.
                RobloxSingletonService.EnsureMutexCore(
                    enabled: false,
                    joinTimeout: TimeSpan.FromMilliseconds(20),
                    startHolderOverride: null);

                Assert.False(capturedHolder!.IsDisposed);
                Assert.False(RobloxSingletonService.MutexHeld);

                allowHolderExit.Set();
                bool disposedAfterExit = SpinWait.SpinUntil(() => capturedHolder.IsDisposed, TimeSpan.FromSeconds(2));
                Assert.True(disposedAfterExit);
                Assert.True(observedStopSet);
                Assert.Null(holderException);
            }
            finally
            {
                allowHolderExit.Set();
                RobloxSingletonService.Stop();
            }
        }

        [Fact]
        public async Task RobloxSingletonService_EnsureMutex_ConcurrentRapidAlternation_HasZeroObjectDisposedExceptions()
        {
            var exceptions = new ConcurrentQueue<Exception>();
            try
            {
                RobloxSingletonService.Stop();
                Task workerA = Task.Run(() =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        try
                        {
                            RobloxSingletonService.EnsureMutex(true);
                            RobloxSingletonService.EnsureMutex(false);
                        }
                        catch (Exception ex)
                        {
                            exceptions.Enqueue(ex);
                        }
                    }
                });

                Task workerB = Task.Run(() =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        try
                        {
                            RobloxSingletonService.EnsureMutex(false);
                            RobloxSingletonService.EnsureMutex(true);
                        }
                        catch (Exception ex)
                        {
                            exceptions.Enqueue(ex);
                        }
                    }
                });

                await Task.WhenAll(workerA, workerB);
                Assert.Empty(exceptions);
            }
            finally
            {
                RobloxSingletonService.Stop();
            }

            Assert.False(RobloxSingletonService.MutexHeld);
        }

        [Fact]
        public async Task RobloxSingletonService_HoldHandles_ZeroEvent_ReleasesMutexAndExitsWithoutWaiting()
        {
            Assert.True(RobloxSingletonService.ShouldAbortHoldOnZeroEvent(IntPtr.Zero));
            Assert.False(RobloxSingletonService.ShouldAbortHoldOnZeroEvent(new IntPtr(1)));

            RobloxSingletonService.Stop();
            try
            {
                // Case 1: mutex created and owned, but CreateEventW fails (IntPtr.Zero) with ERROR_ACCESS_DENIED (5).
                var releaseCalls = new List<IntPtr>();
                var closeCalls = new List<IntPtr>();
                var ownedHolder = new RobloxSingletonService.HolderHandle(initialRefs: 2);
                try
                {
                    long gen1 = RobloxSingletonService.ReserveGenerationForTest();
                    var ownedOps = new RobloxSingletonService.HolderNativeOps(
                        CreateMutex: (_, _, _) => (new IntPtr(0x1234), 0),
                        CreateEvent: (_, _, _, _) => (IntPtr.Zero, 5),
                        WaitForSingleObject: (_, _) => 0u,
                        ReleaseMutex: h => releaseCalls.Add(h),
                        CloseHandle: h => closeCalls.Add(h));

                    await Task.Run(() => RobloxSingletonService.HoldHandlesCore(
                        ownedHolder,
                        gen1,
                        ownedOps,
                        buildSecurityDescriptor: false)).WaitAsync(TimeSpan.FromSeconds(2));

                    Assert.True(ownedHolder.Ready.IsSet);
                    Assert.False(RobloxSingletonService.MutexHeld);
                    Assert.True(RobloxSingletonService.AccessDenied);
                    Assert.Equal(new[] { new IntPtr(0x1234) }, releaseCalls);
                    Assert.Equal(new[] { new IntPtr(0x1234) }, closeCalls);
                }
                finally
                {
                    ownedHolder.Release();
                }
                Assert.True(ownedHolder.IsDisposed);

                // Case 2: mutex already exists (not owned yet) and CreateEventW fails (IntPtr.Zero) with err 87.
                releaseCalls.Clear();
                closeCalls.Clear();
                int waitCalls = 0;
                var unownedHolder = new RobloxSingletonService.HolderHandle(initialRefs: 2);
                try
                {
                    long gen2 = RobloxSingletonService.ReserveGenerationForTest();
                    var unownedOps = new RobloxSingletonService.HolderNativeOps(
                        CreateMutex: (_, _, _) => (new IntPtr(0x5678), 183),
                        CreateEvent: (_, _, _, _) => (IntPtr.Zero, 87),
                        WaitForSingleObject: (_, _) =>
                        {
                            waitCalls++;
                            return 0x00000102u;
                        },
                        ReleaseMutex: h => releaseCalls.Add(h),
                        CloseHandle: h => closeCalls.Add(h));

                    await Task.Run(() => RobloxSingletonService.HoldHandlesCore(
                        unownedHolder,
                        gen2,
                        unownedOps,
                        buildSecurityDescriptor: false)).WaitAsync(TimeSpan.FromSeconds(2));

                    Assert.True(unownedHolder.Ready.IsSet);
                    Assert.False(RobloxSingletonService.MutexHeld);
                    Assert.False(RobloxSingletonService.AccessDenied);
                    Assert.Equal(1, waitCalls);
                    Assert.Empty(releaseCalls);
                    Assert.Equal(new[] { new IntPtr(0x5678) }, closeCalls);
                }
                finally
                {
                    unownedHolder.Release();
                }
                Assert.True(unownedHolder.IsDisposed);
            }
            finally
            {
                RobloxSingletonService.Stop();
            }

            Assert.False(RobloxSingletonService.MutexHeld);
            Assert.False(RobloxSingletonService.AccessDenied);
            Assert.DoesNotContain("Warning", RobloxSingletonService.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain("Error", RobloxSingletonService.StatusText, StringComparison.Ordinal);
        }
    }
}
