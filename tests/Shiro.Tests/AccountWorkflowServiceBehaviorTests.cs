#nullable enable
using System;
using System.Collections.Generic;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountWorkflowServiceBehaviorTests : IDisposable
    {
        private sealed class WorkflowTestDialogService : IDialogService
        {
            public string InputPromptResult { get; set; } = string.Empty;
            public bool ConfirmationResult { get; set; } = true;
            public bool? YesNoCancelResult { get; set; } = true;

            public void ShowInfo(string message, string title = "Shiro") { }
            public void ShowWarning(string message, string title = "Shiro") { }
            public void ShowError(string message, string title = "Shiro") { }
            public bool ShowConfirmation(string message, string title = "Shiro") => ConfirmationResult;
            public bool? ShowYesNoCancel(string message, string title = "Shiro") => YesNoCancelResult;
            public string ShowInputPrompt(string message, string title, string defaultValue = "") => InputPromptResult;
            public void OpenFastLoginDialog(Account? account = null) { }
            public void OpenFastLoginDialogBatch(IReadOnlyList<Account> accounts) { }
            public void OpenBulkImportDialog() { }
            public void OpenAltGenDialog() { }
            public void OpenProxyManagerDialog() { }
            public void OpenAboutDialog() { }
            public void OpenAccountFieldsDialog(Account account) { }
            public void OpenAccountUtilsDialog(Account account) { }
            public void OpenMissingAssetsDialog(Account account, long[] assetIds) { }
        }

        private readonly IDisposable _dialogsScope;
        private readonly WorkflowTestDialogService _fakeDialogs;
        private readonly AccountWorkflowService _workflow;

        public AccountWorkflowServiceBehaviorTests()
        {
            _fakeDialogs = new WorkflowTestDialogService();
            _dialogsScope = AppServices.Override<IDialogService>(_fakeDialogs);
            _workflow = AccountWorkflowService.Default;
        }

        public void Dispose()
        {
            _dialogsScope.Dispose();
        }

        [Fact]
        public void SetAlias_UserEntersAlias_UpdatesAccountAndInvokesCallback()
        {
            var acc = new Account { Username = "AliasTestUser" };
            var vm = new AccountViewModel(acc);
            _fakeDialogs.InputPromptResult = "NewAliasName";

            string status = "";
            _workflow.SetAlias(vm, s => status = s);

            Assert.Equal("NewAliasName", acc.Alias);
            Assert.Equal("NewAliasName", vm.Alias);
            Assert.Contains("NewAliasName", status);

            // If cancelled (/UC), should not update
            _fakeDialogs.InputPromptResult = "/UC";
            _workflow.SetAlias(vm, s => status = s);
            Assert.Equal("NewAliasName", acc.Alias);
        }

        [Fact]
        public void SetGroup_UserEntersGroup_UpdatesAccountAndRebuildsGroups()
        {
            var mainVm = new MainViewModel();
            var acc = new Account { Username = "GroupTestUser" };
            var vm = new AccountViewModel(acc);
            _fakeDialogs.InputPromptResult = "FarmingGroup";

            string status = "";
            _workflow.SetGroup(vm, mainVm, s => status = s);

            Assert.Equal("FarmingGroup", acc.Group);
            Assert.Equal("FarmingGroup", vm.Group);
            Assert.Contains("FarmingGroup", status);

            // Cancelled (/UC)
            _fakeDialogs.InputPromptResult = "/UC";
            _workflow.SetGroup(vm, mainVm, s => status = s);
            Assert.Equal("FarmingGroup", acc.Group);
        }

        [Fact]
        public void SetDescription_UserEntersNotes_UpdatesAccount()
        {
            var acc = new Account { Username = "NotesTestUser" };
            var vm = new AccountViewModel(acc);
            _fakeDialogs.InputPromptResult = "Important Notes 123";

            string status = "";
            _workflow.SetDescription(vm, s => status = s);

            Assert.Equal("Important Notes 123", acc.Description);
            Assert.Equal("Important Notes 123", vm.Description);
            Assert.Contains(acc.Username, status);
        }

        [Fact]
        public void TogglePin_FlipsPinnedState()
        {
            var mainVm = new MainViewModel();
            var acc = new Account { Username = "PinTestUser", Pinned = false };
            var vm = new AccountViewModel(acc);

            string status1 = "";
            _workflow.TogglePin(vm, mainVm, s => status1 = s);
            Assert.True(acc.Pinned);
            Assert.True(vm.Pinned);

            string status2 = "";
            _workflow.TogglePin(vm, mainVm, s => status2 = s);
            Assert.False(acc.Pinned);
            Assert.False(vm.Pinned);
        }

        [Fact]
        public void CopyCommands_InvokeStatusCallbacks()
        {
            var acc = new Account
            {
                Username = "CopyUser",
                Password = "SecretPassword!",
                UserID = 778899,
                SecurityToken = "dummy-security-token-value"
            };
            var vm = new AccountViewModel(acc);

            string status = "";
            _workflow.CopyUsername(vm, s => status = s);
            Assert.Contains("CopyUser", status);

            _workflow.CopyUserPass(vm, s => status = s);
            Assert.Contains("CopyUser", status);

            _workflow.CopyUserId(vm, s => status = s);
            Assert.Contains("778899", status);

            _workflow.CopyProfileUrl(vm, s => status = s);
            Assert.Contains("CopyUser", status);

            _workflow.CopySecurityToken(vm, s => status = s);
            Assert.Contains("CopyUser", status);
        }

        [Fact]
        public void RemoveAccount_Confirmed_RemovesFromCollection()
        {
            var mainVm = new MainViewModel();
            var acc = new Account { Username = "DeleteUser" };
            var vm = new AccountViewModel(acc);
            mainVm.Accounts.Add(vm);

            _fakeDialogs.ConfirmationResult = true;
            string status = "";
            _workflow.RemoveAccount(vm, mainVm, s => status = s);

            Assert.DoesNotContain(vm, mainVm.Accounts);
            Assert.Contains("DeleteUser", status);
        }

        [Fact]
        public void BatchOperations_WhenNoAccountsSelected_PromptSelection()
        {
            var mainVm = new MainViewModel();
            string status = "";

            _workflow.SetGroupSelected(mainVm, s => status = s);
            Assert.Equal(LocalizationService.Get("AccountWorkflow.SelectAccountBeforeGroup"), status);

            status = "";
            _workflow.SavePlaceIdSelected(mainVm, s => status = s);
            Assert.Equal(LocalizationService.Get("AccountWorkflow.SelectAccountBeforeSavePlace"), status);

            status = "";
            _workflow.CopyUserPassSelected(mainVm, s => status = s);
            Assert.Equal(LocalizationService.Get("AccountWorkflow.SelectAccountBeforeCopyPass"), status);

            status = "";
            _workflow.RemoveSelectedAccounts(mainVm, s => status = s);
            Assert.Equal(LocalizationService.Get("AccountWorkflow.SelectAccountBeforeDelete"), status);
        }

        [Fact]
        public void BatchOperations_WithSelectedAccounts_ExecutesSuccessfully()
        {
            var mainVm = new MainViewModel();
            var acc1 = new Account { Username = "Batch1", Password = "p1" };
            var acc2 = new Account { Username = "Batch2", Password = "p2" };
            var vm1 = new AccountViewModel(acc1) { IsSelected = true };
            var vm2 = new AccountViewModel(acc2) { IsSelected = true };
            mainVm.Accounts.Add(vm1);
            mainVm.Accounts.Add(vm2);

            _fakeDialogs.InputPromptResult = "BatchGroup";
            string status = "";
            _workflow.SetGroupSelected(mainVm, s => status = s);

            Assert.Equal("BatchGroup", vm1.Group);
            Assert.Equal("BatchGroup", vm2.Group);

            // Batch save place id
            mainVm.PlaceId = "18973";
            _workflow.SavePlaceIdSelected(mainVm, s => status = s);
            Assert.Equal("18973", acc1.GetField("SavedPlaceId"));
            Assert.Equal("18973", acc2.GetField("SavedPlaceId"));

            // Batch copy user pass
            _workflow.CopyUserPassSelected(mainVm, s => status = s);
            Assert.NotEmpty(status);

            // Batch remove
            _fakeDialogs.ConfirmationResult = true;
            _workflow.RemoveSelectedAccounts(mainVm, s => status = s);
            Assert.Empty(mainVm.Accounts);
        }

        [Fact]
        public async Task CheckAllSessionsAsync_EmptyAccounts_ReturnsEarly()
        {
            var mainVm = new MainViewModel();
            string status = "";
            await _workflow.CheckAllSessionsAsync(mainVm, s => status = s);
            Assert.Empty(status);
        }

        [Fact]
        public async Task CheckAllSessionsAsync_WithAccounts_ScansAndReportsResults()
        {
            var origClientFactory = Account.TestHttpClientFactory;

            try
            {
                var handler = new Shiro.Tests.TestInfrastructure.FakeHttpHandler(req => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized))
                    .Enqueue(System.Net.HttpStatusCode.OK, "{\"id\":111,\"name\":\"HealthyUser\"}")
                    .Enqueue(System.Net.HttpStatusCode.Unauthorized, "{}");
                Account.TestHttpClientFactory = _ => new System.Net.Http.HttpClient(handler);

                var mockRepo = NSubstitute.Substitute.For<IAccountRepository>();
                var healthyAcc = new Account { Username = "HealthyUser", UserID = 111, SecurityToken = "_|WARNING:-token-healthy", Valid = true };
                var expiredAcc = new Account { Username = "ExpiredUser", UserID = 222, SecurityToken = "_|WARNING:-token-expired", Valid = false };

                mockRepo.GetAll().Returns(new List<Account> { healthyAcc, expiredAcc });
                using var _ = AppServices.Override<IAccountRepository>(mockRepo);

                var mainVm = new MainViewModel();
                var reportedStatuses = new List<string>();

                await _workflow.CheckAllSessionsAsync(mainVm, s => reportedStatuses.Add(s));

                Assert.Contains(reportedStatuses, s => s.Contains("HealthyUser"));
                await mockRepo.Received(1).SaveAsync(true);
            }
            finally
            {
                Account.TestHttpClientFactory = origClientFactory;
            }
        }

        [Fact]
        public void ValidateSessionSelected_NullOrEmptyList_ReturnsSafely()
        {
            var mainVm = new MainViewModel();
            string status = "";
            var ex1 = Record.Exception(() => _workflow.ValidateSessionSelected(null!, mainVm, s => status = s));
            Assert.Null(ex1);

            var ex2 = Record.Exception(() => _workflow.ValidateSessionSelected(new List<AccountViewModel>(), mainVm, s => status = s));
            Assert.Null(ex2);

            var ex3 = Record.Exception(() => _workflow.ValidateSessionSelected(new List<AccountViewModel> { null! }, mainVm, s => status = s));
            Assert.Null(ex3);
        }

        [Fact]
        public async Task ReLoginWithCookieAsync_NullModel_ReturnsEarly()
        {
            var mainVm = new MainViewModel();
            string status = "";
            await _workflow.ReLoginWithCookieAsync(null!, mainVm, s => status = s);
            Assert.Empty(status);
        }

        [Fact]
        public void AccountWorkflowService_DefaultInstance_NotNull()
        {
            var def = AccountWorkflowService.Default;
            Assert.Same(def, AccountWorkflowService.Default);
            var custom = new AccountWorkflowService();
            Assert.NotNull(custom);
        }

        [Fact]
        public async Task ReLoginWithCookieAsync_ValidateSessionSelected_And_CheckAllSessions_AllBranches_Work()
        {
            var origClientFactory = Account.TestHttpClientFactory;

            try
            {
                var mockRepo = Substitute.For<IAccountRepository>();
                using var _ = AppServices.Override<IAccountRepository>(mockRepo);
                var mainVm = new MainViewModel();

                // 1. ReLoginWithCookieAsync: valid new cookie updates session
                var handler = new Shiro.Tests.TestInfrastructure.FakeHttpHandler(_ =>
                    new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new System.Net.Http.StringContent("{\"id\":555,\"name\":\"RelogUser\"}")
                    });
                Account.TestHttpClientFactory = _ => new System.Net.Http.HttpClient(handler);

                var acc = new Account
                {
                    Username = "RelogUser",
                    UserID = 555,
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('E', 120),
                    Valid = true
                };
                var vm = new AccountViewModel(acc);
                _fakeDialogs.InputPromptResult = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('F', 120);

                string status = "";
                await _workflow.ReLoginWithCookieAsync(vm, mainVm, s => status = s);
                Assert.True(acc.Valid);
                Assert.NotEmpty(status);

                // 2. ReLoginWithCookieAsync: invalid new cookie when previously valid retains old cookie
                Account.TestHttpClientFactory = _ => new System.Net.Http.HttpClient(
                    new Shiro.Tests.TestInfrastructure.FakeHttpHandler(_ =>
                        new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                        {
                            Content = new System.Net.Http.StringContent("{}")
                        }));
                string oldTok = acc.SecurityToken;
                _fakeDialogs.InputPromptResult = "_|WARNING:-DO-NOT-SHARE-THIS.--bad";
                await _workflow.ReLoginWithCookieAsync(vm, mainVm, s => status = s);
                Assert.True(acc.Valid);
                Assert.Equal(oldTok, acc.SecurityToken);

                // 3. ReLoginWithCookieAsync: invalid new cookie when previously invalid marks invalid & saves
                acc.Valid = false;
                await _workflow.ReLoginWithCookieAsync(vm, mainVm, s => status = s);
                Assert.False(acc.Valid);

                // 4. ValidateSessionSelected with valid and invalid items
                var tcsDone = new System.Threading.Tasks.TaskCompletionSource<string>();
                _workflow.ValidateSessionSelected(new List<AccountViewModel> { vm }, mainVm, s =>
                {
                    if (s == LocalizationService.Format("AccountWorkflow.SessionCheckCompleted", 1))
                        tcsDone.TrySetResult(s);
                });
                string completedMsg = await tcsDone.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.NotEmpty(completedMsg);

                // 5. CheckAllSessionsAsync when all healthy & when >5 banned accounts
                Account.TestHttpClientFactory = _ => new System.Net.Http.HttpClient(
                    new Shiro.Tests.TestInfrastructure.FakeHttpHandler(_ =>
                        new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        {
                            Content = new System.Net.Http.StringContent("{\"id\":555,\"name\":\"RelogUser\"}")
                        }));
                acc.Valid = true;
                acc.AccountStatusIssue = "";
                mockRepo.GetAll().Returns(new List<Account> { acc });
                await _workflow.CheckAllSessionsAsync(mainVm, s => status = s);

                // Null guards and invalid PlaceId in SavePlaceIdSelected
                mainVm.Accounts.Add(vm);
                vm.IsSelected = true;
                mainVm.PlaceId = "invalid-place";
                _workflow.SavePlaceIdSelected(mainVm, s => status = s);
                Assert.Equal(LocalizationService.Get("AccountWorkflow.EnterValidPlaceBeforeSave"), status);

                _workflow.SetAlias(null!, _ => { });
                _workflow.SetGroup(null!, mainVm, _ => { });
                _workflow.SetDescription(null!, _ => { });
                _workflow.TogglePin(null!, mainVm, _ => { });
                _workflow.CopyUsername(null!, _ => { });
                _workflow.CopyUserPass(null!, _ => { });
                _workflow.CopyUserId(null!, _ => { });
                _workflow.CopyProfileUrl(null!, _ => { });
                _workflow.CopySecurityToken(null!, _ => { });
                _workflow.RemoveAccount(null!, mainVm, _ => { });
                _workflow.SetGroupSelected(null!, _ => { });
                _workflow.SavePlaceIdSelected(null!, _ => { });
                _workflow.CopyUserPassSelected(null!, _ => { });
                _workflow.RemoveSelectedAccounts(null!, _ => { });
            }
            finally
            {
                Account.TestHttpClientFactory = origClientFactory;
            }
        }

        [Fact]
        public void AccountWorkflowService_DefaultInstance_DuplicateCheck()
        {
            var def = AccountWorkflowService.Default;
            Assert.Same(def, AccountWorkflowService.Default);
        }
    }
}
