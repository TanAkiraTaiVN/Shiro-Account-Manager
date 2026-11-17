#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class P0Batch2RegressionTests
    {
        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root containing Shiro.sln.");
        }

        [Fact]
        public void Crit02_ShouldCloseForMemoryThreshold_KillsOnlyAboveThresholdAndUsesHighMemoryMessage()
        {
            Assert.True(RobloxWatcher.ShouldCloseForMemoryThreshold(true, workingSetMb: 1024, thresholdMb: 512));
            Assert.False(RobloxWatcher.ShouldCloseForMemoryThreshold(true, workingSetMb: 128, thresholdMb: 512));
            Assert.False(RobloxWatcher.ShouldCloseForMemoryThreshold(true, workingSetMb: 512, thresholdMb: 512));
            Assert.False(RobloxWatcher.ShouldCloseForMemoryThreshold(true, workingSetMb: 0, thresholdMb: 512));
            Assert.False(RobloxWatcher.ShouldCloseForMemoryThreshold(false, workingSetMb: 2048, thresholdMb: 512));

            string watcherPath = Path.Combine(FindRepoRoot(), "src", "Shiro", "Services", "Windows", "RobloxWatcher.cs");
            string source = File.ReadAllText(watcherPath);
            Assert.Contains("High Memory", source, StringComparison.Ordinal);
        }

        [Fact]
        public void HighG2_HideAndShowWindowsCore_HandlesWin32PreviousVisibilityReturnSemantics()
        {
            var visibility = new Dictionary<IntPtr, bool>
            {
                [(IntPtr)1001] = true,  // currently visible
                [(IntPtr)1002] = true   // currently visible
            };

            // Simulate Win32 ShowWindow: returns TRUE iff window WAS visible BEFORE the call!
            bool FakeShowWindow(IntPtr hWnd, int cmd)
            {
                bool wasVisible = visibility.TryGetValue(hWnd, out bool cur) && cur;
                visibility[hWnd] = cmd != 0; // SW_HIDE == 0
                return wasVisible;
            }

            bool FakeIsVisible(IntPtr hWnd) => visibility.TryGetValue(hWnd, out bool cur) && cur;

            try
            {
                int hidden = RobloxWindowService.HideWindowsCore(visibility.Keys, FakeIsVisible, FakeShowWindow);
                Assert.Equal(2, hidden);
                Assert.True(RobloxWindowService.AreWindowsHiddenCore(_ => true));

                // Calling ShowWindow(SW_RESTORE) on hidden windows returns FALSE (previous state was hidden),
                // yet both windows become visible afterward!
                int shown = RobloxWindowService.ShowWindowsCore(visibility.Keys, FakeIsVisible, FakeShowWindow);
                Assert.Equal(2, shown);
                Assert.False(RobloxWindowService.AreWindowsHiddenCore(_ => true));
            }
            finally
            {
                RobloxWindowService.ShowWindowsCore(Array.Empty<IntPtr>(), _ => false, (_, _) => false);
            }
        }

        [Fact]
        public void HighG7_SmartMute_DoesNotCommitActivePidOnFailure_AndRetriesSamePidAfterBackoff()
        {
            try
            {
                RobloxAudioService.ConfigureSmartMute(true);
                int attempts = 0;
                var pids = new List<int> { 4242 };

                // Tick 0 (t = 5s): Initial focus on Roblox pid 4242 succeeds and locks _lastRunningRobloxCount = 1.
                RobloxAudioService.EvaluateSmartMuteWithHook(4242, pids, 5_000, (activePid, _) =>
                {
                    attempts++;
                    return (true, 0, 1, 1);
                });
                Assert.Equal(1, attempts);

                // Tick 1 (t = 20s): Focus moves away from Roblox (fgPid = 999 -> activePid = 0), WASAPI call fails transiently.
                RobloxAudioService.EvaluateSmartMuteWithHook(999, pids, 20_000, (activePid, _) =>
                {
                    attempts++;
                    return (false, 0, 0, 0);
                });
                Assert.Equal(2, attempts);

                // Tick 2 (t = 25s): within 10s ComFailureBackoffMs -> must stay backed off.
                RobloxAudioService.EvaluateSmartMuteWithHook(999, pids, 25_000, (activePid, _) =>
                {
                    attempts++;
                    return (true, 1, 0, 1);
                });
                Assert.Equal(2, attempts);

                // Tick 3 (t = 35s): past 10s ComFailureBackoffMs with SAME fgPid=999 -> MUST retry and succeed!
                RobloxAudioService.EvaluateSmartMuteWithHook(999, pids, 35_000, (activePid, _) =>
                {
                    attempts++;
                    return (true, 1, 0, 1);
                });
                Assert.Equal(3, attempts);
            }
            finally
            {
                RobloxAudioService.ConfigureSmartMute(false);
            }
        }

        [Fact]
        public void HighG3_WasapiOperations_ExecuteOnSingleDedicatedMtaWorkerThread()
        {
            var contexts = new List<(ApartmentState Apartment, int ThreadId)>();

            // Simulate Avalonia UI thread (STA) caller
            var staThread = new Thread(() =>
            {
                contexts.Add(RobloxAudioService.ProbeWasapiExecutionContext());
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();

            // Simulate SmartMute Timer / ThreadPool (MTA) caller on a different thread
            var mtaThread = new Thread(() =>
            {
                contexts.Add(RobloxAudioService.ProbeWasapiExecutionContext());
            });
            mtaThread.SetApartmentState(ApartmentState.MTA);
            mtaThread.Start();
            mtaThread.Join();

            Assert.Equal(2, contexts.Count);
            Assert.All(contexts, ctx => Assert.Equal(ApartmentState.MTA, ctx.Apartment));
            Assert.Equal(contexts[0].ThreadId, contexts[1].ThreadId);

            // Structural guard: ForEachRobloxAudioSession & TryForEachCachedAudioSession must NOT call CoUninitialize per call
            string audioMainPath = Path.Combine(FindRepoRoot(), "src", "Shiro", "Services", "Windows", "RobloxAudioService.cs");
            string audioWasapiPath = Path.Combine(FindRepoRoot(), "src", "Shiro", "Services", "Windows", "RobloxAudioService.Wasapi.cs");
            Assert.DoesNotContain("CoUninitialize();", File.ReadAllText(audioMainPath), StringComparison.Ordinal);

            // TryForEachCachedAudioSession must not call CoInitializeEx/CoUninitialize directly outside dedicated worker
            string wasapiSource = File.ReadAllText(audioWasapiPath);
            int tryCachedIdx = wasapiSource.IndexOf("TryForEachCachedAudioSession(", StringComparison.Ordinal);
            Assert.True(tryCachedIdx >= 0);
            int tryCachedBodyEnd = wasapiSource.IndexOf("CLSID_MMDeviceEnumerator", tryCachedIdx, StringComparison.Ordinal);
            string tryCachedSlice = wasapiSource.Substring(tryCachedIdx, tryCachedBodyEnd - tryCachedIdx);
            Assert.DoesNotContain("CoUninitialize()", tryCachedSlice, StringComparison.Ordinal);
        }
    }
}
