using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shiro;
using Shiro.Classes;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class WebServerEndpointsAndWorkflowTests
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
            AccountManager.IniSettings ??= new IniFile();
            AccountManager.WebServer ??= AccountManager.IniSettings.Section("WebServer");
            AccountManager.General ??= AccountManager.IniSettings.Section("General");
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

            string oldPass = AccountManager.WSPassword;
            var oldAccounts = AccountManager.GetAccountsSnapshot().ToList();
            string? oldAllowGetAccounts = AccountManager.WebServer.Get("AllowGetAccounts");
            string? oldAllowGetCookie = AccountManager.WebServer.Get("AllowGetCookie");
            string? oldAllowEdit = AccountManager.WebServer.Get("AllowAccountEditing");
            string? oldAllowLaunch = AccountManager.WebServer.Get("AllowLaunchAccount");

            try
            {
                const string pass = "TestSecretPass99";
                AccountManager.WSPassword = pass;
                AccountManager.WebServer.Set("AllowGetAccounts", "true");
                AccountManager.WebServer.Set("AllowGetCookie", "true");
                AccountManager.WebServer.Set("AllowAccountEditing", "true");
                AccountManager.WebServer.Set("AllowLaunchAccount", "false");

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
                    AccountManager.AccountsList.Clear();
                }
                AccountManager.AddAccountThreadSafe(testAcc);

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
                AccountManager.WebServer.Set("AllowGetAccounts", "false");
                AccountManager.WebServer.Set("AllowGetCookie", "false");
                AccountManager.WebServer.Set("AllowAccountEditing", "false");

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
                AccountManager.WSPassword = oldPass;
                AccountManager.WebServer.Set("AllowGetAccounts", oldAllowGetAccounts);
                AccountManager.WebServer.Set("AllowGetCookie", oldAllowGetCookie);
                AccountManager.WebServer.Set("AllowAccountEditing", oldAllowEdit);
                AccountManager.WebServer.Set("AllowLaunchAccount", oldAllowLaunch);

                lock (AccountManager.AccountListLock)
                {
                    AccountManager.AccountsList.Clear();
                }
                foreach (var oldAcc in oldAccounts)
                {
                    AccountManager.AddAccountThreadSafe(oldAcc);
                }
            }
        }

        [Fact]
        public void AccountWorkflowService_SynchronousOperations_WorkWithMockDialogs()
        {
            var originalDialog = AppServices.DialogService;
            var fakeDialog = new FakeDialogService();
            AppServices.DialogService = fakeDialog;

            var oldAccounts = AccountManager.GetAccountsSnapshot().ToList();
            try
            {
                var acc1 = new Account
                {
                    Username = "WorkflowUser1",
                    Password = "Pass123",
                    UserID = 111222333,
                    Group = "Default",
                    Alias = "OldAlias",
                    Description = "OldDesc",
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--token1",
                    Valid = true
                };
                var acc2 = new Account
                {
                    Username = "WorkflowUser2",
                    Password = "Pass456",
                    UserID = 444555666,
                    Group = "Default",
                    Valid = true
                };

                lock (AccountManager.AccountListLock)
                {
                    AccountManager.AccountsList.Clear();
                }
                AccountManager.AddAccountThreadSafe(acc1);
                AccountManager.AddAccountThreadSafe(acc2);

                var mainVm = new MainViewModel();
                mainVm.LoadAccounts(AccountManager.GetAccountsSnapshot());
                var vm1 = mainVm.Accounts.First(a => a.Username == "WorkflowUser1");
                var vm2 = mainVm.Accounts.First(a => a.Username == "WorkflowUser2");

                var workflow = AccountWorkflowService.Default;
                string lastStatus = string.Empty;
                Action<string> statusCb = s => lastStatus = s;

                // 1. SetAlias
                fakeDialog.InputPromptResult = "  NewHeroAlias  ";
                workflow.SetAlias(vm1, statusCb);
                Assert.Equal("NewHeroAlias", acc1.Alias);
                Assert.Equal("NewHeroAlias", vm1.Alias);
                Assert.Contains("NewHeroAlias", lastStatus);

                // Cancel SetAlias ("/UC")
                fakeDialog.InputPromptResult = "/UC";
                workflow.SetAlias(vm1, statusCb);
                Assert.Equal("NewHeroAlias", acc1.Alias);

                // 2. SetGroup (single)
                fakeDialog.InputPromptResult = "Farming";
                workflow.SetGroup(vm1, mainVm, statusCb);
                Assert.Equal("Farming", acc1.Group);
                Assert.Contains("Farming", lastStatus);

                // 3. SetDescription
                fakeDialog.InputPromptResult = "Updated Note";
                workflow.SetDescription(vm1, statusCb);
                Assert.Equal("Updated Note", acc1.Description);
                Assert.Contains("ghi chú", lastStatus);

                // 4. TogglePin
                Assert.False(acc1.Pinned);
                workflow.TogglePin(vm1, mainVm, statusCb);
                Assert.True(acc1.Pinned);
                Assert.True(vm1.Pinned);
                workflow.TogglePin(vm1, mainVm, statusCb);
                Assert.False(acc1.Pinned);

                // 5. Copy helpers
                workflow.CopyUsername(vm1, statusCb);
                Assert.Contains("WorkflowUser1", lastStatus);

                workflow.CopyUserPass(vm1, statusCb);
                Assert.Contains("User:Pass", lastStatus);

                workflow.CopyUserId(vm1, statusCb);
                Assert.Contains("111222333", lastStatus);

                workflow.CopyProfileUrl(vm1, statusCb);
                Assert.Contains("hồ sơ", lastStatus);

                workflow.CopySecurityToken(vm1, statusCb);
                Assert.Contains("Security Token", lastStatus);

                // 6. Batch operations when nothing selected
                foreach (var a in mainVm.Accounts) a.IsSelected = false;
                workflow.SetGroupSelected(mainVm, statusCb);
                Assert.Contains("tick chọn", lastStatus);

                workflow.SavePlaceIdSelected(mainVm, statusCb);
                Assert.Contains("tick chọn", lastStatus);

                workflow.CopyUserPassSelected(mainVm, statusCb);
                Assert.Contains("tick chọn", lastStatus);

                workflow.RemoveSelectedAccounts(mainVm, statusCb);
                Assert.Contains("tick chọn", lastStatus);

                // 7. Batch operations with selection
                vm1.IsSelected = true;
                vm2.IsSelected = true;

                fakeDialog.InputPromptResult = "BatchGroup";
                workflow.SetGroupSelected(mainVm, statusCb);
                Assert.Equal("BatchGroup", vm1.Group);
                Assert.Equal("BatchGroup", vm2.Group);

                mainVm.PlaceId = "invalid_place";
                workflow.SavePlaceIdSelected(mainVm, statusCb);
                Assert.Contains("Place ID hợp lệ", lastStatus);

                mainVm.PlaceId = "1818";
                workflow.SavePlaceIdSelected(mainVm, statusCb);
                Assert.Equal("1818", acc1.GetField("SavedPlaceId"));
                Assert.Equal("1818", acc2.GetField("SavedPlaceId"));

                workflow.CopyUserPassSelected(mainVm, statusCb);
                Assert.Contains("2 tài khoản", lastStatus);

                // 8. RemoveAccount (single) & RemoveSelectedAccounts
                fakeDialog.ConfirmationResult = false;
                workflow.RemoveAccount(vm1, mainVm, statusCb);
                Assert.Equal(2, mainVm.Accounts.Count);

                fakeDialog.ConfirmationResult = true;
                workflow.RemoveAccount(vm1, mainVm, statusCb);
                Assert.Single(mainVm.Accounts);

                vm2.IsSelected = true;
                workflow.RemoveSelectedAccounts(mainVm, statusCb);
                Assert.Empty(mainVm.Accounts);
            }
            finally
            {
                AppServices.DialogService = originalDialog;
                lock (AccountManager.AccountListLock)
                {
                    AccountManager.AccountsList.Clear();
                }
                foreach (var oldAcc in oldAccounts)
                {
                    AccountManager.AddAccountThreadSafe(oldAcc);
                }
            }
        }

        [Fact]
        public async Task AccountManager_GetUserIdCache_AndRegisterValidatedAccount_WorkCorrectly()
        {
            var oldAccounts = AccountManager.GetAccountsSnapshot().ToList();
            try
            {
                // Empty/whitespace username returns false immediately
                var emptyRes = await AccountManager.GetUserIDAsync("   ");
                Assert.False(emptyRes.Success);
                Assert.Equal(-1, emptyRes.UserId);

                var knownAcc = new Account
                {
                    Username = "CachedKnownPlayer",
                    UserID = 99887766,
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--token-initial",
                    Valid = true
                };

                lock (AccountManager.AccountListLock)
                {
                    AccountManager.AccountsList.Clear();
                }
                AccountManager.AddAccountThreadSafe(knownAcc);

                // Known account in AccountsList resolves without network call and populates cache
                var knownRes = await AccountManager.GetUserIDAsync("CachedKnownPlayer");
                Assert.True(knownRes.Success);
                Assert.Equal(99887766, knownRes.UserId);

                // Second call hits ResolvedUserIdsCache directly
                var cachedRes = await AccountManager.GetUserIDAsync("cachedknownplayer");
                Assert.True(cachedRes.Success);
                Assert.Equal(99887766, cachedRes.UserId);

                // RegisterValidatedAccount updates existing account when UserID matches
                var updatedAcc = new Account
                {
                    Username = "CachedKnownPlayer",
                    UserID = 99887766,
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--token-updated",
                    Valid = true
                };
                var registered = AccountManager.RegisterValidatedAccount(updatedAcc, updatedAcc.SecurityToken, "NewSecretPass", saveImmediate: false);
                Assert.Same(knownAcc, registered);
                Assert.Equal("NewSecretPass", knownAcc.Password);
                Assert.Equal("_|WARNING:-DO-NOT-SHARE-THIS.--token-updated", knownAcc.SecurityToken);

                // RegisterValidatedAccount returns null for invalid account
                var invalidAcc = new Account { Username = "InvalidAcc", Valid = false };
                Assert.Null(AccountManager.RegisterValidatedAccount(invalidAcc, "bad", saveImmediate: false));
            }
            finally
            {
                lock (AccountManager.AccountListLock)
                {
                    AccountManager.AccountsList.Clear();
                }
                foreach (var oldAcc in oldAccounts)
                {
                    AccountManager.AddAccountThreadSafe(oldAcc);
                }
            }
        }
    }
}
