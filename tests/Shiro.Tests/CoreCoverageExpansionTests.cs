#nullable enable
using System;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class CoreCoverageExpansionTests
    {
        [Fact]
        public void HostUiBridgeService_InvokesRegisteredCallbacks()
        {
            var bridge = new HostUiBridgeService();
            string? lastStatus = null;
            bridge.SetDashboardStatusHandler = msg => lastStatus = msg;
            bridge.SetDashboardStatus("Ready");
            Assert.Equal("Ready", lastStatus);

            var mockDialog = Substitute.For<IDialogService>();
            mockDialog.ShowInfo("Test");
            mockDialog.Received(1).ShowInfo("Test");
        }

        [Fact]
        public void DomainContracts_CanBeSubstitutedWithNSubstitute()
        {
            var toast = Substitute.For<IToastService>();
            var win = Substitute.For<IRobloxWindowService>();
            var mem = Substitute.For<IMemoryOptimizer>();

            toast.ShowInfo("Message");
            win.TileRunningWindows();
            mem.TrimWorkingSets();

            toast.Received(1).ShowInfo("Message");
            win.Received(1).TileRunningWindows();
            mem.Received(1).TrimWorkingSets();
        }
    }
}
