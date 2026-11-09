#nullable enable
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Dialogs
{
    public sealed class CookieImportViewModelTests
    {
        private readonly IAccountRepository _repo = Substitute.For<IAccountRepository>();
        private readonly IHostUiBridgeService _host = Substitute.For<IHostUiBridgeService>();

        [Fact]
        public async Task Verify_Empty_ShowsPasteCookiePrompt()
        {
            var vm = new CookieImportViewModel(_repo, _ => Task.FromResult(new Account()), _host);
            vm.Cookie = "   ";

            await vm.VerifyCommand.ExecuteAsync();

            Assert.Equal(LocalizationService.Get("FastLogin.PasteCookiePrompt"), vm.StatusText);
            Assert.Equal(StatusKind.Error, vm.StatusKind);
        }

        [Fact]
        public async Task Verify_Valid_SetsPreview()
        {
            var validAcc = new Account
            {
                Username = "bob",
                UserID = 5,
                Valid = true,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--valid"
            };
            var vm = new CookieImportViewModel(_repo, _ => Task.FromResult(validAcc), _host);
            vm.Cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--valid";

            await vm.VerifyCommand.ExecuteAsync();

            Assert.Equal("bob (ID: 5)", vm.PreviewUsername);
            Assert.Equal(LocalizationService.Get("FastLogin.CookieValidAndVerified"), vm.StatusText);
            Assert.Equal(StatusKind.Success, vm.StatusKind);
        }

        [Fact]
        public async Task Verify_Invalid_ClearsCache()
        {
            bool validFirst = true;
            var vm = new CookieImportViewModel(_repo, _ =>
            {
                if (validFirst)
                {
                    return Task.FromResult(new Account
                    {
                        Username = "bob",
                        UserID = 5,
                        Valid = true,
                        SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--c1"
                    });
                }
                return Task.FromResult(new Account { Valid = false });
            }, _host);

            vm.Cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--c1";
            await vm.VerifyCommand.ExecuteAsync();
            Assert.Equal("bob (ID: 5)", vm.PreviewUsername);

            validFirst = false;
            vm.Cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--c2";
            await vm.VerifyCommand.ExecuteAsync();

            Assert.Equal(LocalizationService.Get("FastLogin.InvalidAccountTitle"), vm.PreviewUsername);
            Assert.Equal(LocalizationService.Get("FastLogin.CookieExpiredOrInvalid"), vm.StatusText);
            Assert.Equal(StatusKind.Error, vm.StatusKind);
        }

        [Fact]
        public async Task Add_AfterVerifySameCookie_UsesRegisterValidated()
        {
            var validAcc = new Account
            {
                Username = "bob",
                UserID = 5,
                Valid = true,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--same"
            };
            _repo.RegisterValidated(validAcc, validAcc.SecurityToken, Arg.Any<string>(), true).Returns(validAcc);

            var vm = new CookieImportViewModel(_repo, _ => Task.FromResult(validAcc), _host);
            vm.Cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--same";

            await vm.VerifyCommand.ExecuteAsync();
            bool? closed = null;
            vm.CloseRequested += (_, r) => closed = r;

            await vm.AddCommand.ExecuteAsync();

            _repo.Received(1).RegisterValidated(validAcc, validAcc.SecurityToken, Arg.Any<string>(), true);
            await _repo.DidNotReceiveWithAnyArgs().AddOrUpdateAsync(default!, default!);
            Assert.Same(validAcc, vm.CreatedAccount);
            Assert.Equal(true, closed);
        }

        [Fact]
        public async Task Add_CookieEditedAfterVerify_UsesAddOrUpdate()
        {
            var verifiedAcc = new Account
            {
                Username = "bob",
                UserID = 5,
                Valid = true,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--c1"
            };
            var addedAcc = new Account
            {
                Username = "alice",
                UserID = 6,
                Valid = true,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--c2"
            };
            _repo.AddOrUpdateAsync("_|WARNING:-DO-NOT-SHARE-THIS.--c2", Arg.Any<string>()).Returns(addedAcc);

            var vm = new CookieImportViewModel(_repo, _ => Task.FromResult(verifiedAcc), _host);
            vm.Cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--c1";
            await vm.VerifyCommand.ExecuteAsync();

            vm.Cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--c2";
            await vm.AddCommand.ExecuteAsync();

            _repo.DidNotReceiveWithAnyArgs().RegisterValidated(default!, default!);
            await _repo.Received(1).AddOrUpdateAsync("_|WARNING:-DO-NOT-SHARE-THIS.--c2", Arg.Any<string>());
            Assert.Same(addedAcc, vm.CreatedAccount);
        }

        [Fact]
        public async Task Add_WithAliasGroup_SavesMetadata()
        {
            var addedAcc = new Account
            {
                Username = "alice",
                UserID = 6,
                Valid = true,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--c2"
            };
            _repo.AddOrUpdateAsync("_|WARNING:-DO-NOT-SHARE-THIS.--c2", Arg.Any<string>()).Returns(addedAcc);

            var vm = new CookieImportViewModel(_repo, host: _host);
            vm.Cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--c2";
            vm.Alias = "Main";
            vm.Group = "Farm";

            await vm.AddCommand.ExecuteAsync();

            Assert.Equal("Main", addedAcc.Alias);
            Assert.Equal("Farm", addedAcc.Group);
            await _repo.Received(1).SaveAsync(true);
        }

        [Fact]
        public async Task Add_Invalid_ShowsCookieFailedOrExpired_NoClose()
        {
            _repo.AddOrUpdateAsync("_|WARNING:-DO-NOT-SHARE-THIS.--expired", Arg.Any<string>())
                .Returns(new Account { Valid = false });

            var vm = new CookieImportViewModel(_repo, host: _host);
            vm.Cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--expired";

            bool? closed = null;
            vm.CloseRequested += (_, r) => closed = r;

            await vm.AddCommand.ExecuteAsync();

            Assert.Null(closed);
            Assert.Equal(LocalizationService.Get("FastLogin.CookieFailedOrExpired"), vm.StatusText);
            Assert.Equal(StatusKind.Error, vm.StatusKind);
        }
    }
}
