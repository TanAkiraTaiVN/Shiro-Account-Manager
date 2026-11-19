using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class RobloxAudioServiceConcurrencyTests
    {
        [Fact]
        public async Task ConcurrentInvalidateAndClearCache_WithSmartMuteToggle_DoesNotThrowOrDeadlock()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            Task t1 = Task.Run(() =>
            {
                int pid = 1000;
                while (!cts.IsCancellationRequested)
                {
                    RobloxAudioService.InvalidateCachedAudioSession(pid++);
                }
            });
            Task t2 = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    RobloxAudioService.ClearAudioSessionCache();
                }
            });
            Task t3 = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    RobloxAudioService.ConfigureSmartMute(false);
                }
            });

            await Task.WhenAll(t1, t2, t3);
            Assert.False(RobloxAudioService.IsSmartMuteEnabled);
        }

        [Fact]
        public async Task ConfigureSmartMuteFalse_DuringInFlightApplyFocus_PreventsStateOverwriteWhenApplyFocusFinishes()
        {
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);

            try
            {
                RobloxAudioService.ConfigureSmartMute(true);
                var pids = new List<int> { 4242 };

                Task evalTask = Task.Run(() =>
                {
                    RobloxAudioService.EvaluateSmartMuteWithHook(4242, pids, 10_000, (_, _) =>
                    {
                        entered.Set();
                        release.Wait(2_000);
                        return (true, 0, 1, 1);
                    });
                });

                Assert.True(entered.Wait(2_000));

                RobloxAudioService.ConfigureSmartMute(false);
                Assert.False(RobloxAudioService.IsSmartMuteEnabled);

                release.Set();
                await evalTask;

                Assert.False(RobloxAudioService.IsSmartMuteEnabled);
                var lastPidField = typeof(RobloxAudioService).GetField("_lastActiveRobloxPid", BindingFlags.NonPublic | BindingFlags.Static);
                var lastCountField = typeof(RobloxAudioService).GetField("_lastRunningRobloxCount", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(lastPidField);
                Assert.NotNull(lastCountField);
                Assert.Equal(-1, (int)lastPidField!.GetValue(null)!);
                Assert.Equal(-1, (int)lastCountField!.GetValue(null)!);
            }
            finally
            {
                release.Set();
                RobloxAudioService.ConfigureSmartMute(false);
            }
        }

        [Fact]
        public async Task ConfigureSmartMuteToggleAba_DuringInFlightApplyFocus_RejectsStaleVersionCommit()
        {
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);

            try
            {
                RobloxAudioService.ConfigureSmartMute(true);
                var pids = new List<int> { 4242 };

                Task evalTask = Task.Run(() =>
                {
                    RobloxAudioService.EvaluateSmartMuteWithHook(4242, pids, 10_000, (_, _) =>
                    {
                        entered.Set();
                        release.Wait(2_000);
                        return (true, 0, 1, 1);
                    });
                });

                Assert.True(entered.Wait(2_000));

                RobloxAudioService.ConfigureSmartMute(false);
                RobloxAudioService.ConfigureSmartMute(true);

                var lastCountField = typeof(RobloxAudioService).GetField("_lastRunningRobloxCount", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(lastCountField);
                SpinWait.SpinUntil(() => (int)lastCountField!.GetValue(null)! == 0, TimeSpan.FromMilliseconds(200));

                release.Set();
                await evalTask;

                int reapplied = 0;
                RobloxAudioService.EvaluateSmartMuteWithHook(4242, pids, 20_000, (_, _) =>
                {
                    reapplied++;
                    return (true, 0, 1, 1);
                });

                Assert.Equal(1, reapplied);
            }
            finally
            {
                release.Set();
                RobloxAudioService.ConfigureSmartMute(false);
            }
        }

        [Fact]
        public async Task ConfigureSmartMuteToggleAba_WhenStaleApplyFocusFails_DoesNotPoisonComFailureBackoff()
        {
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);

            try
            {
                RobloxAudioService.ConfigureSmartMute(true);
                var pids = new List<int> { 4242 };

                Task evalTask = Task.Run(() =>
                {
                    RobloxAudioService.EvaluateSmartMuteWithHook(4242, pids, 10_000, (_, _) =>
                    {
                        entered.Set();
                        release.Wait(2_000);
                        return (false, 0, 0, 0);
                    });
                });

                Assert.True(entered.Wait(2_000));

                RobloxAudioService.ConfigureSmartMute(false);
                RobloxAudioService.ConfigureSmartMute(true);

                var lastCountField = typeof(RobloxAudioService).GetField("_lastRunningRobloxCount", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(lastCountField);
                SpinWait.SpinUntil(() => (int)lastCountField!.GetValue(null)! == 0, TimeSpan.FromMilliseconds(200));

                release.Set();
                await evalTask;

                int reapplied = 0;
                RobloxAudioService.EvaluateSmartMuteWithHook(4242, pids, 12_000, (_, _) =>
                {
                    reapplied++;
                    return (true, 0, 1, 1);
                });

                Assert.Equal(1, reapplied);
            }
            finally
            {
                release.Set();
                RobloxAudioService.ConfigureSmartMute(false);
            }
        }
    }
}
