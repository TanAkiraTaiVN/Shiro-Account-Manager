#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class RobloxSingletonServiceBehaviorTests : IDisposable
    {
        public RobloxSingletonServiceBehaviorTests()
        {
            RobloxSingletonService.Apply(true);
        }

        public void Dispose()
        {
            RobloxSingletonService.Apply(true);
        }

        [Fact]
        public void ServiceProperties_ReflectValidStates()
        {
            Assert.True(RobloxSingletonService.IsEnabled);

            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            bool expectedElevated = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            Assert.Equal(expectedElevated, RobloxSingletonService.IsElevated);

            string status = RobloxSingletonService.StatusText;
            Assert.False(string.IsNullOrWhiteSpace(status));

            int totalClosed = RobloxSingletonService.TotalClosed;
            Assert.InRange(totalClosed, 0, int.MaxValue);

            Assert.False(RobloxSingletonService.AccessDenied);
        }

        [Fact]
        public void Apply_ToggleEnabledAndDisabled_UpdatesStateProperly()
        {
            try
            {
                RobloxSingletonService.Apply(false);
                Assert.False(RobloxSingletonService.IsEnabled);
                Assert.Contains("Disabled", RobloxSingletonService.StatusText);

                RobloxSingletonService.Apply(true);
                Assert.True(RobloxSingletonService.IsEnabled);
            }
            finally
            {
                RobloxSingletonService.Apply(true);
            }
        }

        [Fact]
        public void Sweep_ForceSweep_ExecutesWithoutThrowing()
        {
            var ex = Record.Exception(() =>
            {
                int closed = RobloxSingletonService.Sweep(force: true);
                Assert.InRange(closed, 0, int.MaxValue);
            });
            Assert.Null(ex);
        }

        [Fact]
        public void CloseEventsInPid_InvalidPid_ReturnsZeroClosed()
        {
            var (closed, accessDenied, isPending) = RobloxSingletonService.CloseEventsInPid(-1);
            Assert.Equal(0, closed);
            Assert.False(accessDenied);
            Assert.False(isPending);

            int currentPid = Process.GetCurrentProcess().Id;
            var (closedCurrent, _, _) = RobloxSingletonService.CloseEventsInPid(currentPid);
            Assert.Equal(0, closedCurrent);
        }

        [Fact]
        public async Task WaitForClientUnlockedAsync_InvalidOrDisabledPids_HandledProperly()
        {
            // When target PID is 0 and no Roblox client is running, returns false after timeout
            bool resultZero = await RobloxSingletonService.WaitForClientUnlockedAsync(0, TimeSpan.FromMilliseconds(50));
            Assert.False(resultZero);

            // When disabled, returns true immediately
            try
            {
                RobloxSingletonService.Apply(false);
                bool resultDisabled = await RobloxSingletonService.WaitForClientUnlockedAsync(999999, TimeSpan.FromMilliseconds(50));
                Assert.True(resultDisabled);
            }
            finally
            {
                RobloxSingletonService.Apply(true);
            }

            // Non-existent PID with timeout returns false
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            bool resultDead = await RobloxSingletonService.WaitForClientUnlockedAsync(
                targetPid: 99999999,
                timeout: TimeSpan.FromSeconds(2),
                cancellationToken: cts.Token);
            Assert.False(resultDead);
        }

        [Fact]
        public void StatusChanged_EventSubscription_WorksWithoutThrowing()
        {
            bool fired = false;
            Action handler = () => fired = true;

            RobloxSingletonService.StatusChanged += handler;
            try
            {
                // Toggle to trigger status changed
                RobloxSingletonService.Apply(false);
                Assert.True(fired);
            }
            finally
            {
                RobloxSingletonService.StatusChanged -= handler;
                RobloxSingletonService.Apply(true);
            }
        }
    }
}
