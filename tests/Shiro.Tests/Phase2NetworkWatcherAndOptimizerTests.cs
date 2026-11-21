#nullable enable
using System.Net.NetworkInformation;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class Phase2NetworkWatcherAndOptimizerTests
    {
        [Fact]
        public void NetworkSignatureHelper_IsVpnOrTunnelAdapter_IgnoresResidentialPppoe_DetectsVpns()
        {
            Assert.False(NetworkSignatureHelper.IsVpnOrTunnelAdapter("Broadband Connection", "WAN Miniport (PPPOE)", NetworkInterfaceType.Ppp));
            Assert.False(NetworkSignatureHelper.IsVpnOrTunnelAdapter("Viettel Fiber", "PPPoE Connection", NetworkInterfaceType.Ppp));
            Assert.False(NetworkSignatureHelper.IsVpnOrTunnelAdapter("FPT Telecom", "Dial-up PPP", NetworkInterfaceType.Ppp));
            Assert.False(NetworkSignatureHelper.IsVpnOrTunnelAdapter("Teredo Tunneling Pseudo-Interface", "Microsoft Teredo", NetworkInterfaceType.Tunnel));
            Assert.False(NetworkSignatureHelper.IsVpnOrTunnelAdapter("Ethernet", "Intel(R) Ethernet", NetworkInterfaceType.Ethernet));

            Assert.True(NetworkSignatureHelper.IsVpnOrTunnelAdapter("Work VPN", "WAN Miniport (L2TP)", NetworkInterfaceType.Ppp));
            Assert.True(NetworkSignatureHelper.IsVpnOrTunnelAdapter("Corp", "WAN Miniport (SSTP)", NetworkInterfaceType.Ppp));
            Assert.True(NetworkSignatureHelper.IsVpnOrTunnelAdapter("Office", "WAN Miniport (IKEv2)", NetworkInterfaceType.Ppp));
            Assert.True(NetworkSignatureHelper.IsVpnOrTunnelAdapter("ProtonVPN", "ProtonVPN Tunnel", NetworkInterfaceType.Tunnel));
            Assert.True(NetworkSignatureHelper.IsVpnOrTunnelAdapter("WARP", "Cloudflare WARP", NetworkInterfaceType.GenericModem));
            Assert.True(NetworkSignatureHelper.IsVpnOrTunnelAdapter("wg0", "WireGuard Tunnel", NetworkInterfaceType.Unknown));
        }

        [Fact]
        public void Watcher_DefaultMemoryThresholdMb_IsSafe2048Mb_AndPreservesSavedValues()
        {
            Assert.Equal(2048, RobloxWatcher.DefaultMemoryThresholdMb);

            var service = new ServerWatcherConfigService();
            var emptySection = new IniSection("Watcher");
            ServerWatcherConfig defaults = service.Load(emptySection, syncRuntime: false);
            Assert.Equal(2048m, defaults.MemoryThresholdMb);

            var savedSection = new IniSection("Watcher");
            savedSection.Set("MemoryLowValue", "1536");
            ServerWatcherConfig loaded = service.Load(savedSection, syncRuntime: false);
            Assert.Equal(1536m, loaded.MemoryThresholdMb);

            Assert.False(RobloxWatcher.ShouldCloseForMemoryThreshold(true, workingSetMb: 1500, thresholdMb: RobloxWatcher.DefaultMemoryThresholdMb));
            Assert.True(RobloxWatcher.ShouldCloseForMemoryThreshold(true, workingSetMb: 2500, thresholdMb: RobloxWatcher.DefaultMemoryThresholdMb));
        }

        [Fact]
        public void MemoryOptimizer_ShouldRestoreFullPerformanceOnSolo_DefersToActiveBackgroundThrottler()
        {
            Assert.True(MemoryOptimizer.ShouldRestoreFullPerformanceOnSolo(1, backgroundThrottlerEnabled: false));
            Assert.False(MemoryOptimizer.ShouldRestoreFullPerformanceOnSolo(1, backgroundThrottlerEnabled: true));
            Assert.False(MemoryOptimizer.ShouldRestoreFullPerformanceOnSolo(2, backgroundThrottlerEnabled: false));
            Assert.True(MemoryOptimizer.ShouldRestoreFullPerformanceOnSolo(1));
        }
    }
}
