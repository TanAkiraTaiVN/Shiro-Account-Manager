#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
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
    }
}
