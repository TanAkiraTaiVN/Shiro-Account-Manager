using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shiro;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public partial class WebServerEndpointsAndWorkflowTests
    {
        private sealed class FakeDialogService : IDialogService
        {
            public string LastInfoMessage { get; private set; } = string.Empty;
            public string LastWarningMessage { get; private set; } = string.Empty;
            public string LastErrorMessage { get; private set; } = string.Empty;
            public bool ConfirmationResult { get; set; } = true;
            public bool? YesNoCancelResult { get; set; } = true;
            public string InputPromptResult { get; set; } = string.Empty;

            public void ShowInfo(string message, string title = "Shiro") => LastInfoMessage = message;
            public void ShowWarning(string message, string title = "Shiro") => LastWarningMessage = message;
            public void ShowError(string message, string title = "Shiro") => LastErrorMessage = message;
            public bool ShowConfirmation(string message, string title = "Shiro") => ConfirmationResult;
            public bool? ShowYesNoCancel(string message, string title = "Shiro") => YesNoCancelResult;
            public string ShowInputPrompt(string message, string title, string defaultValue = "") => InputPromptResult;
            public void OpenFastLoginDialog(Account? account = null) { }
            public void OpenFastLoginDialogBatch(IReadOnlyList<Account> accounts) { }
            public void OpenBulkImportDialog() { }
            public void OpenAltGenDialog() { }
            public void OpenProxyManagerDialog() { }
            public void OpenNexusWindow() { }
            public void OpenAboutDialog() { }
            public void OpenAccountFieldsDialog(Account account) { }
            public void OpenAccountUtilsDialog(Account account) { }
            public void OpenMissingAssetsDialog(Account account, long[] assetIds) { }
        }

        private static void EnsureWebServerSettings()
        {
            AppSettings.ActiveIniFile ??= new IniFile();
        }

        private static AccountManager.ApiRequestContext CreateLocalApiRequest(
            string path,
            string password,
            Dictionary<string, string>? query = null,
            string body = "",
            bool isV2 = true,
            Action<int>? onStatus = null)
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
                SetStatusCode = onStatus ?? (_ => { })
            };
        }

        [Fact]
        public async Task WebServerEndpoints_GlobalAndAccountCommands_ExecuteCorrectly()
        {
            EnsureWebServerSettings();
            var mgr = AccountManager.Instance ?? new AccountManager();

            string oldPass = AppSettings.WebSocketPassword ?? string.Empty;
            var oldAccounts = AppServices.AccountRepository.GetAll().ToList();
            string? oldAllowGetAccounts = AppSettings.WebServer.Get("AllowGetAccounts");
            string? oldAllowGetCookie = AppSettings.WebServer.Get("AllowGetCookie");
            string? oldAllowEdit = AppSettings.WebServer.Get("AllowAccountEditing");
            string? oldAllowLaunch = AppSettings.WebServer.Get("AllowLaunchAccount");

            try
            {
                const string pass = "TestSecretPass99";
                AppSettings.WebSocketPassword = pass;
                AppSettings.WebServer.Set("AllowGetAccounts", "true");
                AppSettings.WebServer.Set("AllowGetCookie", "true");
                AppSettings.WebServer.Set("AllowAccountEditing", "true");
                AppSettings.WebServer.Set("AllowLaunchAccount", "false");

                var testAcc = new Account
                {
                    Username = "EndpointTestUser",
                    UserID = 123456789,
                    Group = "TestGroup",
                    Alias = "InitialAlias",
                    Description = "InitialDesc",
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--test-cookie-token",
                    Valid = true
                };
                testAcc.SetField("CustomKey", "CustomVal");

                lock (AccountManager.AccountListLock)
                {
                    AccountManager.ResetAccountsForTests(Array.Empty<Account>());
                }
                AppServices.AccountRepository.AddThreadSafe(testAcc);

                // 1. GetWebSocketStats
                string statsResp = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/GetWebSocketStats", pass));
                Assert.Contains("Shiro Kestrel Gateway", statsResp);

                // 2. GetAccounts (all & filtered by Group)
                string getAccountsResp = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/GetAccounts", pass));
                Assert.Contains("EndpointTestUser", getAccountsResp);

                string getAccountsFiltered = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest(
                    "/GetAccounts",
                    pass,
                    new Dictionary<string, string> { ["Group"] = "OtherGroup" }));
                Assert.DoesNotContain("EndpointTestUser", getAccountsFiltered);

                // 3. GetAccountsJson (with & without cookies)
                string jsonWithCookies = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest(
                    "/GetAccountsJson",
                    pass,
                    new Dictionary<string, string> { ["IncludeCookies"] = "true", ["Group"] = "TestGroup" }));
                Assert.Contains("EndpointTestUser", jsonWithCookies);
                Assert.Contains("test-cookie-token", jsonWithCookies);

                // 4. AntiAfk Status / Enable / Disable / Toggle
                string afkStatus = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/AntiAfk/Status", pass));
                Assert.Contains("Enabled", afkStatus);

                string afkEnable = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/AntiAfk/Enable", pass));
                Assert.Contains("true", afkEnable, StringComparison.OrdinalIgnoreCase);

                string afkToggle = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/AntiAfk/Toggle", pass));
                Assert.Contains("Enabled", afkToggle);

                string afkDisable = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/AntiAfk/Disable", pass));
                Assert.Contains("false", afkDisable, StringComparison.OrdinalIgnoreCase);

                // 5. MultiRoblox Status & AutoReconnect Status/Toggle
                string multiStatus = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/MultiRoblox/Status", pass));
                Assert.Contains("MutexHeld", multiStatus);

                string autoRecStatus = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/AutoReconnect/Status", pass));
                Assert.Contains("ActiveSessions", autoRecStatus);

                string autoRecToggle = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/AutoReconnect/Toggle", pass));
                Assert.Contains("Enabled", autoRecToggle);
                // Toggle back
                await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/AutoReconnect/Toggle", pass));

                // 6. Accounts/Export
                string exportResp = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest(
                    "/Accounts/Export",
                    pass,
                    new Dictionary<string, string> { ["Format"] = "Json", ["Group"] = "TestGroup" }));
                Assert.Contains("EndpointTestUser", exportResp);

                // 7. Account-specific endpoints: Empty Account & Invalid Account
                string emptyAccResp = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/GetAlias", pass));
                Assert.Contains("Empty Account", emptyAccResp);

                string invalidAccResp = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest(
                    "/GetAlias",
                    pass,
                    new Dictionary<string, string> { ["Account"] = "NonExistentAccount999" }));
                Assert.Contains("Invalid Account", invalidAccResp);

                // 8. Account lookup by Username and by numeric UserID
                var accQuery = new Dictionary<string, string> { ["Account"] = "EndpointTestUser" };
                var idQuery = new Dictionary<string, string> { ["Account"] = "123456789" };

                string aliasResp = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/GetAlias", pass, accQuery));
                Assert.Contains("InitialAlias", aliasResp);

                string descResp = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/GetDescription", pass, idQuery));
                Assert.Contains("InitialDesc", descResp);

                string cookieResp = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/GetCookie", pass, accQuery));
                Assert.Contains("test-cookie-token", cookieResp);

                // 9. Proxy Get / Set
                await mgr.ProcessApiRequestAsync(CreateLocalApiRequest(
                    "/SetProxy",
                    pass,
                    new Dictionary<string, string> { ["Account"] = "EndpointTestUser", ["Proxy"] = "127.0.0.1:8888" }));
                string getProxyResp = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/GetProxy", pass, accQuery));
                Assert.Contains("127.0.0.1:8888", getProxyResp);

                // 10. Fields: GetField, SetField, RemoveField
                string getFieldResp = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest(
                    "/GetField",
                    pass,
                    new Dictionary<string, string> { ["Account"] = "EndpointTestUser", ["Field"] = "CustomKey" }));
                Assert.Contains("CustomVal", getFieldResp);

                await mgr.ProcessApiRequestAsync(CreateLocalApiRequest(
                    "/SetField",
                    pass,
                    new Dictionary<string, string> { ["Account"] = "EndpointTestUser", ["Field"] = "NewKey", ["Value"] = "NewVal" }));
                Assert.Equal("NewVal", testAcc.GetField("NewKey"));

                await mgr.ProcessApiRequestAsync(CreateLocalApiRequest(
                    "/RemoveField",
                    pass,
                    new Dictionary<string, string> { ["Account"] = "EndpointTestUser", ["Field"] = "NewKey" }));
                Assert.Equal(string.Empty, testAcc.GetField("NewKey"));

                // 11. SetAlias, SetDescription, AppendDescription
                await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/SetAlias", pass, accQuery, body: "UpdatedAlias"));
                Assert.Equal("UpdatedAlias", testAcc.Alias);

                await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/SetDescription", pass, accQuery, body: "BaseDesc"));
                Assert.Equal("BaseDesc", testAcc.Description);

                await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/AppendDescription", pass, accQuery, body: "_Appended"));
                Assert.Equal("BaseDesc_Appended", testAcc.Description);

                // 12. Permission checks when disabled
                AppSettings.WebServer.Set("AllowGetAccounts", "false");
                AppSettings.WebServer.Set("AllowGetCookie", "false");
                AppSettings.WebServer.Set("AllowAccountEditing", "false");

                int status = 200;
                string deniedAccounts = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/GetAccounts", pass, onStatus: c => status = c));
                Assert.Equal(401, status);
                Assert.Contains("not allowed", deniedAccounts);

                string deniedCookie = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/GetCookie", pass, accQuery, onStatus: c => status = c));
                Assert.Equal(401, status);
                Assert.Contains("not allowed", deniedCookie);

                string deniedSetField = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest(
                    "/SetField",
                    pass,
                    new Dictionary<string, string> { ["Account"] = "EndpointTestUser", ["Field"] = "K", ["Value"] = "V" },
                    onStatus: c => status = c));
                Assert.Equal(401, status);

                string deniedLaunch = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/LaunchAccount", pass, accQuery, onStatus: c => status = c));
                Assert.Equal(401, status);

                string deniedFollow = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/FollowUser", pass, accQuery, onStatus: c => status = c));
                Assert.Equal(401, status);

                // 13. Swagger / OpenAPI / Docs / Favicon endpoints
                string favicon = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/favicon.ico", pass));
                Assert.Equal(string.Empty, favicon);

                string openapi = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/openapi.json", pass));
                Assert.Contains("openapi", openapi, StringComparison.OrdinalIgnoreCase);

                string docs = await mgr.ProcessApiRequestAsync(CreateLocalApiRequest("/docs", pass));
                Assert.Contains("<html", docs, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                AppSettings.WebSocketPassword = oldPass;
                AppSettings.WebServer.Set("AllowGetAccounts", oldAllowGetAccounts);
                AppSettings.WebServer.Set("AllowGetCookie", oldAllowGetCookie);
                AppSettings.WebServer.Set("AllowAccountEditing", oldAllowEdit);
                AppSettings.WebServer.Set("AllowLaunchAccount", oldAllowLaunch);

                lock (AccountManager.AccountListLock)
                {
                    AccountManager.ResetAccountsForTests(Array.Empty<Account>());
                }
                foreach (var oldAcc in oldAccounts)
                {
                    AppServices.AccountRepository.AddThreadSafe(oldAcc);
                }
            }
        }
    }
}
