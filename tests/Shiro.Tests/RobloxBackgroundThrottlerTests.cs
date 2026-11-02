#nullable enable
using System;
using System.Diagnostics;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class RobloxBackgroundThrottlerTests
    {
        [Fact]
        public void Throttler_WhenProcessLosesFocus_MarksAsBackgroundAndAppliesThrottle()
        {
            var service = new RobloxBackgroundThrottlerService();
            service.IsEnabled = true;
            service.TargetBackgroundFps = 10;

            int dummyPid = Process.GetCurrentProcess().Id;
            service.OnForegroundWindowChanged(activeProcessId: dummyPid + 100, runningRobloxPids: new[] { dummyPid });

            Assert.True(service.IsProcessThrottled(dummyPid));
        }

        [Fact]
        public void Throttler_WhenRepeatedWindowChangesOccur_ProcessRemainsThrottled()
        {
            var service = new RobloxBackgroundThrottlerService();
            service.IsEnabled = true;
            service.TargetBackgroundFps = 10;

            int dummyPid = Process.GetCurrentProcess().Id;
            // 1st transition to background
            service.OnForegroundWindowChanged(activeProcessId: dummyPid + 100, runningRobloxPids: new[] { dummyPid });
            Assert.True(service.IsProcessThrottled(dummyPid));

            // 2nd focus switch to another non-Roblox window
            service.OnForegroundWindowChanged(activeProcessId: dummyPid + 200, runningRobloxPids: new[] { dummyPid });
            Assert.True(service.IsProcessThrottled(dummyPid));
        }

        [Fact]
        public void Throttler_WhenProcessGainsFocus_RestoresActiveState()
        {
            var service = new RobloxBackgroundThrottlerService();
            service.IsEnabled = true;

            int dummyPid = Process.GetCurrentProcess().Id;
            service.OnForegroundWindowChanged(activeProcessId: dummyPid, runningRobloxPids: new[] { dummyPid });

            Assert.False(service.IsProcessThrottled(dummyPid));
        }

        [Fact]
        public void Throttler_WhenDisabled_DoesNotThrottleProcesses()
        {
            var service = new RobloxBackgroundThrottlerService();
            service.IsEnabled = false;

            int dummyPid = Process.GetCurrentProcess().Id;
            service.OnForegroundWindowChanged(activeProcessId: 999999, runningRobloxPids: new[] { dummyPid });

            Assert.False(service.IsProcessThrottled(dummyPid));
        }

        [Fact]
        public void Throttler_ApplyThrottleState_DirectCall_UpdatesThrottledState()
        {
            var service = new RobloxBackgroundThrottlerService();
            int dummyPid = Process.GetCurrentProcess().Id;

            service.ApplyThrottleState(dummyPid, isForeground: false, backgroundFps: 15);
            Assert.True(service.IsProcessThrottled(dummyPid));

            service.ApplyThrottleState(dummyPid, isForeground: true);
            Assert.False(service.IsProcessThrottled(dummyPid));
        }

        [Fact]
        public void Throttler_TrimWorkingSet_CurrentProcess_DoesNotThrow()
        {
            var service = new RobloxBackgroundThrottlerService();
            int dummyPid = Process.GetCurrentProcess().Id;

            bool result = service.TrimWorkingSet(dummyPid);
            Assert.True(result || !OperatingSystem.IsWindows());
        }

        [Fact]
        public void Throttler_SettingIsEnabledFalse_RestoresAllThrottledProcesses()
        {
            var service = new RobloxBackgroundThrottlerService();
            service.IsEnabled = true;
            int dummyPid = Process.GetCurrentProcess().Id;
            service.OnForegroundWindowChanged(activeProcessId: 999999, runningRobloxPids: new[] { dummyPid });
            Assert.True(service.IsProcessThrottled(dummyPid));

            service.IsEnabled = false;
            Assert.False(service.IsProcessThrottled(dummyPid));
        }

        [Fact]
        public void Throttler_NullOrEmptyRobloxPids_HandledGracefully()
        {
            var service = new RobloxBackgroundThrottlerService();
            service.IsEnabled = true;

            service.OnForegroundWindowChanged(1234, null);
            service.OnForegroundWindowChanged(1234, Array.Empty<int>());
            Assert.False(service.IsProcessThrottled(1234));
        }

        [Fact]
        public void AppSettingsKeys_Contains_BackgroundThrottlingKeys()
        {
            Assert.Equal("EnableBackgroundThrottling", AppSettingsKeys.General.EnableBackgroundThrottling);
            Assert.Equal("BackgroundTargetFps", AppSettingsKeys.General.BackgroundTargetFps);
        }
    }
}
