#nullable enable
using System;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Classes;
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

        [Theory]
        [InlineData("1234", true)]
        [InlineData(" 1234 ", true)]
        [InlineData("12a4", false)]
        [InlineData("123", false)]
        [InlineData("12345", false)]
        [InlineData(null, false)]
        [InlineData("١٢٣٤", false)]
        public void IsValidPin_ValidatesExactlyFourAsciiDigits(string? pin, bool expected)
        {
            Assert.Equal(expected, AccountUtilsViewModel.IsValidPin(pin));
        }

        [Fact]
        public async Task UnlockPin_Invalid_DoesNotCallApi()
        {
            bool called = false;
            var vm = new AccountUtilsViewModel(_account, _repo, _prompt, unlockPin: (_, _) =>
            {
                called = true;
                return Task.FromResult(true);
            })
            {
                Pin = "12a4"
            };

            await vm.UnlockPinAsync();

            Assert.False(called);
            Assert.Equal(LocalizationService.Get("AccountUtils.PinMustBeFourDigits"), vm.StatusText);
            Assert.Equal(StatusKind.Error, vm.StatusKind);
        }

        [Fact]
        public async Task UnlockPin_Success_SetsSuccessStatus()
        {
            string? capturedPin = null;
            var vm = new AccountUtilsViewModel(_account, _repo, _prompt, unlockPin: (_, p) =>
            {
                capturedPin = p;
                return Task.FromResult(true);
            })
            {
                Pin = " 9876 "
            };

            await vm.UnlockPinAsync();

            Assert.Equal("9876", capturedPin);
            Assert.Equal(LocalizationService.Get("AccountUtils.UnlockPinSuccess"), vm.StatusText);
            Assert.Equal(StatusKind.Success, vm.StatusKind);
        }

        [Fact]
        public async Task UnlockPin_Throws_UsesUnlockPinErrorFormat()
        {
            var vm = new AccountUtilsViewModel(_account, _repo, _prompt, unlockPin: (_, _) =>
                throw new InvalidOperationException("bad pin"))
            {
                Pin = "0000"
            };

            await vm.UnlockPinAsync();

            Assert.Equal(LocalizationService.Format("AccountUtils.UnlockPinError", "bad pin"), vm.StatusText);
            Assert.Equal(StatusKind.Error, vm.StatusKind);
        }

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
