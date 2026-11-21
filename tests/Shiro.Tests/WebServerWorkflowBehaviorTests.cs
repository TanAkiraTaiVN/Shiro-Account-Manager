using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shiro;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    public partial class WebServerEndpointsAndWorkflowTests
    {
        [Fact]
        public async Task AccountWorkflowService_SynchronousOperations_WorkWithMockDialogs()
        {
            var fakeDialog = new FakeDialogService();
            using var _ = AppServices.Override<IDialogService>(fakeDialog);

            var oldAccounts = AppServices.AccountRepository.GetAll().ToList();
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
                    AccountManager.ResetAccountsForTests(Array.Empty<Account>());
                }
                AppServices.AccountRepository.AddThreadSafe(acc1);
                AppServices.AccountRepository.AddThreadSafe(acc2);

                var mainVm = new MainViewModel();
                mainVm.LoadAccounts(AppServices.AccountRepository.GetAll());
                var vm1 = mainVm.Accounts.First(a => a.Username == "WorkflowUser1");
                var vm2 = mainVm.Accounts.First(a => a.Username == "WorkflowUser2");

                var workflow = AccountWorkflowService.Default;
                string lastStatus = string.Empty;
                Action<string> statusCb = s => lastStatus = s;

                // 1. SetAlias
                fakeDialog.InputPromptResult = "  NewHeroAlias  ";
                await workflow.SetAliasAsync(vm1, statusCb);
                Assert.Equal("NewHeroAlias", acc1.Alias);
                Assert.Equal("NewHeroAlias", vm1.Alias);
                Assert.Contains("NewHeroAlias", lastStatus);

                // Cancel SetAlias ("/UC")
                fakeDialog.InputPromptResult = "/UC";
                await workflow.SetAliasAsync(vm1, statusCb);
                Assert.Equal("NewHeroAlias", acc1.Alias);

                // 2. SetGroup (single)
                fakeDialog.InputPromptResult = "Farming";
                await workflow.SetGroupAsync(vm1, mainVm, statusCb);
                Assert.Equal("Farming", acc1.Group);
                Assert.Contains("Farming", lastStatus);

                // 3. SetDescription
                fakeDialog.InputPromptResult = "Updated Note";
                await workflow.SetDescriptionAsync(vm1, statusCb);
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
                await workflow.SetGroupSelectedAsync(mainVm, statusCb);
                Assert.Contains("tick chọn", lastStatus);

                workflow.SavePlaceIdSelected(mainVm, statusCb);
                Assert.Contains("tick chọn", lastStatus);

                workflow.CopyUserPassSelected(mainVm, statusCb);
                Assert.Contains("tick chọn", lastStatus);

                await workflow.RemoveSelectedAccountsAsync(mainVm, statusCb);
                Assert.Contains("tick chọn", lastStatus);

                // 7. Batch operations with selection
                vm1.IsSelected = true;
                vm2.IsSelected = true;

                fakeDialog.InputPromptResult = "BatchGroup";
                await workflow.SetGroupSelectedAsync(mainVm, statusCb);
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
                await workflow.RemoveAccountAsync(vm1, mainVm, statusCb);
                Assert.Equal(2, mainVm.Accounts.Count);

                fakeDialog.ConfirmationResult = true;
                await workflow.RemoveAccountAsync(vm1, mainVm, statusCb);
                Assert.Single(mainVm.Accounts);

                vm2.IsSelected = true;
                await workflow.RemoveSelectedAccountsAsync(mainVm, statusCb);
                Assert.Empty(mainVm.Accounts);
            }
            finally
            {
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

        [Fact]
        public async Task BlockUserEndpoint_WhenAccountThrows_Returns500WithoutExceptionMessage()
        {
            EnsureWebServerSettings();
            var mgr = AccountManager.Instance ?? new AccountManager();
            string oldPass = AppSettings.WebSocketPassword ?? string.Empty;
            var oldAccounts = AppServices.AccountRepository.GetAll().ToList();

            try
            {
                const string pass = "TestSecretPass99";
                AppSettings.WebSocketPassword = pass;

                var testAcc = new Account
                {
                    Username = "ThrowingEndpointUser",
                    UserID = 987654321,
                    Valid = true
                };

                lock (AccountManager.AccountListLock)
                {
                    AccountManager.ResetAccountsForTests(Array.Empty<Account>());
                }
                AppServices.AccountRepository.AddThreadSafe(testAcc);

                var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Account"] = "ThrowingEndpointUser",
                    ["Password"] = pass
                };

                var prevFactory = RobloxAccountSessionClient.TestHttpClientFactory;
                try
                {
                    RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(req =>
                    {
                        string url = req.RequestUri?.ToString() ?? string.Empty;
                        if (url.Contains("auth.roblox.com", StringComparison.OrdinalIgnoreCase))
                        {
                            var csrf = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
                            {
                                Content = new StringContent("{}")
                            };
                            csrf.Headers.TryAddWithoutValidation("x-csrf-token", "csrf-throw");
                            return csrf;
                        }
                        throw new InvalidOperationException("leak-https://x/?cookie=ABC");
                    }));

                    int statusCode = 0;
                    var req = new AccountManager.ApiRequestContext
                    {
                        AbsolutePath = "/BlockUser",
                        IsV2 = true,
                        IsLocal = true,
                        Host = "localhost",
                        Origin = "",
                        Body = "",
                        GetHeader = _ => null,
                        SetStatusCode = code => statusCode = code,
                        GetQuery = key =>
                        {
                            if (key == "UserId") return "12345";
                            return q.TryGetValue(key, out var val) ? val : null;
                        }
                    };

                    string responseBody = await mgr.ProcessApiRequestAsync(req);

                    Assert.Equal(502, statusCode);
                    Assert.DoesNotContain("leak-", responseBody);
                    Assert.Contains("Upstream transport failure", responseBody);
                }
                finally
                {
                    RobloxAccountSessionClient.TestHttpClientFactory = prevFactory;
                }
            }
            finally
            {
                AppSettings.WebSocketPassword = oldPass;
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

        [Fact]
        public async Task AccountManager_GetUserIdCache_AndRegisterValidatedAccount_WorkCorrectly()
        {
            var oldAccounts = AppServices.AccountRepository.GetAll().ToList();
            try
            {
                // Empty/whitespace username returns false immediately
                var emptyRes = await AccountRegistrationService.Default.GetUserIdAsync("   ");
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
                    AccountManager.ResetAccountsForTests(Array.Empty<Account>());
                }
                AppServices.AccountRepository.AddThreadSafe(knownAcc);

                // Known account in AccountsList resolves without network call and populates cache
                var knownRes = await AccountRegistrationService.Default.GetUserIdAsync("CachedKnownPlayer");
                Assert.True(knownRes.Success);
                Assert.Equal(99887766, knownRes.UserId);

                // Second call hits ResolvedUserIdsCache directly
                var cachedRes = await AccountRegistrationService.Default.GetUserIdAsync("cachedknownplayer");
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
                var registered = AccountRegistrationService.Default.RegisterValidatedAccount(updatedAcc, updatedAcc.SecurityToken, "NewSecretPass", saveImmediate: false);
                Assert.Same(knownAcc, registered);
                Assert.Equal("NewSecretPass", knownAcc.Password);
                Assert.Equal("_|WARNING:-DO-NOT-SHARE-THIS.--token-updated", knownAcc.SecurityToken);

                // RegisterValidatedAccount returns null for invalid account
                var invalidAcc = new Account { Username = "InvalidAcc", Valid = false };
                Assert.Null(AccountRegistrationService.Default.RegisterValidatedAccount(invalidAcc, "bad", saveImmediate: false));
            }
            finally
            {
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
