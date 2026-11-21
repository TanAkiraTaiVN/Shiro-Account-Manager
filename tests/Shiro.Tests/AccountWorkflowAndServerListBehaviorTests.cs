#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountWorkflowAndServerListBehaviorTests : IDisposable
    {
        private sealed class FakeDialogService : IDialogService
        {
            public string InputPromptResult { get; set; } = string.Empty;
            public bool ConfirmationResult { get; set; } = true;
            public bool? YesNoCancelResult { get; set; } = true;

            public void ShowInfo(string message, string title = "Shiro") { }
            public void ShowWarning(string message, string title = "Shiro") { }
            public void ShowError(string message, string title = "Shiro") { }
            public Task<MessageBoxResult> ShowMessageBoxAsync(string message, string title = "Shiro", MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None) => Task.FromResult(ConfirmationResult ? MessageBoxResult.OK : MessageBoxResult.Cancel);
            public Task<bool> ShowConfirmationAsync(string message, string title = "Shiro") => Task.FromResult(ConfirmationResult);
            public Task<bool?> ShowYesNoCancelAsync(string message, string title = "Shiro") => Task.FromResult(YesNoCancelResult);
            public Task<string> ShowInputPromptAsync(string message, string title, string defaultValue = "") => Task.FromResult(InputPromptResult);
            public Task<string> ShowSecretPromptAsync(string message, string title, string defaultValue = "") => Task.FromResult(InputPromptResult);
            public void OpenFastLoginDialog(Account? account = null) { }
            public void OpenFastLoginDialogBatch(IReadOnlyList<Account> accounts) { }
            public void OpenBulkImportDialog() { }
            public void OpenProxyManagerDialog() { }
            public void OpenAccountFieldsDialog(Account account) { }
            public void OpenAccountUtilsDialog(Account account) { }
            public void OpenMissingAssetsDialog(Account account, long[] assetIds) { }
        }

        private readonly IDisposable _dialogsScope;
        private readonly FakeDialogService _fakeDialogs;
        private readonly AccountWorkflowService _workflow;

        public AccountWorkflowAndServerListBehaviorTests()
        {
            _fakeDialogs = new FakeDialogService();
            _dialogsScope = AppServices.Override<IDialogService>(_fakeDialogs);
            _workflow = AccountWorkflowService.Default;
        }

        public void Dispose()
        {
            _dialogsScope.Dispose();
        }

        [Fact]
        public async Task SetAlias_WithUserInput_UpdatesAccountAlias()
        {
            var acc = new Account { Username = "AliasUser", Alias = "Old" };
            var vm = new AccountViewModel(acc);
            _fakeDialogs.InputPromptResult = "NewSuperAlias";

            string status = "";
            await _workflow.SetAliasAsync(vm, s => status = s);

            Assert.Equal("NewSuperAlias", vm.Alias);
            Assert.Equal("NewSuperAlias", acc.Alias);
            Assert.Contains("NewSuperAlias", status);
        }

        [Fact]
        public async Task SetGroup_WithUserInput_UpdatesGroup()
        {
            var acc = new Account { Username = "GroupUser", Group = "OldGroup" };
            var vm = new AccountViewModel(acc);
            var mainVm = new MainViewModel();
            _fakeDialogs.InputPromptResult = "VIPs";

            string status = "";
            await _workflow.SetGroupAsync(vm, mainVm, s => status = s);

            Assert.Equal("VIPs", vm.Group);
            Assert.Equal("VIPs", acc.Group);
            Assert.Contains("VIPs", status);
        }

        [Fact]
        public async Task SetDescription_WithUserInput_UpdatesDescription()
        {
            var acc = new Account { Username = "DescUser", Description = "OldDesc" };
            var vm = new AccountViewModel(acc);
            _fakeDialogs.InputPromptResult = "New Description Note";

            string status = "";
            await _workflow.SetDescriptionAsync(vm, s => status = s);

            Assert.Equal("New Description Note", vm.Description);
            Assert.Equal("New Description Note", acc.Description);
            Assert.Contains("DescUser", status);
        }

        [Fact]
        public void TogglePin_TogglesPinnedState()
        {
            var acc = new Account { Username = "PinUser", Pinned = false };
            var vm = new AccountViewModel(acc);
            var mainVm = new MainViewModel();

            string status = "";
            _workflow.TogglePin(vm, mainVm, s => status = s);

            Assert.True(vm.Pinned);
            Assert.True(acc.Pinned);

            _workflow.TogglePin(vm, mainVm, s => status = s);
            Assert.False(vm.Pinned);
            Assert.False(acc.Pinned);
        }

        [Fact]
        public void CopyCommands_InvokeCallbacksSafely()
        {
            var acc = new Account
            {
                Username = "CopyUser",
                Password = "SecretPassword123",
                UserID = 1234567,
                SecurityToken = "SampleTokenABC"
            };
            var vm = new AccountViewModel(acc);

            string status = "";
            _workflow.CopyUsername(vm, s => status = s);
            Assert.Contains("CopyUser", status);

            status = "";
            _workflow.CopyUserPass(vm, s => status = s);
            Assert.Contains("CopyUser", status);

            status = "";
            _workflow.CopyUserId(vm, s => status = s);
            Assert.Contains("1234567", status);

            status = "";
            _workflow.CopyProfileUrl(vm, s => status = s);
            Assert.Contains("CopyUser", status);

            status = "";
            _workflow.CopySecurityToken(vm, s => status = s);
            Assert.False(string.IsNullOrEmpty(status));
        }

        [Fact]
        public async Task RemoveAccount_WhenConfirmed_RemovesAccountFromViewModel()
        {
            var acc = new Account { Username = "DeleteMeUser" };
            var vm = new AccountViewModel(acc);
            var mainVm = new MainViewModel();
            mainVm.Accounts.Add(vm);
            _fakeDialogs.ConfirmationResult = true;

            string status = "";
            await _workflow.RemoveAccountAsync(vm, mainVm, s => status = s);

            Assert.DoesNotContain(vm, mainVm.Accounts);
            Assert.Contains("DeleteMeUser", status);
        }

        [Fact]
        public async Task RemoveAccount_WhenCancelled_LeavesAccountIntact()
        {
            var acc = new Account { Username = "KeepMeUser" };
            var vm = new AccountViewModel(acc);
            var mainVm = new MainViewModel();
            mainVm.Accounts.Add(vm);
            _fakeDialogs.ConfirmationResult = false;

            string status = "";
            await _workflow.RemoveAccountAsync(vm, mainVm, s => status = s);

            Assert.Contains(vm, mainVm.Accounts);
            Assert.Empty(status);
        }

        [Fact]
        public async Task SetGroupSelected_NoSelection_NotifiesWarning()
        {
            var mainVm = new MainViewModel();
            string status = "";
            await _workflow.SetGroupSelectedAsync(mainVm, s => status = s);

            Assert.Equal(LocalizationService.Get("AccountWorkflow.SelectAccountBeforeGroup"), status);
        }

        [Fact]
        public async Task SetGroupSelected_WithSelection_UpdatesSelectedGroups()
        {
            var mainVm = new MainViewModel();
            var acc1 = new Account { Username = "BatchUser1", Group = "OldGroup" };
            var acc2 = new Account { Username = "BatchUser2", Group = "OldGroup" };
            var vm1 = new AccountViewModel(acc1) { IsSelected = true };
            var vm2 = new AccountViewModel(acc2) { IsSelected = true };
            mainVm.Accounts.Add(vm1);
            mainVm.Accounts.Add(vm2);
            _fakeDialogs.InputPromptResult = "SuperBatch";

            string status = "";
            await _workflow.SetGroupSelectedAsync(mainVm, s => status = s);

            Assert.Equal("SuperBatch", vm1.Group);
            Assert.Equal("SuperBatch", vm2.Group);
            Assert.Contains("SuperBatch", status);
        }

        [Fact]
        public void SavePlaceIdSelected_ValidationAndPersistence()
        {
            var mainVm = new MainViewModel();
            string statusNoSelection = "";
            _workflow.SavePlaceIdSelected(mainVm, s => statusNoSelection = s);
            Assert.Equal(LocalizationService.Get("AccountWorkflow.SelectAccountBeforeSavePlace"), statusNoSelection);

            var acc = new Account { Username = "PlaceSaveUser" };
            var vm = new AccountViewModel(acc) { IsSelected = true };
            mainVm.Accounts.Add(vm);

            mainVm.PlaceId = "invalid_id";
            string statusInvalid = "";
            _workflow.SavePlaceIdSelected(mainVm, s => statusInvalid = s);
            Assert.Equal(LocalizationService.Get("AccountWorkflow.EnterValidPlaceBeforeSave"), statusInvalid);

            mainVm.PlaceId = "987654321";
            string statusValid = "";
            _workflow.SavePlaceIdSelected(mainVm, s => statusValid = s);
            Assert.Equal("987654321", acc.GetField("SavedPlaceId"));
            Assert.Contains("987654321", statusValid);
        }

        [Fact]
        public void CopyUserPassSelected_ValidatesSelectionAndCopies()
        {
            var mainVm = new MainViewModel();
            string statusNoSelection = "";
            _workflow.CopyUserPassSelected(mainVm, s => statusNoSelection = s);
            Assert.Equal(LocalizationService.Get("AccountWorkflow.SelectAccountBeforeCopyPass"), statusNoSelection);

            var acc = new Account { Username = "PassUser", Password = "Pass123Password" };
            var vm = new AccountViewModel(acc) { IsSelected = true };
            mainVm.Accounts.Add(vm);

            string statusCopied = "";
            _workflow.CopyUserPassSelected(mainVm, s => statusCopied = s);
            Assert.Contains("1", statusCopied);
        }

        [Fact]
        public async Task RemoveSelectedAccounts_ValidatesSelectionAndDeletes()
        {
            var mainVm = new MainViewModel();
            string statusNoSelection = "";
            await _workflow.RemoveSelectedAccountsAsync(mainVm, s => statusNoSelection = s);
            Assert.Equal(LocalizationService.Get("AccountWorkflow.SelectAccountBeforeDelete"), statusNoSelection);

            var acc = new Account { Username = "BatchDeleteUser" };
            var vm = new AccountViewModel(acc) { IsSelected = true };
            mainVm.Accounts.Add(vm);
            _fakeDialogs.ConfirmationResult = true;

            string statusDeleted = "";
            await _workflow.RemoveSelectedAccountsAsync(mainVm, s => statusDeleted = s);
            Assert.DoesNotContain(vm, mainVm.Accounts);
            Assert.False(string.IsNullOrEmpty(statusDeleted));
        }

        [Fact]
        public async Task ReLoginWithCookieAsync_WhenCancelled_PreservesOldState()
        {
            var acc = new Account { Username = "ReloginUser", SecurityToken = "ExistingSecurityToken" };
            var vm = new AccountViewModel(acc);
            var mainVm = new MainViewModel();
            _fakeDialogs.InputPromptResult = "/UC"; // User Cancelled

            string status = "";
            await _workflow.ReLoginWithCookieAsync(vm, mainVm, s => status = s);

            Assert.Equal("ExistingSecurityToken", acc.SecurityToken);
            Assert.Empty(status);
        }

        [Fact]
        public void ServerListViewModel_PlayerSearch_EmptyUsername_SetsStatus()
        {
            var vm = new ServerListViewModel();
            vm.SearchPlayerUsername = "";
            vm.SearchPlayerCommand.Execute(null);

            Assert.Equal(LocalizationService.Get("ServerList.EnterPlayerUsername"), vm.PlayerSearchStatus);
        }

        [Fact]
        public void ServerListViewModel_PlayerSearch_InvalidPlaceId_SetsStatus()
        {
            var vm = new ServerListViewModel();
            vm.SearchPlayerUsername = "RobloxPlayer";
            vm.PlaceIdInput = "not-a-number";
            vm.SearchPlayerCommand.Execute(null);

            Assert.Equal(LocalizationService.Get("ServerList.EnterPlaceIdFirst"), vm.PlayerSearchStatus);
        }

        [Fact]
        public async Task ServerListViewModel_PlayerSearch_UserNotFound_SetsNotFoundStatus()
        {
            var vm = new ServerListViewModel();
            vm.SearchPlayerUsername = "DefinitelyNoSuchUserExists_99999";
            vm.PlaceIdInput = "1818";
            vm.SearchPlayerCommand.Execute(null);

            for (int i = 0; i < 20 && vm.IsSearchingPlayer; i++)
            {
                await Task.Delay(50);
            }

            Assert.False(string.IsNullOrEmpty(vm.PlayerSearchStatus));
        }

        [Fact]
        public void ServerListViewModel_TokenRequest_InitializesProperties()
        {
            var req = new TokenRequest("sample-player-token");
            Assert.Equal("sample-player-token", req.token);
            Assert.Equal("AvatarHeadShot", req.type);
            Assert.Equal("48x48", req.size);
            Assert.Equal("png", req.format);
            Assert.False(string.IsNullOrEmpty(req.requestId));
        }

        [Fact]
        public void ServerListViewModel_Games_CollectionsAndFlags()
        {
            string isolated = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Shiro-GamesFlags-" + Guid.NewGuid().ToString("N"));
            using (StoragePaths.OverrideDataDirectoryForTesting(isolated))
            {
                var vm = new ServerListViewModel();
                Assert.True(vm.IsSearchGamesEmpty);
                Assert.True(vm.IsFavoritesEmpty);
                Assert.NotNull(vm.SearchGames);
                Assert.NotNull(vm.Favorites);

                vm.GameSearchTerm = "Blox";
                Assert.Equal("Blox", vm.GameSearchTerm);

                vm.IsSearchingGames = true;
                Assert.True(vm.IsSearchingGames);
                Assert.False(vm.IsSearchGamesEmpty);
            }
        }

        [Fact]
        public async Task Presence_GetPresence_WithNullOrEmpty_ReturnsEmptyDictionary()
        {
            var resNull = await Presence.GetPresence(null!);
            Assert.Empty(resNull!);

            var resEmpty = await Presence.GetPresence(Array.Empty<long>());
            Assert.Empty(resEmpty!);
        }

        [Fact]
        public async Task Presence_UpdatePresence_WithNullOrEmpty_CompletesSafely()
        {
            var ex1 = await Record.ExceptionAsync(() => Presence.UpdatePresence(null!));
            Assert.Null(ex1);

            var ex2 = await Record.ExceptionAsync(() => Presence.UpdatePresence(Array.Empty<long>()));
            Assert.Null(ex2);
        }
    }
}
