#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountApiDispatcherEndpointsBehaviorTests
    {
        private const string AdminPassword = "StrongAdminPass123";

        private static AccountManager.ApiRequestContext CreateRequest(
            string path,
            string password = AdminPassword,
            Dictionary<string, string>? query = null,
            string body = "")
        {
            var q = query ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(password))
                q["Password"] = password;

            return new AccountManager.ApiRequestContext
            {
                AbsolutePath = path,
                IsV2 = true,
                IsLocal = true,
                Host = "localhost",
                Origin = "",
                Body = body,
                GetQuery = k => q.TryGetValue(k, out var val) ? val : null,
                GetHeader = _ => null,
                SetStatusCode = _ => { }
            };
        }

        [Fact]
        public async Task MemoryTrim_Endpoint_ReturnsBytesSaved()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AccountManager.WSPassword = AdminPassword;

            string resp = await mgr.ProcessApiRequestAsync(CreateRequest("/Memory/Trim"));
            Assert.Contains("BytesSaved", resp);
            Assert.Contains("ClientsCount", resp);
        }

        [Fact]
        public async Task MultiRoblox_StatusAndToggle_Work()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AccountManager.WSPassword = AdminPassword;

            string statusResp = await mgr.ProcessApiRequestAsync(CreateRequest("/MultiRoblox/Status"));
            Assert.Contains("MutexHeld", statusResp);

            string toggleResp = await mgr.ProcessApiRequestAsync(CreateRequest("/MultiRoblox/Toggle"));
            Assert.Contains("Enabled", toggleResp);
        }

        [Fact]
        public async Task PotatoMode_Toggle_Works()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AccountManager.WSPassword = AdminPassword;

            string resp = await mgr.ProcessApiRequestAsync(CreateRequest("/PotatoMode/Toggle"));
            Assert.Contains("PotatoMode", resp);
        }

        [Fact]
        public async Task AutoReconnect_StatusAndToggle_Work()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AccountManager.WSPassword = AdminPassword;

            string statusResp = await mgr.ProcessApiRequestAsync(CreateRequest("/AutoReconnect/Status"));
            Assert.Contains("ActiveSessions", statusResp);

            string toggleResp = await mgr.ProcessApiRequestAsync(CreateRequest("/AutoReconnect/Toggle"));
            Assert.Contains("ActiveSessions", toggleResp);
        }

        [Fact]
        public async Task AccountsExport_FormatsAndSecurity_Enforced()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AccountManager.WSPassword = AdminPassword;
            AccountManager.WebServer.Set("AllowGetAccounts", "true");
            AccountManager.WebServer.Set("AllowGetCookie", "true");

            // Invalid password returns 401
            string badPassResp = await mgr.ProcessApiRequestAsync(CreateRequest("/Accounts/Export", password: "wrong"));
            Assert.Contains("Invalid Password", badPassResp);

            // Export CSV
            string csvResp = await mgr.ProcessApiRequestAsync(CreateRequest("/Accounts/Export", query: new Dictionary<string, string> { ["Format"] = "Csv" }));
            Assert.NotEmpty(csvResp);

            // Export SimpleText
            string txtResp = await mgr.ProcessApiRequestAsync(CreateRequest("/Accounts/Export", query: new Dictionary<string, string> { ["Format"] = "SimpleText" }));
            Assert.NotEmpty(txtResp);
        }

        [Fact]
        public async Task AccountSpecificEndpoints_ProxyAliasDescriptionAndFields_Work()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AccountManager.WSPassword = AdminPassword;
            AccountManager.WebServer.Set("AllowGetAccounts", "true");
            AccountManager.WebServer.Set("AllowGetCookie", "true");
            AccountManager.WebServer.Set("AllowAccountEditing", "true");

            var testAcc = new Account
            {
                Username = "DispatcherEndpointUser",
                UserID = 998877,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.DISPATCHER123"
            };

            lock (AccountManager.AccountListLock)
            {
                AccountManager.AccountsList.Clear();
            }
            AccountManager.AddAccountThreadSafe(testAcc);

            // 1. SetProxy and GetProxy
            string setProxyResp = await mgr.ProcessApiRequestAsync(CreateRequest(
                "/SetProxy",
                query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser", ["Proxy"] = "http://127.0.0.1:8888" }));
            Assert.Contains("Proxy updated", setProxyResp);

            string getProxyResp = await mgr.ProcessApiRequestAsync(CreateRequest(
                "/GetProxy",
                query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser" }));
            Assert.Contains("127.0.0.1:8888", getProxyResp);

            // 2. SetAlias and GetAlias
            string setAliasResp = await mgr.ProcessApiRequestAsync(CreateRequest(
                "/SetAlias",
                query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser" },
                body: "DispatcherAlias99"));
            Assert.Contains("DispatcherAlias99", setAliasResp);

            string getAliasResp = await mgr.ProcessApiRequestAsync(CreateRequest(
                "/GetAlias",
                query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser" }));
            Assert.Contains("DispatcherAlias99", getAliasResp);

            // 3. SetDescription and AppendDescription
            await mgr.ProcessApiRequestAsync(CreateRequest(
                "/SetDescription",
                query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser" },
                body: "BaseDesc"));

            string appendDescResp = await mgr.ProcessApiRequestAsync(CreateRequest(
                "/AppendDescription",
                query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser" },
                body: "ExtraNotes"));
            Assert.Contains("ExtraNotes", appendDescResp);
            Assert.Equal("BaseDescExtraNotes", testAcc.Description);

            // 4. SetField, GetField, and RemoveField
            string setFieldResp = await mgr.ProcessApiRequestAsync(CreateRequest(
                "/SetField",
                query: new Dictionary<string, string>
                {
                    ["Account"] = "DispatcherEndpointUser",
                    ["Field"] = "CustomField1",
                    ["Value"] = "CustomValue1"
                }));
            Assert.Contains("CustomField1", setFieldResp);

            string getFieldResp = await mgr.ProcessApiRequestAsync(CreateRequest(
                "/GetField",
                query: new Dictionary<string, string>
                {
                    ["Account"] = "DispatcherEndpointUser",
                    ["Field"] = "CustomField1"
                }));
            Assert.Contains("CustomValue1", getFieldResp);

            string removeFieldResp = await mgr.ProcessApiRequestAsync(CreateRequest(
                "/RemoveField",
                query: new Dictionary<string, string>
                {
                    ["Account"] = "DispatcherEndpointUser",
                    ["Field"] = "CustomField1"
                }));
            Assert.Contains("Removed Field CustomField1", removeFieldResp);
        }
    }
}
