using System.Collections.Generic;
using Shiro.Classes;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    public class AccountViewModelTests
    {
        [Fact]
        public void AccountViewModel_InitializesFromAccountModel()
        {
            var account = new Account
            {
                Username = "RobloxHero",
                UserID = 987654321,
                Group = "VIPs",
                Pinned = true,
                Alias = "MainAccount",
                Description = "My primary farming account"
            };

            var vm = new AccountViewModel(account);

            Assert.Same(account, vm.Model);
            Assert.Equal("RobloxHero", vm.Username);
            Assert.Equal(987654321, vm.UserId);
            Assert.Equal("VIPs", vm.Group);
            Assert.True(vm.Pinned);
            Assert.Equal("MainAccount", vm.Alias);
            Assert.Equal("My primary farming account", vm.Description);
            Assert.Equal("MainAccount", vm.PrimaryTitle);
            Assert.Equal("R", vm.Initials);
        }

        [Fact]
        public void AccountViewModel_AliasChange_UpdatesTitlesAndRaisesPropertyEvents()
        {
            var account = new Account
            {
                Username = "Player1",
                UserID = 11111
            };

            var vm = new AccountViewModel(account);
            var changedProperties = new List<string>();
            vm.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName);

            Assert.Equal("Player1", vm.PrimaryTitle);

            vm.Alias = "Alt1";

            Assert.Equal("Alt1", vm.Alias);
            Assert.Equal("Alt1", vm.PrimaryTitle);
            Assert.Contains(nameof(AccountViewModel.Alias), changedProperties);
            Assert.Contains(nameof(AccountViewModel.PrimaryTitle), changedProperties);
            Assert.Contains(nameof(AccountViewModel.SecondaryTitle), changedProperties);
            Assert.Contains(nameof(AccountViewModel.DisplayTitle), changedProperties);
        }

        [Fact]
        public void AccountViewModel_DescriptionChange_RaisesPropertyEvent()
        {
            var account = new Account { Username = "Player2" };
            var vm = new AccountViewModel(account);
            bool propertyFired = false;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AccountViewModel.Description))
                    propertyFired = true;
            };

            vm.Description = "New Note";

            Assert.True(propertyFired);
            Assert.Equal("New Note", vm.Description);
            Assert.Equal("New Note", account.Description);
        }

        [Fact]
        public void AccountViewModel_GroupChange_UpdatesModelAndRaisesPropertyEvent()
        {
            var account = new Account { Username = "Player3" };
            var vm = new AccountViewModel(account);
            bool groupFired = false;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AccountViewModel.Group))
                    groupFired = true;
            };

            vm.Group = "TradingGroup";

            Assert.True(groupFired);
            Assert.Equal("TradingGroup", vm.Group);
            Assert.Equal("TradingGroup", account.Group);
        }

        [Fact]
        public void AccountViewModel_PinnedToggle_UpdatesModelAndRaisesPropertyEvent()
        {
            var account = new Account { Username = "Player4", Pinned = false };
            var vm = new AccountViewModel(account);
            bool pinnedFired = false;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AccountViewModel.Pinned))
                    pinnedFired = true;
            };

            vm.Pinned = true;

            Assert.True(pinnedFired);
            Assert.True(vm.Pinned);
            Assert.True(account.Pinned);
        }

        [Fact]
        public void AccountViewModel_Initials_HandlesEmptyGracefully()
        {
            var account = new Account();
            var vm = new AccountViewModel(account);
            Assert.NotNull(vm.Initials);
        }

        [Fact]
        public void AccountViewModel_AvatarImage_HandlesMissingGracefully()
        {
            var account = new Account { UserID = 99999999999L };
            var vm = new AccountViewModel(account);
            // Should not throw and return null when no image exists on disk
            Assert.Null(vm.AvatarImage);
        }

        [Fact]
        public void AccountViewModel_IsSelectedToggle_UpdatesCardBorderBrushAndRaisesEvents()
        {
            var account = new Account { Username = "TargetPlayer", UserID = 123456 };
            var vm = new AccountViewModel(account);
            var changedProps = new List<string>();
            vm.PropertyChanged += (s, e) => changedProps.Add(e.PropertyName);

            Assert.False(vm.IsSelected);

            vm.IsSelected = true;

            Assert.True(vm.IsSelected);
            Assert.Contains(nameof(AccountViewModel.IsSelected), changedProps);
            Assert.Contains(nameof(AccountViewModel.CardBorderBrush), changedProps);

            vm.IsSelected = false;

            Assert.False(vm.IsSelected);
        }
    }
}
