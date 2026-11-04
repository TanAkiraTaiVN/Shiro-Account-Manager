#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class ProxyManagerAndAntiAfkBehaviorTests
    {
        [Fact]
        public async Task ProxyManagerService_LatencyTestingAndCache_CoversBranches()
        {
            ProxyManagerService.InvalidateCache();
            Assert.Contains("proxies.txt", ProxyManagerService.ProxiesFilePath);

            var cached = ProxyManagerService.GetCachedProxyStrings();
            Assert.NotNull(cached);

            var item = new ProxyItem
            {
                RawEntry = "http://127.0.0.1:1",
                Protocol = "http",
                Host = "127.0.0.1",
                Port = 1,
                Username = "testUser",
                Password = "testPassword"
            };

            Assert.Equal("127.0.0.1:1", item.AddressDisplay);
            Assert.Equal("testUser:***", item.AuthDisplay);
            Assert.Equal("HTTP", item.ProtocolUpper);

            // StatusColor and StatusText tests
            item.IsTesting = true;
            Assert.Equal("#F59E0B", item.StatusColor);
            Assert.False(string.IsNullOrEmpty(item.StatusText));

            item.IsTesting = false;
            item.LatencyMs = -1;
            Assert.Equal("#71717A", item.StatusColor);
            Assert.False(item.IsOnline);
            Assert.False(item.IsTested);

            item.LatencyMs = -2;
            Assert.Equal("#EF4444", item.StatusColor);
            Assert.False(item.IsOnline);
            Assert.True(item.IsTested);

            item.LatencyMs = 80;
            Assert.Equal("#22C55E", item.StatusColor);
            Assert.True(item.IsOnline);
            Assert.Contains("80", item.StatusText);

            item.LatencyMs = 250;
            Assert.Equal("#EAB308", item.StatusColor);

            item.LatencyMs = 600;
            Assert.Equal("#F97316", item.StatusColor);

            // TestProxyLatencyAsync null & live item
            await ProxyManagerService.TestProxyLatencyAsync(null!);

            await ProxyManagerService.TestProxyLatencyAsync(item, timeoutMs: 100);
            Assert.False(item.IsTesting);
            Assert.Equal(-2, item.LatencyMs);

            // TestAllProxiesAsync null, empty, and non-empty
            await ProxyManagerService.TestAllProxiesAsync(null!);
            await ProxyManagerService.TestAllProxiesAsync(Array.Empty<ProxyItem>());

            var progressReports = new List<ProxyItem>();
            var progress = new Progress<ProxyItem>(p => progressReports.Add(p));

            await ProxyManagerService.TestAllProxiesAsync(
                new[] { item },
                maxConcurrency: 1,
                timeoutMs: 100,
                progress: progress);

            Assert.False(item.IsTesting);
            Assert.Equal(-2, item.LatencyMs);
        }

        [Fact]
        public async Task AntiAfkService_ConfigAndExecutionPass_CoversBranches()
        {
            // 1. SetConfig true & check status
            AntiAfkService.SetConfig(true, intervalMinutes: 10);
            Assert.True(AntiAfkService.IsEnabled);
            Assert.Contains("Active", AntiAfkService.StatusText);

            // 2. Toggle on/off
            AntiAfkService.Toggle();
            Assert.False(AntiAfkService.IsEnabled);
            Assert.Contains("Inactive", AntiAfkService.StatusText);

            AntiAfkService.Toggle();
            Assert.True(AntiAfkService.IsEnabled);

            // 3. ComputeNextDueUtc
            DateTime now = DateTime.UtcNow;
            DateTime nextDue = AntiAfkService.ComputeNextDueUtc(now, intervalMinutes: 10, jitterSeconds: 30);
            Assert.True(nextDue > now);

            // 4. ExecutePassAsync empty
            int sent = await AntiAfkService.ExecutePassAsync(Array.Empty<int>());
            Assert.Equal(0, sent);

            // 5. Stop
            AntiAfkService.Stop();
            Assert.False(AntiAfkService.IsEnabled);
        }
    }
}
