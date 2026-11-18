#nullable enable
using System;
using System.Collections.Generic;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class MainViewModelStatusAndCountsTests
    {
        [Fact]
        public void LoadAccounts_And_NotifyCounts_ComputesExpectedAggregateMetrics()
        {
            var vm = new MainViewModel();

            var a1 = new Account { Username = "StatusUser1", Valid = true, RobuxBalance = 150, SecurityToken = "token1" };
            var a2 = new Account { Username = "StatusUser2", Valid = true, RobuxBalance = 250, SecurityToken = "token2" };
            var a3 = new Account { Username = "StatusUser3", Valid = false, AccountStatusIssue = "" };
            var a4 = new Account { Username = "StatusUser4", Valid = false, AccountStatusIssue = "Banned" };

            vm.LoadAccounts(new[] { a1, a2, a3, a4 });

            Assert.Equal(4, vm.TotalCount);

            // Mutate individual account view models to exercise counter permutations
            var avm1 = vm.Accounts[0];
            var avm2 = vm.Accounts[1];
            avm1.IsInGame = true;
            avm1.Pinned = true;
            avm1.IsSelected = true;
            avm2.IsSelected = true;

            vm.NotifyCounts();

            Assert.Equal(4, vm.TotalCount);
            Assert.Equal(1, vm.InGameCount);
            Assert.Equal(1, vm.ReadyCount);
            Assert.Equal(1, vm.ExpiredCount);
            Assert.True(vm.HasExpiredAccounts);
            Assert.Equal(1, vm.BannedCount);
            Assert.True(vm.HasBannedAccounts);
            Assert.Equal(1, vm.PinnedCount);
            Assert.Equal(2, vm.SelectedCount);
            Assert.True(vm.HasSelection);
            Assert.Equal(400L, vm.TotalRobuxCount);
            Assert.Equal("R$ 400", vm.TotalRobuxText);
        }

        [Fact]
        public void IsAllSelected_TriStateGetter_ReflectsSelectionRatio()
        {
            var vm = new MainViewModel();
            var a1 = new Account { Username = "SelectA1", Valid = true };
            var a2 = new Account { Username = "SelectA2", Valid = true };
            vm.LoadAccounts(new[] { a1, a2 });

            // 0 of 2 selected -> false
            vm.Accounts[0].IsSelected = false;
            vm.Accounts[1].IsSelected = false;
            Assert.False(vm.IsAllSelected);

            // 1 of 2 selected -> null (indeterminate)
            vm.Accounts[0].IsSelected = true;
            Assert.Null(vm.IsAllSelected);

            // 2 of 2 selected -> true
            vm.Accounts[1].IsSelected = true;
            Assert.True(vm.IsAllSelected);
        }

        [Fact]
        public void IsAllSelected_Setter_BulkMutatesSelection()
        {
            var vm = new MainViewModel();
            var a1 = new Account { Username = "BulkA1", Valid = true };
            var a2 = new Account { Username = "BulkA2", Valid = true };
            vm.LoadAccounts(new[] { a1, a2 });

            vm.IsAllSelected = true;
            Assert.Equal(2, vm.SelectedCount);
            Assert.True(vm.Accounts[0].IsSelected);
            Assert.True(vm.Accounts[1].IsSelected);

            vm.IsAllSelected = false;
            Assert.Equal(0, vm.SelectedCount);
            Assert.False(vm.Accounts[0].IsSelected);
            Assert.False(vm.Accounts[1].IsSelected);
        }

        [Fact]
        public void GetSelectedAccounts_And_ViewModels_ReturnOnlySelectedItems()
        {
            var vm = new MainViewModel();
            var a1 = new Account { Username = "SelectedPrime", Valid = true };
            var a2 = new Account { Username = "UnselectedSecond", Valid = true };
            vm.LoadAccounts(new[] { a1, a2 });

            vm.Accounts[0].IsSelected = true;
            vm.Accounts[1].IsSelected = false;

            var models = vm.GetSelectedAccounts();
            Assert.Single(models);
            Assert.Equal("SelectedPrime", models[0].Username);

            var viewModels = vm.GetSelectedAccountViewModels();
            Assert.Single(viewModels);
            Assert.Equal("SelectedPrime", viewModels[0].Username);
        }

        [Fact]
        public void UpdateAccountValidity_MutatesValidityAndExecutesSafely()
        {
            var vm = new MainViewModel();
            var acc = new Account { Username = "ValidityTarget", Valid = true, SecurityToken = "val-token" };
            vm.LoadAccounts(new[] { acc });

            Assert.True(vm.Accounts[0].Valid);

            vm.UpdateAccountValidity(acc, false, refreshFilterAndCounts: true);
            Assert.False(vm.Accounts[0].Valid);

            // Calling with null account returns safely without throwing
            vm.UpdateAccountValidity(null!, true);
        }

        [Fact]
        public void RefreshLiveProcessStatus_InlineExecution_RunsSafely()
        {
            var vm = new MainViewModel();
            var acc = new Account { Username = "LiveProcessUser", Valid = true };
            vm.LoadAccounts(new[] { acc });

            var ex = Record.Exception(() => vm.RefreshLiveProcessStatus());
            Assert.Null(ex);
            Assert.InRange(vm.RunningRobloxProcessCount, 0, int.MaxValue);
        }

        [Fact]
        public void MainViewModel_NavigationAndProperties_CoversBranches()
        {
            var vm = new MainViewModel();

            // Navigation
            vm.CurrentPage = NavigationPage.ServerList;
            Assert.True(vm.IsServerListActive);
            Assert.False(vm.IsAccountsActive);

            vm.CurrentPage = NavigationPage.RecentGames;
            Assert.True(vm.IsRecentGamesActive);

            vm.CurrentPage = NavigationPage.Settings;
            Assert.True(vm.IsSettingsActive);

            vm.CurrentPage = NavigationPage.Accounts;
            Assert.True(vm.IsAccountsActive);

            // Properties
            vm.PlaceId = "987654321";
            Assert.Equal("987654321", vm.PlaceId);

            vm.JobId = "job-guid-123";
            Assert.Equal("job-guid-123", vm.JobId);

            vm.RunningRobloxProcessCount = 2;
            Assert.Equal(2, vm.RunningRobloxProcessCount);
            Assert.True(vm.HasRunningRobloxProcesses);

            vm.IsCompactView = true;
            Assert.True(vm.IsCompactView);
            Assert.False(vm.IsCardView);

            vm.IsCompactView = false;
            Assert.True(vm.IsCardView);

            vm.IsShuffleJobId = true;
            Assert.True(vm.IsShuffleJobId);
            vm.IsShuffleJobId = false;
            Assert.False(vm.IsShuffleJobId);

            vm.StatusMessage = "Testing status message";
            Assert.Equal("Testing status message", vm.StatusMessage);

            // TryGetAccountViewModel lookups
            var acc = new Account { Username = "LookupUser", UserID = 554433, Valid = true };
            vm.LoadAccounts(new[] { acc });

            var byModel = vm.TryGetAccountViewModel(acc);
            Assert.NotNull(byModel);
            Assert.Equal("LookupUser", byModel.Username);

            var byId = vm.TryGetAccountViewModel(new Account { UserID = 554433 });
            Assert.NotNull(byId);
            Assert.Equal("LookupUser", byId.Username);

            var byName = vm.TryGetAccountViewModel(new Account { Username = "LookupUser" });
            Assert.NotNull(byName);
            Assert.Equal("LookupUser", byName.Username);

            Assert.Null(vm.TryGetAccountViewModel(null));
            Assert.Null(vm.TryGetAccountViewModel(new Account { Username = "NoSuchUser" }));
        }

        [Fact]
        public void MainViewModel_CurrentPage_SupportsAbout_GuardsDuplicates_AndDisposeClearsOnPageNavigated()
        {
            var vm = new MainViewModel();
            var navigated = new List<NavigationPage>();
            var changedProperties = new List<string>();
            vm.OnPageNavigated = page => navigated.Add(page);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != null)
                    changedProperties.Add(e.PropertyName);
            };

            Assert.Equal(NavigationPage.Accounts, vm.CurrentPage);
            Assert.True(vm.IsAccountsActive);
            Assert.False(vm.IsAboutActive);

            // Setting the same page must not re-fire OnPageNavigated
            vm.CurrentPage = NavigationPage.Accounts;
            Assert.Empty(navigated);

            vm.CurrentPage = NavigationPage.About;
            Assert.Equal(NavigationPage.About, vm.CurrentPage);
            Assert.True(vm.IsAboutActive);
            Assert.False(vm.IsAccountsActive);
            Assert.False(vm.IsServerListActive);
            Assert.False(vm.IsRecentGamesActive);
            Assert.False(vm.IsSettingsActive);
            Assert.Contains(nameof(MainViewModel.CurrentPage), changedProperties);
            Assert.Contains(nameof(MainViewModel.IsAboutActive), changedProperties);
            Assert.Equal(new[] { NavigationPage.About }, navigated);

            vm.Dispose();
            Assert.Null(vm.OnPageNavigated);

            vm.CurrentPage = NavigationPage.Settings;
            Assert.Single(navigated);
        }
    }
}
