#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class Phase3LaunchAndFastLoginHardeningTests : IDisposable
    {
        public Phase3LaunchAndFastLoginHardeningTests()
        {
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
            ProxyBindingService.ClearClients();
        }

        public void Dispose()
        {
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
            ProxyBindingService.ClearClients();
        }

        [Fact]
        public async Task LaunchCoordinator_ProxyAccount429_IsolatesBackoffToAccountProxyScope()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroPhase3Launch_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var ini = new IniFile();
            var settings = ini["General"];
            settings.Set(AppSettingsKeys.General.UseProxies, "true");
            settings.Set(AppSettingsKeys.General.SmartFastLaunch, "false");
            settings.Set(AppSettingsKeys.General.SmartLaunchMinDelaySeconds, "0");
            settings.Set(AppSettingsKeys.General.WaitForCaptchaInQueue, "false");

            ISettingsService prevProxySettings = ProxyBindingService.Settings;
            using var storageScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
            using var settingsScope = AppServices.Override<ISettingsService>(settings);
            ProxyBindingService.Settings = settings;

            try
            {
                var accountA = new Account { Username = "ProxyUserA", Proxy = "http://10.0.0.1:8080", Valid = true };
                var accountB = new Account { Username = "ProxyUserB", Proxy = "http://10.0.0.2:8080", Valid = true };
                var accountC = new Account { Username = "DirectUserC", Proxy = null, Valid = true };

                string scopeA = RobloxRateLimitCoordinator.ResolveProxyScope(accountA);
                string scopeB = RobloxRateLimitCoordinator.ResolveProxyScope(accountB);
                string scopeC = RobloxRateLimitCoordinator.ResolveProxyScope(accountC);

                Assert.Equal("http://10.0.0.1:8080", scopeA);
                Assert.Equal("http://10.0.0.2:8080", scopeB);
                Assert.Equal(RobloxRateLimitCoordinator.DirectProxyScope, scopeC);

                using var ctsA = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                int attemptsA = 0;
                using var coordinatorA = new LaunchCoordinator((_, _) =>
                {
                    attemptsA++;
                    return Task.FromResult(LaunchResult.Fail(LaunchOutcome.RateLimited, "HTTP 429 on Proxy A"));
                });

                Action<LaunchProgress> progressA = p =>
                {
                    if (attemptsA >= 1 && p.Status == LaunchOutcome.RateLimited)
                        ctsA.Cancel();
                };

                var batchA = await coordinatorA.EnqueueAsync(
                    new[] { new LaunchRequest(accountA, 189707, string.Empty, false, false) },
                    delaySeconds: 0,
                    asyncJoin: false,
                    progress: progressA,
                    cancellationToken: ctsA.Token);

                Assert.True(batchA.Cancelled);
                Assert.Equal(1, attemptsA);
                Assert.True(
                    RobloxRateLimitCoordinator.Instance.IsBackingOffCategory(RateLimitCategory.Authentication, scopeA),
                    "Expected 429 from Account A to activate backoff on Proxy A's scope.");
                Assert.True(
                    RobloxRateLimitCoordinator.Instance.GetRemainingBackoff(RateLimitCategory.Authentication, scopeA) > TimeSpan.FromSeconds(1),
                    "Expected Proxy A backoff to remain active (> 1s).");
                Assert.False(
                    RobloxRateLimitCoordinator.Instance.IsBackingOffCategory(RateLimitCategory.Authentication, scopeB),
                    "Proxy B must not inherit Proxy A's 429 backoff.");
                Assert.False(
                    RobloxRateLimitCoordinator.Instance.IsBackingOffCategory(RateLimitCategory.Authentication, RobloxRateLimitCoordinator.DirectProxyScope),
                    "Direct scope must not inherit Proxy A's 429 backoff.");

                var launchedUsers = new List<string>();
                var observedStatuses = new List<LaunchOutcome>();
                using var ctsBC = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                using var coordinatorBC = new LaunchCoordinator((req, _) =>
                {
                    launchedUsers.Add(req.Account.Username);
                    return Task.FromResult(LaunchResult.Success());
                });

                var sw = Stopwatch.StartNew();
                var batchBC = await coordinatorBC.EnqueueAsync(
                    new[]
                    {
                        new LaunchRequest(accountB, 189707, string.Empty, false, false),
                        new LaunchRequest(accountC, 189707, string.Empty, false, false)
                    },
                    delaySeconds: 0,
                    asyncJoin: false,
                    progress: p => observedStatuses.Add(p.Status),
                    cancellationToken: ctsBC.Token);
                sw.Stop();

                Assert.False(batchBC.Cancelled);
                Assert.Equal(2, batchBC.Results.Count);
                Assert.Equal(LaunchOutcome.Success, batchBC.Results[0].Status);
                Assert.Equal(LaunchOutcome.Success, batchBC.Results[1].Status);
                Assert.Equal(new[] { "ProxyUserB", "DirectUserC" }, launchedUsers);
                Assert.DoesNotContain(LaunchOutcome.RateLimited, observedStatuses);
                Assert.True(sw.ElapsedMilliseconds < 1500, $"Expected Proxy B and Direct to launch immediately, took {sw.ElapsedMilliseconds}ms.");
            }
            finally
            {
                ProxyBindingService.Settings = prevProxySettings;
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void FastLoginScripts_GetLoginPageEnhancementScript_GuardsOriginToRobloxDomains()
        {
            string script = FastLoginScripts.GetLoginPageEnhancementScript();

            Assert.Contains("(() => {", script, StringComparison.Ordinal);
            Assert.Contains("location.protocol !== 'https:'", script, StringComparison.Ordinal);
            Assert.Contains("location.hostname", script, StringComparison.Ordinal);
            Assert.Contains("h !== 'roblox.com' && !h.endsWith('.roblox.com')", script, StringComparison.Ordinal);

            int guardIndex = script.IndexOf(".endsWith('.roblox.com')", StringComparison.Ordinal);
            int returnIndex = script.IndexOf("return;", guardIndex, StringComparison.Ordinal);
            int styleIndex = script.IndexOf("createElement('style')", StringComparison.Ordinal);
            int hookIndex = script.IndexOf("shiro_password_captured", StringComparison.Ordinal);

            Assert.True(guardIndex >= 0, "Expected .roblox.com domain suffix check.");
            Assert.True(returnIndex > guardIndex, "Expected early return inside origin guard.");
            Assert.True(styleIndex > returnIndex, "Expected origin guard to execute before injecting style.");
            Assert.True(hookIndex > returnIndex, "Expected origin guard to execute before hooking password inputs.");
        }

        [Fact]
        public void FastLoginScripts_GetLoginPageEnhancementScript_BoundsAndClearsSetInterval()
        {
            string script = FastLoginScripts.GetLoginPageEnhancementScript();

            Assert.Contains("const timerId = setInterval(", script, StringComparison.Ordinal);
            Assert.Contains("attempts >= 50", script, StringComparison.Ordinal);
            Assert.Contains("clearInterval(timerId)", script, StringComparison.Ordinal);
            Assert.Contains("pagehide", script, StringComparison.Ordinal);
            Assert.DoesNotContain("setInterval(hookPass, 400);", script, StringComparison.Ordinal);
        }
    }
}
