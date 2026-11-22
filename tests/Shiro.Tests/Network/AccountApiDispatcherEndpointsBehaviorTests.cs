#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountApiDispatcherEndpointsBehaviorTests : IDisposable
    {
        private const string AdminPassword = "StrongAdminPass123";
        private const string ValidAvatarJson = "{\"playerAvatarType\":\"R15\"}";

        private readonly string? _origWebSocketPassword;
        private readonly string? _origAllowGetAccounts;
        private readonly string? _origAllowGetCookie;
        private readonly string? _origAllowAccountEditing;
        private readonly string? _origAllowLaunchAccount;
        private readonly string? _origEnableMultiRbx;
        private readonly string? _origEnablePotatoMode;
        private readonly string? _origAutoReconnect;
        private readonly bool _origAutoReconnectEnabled;
        private readonly bool _origSingletonEnabled;

        public AccountApiDispatcherEndpointsBehaviorTests()
        {
            RobloxAccountSessionClient.TestHttpClientFactory = null;
            _origWebSocketPassword = AppSettings.WebSocketPassword;
            _origAllowGetAccounts = AppSettings.WebServer.Get("AllowGetAccounts");
            _origAllowGetCookie = AppSettings.WebServer.Get("AllowGetCookie");
            _origAllowAccountEditing = AppSettings.WebServer.Get("AllowAccountEditing");
            _origAllowLaunchAccount = AppSettings.WebServer.Get("AllowLaunchAccount");
            _origEnableMultiRbx = AppSettings.General.Get("EnableMultiRbx");
            _origEnablePotatoMode = AppSettings.General.Get("EnablePotatoMode");
            _origAutoReconnect = AppSettings.General.Get("AutoReconnect");
            _origAutoReconnectEnabled = AutoReconnectService.Instance.IsEnabled;
            _origSingletonEnabled = RobloxSingletonService.IsEnabled;
        }

        public void Dispose()
        {
            AccountManager.CancelDeferredSaveSettings();
            RobloxAccountSessionClient.TestHttpClientFactory = null;
            AppSettings.WebSocketPassword = _origWebSocketPassword;
            AppSettings.WebServer.Set("AllowGetAccounts", _origAllowGetAccounts);
            AppSettings.WebServer.Set("AllowGetCookie", _origAllowGetCookie);
            AppSettings.WebServer.Set("AllowAccountEditing", _origAllowAccountEditing);
            AppSettings.WebServer.Set("AllowLaunchAccount", _origAllowLaunchAccount);
            AppSettings.General.Set("EnableMultiRbx", _origEnableMultiRbx ?? (_origSingletonEnabled ? "true" : "false"));
            AppSettings.General.Set("EnablePotatoMode", _origEnablePotatoMode);
            AutoReconnectService.Instance.IsEnabled = _origAutoReconnectEnabled;
            AppSettings.General.Set("AutoReconnect", _origAutoReconnect);
            RobloxSingletonService.ResetForTest(_origSingletonEnabled);
        }

        private static AccountManager.ApiRequestContext CreateRequest(
            string path,
            string password = AdminPassword,
            Dictionary<string, string>? query = null,
            string body = "",
            bool isV2 = true)
        {
            var q = query ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(password))
                q["Password"] = password;

            return new AccountManager.ApiRequestContext
            {
                AbsolutePath = path,
                IsV2 = isV2,
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
            AppSettings.WebSocketPassword = AdminPassword;

            string resp = await mgr.ProcessApiRequestAsync(CreateRequest("/Memory/Trim"));
            Assert.Contains("BytesSaved", resp);
            Assert.Contains("ClientsCount", resp);
        }

        [Fact]
        public async Task GlobalControlEndpoints_WebSocketStats_AntiAfk_KillRoblox_Work()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = AdminPassword;

            string wsStats = await mgr.ProcessApiRequestAsync(CreateRequest("/GetWebSocketStats"));
            Assert.Contains("Shiro Kestrel Gateway", wsStats);

            string afkStatus = await mgr.ProcessApiRequestAsync(CreateRequest("/AntiAfk/Status"));
            Assert.Contains("TapsSent", afkStatus);

            string afkEnable = await mgr.ProcessApiRequestAsync(CreateRequest("/AntiAfk/Enable"));
            Assert.Contains("Enabled", afkEnable);

            string afkDisable = await mgr.ProcessApiRequestAsync(CreateRequest("/AntiAfk/Disable"));
            Assert.Contains("Enabled", afkDisable);

            string afkToggle = await mgr.ProcessApiRequestAsync(CreateRequest("/AntiAfk/Toggle"));
            Assert.Contains("Enabled", afkToggle);

            await mgr.ProcessApiRequestAsync(CreateRequest("/AntiAfk/Disable"));

            string killResp = await mgr.ProcessApiRequestAsync(CreateRequest("/KillRoblox"));
            Assert.Contains("Closed all Roblox processes", killResp);
        }

        [Fact]
        public async Task GetAccounts_GetAccountsJson_ImportCookie_PermissionAndFilterPaths()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = AdminPassword;

            var acc1 = new Account { Username = "UserA", UserID = 101, Group = "G1", SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--TokA" };
            var acc2 = new Account { Username = "UserB", UserID = 102, Group = "G2", SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--TokB" };
            AccountManager.ResetAccountsForTests(Array.Empty<Account>());
            AppServices.AccountRepository.AddThreadSafe(acc1);
            AppServices.AccountRepository.AddThreadSafe(acc2);

            AppSettings.WebServer.Set("AllowGetAccounts", "false");
            string deniedGet = await mgr.ProcessApiRequestAsync(CreateRequest("/GetAccounts"));
            Assert.Contains("not allowed", deniedGet);

            string deniedJson = await mgr.ProcessApiRequestAsync(CreateRequest("/GetAccountsJson"));
            Assert.Contains("not allowed", deniedJson);

            AppSettings.WebServer.Set("AllowGetAccounts", "true");
            AppSettings.WebServer.Set("AllowGetCookie", "true");
            AppSettings.WebServer.Set("AllowAccountEditing", "true");

            string groupAccounts = await mgr.ProcessApiRequestAsync(CreateRequest(
                "/GetAccounts",
                query: new Dictionary<string, string> { ["Group"] = "G1" }));
            Assert.Contains("UserA", groupAccounts);
            Assert.DoesNotContain("UserB", groupAccounts);

            string jsonWithCookies = await mgr.ProcessApiRequestAsync(CreateRequest(
                "/GetAccountsJson",
                query: new Dictionary<string, string> { ["Group"] = "G1", ["IncludeCookies"] = "true" }));
            Assert.Contains("UserA", jsonWithCookies);
            Assert.Contains("TokA", jsonWithCookies);

            string missingCookieImport = await mgr.ProcessApiRequestAsync(CreateRequest("/ImportCookie"));
            Assert.Contains("Cookie parameter required", missingCookieImport);
        }

        [Fact]
        public async Task MultiRoblox_StatusAndToggle_Work()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = AdminPassword;

            string statusResp = await mgr.ProcessApiRequestAsync(CreateRequest("/MultiRoblox/Status"));
            Assert.Contains("MutexHeld", statusResp);

            string toggleResp = await mgr.ProcessApiRequestAsync(CreateRequest("/MultiRoblox/Toggle"));
            Assert.Contains("Enabled", toggleResp);
        }

        [Fact]
        public async Task PotatoMode_Toggle_Works()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = AdminPassword;

            string resp = await mgr.ProcessApiRequestAsync(CreateRequest("/PotatoMode/Toggle"));
            Assert.Contains("PotatoMode", resp);
        }

        [Fact]
        public async Task AutoReconnect_StatusAndToggle_Work()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = AdminPassword;

            string statusResp = await mgr.ProcessApiRequestAsync(CreateRequest("/AutoReconnect/Status"));
            Assert.Contains("ActiveSessions", statusResp);

            string toggleResp = await mgr.ProcessApiRequestAsync(CreateRequest("/AutoReconnect/Toggle"));
            Assert.Contains("ActiveSessions", toggleResp);
        }

        [Fact]
        public async Task AccountsExport_FormatsAndSecurity_Enforced()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = AdminPassword;
            AppSettings.WebServer.Set("AllowGetAccounts", "true");
            AppSettings.WebServer.Set("AllowGetCookie", "true");
            AppSettings.WebServer.Set("AllowAccountEditing", "true");

            string badPassResp = await mgr.ProcessApiRequestAsync(CreateRequest("/Accounts/Export", password: "wrong"));
            Assert.Contains("Invalid Password", badPassResp);

            string csvResp = await mgr.ProcessApiRequestAsync(CreateRequest("/Accounts/Export", query: new Dictionary<string, string> { ["Format"] = "Csv" }));
            Assert.NotEmpty(csvResp);

            string txtResp = await mgr.ProcessApiRequestAsync(CreateRequest("/Accounts/Export", query: new Dictionary<string, string> { ["Format"] = "SimpleText", ["Group"] = "G1" }));
            Assert.NotEmpty(txtResp);

            AppSettings.WebServer.Set("AllowGetCookie", "false");
            string deniedExport = await mgr.ProcessApiRequestAsync(CreateRequest("/Accounts/Export"));
            Assert.Contains("not allowed", deniedExport);
        }

        [Fact]
        public async Task AccountSpecificEndpoints_PermissionsAndCsrfAuthenticatedRoutes_Work()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = AdminPassword;
            AppSettings.WebServer.Set("AllowGetAccounts", "true");
            AppSettings.WebServer.Set("AllowGetCookie", "true");
            AppSettings.WebServer.Set("AllowAccountEditing", "true");
            AppSettings.WebServer.Set("AllowAccountEditing", "true");
            AppSettings.WebServer.Set("AllowLaunchAccount", "true");

            var testAcc = new Account
            {
                Username = "DispatcherEndpointUser",
                UserID = 998877,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.DISPATCHER123"
            };

            AccountManager.ResetAccountsForTests(Array.Empty<Account>());
            AppServices.AccountRepository.AddThreadSafe(testAcc);

            // Empty and unknown account
            Assert.Contains("Empty Account", await mgr.ProcessApiRequestAsync(CreateRequest("/GetAlias")));
            Assert.Contains("Invalid Account", await mgr.ProcessApiRequestAsync(CreateRequest("/GetAlias", query: new Dictionary<string, string> { ["Account"] = "NoSuchUser" })));

            // GetCookie allowed & denied
            string cookieAllowed = await mgr.ProcessApiRequestAsync(CreateRequest("/GetCookie", query: new Dictionary<string, string> { ["Account"] = "998877" }));
            Assert.Contains("DISPATCHER123", cookieAllowed);

            AppSettings.WebServer.Set("AllowGetCookie", "false");
            Assert.Contains("not allowed", await mgr.ProcessApiRequestAsync(CreateRequest("/GetCookie", query: new Dictionary<string, string> { ["Account"] = "998877" })));

            // Editing denied paths
            AppSettings.WebServer.Set("AllowAccountEditing", "false");
            var editQuery = new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser", ["Field"] = "F1", ["Value"] = "V1" };
            Assert.Contains("not allowed", await mgr.ProcessApiRequestAsync(CreateRequest("/SetField", query: editQuery)));
            Assert.Contains("not allowed", await mgr.ProcessApiRequestAsync(CreateRequest("/RemoveField", query: editQuery)));
            Assert.Contains("not allowed", await mgr.ProcessApiRequestAsync(CreateRequest("/SetAlias", query: editQuery, body: "A")));
            Assert.Contains("not allowed", await mgr.ProcessApiRequestAsync(CreateRequest("/SetDescription", query: editQuery, body: "D")));
            Assert.Contains("not allowed", await mgr.ProcessApiRequestAsync(CreateRequest("/AppendDescription", query: editQuery, body: "D", isV2: true)));
            Assert.Contains("not allowed", await mgr.ProcessApiRequestAsync(CreateRequest("/AppendDescription", query: editQuery, body: "D", isV2: false)));

            // LaunchAccount & FollowUser validation
            AppSettings.WebServer.Set("AllowLaunchAccount", "false");
            Assert.Contains("not allowed", await mgr.ProcessApiRequestAsync(CreateRequest("/LaunchAccount", query: editQuery)));
            Assert.Contains("not allowed", await mgr.ProcessApiRequestAsync(CreateRequest("/FollowUser", query: editQuery)));

            AppSettings.WebServer.Set("AllowLaunchAccount", "true");
            Assert.Contains("Invalid PlaceId", await mgr.ProcessApiRequestAsync(CreateRequest("/LaunchAccount", query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser", ["PlaceId"] = "not-a-number" })));
            Assert.Contains("Invalid Username", await mgr.ProcessApiRequestAsync(CreateRequest("/FollowUser", query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser" })));

            // Authenticated endpoints with route-based FakeHttpHandler
            var handler = new FakeHttpHandler(req =>
            {
                string url = req.RequestUri?.ToString() ?? string.Empty;
                if (url.Contains("auth.roblox.com"))
                {
                    var csrfResp = new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new System.Net.Http.StringContent("{}")
                    };
                    csrfResp.Headers.TryAddWithoutValidation("x-csrf-token", "csrf-1");
                    return csrfResp;
                }
                if (url.Contains("get-blocked-users"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new System.Net.Http.StringContent("{\"blockedUsers\":[{\"userId\":777}]}")
                    };
                }
                if (url.Contains("gamejoin.roblox.com"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new System.Net.Http.StringContent("{\"joinScriptUrl\":\"ok\"}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent("{\"success\":true}")
                };
            });

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);

            string csrfResp = await mgr.ProcessApiRequestAsync(CreateRequest("/GetCSRFToken", query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser" }));
            Assert.Contains("csrf-1", csrfResp);

            AppSettings.WebServer.Set("AllowAccountEditing", "true");
            string blockResp = await mgr.ProcessApiRequestAsync(CreateRequest("/BlockUser", query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser", ["UserId"] = "12345" }));
            Assert.Contains("success", blockResp);

            string unblockResp = await mgr.ProcessApiRequestAsync(CreateRequest("/UnblockUser", query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser", ["UserId"] = "12345" }));
            Assert.Contains("success", unblockResp);

            string blockedListResp = await mgr.ProcessApiRequestAsync(CreateRequest("/GetBlockedList", query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser" }));
            Assert.Contains("blockedUsers", blockedListResp);

            string unblockAllResp = await mgr.ProcessApiRequestAsync(CreateRequest("/UnblockEveryone", query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser" }));
            Assert.NotEmpty(unblockAllResp);

            string badPlaceServer = await mgr.ProcessApiRequestAsync(CreateRequest("/SetServer", query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser", ["PlaceId"] = "bad", ["JobId"] = "j1" }));
            Assert.Contains("Invalid PlaceId", badPlaceServer);

            string okServer = await mgr.ProcessApiRequestAsync(CreateRequest("/SetServer", query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser", ["PlaceId"] = "1818", ["JobId"] = "j1" }));
            Assert.Contains("Success", okServer);

            AppServices.ServerBrowserState.CachedServers.Clear();
            AppServices.ServerBrowserState.CachedServers.Add(new ServerData { id = "rec-srv-1" });
            AppServices.ServerBrowserState.CachedServersPlaceId = 1818;
            string recServer = await mgr.ProcessApiRequestAsync(CreateRequest("/SetRecommendedServer", query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser", ["PlaceId"] = "1818" }));
            Assert.Contains("Success", recServer);

            string setAvatarResp = await mgr.ProcessApiRequestAsync(CreateRequest("/SetAvatar", query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser" }, body: ValidAvatarJson));
            Assert.Contains("Updated avatar", setAvatarResp);
            Assert.Contains("\"Success\":true", setAvatarResp);

            string unknownResp = await mgr.ProcessApiRequestAsync(CreateRequest("/NonExistentEndpoint", query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser" }));
            Assert.Contains("404 not found", unknownResp);
        }

        [Fact]
        public async Task AccountSpecificEndpoints_ProxyAliasDescriptionAndFields_Work()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = AdminPassword;
            AppSettings.WebServer.Set("AllowGetAccounts", "true");
            AppSettings.WebServer.Set("AllowGetCookie", "true");
            AppSettings.WebServer.Set("AllowAccountEditing", "true");
            AppSettings.WebServer.Set("AllowAccountEditing", "true");

            var testAcc = new Account
            {
                Username = "DispatcherEndpointUser",
                UserID = 998877,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.DISPATCHER123"
            };

            AccountManager.ResetAccountsForTests(Array.Empty<Account>());
            AppServices.AccountRepository.AddThreadSafe(testAcc);

            AppSettings.WebServer.Set("AllowAccountEditing", "true");
            string setProxyResp = await mgr.ProcessApiRequestAsync(CreateRequest(
                "/SetProxy",
                query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser", ["Proxy"] = "http://127.0.0.1:8888" }));
            Assert.Contains("Proxy updated", setProxyResp);

            string getProxyResp = await mgr.ProcessApiRequestAsync(CreateRequest(
                "/GetProxy",
                query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser" }));
            Assert.Contains("127.0.0.1:8888", getProxyResp);

            string setAliasResp = await mgr.ProcessApiRequestAsync(CreateRequest(
                "/SetAlias",
                query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser" },
                body: "DispatcherAlias99"));
            Assert.Contains("DispatcherAlias99", setAliasResp);

            string getAliasResp = await mgr.ProcessApiRequestAsync(CreateRequest(
                "/GetAlias",
                query: new Dictionary<string, string> { ["Account"] = "DispatcherEndpointUser" }));
            Assert.Contains("DispatcherAlias99", getAliasResp);

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

        [Fact]
        public async Task Dispatch_BlocksProtectedInternalFields_CaseInsensitively_AndScrubsAccountsJson()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = AdminPassword;
            AppSettings.WebServer.Set("AllowGetAccounts", "true");
            AppSettings.WebServer.Set("AllowAccountEditing", "true");
            AppSettings.WebServer.Set("AllowGetCookie", "false");

            var acc = new Account { Username = "HbaGuardUser", UserID = 777, Valid = true };
            acc.HbaPrivateKey = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgSecretKeyMaterial";
            acc.LastBoundIpSignature = "sig-secret-123";
            acc.SetField("Note", "SafeVal");
            AccountManager.ResetAccountsForTests(new[] { acc });

            int statusGet = 200;
            var reqGet = CreateRequest("/GetField", query: new(StringComparer.OrdinalIgnoreCase) { ["Account"] = "HbaGuardUser", ["Field"] = "hbaprivatekey" });
            reqGet.SetStatusCode = s => statusGet = s;
            string getBody = await mgr.ProcessApiRequestAsync(reqGet);
            Assert.Equal(403, statusGet);
            Assert.DoesNotContain("SecretKeyMaterial", getBody, StringComparison.Ordinal);

            int statusSet = 200;
            var reqSet = CreateRequest("/SetField", query: new(StringComparer.OrdinalIgnoreCase) { ["Account"] = "HbaGuardUser", ["Field"] = "HbaPrivateKey", ["Value"] = "overwritten" });
            reqSet.SetStatusCode = s => statusSet = s;
            _ = await mgr.ProcessApiRequestAsync(reqSet);
            Assert.Equal(403, statusSet);
            Assert.Contains("SecretKeyMaterial", acc.HbaPrivateKey, StringComparison.Ordinal);

            int statusRem = 200;
            var reqRem = CreateRequest("/RemoveField", query: new(StringComparer.OrdinalIgnoreCase) { ["Account"] = "HbaGuardUser", ["Field"] = "lastboundipsig" });
            reqRem.SetStatusCode = s => statusRem = s;
            _ = await mgr.ProcessApiRequestAsync(reqRem);
            Assert.Equal(403, statusRem);
            Assert.Equal("sig-secret-123", acc.LastBoundIpSignature);

            string safeField = await mgr.ProcessApiRequestAsync(CreateRequest("/GetField", query: new(StringComparer.OrdinalIgnoreCase) { ["Account"] = "HbaGuardUser", ["Field"] = "Note" }));
            Assert.Contains("SafeVal", safeField, StringComparison.Ordinal);

            string listJson = await mgr.ProcessApiRequestAsync(CreateRequest("/GetAccountsJson"));
            Assert.Contains("SafeVal", listJson, StringComparison.Ordinal);
            Assert.DoesNotContain("SecretKeyMaterial", listJson, StringComparison.Ordinal);
            Assert.DoesNotContain("HbaPrivateKey", listJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sig-secret-123", listJson, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Dispatch_BlocksAllProtectedSessionAndHbaPrefixFields_AcrossV1AndV2()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = AdminPassword;
            AppSettings.WebServer.Set("AllowGetAccounts", "true");
            AppSettings.WebServer.Set("AllowAccountEditing", "true");
            AppSettings.WebServer.Set("AllowGetCookie", "false");

            var acc = new Account { Username = "HbaPrefixUser", UserID = 778, Valid = true };
            acc.HbaPublicKey = "PubKeySpkiSecret";
            acc.HbaTemplate = "v1|{0}";
            acc.SessionUserAgent = "Mozilla/5.0 SecretFingerprintUA";
            acc.Fields["HbaCustomFuture"] = "FutureSecret";
            acc.SetField("PublicTag", "VisibleTag");
            AccountManager.ResetAccountsForTests(new[] { acc });

            foreach (string blockedField in new[] { "hbapublickey", "HBATEMPLATE", "sessionuseragent", "hbacustomfuture", "  HbaPrivateKey  " })
            {
                int v1Status = 200;
                var v1Req = CreateRequest("/GetField", query: new(StringComparer.OrdinalIgnoreCase) { ["Account"] = "HbaPrefixUser", ["Field"] = blockedField }, isV2: false);
                v1Req.SetStatusCode = s => v1Status = s;
                string v1Body = await mgr.ProcessApiRequestAsync(v1Req);
                Assert.Equal(403, v1Status);
                Assert.Equal("Forbidden field", v1Body);
            }

            string listJson = await mgr.ProcessApiRequestAsync(CreateRequest("/GetAccountsJson"));
            Assert.Contains("VisibleTag", listJson, StringComparison.Ordinal);
            Assert.DoesNotContain("PubKeySpkiSecret", listJson, StringComparison.Ordinal);
            Assert.DoesNotContain("SecretFingerprintUA", listJson, StringComparison.Ordinal);
            Assert.DoesNotContain("FutureSecret", listJson, StringComparison.Ordinal);
            Assert.DoesNotContain("SessionUserAgent", listJson, StringComparison.OrdinalIgnoreCase);

            Assert.False(Account.IsProtectedInternalField(null));
            Assert.False(Account.IsProtectedInternalField("   "));
            Assert.False(Account.IsProtectedInternalField("Note"));
            Assert.True(Account.IsProtectedInternalField("  SessionUserAgent  "));

            var nullFieldsAcc = new Account { Fields = null! };
            Assert.Empty(nullFieldsAcc.GetPublicFieldsSnapshot());
        }
    }
}
