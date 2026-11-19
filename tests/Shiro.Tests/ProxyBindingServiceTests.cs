#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class ProxyBindingServiceTests : IDisposable
    {
        private readonly ISettingsService _originalSettings;

        public ProxyBindingServiceTests()
        {
            _originalSettings = ProxyBindingService.Settings;
            ProxyBindingService.ClearClients();
        }

        public void Dispose()
        {
            ProxyBindingService.Settings = _originalSettings;
            ProxyBindingService.ClearClients();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ParseWebProxy_NullOrWhitespace_ReturnsNull(string? input)
        {
            Assert.Null(ProxyBindingService.ParseWebProxy(input));
        }

        [Fact]
        public void ParseWebProxy_StandardHttpUri_ParsesCorrectly()
        {
            var proxy = ProxyBindingService.ParseWebProxy("http://127.0.0.1:8080") as WebProxy;
            Assert.NotNull(proxy);
            Assert.Equal("http://127.0.0.1:8080/", proxy!.Address?.ToString());
            Assert.Null(proxy.Credentials);
        }

        [Fact]
        public void ParseWebProxy_StandardSocks5Uri_ParsesCorrectly()
        {
            var proxy = ProxyBindingService.ParseWebProxy("socks5://192.168.1.10:1080") as WebProxy;
            Assert.NotNull(proxy);
            Assert.Equal("socks5://192.168.1.10:1080/", proxy!.Address?.ToString());
        }

        [Fact]
        public void ParseWebProxy_UserPassAtHostPort_ExtractsCredentials()
        {
            var proxy = ProxyBindingService.ParseWebProxy("http://alice:secret123@10.0.0.1:8888") as WebProxy;
            Assert.NotNull(proxy);
            Assert.Equal("http://10.0.0.1:8888/", proxy!.Address?.ToString());

            var cred = proxy.Credentials as NetworkCredential;
            Assert.NotNull(cred);
            Assert.Equal("alice", cred!.UserName);
            Assert.Equal("secret123", cred.Password);
        }

        [Fact]
        public void ParseWebProxy_HostPortUserPass_ExtractsCredentials()
        {
            var proxy = ProxyBindingService.ParseWebProxy("10.0.0.2:9090:bob:mySecretPass") as WebProxy;
            Assert.NotNull(proxy);
            Assert.Equal("http://10.0.0.2:9090/", proxy!.Address?.ToString());

            var cred = proxy.Credentials as NetworkCredential;
            Assert.NotNull(cred);
            Assert.Equal("bob", cred!.UserName);
            Assert.Equal("mySecretPass", cred.Password);
        }

        [Fact]
        public void ParseWebProxy_HostPortUserPass_PasswordWithColons_HandlesProperly()
        {
            var proxy = ProxyBindingService.ParseWebProxy("10.0.0.3:9090:carol:part1:part2:part3") as WebProxy;
            Assert.NotNull(proxy);
            var cred = proxy!.Credentials as NetworkCredential;
            Assert.NotNull(cred);
            Assert.Equal("carol", cred!.UserName);
            Assert.Equal("part1:part2:part3", cred.Password);
        }

        [Fact]
        public void ParseWebProxy_InvalidPort_ReturnsNull()
        {
            Assert.Null(ProxyBindingService.ParseWebProxy("10.0.0.1:999999"));
            Assert.Null(ProxyBindingService.ParseWebProxy("10.0.0.1:invalid"));
        }

        [Fact]
        public void IsProxyRoutingAllowed_FollowsSettings()
        {
            var ini = new IniFile();
            var settings = ini.Section("General");

            ProxyBindingService.Settings = settings;
            Assert.True(ProxyBindingService.IsProxyRoutingAllowed());

            settings.Set(AppSettingsKeys.General.UseProxies, "false");
            Assert.False(ProxyBindingService.IsProxyRoutingAllowed());

            settings.Set(AppSettingsKeys.General.UseProxies, "true");
            Assert.True(ProxyBindingService.IsProxyRoutingAllowed());
        }

        [Fact]
        public void AssignProxy_UpdatesAccountProxyProperty()
        {
            var acc = new Account { Username = "TestUser" };
            ProxyBindingService.AssignProxy(acc, "127.0.0.1:8080");
            Assert.Equal("127.0.0.1:8080", acc.Proxy);

            ProxyBindingService.AssignProxy(acc, "   ");
            Assert.Null(acc.Proxy);

            // Null account should not throw
            ProxyBindingService.AssignProxy(null!, "127.0.0.1:8080");
        }

        [Fact]
        public void DistributeProxiesRoundRobin_CyclesProxiesEvenly()
        {
            var accounts = new[]
            {
                new Account { Username = "A1" },
                new Account { Username = "A2" },
                new Account { Username = "A3" },
                new Account { Username = "A4" },
                new Account { Username = "A5" }
            };
            var proxies = new[] { "10.0.0.1:8080", "10.0.0.2:8080" };

            ProxyBindingService.DistributeProxiesRoundRobin(accounts, proxies);

            Assert.Equal("10.0.0.1:8080", accounts[0].Proxy);
            Assert.Equal("10.0.0.2:8080", accounts[1].Proxy);
            Assert.Equal("10.0.0.1:8080", accounts[2].Proxy);
            Assert.Equal("10.0.0.2:8080", accounts[3].Proxy);
            Assert.Equal("10.0.0.1:8080", accounts[4].Proxy);
        }

        [Fact]
        public void DistributeProxiesRoundRobin_NullOrEmptyInputs_NoOp()
        {
            var accounts = new[] { new Account { Username = "A1", Proxy = "existing" } };
            ProxyBindingService.DistributeProxiesRoundRobin(null!, new[] { "p1" });
            ProxyBindingService.DistributeProxiesRoundRobin(accounts, null!);
            ProxyBindingService.DistributeProxiesRoundRobin(accounts, Array.Empty<string>());
            Assert.Equal("existing", accounts[0].Proxy);
        }

        [Fact]
        public void DistributeProxiesSticky_KeepsExistingAndDistributesLeastUsed()
        {
            var p1 = "10.0.0.1:8080";
            var p2 = "10.0.0.2:8080";
            var proxies = new[] { p1, p2 };

            var a1 = new Account { Username = "A1", Proxy = p1 };
            var a2 = new Account { Username = "A2", Proxy = null };
            var a3 = new Account { Username = "A3", Proxy = null };
            var accounts = new[] { a1, a2, a3 };

            ProxyBindingService.DistributeProxiesSticky(accounts, proxies);

            // a1 already had p1, so p1 has usage 1. p2 had usage 0.
            Assert.Equal(p1, a1.Proxy);
            // a2 should get least used (p2)
            Assert.Equal(p2, a2.Proxy);
            // a3 can now get either (both have usage 1)
            Assert.NotNull(a3.Proxy);
            Assert.Contains(a3.Proxy, proxies);
        }

        [Fact]
        public void DistributeProxiesSticky_NullOrEmptyInputs_NoOp()
        {
            var accounts = new[] { new Account { Username = "A1", Proxy = "p1" } };
            ProxyBindingService.DistributeProxiesSticky(null!, new[] { "p1" });
            ProxyBindingService.DistributeProxiesSticky(accounts, null!);
            ProxyBindingService.DistributeProxiesSticky(accounts, Array.Empty<string>());
            Assert.Equal("p1", accounts[0].Proxy);
        }

        [Fact]
        public void GetClient_AccountNullOrNoProxy_ReturnsSharedClient()
        {
            var shared = SharedHttpClient.Get();
            Assert.Same(shared, ProxyBindingService.GetClient((Account)null!));

            var accNoProxy = new Account { Username = "NoProxy", Proxy = null };
            Assert.Same(shared, ProxyBindingService.GetClient(accNoProxy));
        }

        [Fact]
        public void GetNoRedirectClient_AccountNullOrNoProxy_ReturnsSharedNoRedirectClient()
        {
            var shared = SharedHttpClient.GetNoRedirect();
            Assert.Same(shared, ProxyBindingService.GetNoRedirectClient(null));

            var accNoProxy = new Account { Username = "NoProxy", Proxy = null };
            Assert.Same(shared, ProxyBindingService.GetNoRedirectClient(accNoProxy));
        }

        [Fact]
        public void GetNoRedirectClient_EmptyAccountProxyWithDistributeProxiesEnabled_UsesStickyPoolProxy()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroNoRedirectProxy_" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    ProxyManagerService.InvalidateCache();
                    ProxyManagerService.SaveProxies(new[]
                    {
                        new ProxyItem { RawEntry = "10.0.0.77:8080", Host = "10.0.0.77", Port = 8080 }
                    });

                    var ini = new IniFile();
                    var settings = ini.Section("General");
                    settings.Set(AppSettingsKeys.General.UseProxies, "true");
                    ProxyBindingService.Settings = settings;

                    var acc = new Account { Username = "NoRedirectPoolUser", Proxy = null };
                    var noRedirectClient = ProxyBindingService.GetNoRedirectClient(acc);
                    var expectedPooledClient = ProxyBindingService.GetNoRedirectClient(
                        new Account { Username = "ExplicitProxyUser", Proxy = "10.0.0.77:8080" });

                    Assert.NotSame(SharedHttpClient.GetNoRedirect(), noRedirectClient);
                    Assert.Equal("10.0.0.77:8080", acc.Proxy);
                    Assert.Same(expectedPooledClient, noRedirectClient);
                }
            }
            finally
            {
                ProxyManagerService.InvalidateCache();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void GetClient_ValidProxyString_ReturnsPooledClient()
        {
            var client1 = ProxyBindingService.GetClient("http://127.0.0.1:8080");
            var client2 = ProxyBindingService.GetClient("http://127.0.0.1:8080");
            Assert.NotNull(client1);
            Assert.Same(client1, client2);
        }

        [Fact]
        public void GetClient_InvalidProxy_ThrowsInvalidOperationException_FailClosed()
        {
            Assert.Throws<InvalidOperationException>(() => ProxyBindingService.GetClient("invalid::format::proxy:999999"));
        }

        [Fact]
        public void PruneClients_RemovesStaleProxiesAndKeepsActive()
        {
            var client = ProxyBindingService.GetClient("http://127.0.0.1:8081");
            Assert.NotNull(client);

            // Pruning with different active proxies removes 8081
            ProxyBindingService.PruneClients(new[] { "http://127.0.0.1:8082" });

            // Next GetClient for 8081 will create a new instance
            var clientNew = ProxyBindingService.GetClient("http://127.0.0.1:8081");
            Assert.NotNull(clientNew);
            Assert.NotSame(client, clientNew);
        }

        [Fact]
        public async System.Threading.Tasks.Task TestProxyAsync_EmptyOrInvalidProxy_ReturnsFailure()
        {
            var (success1, _, err1) = await ProxyBindingService.TestProxyAsync("");
            Assert.False(success1);
            Assert.Equal("Proxy string is empty.", err1);

            var (success2, _, err2) = await ProxyBindingService.TestProxyAsync("invalid-proxy");
            Assert.False(success2);
            Assert.Equal("Invalid proxy format.", err2);
        }

        [Fact]
        public void RebalanceRemovedPoolProxies_ReassignsRemovedPoolDeterministically_PreservingManualAndSurviving()
        {
            var manual = new Account { Username = "Manual", Proxy = "127.0.0.99:9999" };
            var surviving = new Account { Username = "Surviving", Proxy = "10.0.0.2:8080" };
            var removed1 = new Account { Username = "PoolOld1", Proxy = "10.0.0.1:8080" };
            var removed2 = new Account { Username = "PoolOld2", Proxy = "10.0.0.1:8080" };
            var empty = new Account { Username = "Empty", Proxy = null };

            int changed = ProxyBindingService.RebalanceRemovedPoolProxies(
                new[] { manual, surviving, removed1, removed2, empty },
                new[] { "10.0.0.1:8080", "10.0.0.2:8080" },
                new[] { "10.0.0.2:8080", "10.0.0.3:8080" });

            Assert.Equal(2, changed);
            Assert.Equal("127.0.0.99:9999", manual.Proxy);
            Assert.Equal("10.0.0.2:8080", surviving.Proxy);
            Assert.Null(empty.Proxy);
            Assert.Equal("10.0.0.3:8080", removed1.Proxy);
            Assert.Equal("10.0.0.2:8080", removed2.Proxy);
        }

        [Fact]
        public void RebalanceRemovedPoolProxies_WhenNoActiveProxiesRemain_ClearsRemovedAccountsOnly()
        {
            var manual = new Account { Username = "Manual", Proxy = "127.0.0.99:9999" };
            var removed = new Account { Username = "PoolOld", Proxy = "10.0.0.1:8080" };
            var empty = new Account { Username = "Empty", Proxy = null };

            int changed = ProxyBindingService.RebalanceRemovedPoolProxies(
                new[] { manual, removed, empty },
                new[] { "10.0.0.1:8080" },
                Array.Empty<string>());

            Assert.Equal(1, changed);
            Assert.Equal("127.0.0.99:9999", manual.Proxy);
            Assert.Equal(string.Empty, removed.Proxy);
            Assert.Null(empty.Proxy);
        }

        [Fact]
        public void SaveProxies_WhenPoolProxiesRemoved_RebalancesAccountsAndPreservesManualProxy()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroProxyRebalance_" + Guid.NewGuid().ToString("N"));
            try
            {
                var manual = new Account { Username = "Manual", Proxy = "127.0.0.99:9999" };
                var pool1 = new Account { Username = "Pool1", Proxy = "10.0.0.1:8080" };
                var pool2 = new Account { Username = "Pool2", Proxy = "10.0.0.2:8080" };

                var repo = Substitute.For<IAccountRepository>();
                repo.GetAll().Returns(new[] { manual, pool1, pool2 });

                var ini = new IniFile();
                var settings = ini.Section("General");
                settings.Set(AppSettingsKeys.General.UseProxies, "true");

                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                using (AppServices.Override<IAccountRepository>(repo))
                using (AppServices.Override<ISettingsService>(settings))
                {
                    ProxyManagerService.InvalidateCache();
                    ProxyManagerService.SaveProxies(new[]
                    {
                        new ProxyItem { RawEntry = "10.0.0.1:8080", Host = "10.0.0.1", Port = 8080 },
                        new ProxyItem { RawEntry = "10.0.0.2:8080", Host = "10.0.0.2", Port = 8080 }
                    });

                    Assert.Equal("10.0.0.1:8080", pool1.Proxy);

                    ProxyManagerService.SaveProxies(new[]
                    {
                        new ProxyItem { RawEntry = "10.0.0.2:8080", Host = "10.0.0.2", Port = 8080 }
                    });

                    Assert.Equal("127.0.0.99:9999", manual.Proxy);
                    Assert.Equal("10.0.0.2:8080", pool1.Proxy);
                    Assert.Equal("10.0.0.2:8080", pool2.Proxy);

                    ProxyManagerService.SaveProxies(Array.Empty<ProxyItem>());

                    Assert.Equal("127.0.0.99:9999", manual.Proxy);
                    Assert.Equal(string.Empty, pool1.Proxy);
                    Assert.Equal(string.Empty, pool2.Proxy);
                }
            }
            finally
            {
                ProxyManagerService.InvalidateCache();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }
    }
}
