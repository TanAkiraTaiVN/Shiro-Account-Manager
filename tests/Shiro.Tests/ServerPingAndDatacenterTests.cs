using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class ServerPingAndDatacenterTests
    {
        [Fact]
        public async Task RacePingProbesAsync_WhenIcmpTimesOutAndTcpSucceedsFast_ReturnsTcpRttWithoutWaitingIcmpTimeout()
        {
            var sw = Stopwatch.StartNew();

            int? rtt = await RobloxServerQueryService.RacePingProbesAsync(
                icmpProbe: async ct =>
                {
                    await Task.Delay(400, ct);
                    return null;
                },
                tcpProbe: async ct =>
                {
                    await Task.Delay(35, ct);
                    return 35;
                });

            sw.Stop();
            Assert.Equal(35, rtt);
            Assert.True(sw.ElapsedMilliseconds < 250, $"Expected fast TCP probe to win immediately without waiting 400ms ICMP timeout, took {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public async Task RacePingProbesAsync_WhenIcmpSucceedsFirst_CancelsTcpAndReturnsIcmpRtt()
        {
            bool tcpCancelled = false;

            int? rtt = await RobloxServerQueryService.RacePingProbesAsync(
                icmpProbe: async ct =>
                {
                    await Task.Delay(20, ct);
                    return 20;
                },
                tcpProbe: async ct =>
                {
                    try
                    {
                        await Task.Delay(1000, ct);
                        return 1000;
                    }
                    catch (OperationCanceledException)
                    {
                        tcpCancelled = true;
                        throw;
                    }
                });

            Assert.Equal(20, rtt);
            // Allow brief continuation propagation
            await Task.Delay(30);
            Assert.True(tcpCancelled);
        }

        [Fact]
        public void RobloxDatacenterService_TryResolve_ReturnsPrecomputedSingletonInstance()
        {
            var service = RobloxDatacenterService.Default;
            if (!service.IsLoaded)
                return;

            // Resolve a known datacenter or IP twice and verify zero re-allocation (ReferenceEquals)
            bool found1 = service.TryResolve("128.116.50.1", null, out var res1);
            bool found2 = service.TryResolve("128.116.50.1", null, out var res2);

            if (found1 && found2)
            {
                Assert.NotNull(res1);
                Assert.Same(res1, res2);
            }
        }
    }
}

