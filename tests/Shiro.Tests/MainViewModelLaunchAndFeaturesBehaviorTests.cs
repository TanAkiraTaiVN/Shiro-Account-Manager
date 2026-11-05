#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Shiro.Classes;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class MainViewModelLaunchAndFeaturesBehaviorTests
    {
        [Fact]
        public void NormalizePlaceIdInput_ValidNumbersAndUrls_NormalizesInput()
        {
            var vm = new MainViewModel();

            vm.NormalizePlaceIdInput("189707");
            Assert.Equal("189707", vm.PlaceId);

            vm.NormalizePlaceIdInput("https://www.roblox.com/games/998877/TestExperience");
            Assert.Equal("998877", vm.PlaceId);

            // Empty input is ignored
            vm.NormalizePlaceIdInput("");
            Assert.Equal("998877", vm.PlaceId);
        }

        [Fact]
        public void LaunchAccountCommand_And_OpenHomeCommand_ExecuteCallbacksOrFallback()
        {
            var vm = new MainViewModel();
            var acc = new Account { Username = "TestLaunchUser", Valid = true };
            var avm = new AccountViewModel(acc);

            AccountViewModel? launched = null;
            vm.OnLaunchRequested = a => launched = a;

            vm.LaunchAccountCommand.Execute(avm);
            Assert.Same(avm, launched);

            AccountViewModel? openedHome = null;
            vm.OnOpenHomeRequested = a => openedHome = a;

            vm.OpenHomeCommand.Execute(avm);
            Assert.Same(avm, openedHome);
        }

        [Fact]
        public void PrimaryCardActionCommand_BannedOrExpiredOrValid_DispatchesAppropriately()
        {
            var vm = new MainViewModel();

            // 1. Banned or Locked account -> ViewBan logic
            var bannedAcc = new Account { Username = "BannedUser", Valid = false, AccountStatusIssue = "Banned: Terms of Service" };
            var bannedAvm = new AccountViewModel(bannedAcc);

            AccountViewModel? reLoginTarget = null;
            vm.OnFastReLoginRequested = a => reLoginTarget = a;

            vm.PrimaryCardActionCommand.Execute(bannedAvm);
            Assert.Same(bannedAvm, reLoginTarget);
            Assert.Contains("BannedUser", vm.StatusMessage);

            // 2. Expired account -> Dialog logic
            var expiredAcc = new Account { Username = "ExpiredUser", Valid = false, AccountStatusIssue = "SessionExpired" };
            var expiredAvm = new AccountViewModel(expiredAcc);
            var ex = Record.Exception(() => vm.PrimaryCardActionCommand.Execute(expiredAvm));
            Assert.Null(ex);

            // 3. Valid account -> Launch callback
            var validAcc = new Account { Username = "NormalUser", Valid = true, SecurityToken = "valid_security_token_sample" };
            var validAvm = new AccountViewModel(validAcc);
            AccountViewModel? launchedTarget = null;
            vm.OnLaunchRequested = a => launchedTarget = a;

            vm.PrimaryCardActionCommand.Execute(validAvm);
            Assert.Same(validAvm, launchedTarget);
        }

        [Fact]
        public void ViewBanAccountCommand_And_CloseAccountRobloxCommand_ExecuteCleanly()
        {
            var vm = new MainViewModel();
            var acc = new Account { Username = "TargetUser", Valid = false, AccountStatusIssue = "TemporarilyBanned" };
            var avm = new AccountViewModel(acc);

            AccountViewModel? reLoginCalled = null;
            vm.OnFastReLoginRequested = a => reLoginCalled = a;

            vm.ViewBanAccountCommand.Execute(avm);
            Assert.Same(avm, reLoginCalled);
            Assert.Contains("TargetUser", vm.StatusMessage);

            var exClose = Record.Exception(() => vm.CloseAccountRobloxCommand.Execute(avm));
            Assert.Null(exClose);
        }

        [Fact]
        public void LaunchSelectedCommand_And_JoinServerSelectedCommand_HandleZeroAndMultiSelection()
        {
            var vm = new MainViewModel();

            // 0 accounts selected -> warning
            vm.LaunchSelectedCommand.Execute(null);
            Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));

            vm.JoinServerSelectedCommand.Execute(null);
            Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));

            // Select accounts
            var acc1 = new Account { Username = "SelUser1", Valid = true };
            var acc2 = new Account { Username = "SelUser2", Valid = true };
            var avm1 = new AccountViewModel(acc1) { IsSelected = true };
            var avm2 = new AccountViewModel(acc2) { IsSelected = true };
            vm.Accounts.Add(avm1);
            vm.Accounts.Add(avm2);

            IList<AccountViewModel>? multiLaunched = null;
            vm.OnMultiLaunchRequested = list => multiLaunched = list;

            vm.LaunchSelectedCommand.Execute(null);
            Assert.NotNull(multiLaunched);
            Assert.Equal(2, multiLaunched.Count);

            // JoinServerSelected with invalid PlaceId -> missing place ID warning
            vm.PlaceId = "";
            vm.JoinServerSelectedCommand.Execute(null);
            Assert.Contains(Shiro.Services.LocalizationService.Get("Main.EnterPlaceIdOrLink"), vm.StatusMessage);

            // JoinServerSelected with valid PlaceId
            vm.PlaceId = "123456";
            IList<AccountViewModel>? joinSelected = null;
            vm.OnJoinServerSelectedRequested = list => joinSelected = list;

            vm.JoinServerSelectedCommand.Execute(null);
            Assert.NotNull(joinSelected);
            Assert.Equal(2, joinSelected.Count);
        }

        [Fact]
        public void Features_TogglesAndProperties_UpdateSettingsAndText()
        {
            AntiAfkService.Stop();
            AppServices.GeneralSettings?.Set("EnablePotatoMode", "false");
            var vm = new MainViewModel();

            // Potato Mode
            vm.IsPotatoMode = true;
            Assert.True(vm.IsPotatoMode);
            Assert.Contains(Shiro.Services.LocalizationService.Get("Main.PotatoModeActivated"), vm.StatusMessage);
            vm.IsPotatoMode = false;
            Assert.False(vm.IsPotatoMode);

            // Anti-Afk
            vm.IsAntiAfk = true;
            Assert.True(vm.IsAntiAfk);
            Assert.Contains(Shiro.Services.LocalizationService.Get("Main.AntiAfkActivated"), vm.StatusMessage);
            vm.IsAntiAfk = false;
            Assert.False(vm.IsAntiAfk);

            // Streamer Mode
            vm.IsStreamerModeEnabled = true;
            Assert.True(vm.IsStreamerModeEnabled);
            vm.IsStreamerModeEnabled = false;
            Assert.False(vm.IsStreamerModeEnabled);

            // Windows hidden
            vm.AreWindowsHidden = true;
            Assert.True(vm.AreWindowsHidden);
            vm.AreWindowsHidden = false;
            Assert.False(vm.AreWindowsHidden);

            // RefreshStateFromSettings
            vm.RefreshStateFromSettings();

            // Localized labels inspection
            Assert.False(string.IsNullOrWhiteSpace(vm.LaunchHomeButtonText));
            Assert.False(string.IsNullOrWhiteSpace(vm.JoinServerButtonText));
            Assert.False(string.IsNullOrWhiteSpace(vm.FooterSelectedText));
            Assert.False(string.IsNullOrWhiteSpace(vm.FooterTotalText));
            Assert.False(string.IsNullOrWhiteSpace(vm.FooterReadyStatusText));
            Assert.False(string.IsNullOrWhiteSpace(vm.SubtitleTotalText));
            Assert.False(string.IsNullOrWhiteSpace(vm.SubtitleReadyText));
            Assert.False(string.IsNullOrWhiteSpace(vm.SubtitleSelectedText));
            Assert.False(string.IsNullOrWhiteSpace(vm.TabAllText));
            Assert.False(string.IsNullOrWhiteSpace(vm.TabInGameText));
            Assert.False(string.IsNullOrWhiteSpace(vm.TabReadyText));
            Assert.False(string.IsNullOrWhiteSpace(vm.TabExpiredText));
            Assert.False(string.IsNullOrWhiteSpace(vm.TabBannedText));
            Assert.False(string.IsNullOrWhiteSpace(vm.TabPinnedText));
            Assert.False(string.IsNullOrWhiteSpace(vm.DeselectCountText));
            Assert.False(string.IsNullOrWhiteSpace(vm.CloseRobloxButtonText));

            // RecentGamesList
            var recentItem = new RecentGameItemViewModel { PlaceId = 554433, Name = "TestGame" };
            vm.RecentGamesList = new ObservableCollection<RecentGameItemViewModel> { recentItem };
            Assert.True(vm.HasRecentGames);
            Assert.Single(vm.RecentGamesList);
        }
    }
}
