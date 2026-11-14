using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class AutoReconnectAndProxyTests
    {
        private sealed class TestDisposable : IDisposable
        {
            public bool Disposed { get; private set; }
            public void Dispose() => Disposed = true;
        }

        #region Upgrade 1: Auto-Reconnect Daemon Tests

        [Fact]
        public void AutoReconnectService_RegisterAndUnregisterSession_MaintainsAccurateCount()
        {
            var service = new AutoReconnectService();
            var account1 = new Account { Username = "PlayerOne" };
            var account2 = new Account { Username = "PlayerTwo" };

            service.RegisterSession(account1, 123456, "job-abc", false, false, 9999);
            service.RegisterSession(account2, 789012, "job-def", false, false, 8888);

            Assert.Equal(2, service.ActiveSessionsCount);
            Assert.True(service.TryGetSession("PlayerOne", out var session1));
            Assert.Equal(123456, session1.PlaceId);
            Assert.Equal(9999, session1.ProcessId);

            service.UnregisterSession("PlayerOne");
            Assert.Equal(1, service.ActiveSessionsCount);
            Assert.False(service.TryGetSession("PlayerOne", out _));

            service.Dispose();
        }

        [Fact]
        public void AutoReconnectService_SuppressReconnect_MarksSuppressedAndInactive()
        {
            var service = new AutoReconnectService();
            var account = new Account { Username = "Survivor" };

            service.RegisterSession(account, 111222, "job-1", false, false, 5555);
            Assert.Equal(1, service.ActiveSessionsCount);

            service.SuppressReconnect("Survivor");
            Assert.Equal(0, service.ActiveSessionsCount);

            Assert.True(service.TryGetSession("Survivor", out var session));
            Assert.True(session.IsSuppressed);
            Assert.False(session.IsActive);

            service.Dispose();
        }

        [Fact]
        public void AutoReconnectService_SuppressAll_SuppressesAllActiveSessions()
        {
            var service = new AutoReconnectService();
            service.RegisterSession(new Account { Username = "A1" }, 1, "j1", false, false, 101);
            service.RegisterSession(new Account { Username = "A2" }, 2, "j2", false, false, 102);
            service.RegisterSession(new Account { Username = "A3" }, 3, "j3", false, false, 103);

            Assert.Equal(3, service.ActiveSessionsCount);

            service.SuppressAll();
            Assert.Equal(0, service.ActiveSessionsCount);

            service.Dispose();
        }

        #endregion

        #region Upgrade 2: Proxy Binding & Routing Tests

        [Theory]
        [InlineData("192.168.1.50:8080", "http://192.168.1.50:8080")]
        [InlineData("http://proxy.local:3128", "http://proxy.local:3128")]
        [InlineData("10.0.0.1:8080:alice:secret", "http://10.0.0.1:8080")]
        [InlineData("http://admin:pass123@myproxy.com:9000", "http://myproxy.com:9000")]
        public void ProxyBindingService_ParseWebProxy_HandlesStandardAndCustomFormats(string proxyString, string expectedHost)
        {
            var proxy = ProxyBindingService.ParseWebProxy(proxyString);
            Assert.NotNull(proxy);

            var webProxy = Assert.IsType<System.Net.WebProxy>(proxy);
            var address = webProxy.GetProxy(new Uri("https://www.roblox.com"));
            Assert.Equal(new Uri(expectedHost), address);
        }

        [Fact]
        public void ProxyBindingService_AssignProxy_UpdatesAccountAndMarksDirty()
        {
            var account = new Account { Username = "ProxyUser" };
            account.IsDirty = false;

            ProxyBindingService.AssignProxy(account, "1.2.3.4:8080");

            Assert.Equal("1.2.3.4:8080", account.Proxy);
            Assert.True(account.IsDirty);
        }

        [Fact]
        public void ProxyBindingService_DistributeProxiesRoundRobin_CyclesEvenly()
        {
            var accounts = new List<Account>
            {
                new Account { Username = "U1" },
                new Account { Username = "U2" },
                new Account { Username = "U3" },
                new Account { Username = "U4" }
            };

            var proxies = new[] { "proxy1:8080", "proxy2:8080" };

            ProxyBindingService.DistributeProxiesRoundRobin(accounts, proxies);

            Assert.Equal("proxy1:8080", accounts[0].Proxy);
            Assert.Equal("proxy2:8080", accounts[1].Proxy);
            Assert.Equal("proxy1:8080", accounts[2].Proxy);
            Assert.Equal("proxy2:8080", accounts[3].Proxy);
        }

        [Fact]
        public void ProxyBindingService_DistributeProxiesSticky_PreservesExistingAndBalances()
        {
            var a1 = new Account { Username = "U1", Proxy = "proxy1:8080" };
            var a2 = new Account { Username = "U2", Proxy = "proxy1:8080" };
            var a3 = new Account { Username = "U3" }; // Unassigned
            var a4 = new Account { Username = "U4" }; // Unassigned

            var proxies = new[] { "proxy1:8080", "proxy2:8080" };

            ProxyBindingService.DistributeProxiesSticky(new[] { a1, a2, a3, a4 }, proxies);

            // Existing proxy1 accounts stay sticky
            Assert.Equal("proxy1:8080", a1.Proxy);
            Assert.Equal("proxy1:8080", a2.Proxy);

            // Unassigned accounts balance onto least-used (proxy2)
            Assert.Equal("proxy2:8080", a3.Proxy);
            Assert.Equal("proxy2:8080", a4.Proxy);
        }

        [Fact]
        public void ProxyBindingService_GetClient_ReturnsSharedClientWhenNullOrEmpty()
        {
            var clientNull = ProxyBindingService.GetClient((Account)null!);
            var clientEmpty = ProxyBindingService.GetClient(string.Empty);

            Assert.NotNull(clientNull);
            Assert.NotNull(clientEmpty);
            Assert.Same(SharedHttpClient.Get(), clientNull);
            Assert.Same(SharedHttpClient.Get(), clientEmpty);
        }

        [Fact]
        public void ProxyBindingService_PruneClients_PreservesActiveNoRedirectClient()
        {
            var acc = new Account { Username = "PruneTester", Proxy = "127.0.0.1:8899" };
            var clientBefore = ProxyBindingService.GetNoRedirectClient(acc);
            Assert.NotNull(clientBefore);

            // Pruning with "127.0.0.1:8899" in the active list must NOT dispose or remove "no-redirect_127.0.0.1:8899"
            ProxyBindingService.PruneClients(new[] { "127.0.0.1:8899" });

            var clientAfter = ProxyBindingService.GetNoRedirectClient(acc);
            Assert.Same(clientBefore, clientAfter);
        }

        [Fact]
        public void AutoReconnectService_RegisterSession_SupportsUserIdFallbackWhenUsernameEmpty()
        {
            using var service = new AutoReconnectService();
            var account = new Account { Username = "", UserID = 777888 };

            service.RegisterSession(account, 123456, "job-xyz", false, false, 4444);
            Assert.Equal(1, service.ActiveSessionsCount);
            Assert.True(service.TryGetSession("uid:777888", out var session));
            Assert.Equal(777888, session!.Account.UserID);
        }

        #endregion

        #region Upgrade 3: LRU Cache Tests

        [Fact]
        public void LruCache_BasicGetSet_StoresAndRetrieves()
        {
            using var cache = new LruCache<string, TestDisposable>(capacity: 3);
            var item1 = new TestDisposable();

            cache.Set("k1", item1);

            Assert.Equal(1, cache.Count);
            Assert.True(cache.TryGetValue("k1", out var fetched));
            Assert.Same(item1, fetched);
        }

        [Fact]
        public void LruCache_CapacityExceeded_EvictsOldestItemAndDisposesIt()
        {
            using var cache = new LruCache<string, TestDisposable>(capacity: 2);
            var item1 = new TestDisposable();
            var item2 = new TestDisposable();
            var item3 = new TestDisposable();

            cache.Set("k1", item1);
            cache.Set("k2", item2);

            // Insert 3rd item: capacity 2 is exceeded -> oldest "k1" should be evicted and disposed
            cache.Set("k3", item3);

            Assert.Equal(2, cache.Count);
            Assert.False(cache.TryGetValue("k1", out _));
            Assert.True(item1.Disposed);

            Assert.True(cache.TryGetValue("k2", out _));
            Assert.False(item2.Disposed);
            Assert.True(cache.TryGetValue("k3", out _));
            Assert.False(item3.Disposed);
        }

        [Fact]
        public void LruCache_AccessMovesToFront_EvictsTrueLru()
        {
            using var cache = new LruCache<string, TestDisposable>(capacity: 2);
            var item1 = new TestDisposable();
            var item2 = new TestDisposable();
            var item3 = new TestDisposable();

            cache.Set("k1", item1);
            cache.Set("k2", item2);

            // Access k1 -> marks k1 as most recently used!
            Assert.True(cache.TryGetValue("k1", out _));

            // Now insert k3 -> oldest is k2, so k2 should be evicted and disposed
            cache.Set("k3", item3);

            Assert.True(cache.TryGetValue("k1", out _));
            Assert.False(item1.Disposed);

            Assert.False(cache.TryGetValue("k2", out _));
            Assert.True(item2.Disposed);
        }

        [Fact]
        public void LruCache_OverwritingExistingKey_DisposesPreviousValue()
        {
            using var cache = new LruCache<string, TestDisposable>(capacity: 2);
            var item1 = new TestDisposable();
            var item1Replacement = new TestDisposable();

            cache.Set("k1", item1);
            cache.Set("k1", item1Replacement);

            Assert.Equal(1, cache.Count);
            Assert.True(item1.Disposed);
            Assert.False(item1Replacement.Disposed);
        }

        [Fact]
        public void LruCache_Clear_DisposesAllCachedItems()
        {
            using var cache = new LruCache<string, TestDisposable>(capacity: 3);
            var item1 = new TestDisposable();
            var item2 = new TestDisposable();

            cache.Set("k1", item1);
            cache.Set("k2", item2);

            cache.Clear();

            Assert.Equal(0, cache.Count);
            Assert.True(item1.Disposed);
            Assert.True(item2.Disposed);
        }

        #endregion

        #region Upgrade 5: Batch Operations & Multi-Format Exporter Tests

        [Fact]
        public void AccountExportService_UserPassCookie_ExportsColonDelimited()
        {
            var accounts = new[]
            {
                new Account { Username = "Alice", Password = "Pass1", SecurityToken = "cookie_alice" },
                new Account { Username = "Bob", Password = "Pass2", SecurityToken = "cookie_bob" }
            };

            string exported = AccountExportService.ExportAccounts(accounts, AccountExportFormat.UserPassCookie);
            var lines = exported.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(2, lines.Length);
            Assert.Equal("Alice:Pass1:cookie_alice", lines[0]);
            Assert.Equal("Bob:Pass2:cookie_bob", lines[1]);
        }

        [Fact]
        public void AccountExportService_CookieOnly_SkipsEmptyCookies()
        {
            var accounts = new[]
            {
                new Account { Username = "Alice", SecurityToken = "cookie_alice" },
                new Account { Username = "Ghost", SecurityToken = "" },
                new Account { Username = "Bob", SecurityToken = "cookie_bob" }
            };

            string exported = AccountExportService.ExportAccounts(accounts, AccountExportFormat.CookieOnly);
            var lines = exported.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(2, lines.Length);
            Assert.Equal("cookie_alice", lines[0]);
            Assert.Equal("cookie_bob", lines[1]);
        }

        [Fact]
        public void AccountExportService_Csv_EscapesQuotesAndCommas()
        {
            var accounts = new[]
            {
                new Account { Username = "Alice,Special", Password = "p\"w\"d", Alias = "Ally", Group = "Farm", RobuxBalance = 500 }
            };

            string exported = AccountExportService.ExportAccounts(accounts, AccountExportFormat.Csv);
            var lines = exported.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(2, lines.Length);
            Assert.StartsWith("Username,UserID,Alias,Group,Password,Robux,Proxy,LastUse", lines[0]);
            Assert.Contains("\"Alice,Special\"", lines[1]);
            Assert.Contains("\"p\"\"w\"\"d\"", lines[1]);
            Assert.Contains("500", lines[1]);
        }

        [Theory]
        [InlineData("=CMD|' /C calc'!A0")]
        [InlineData("  =HYPERLINK(\"http://evil\")")]
        [InlineData("+SUM(1,1)")]
        [InlineData("-2+3")]
        [InlineData("@import")]
        [InlineData("\t=1+1")]
        [InlineData("\r=1+1")]
        public void ExportAccounts_Csv_NeutralizesFormulaInjectionPayloads_IncludingLeadingWhitespace(string maliciousAlias)
        {
            var acc = new Account { Username = "CsvSafeUser", UserID = 42, Alias = maliciousAlias };
            string csv = AccountExportService.ExportAccounts(new[] { acc }, AccountExportFormat.Csv);
            string dataLine = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].TrimEnd('\r');
            string[] cols = dataLine.Split(',');
            Assert.StartsWith("\"'", cols[2], StringComparison.Ordinal);
        }

        [Fact]
        public void ExportAccounts_Csv_PreservesNormalAndSpacesOnlyValuesWithoutFormulaPrefix()
        {
            var acc = new Account { Username = "NormalUser", UserID = 43, Alias = "   ", Group = "MainFarm" };
            string csv = AccountExportService.ExportAccounts(new[] { acc }, AccountExportFormat.Csv);
            string dataLine = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].TrimEnd('\r');
            string[] cols = dataLine.Split(',');
            Assert.Equal("NormalUser", cols[0]);
            Assert.Equal("   ", cols[2]);
            Assert.Equal("MainFarm", cols[3]);
        }

        [Fact]
        public void AccountExportService_Json_ProducesValidJsonArray()
        {
            var accounts = new[]
            {
                new Account { Username = "Alice", UserID = 1001, SecurityToken = "tok_a" }
            };

            string json = AccountExportService.ExportAccounts(accounts, AccountExportFormat.Json);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal(JsonValueKind.Array, root.ValueKind);
            Assert.Equal(1, root.GetArrayLength());
            Assert.Equal("Alice", root[0].GetProperty("Username").GetString());
            Assert.Equal(1001, root[0].GetProperty("UserID").GetInt64());
        }

        #endregion

        #region Legacy Modernization Tests

        [Fact]
        public void IniSection_TypedFastPaths_ParsesBooleansAndIntegersAccurately()
        {
            var section = new IniSection("TestSection");
            section.Set("IsEnabled", "true");
            section.Set("IsDisabled", "0");
            section.Set("MaxThreads", "16");
            section.Set("AppTitle", "Shiro RAM");

            Assert.True(section.GetBool("IsEnabled", false));
            Assert.False(section.GetBool("IsDisabled", true));
            Assert.Equal(16, section.GetInt("MaxThreads", 1));
            Assert.Equal("Shiro RAM", section.Get("AppTitle"));

            // Generic Get<T> routing test
            Assert.True(section.Get<bool>("IsEnabled"));
            Assert.Equal(16, section.Get<int>("MaxThreads"));
            Assert.Equal("Shiro RAM", section.Get<string>("AppTitle"));
        }

        [Fact]
        public void Account_PropertySetters_MarkDirtyWithoutExceptions()
        {
            var account = new Account { Username = "TestUser" };
            account.IsDirty = false;

            account.Alias = "NewAlias";
            Assert.Equal("NewAlias", account.Alias);
            Assert.True(account.IsDirty);

            account.IsDirty = false;
            account.Description = "New Description";
            Assert.Equal("New Description", account.Description);
            Assert.True(account.IsDirty);

            account.IsDirty = false;
            account.Group = "FarmingGroup";
            Assert.Equal("FarmingGroup", account.Group);
            Assert.True(account.IsDirty);
        }

        [Fact]
        public void RobloxProcessService_CloseAccountRobloxProcess_HandlesNullGracefully()
        {
            bool result = RobloxProcessService.CloseAccountRobloxProcess(null!);
            Assert.False(result);
        }

        #endregion
    }
}
