#nullable enable
using System;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Dialogs
{
    [Collection("StaticSeams")]
    public class AccountUtilsViewModelTests
    {
        private readonly Account _account = new() { Username = "alice", Password = "old" };
        private readonly IAccountRepository _repo = Substitute.For<IAccountRepository>();
        private readonly IUserPrompt _prompt = Substitute.For<IUserPrompt>();

        [Fact]
        public async Task LogOut_Declined_DoesNotCallApi()
        {
            _prompt.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
            bool called = false;
            var vm = new AccountUtilsViewModel(_account, _repo, _prompt, logoutOthers: _ =>
            {
                called = true;
                return Task.FromResult(true);
            });

            await vm.LogOutOtherSessionsAsync();

            Assert.False(called);
        }

        [Fact]
        public async Task LogOut_Confirmed_Success()
        {
            _prompt.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
            var vm = new AccountUtilsViewModel(_account, _repo, _prompt, logoutOthers: _ => Task.FromResult(true));

            await vm.LogOutOtherSessionsAsync();

            Assert.Equal(LocalizationService.Get("AccountUtils.LogoutOtherSessionsSuccess"), vm.StatusText);
            Assert.Equal(StatusKind.Success, vm.StatusKind);
        }

        [Fact]
        public async Task LogOut_Throws_UsesCommonErrorFormat()
        {
            _prompt.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
            var vm = new AccountUtilsViewModel(_account, _repo, _prompt, logoutOthers: _ =>
                throw new InvalidOperationException("net fail"));

            await vm.LogOutOtherSessionsAsync();

            Assert.Equal(LocalizationService.Format("Common.ErrorFormat", "net fail"), vm.StatusText);
            Assert.Equal(StatusKind.Error, vm.StatusKind);
        }

        [Fact]
        public void SavePassword_Empty_ShowsPasswordCleared_AndSaves()
        {
            _repo.SaveAsync(true).Returns(Task.CompletedTask);
            var vm = new AccountUtilsViewModel(_account, _repo, _prompt) { Password = "" };

            vm.SavePasswordCommand.Execute(null);

            Assert.Equal("", _account.Password);
            Assert.Equal(LocalizationService.Get("AccountUtils.PasswordCleared"), vm.StatusText);
            Assert.Equal(StatusKind.Success, vm.StatusKind);
            _repo.Received(1).SaveAsync(true);
        }
    }
}
