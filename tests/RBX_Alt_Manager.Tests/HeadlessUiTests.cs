using System;
using System.Collections.Generic;
using System.Linq;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.ViewModels;
using Xunit;

namespace RBX_Alt_Manager.Tests
{
    public class HeadlessUiTests
    {
        [Fact]
        public void ServerListViewModel_InitializationAndProperties_BehaveCorrectly()
        {
            var mainVm = new MainViewModel();
            var vm = new ServerListViewModel(mainVm);
            Assert.NotNull(vm.Servers);
            Assert.Empty(vm.Servers);
            Assert.False(vm.IsLoadingServers);
            Assert.False(string.IsNullOrEmpty(vm.LoadingStatus));

            // Place ID validation & setting
            vm.PlaceIdInput = "189707";
            Assert.Equal("189707", vm.PlaceIdInput);
            Assert.Equal("189707", mainVm.PlaceId);
        }

        [Fact]
        public void ServerItemViewModel_CalculatesDisplayProperties()
        {
            var vm = new ServerItemViewModel
            {
                JobId = "550e8400-e29b-41d4-a716-446655440000",
                MaxPlayers = 20,
                Playing = 15,
                Ping = 65,
                Region = "Singapore"
            };

            Assert.Equal("550e8400-e29b-41d4-a716-446655440000", vm.JobId);
            Assert.Equal(15, vm.Playing);
            Assert.Equal(20, vm.MaxPlayers);
            Assert.Equal("15 / 20", vm.PlayerDisplay);
            Assert.Equal(65, vm.Ping);
            Assert.Equal("65 ms", vm.PingDisplay);
            Assert.Equal("Singapore", vm.Region);
        }

        [Fact]
        public void MainViewModel_AccountRowGroupingAndSelection_OperatesCorrectly()
        {
            var vm = new MainViewModel();
            Assert.NotNull(vm.Accounts);
            Assert.NotNull(vm.FilteredAccounts);
            Assert.NotNull(vm.FilteredAccountRows);

            var acc1 = new Account { Username = "Alpha", Group = "GroupA", RobuxBalance = 100 };
            var acc2 = new Account { Username = "Beta", Group = "GroupA", RobuxBalance = 200 };
            var acc3 = new Account { Username = "Gamma", Group = "GroupB", RobuxBalance = 300 };

            var vm1 = new AccountViewModel(acc1);
            var vm2 = new AccountViewModel(acc2);
            var vm3 = new AccountViewModel(acc3);

            vm.Accounts.Add(vm1);
            vm.Accounts.Add(vm2);
            vm.Accounts.Add(vm3);
            vm.RebuildGroupList();
            vm.ApplyFilter();

            // Filter by search text
            vm.SearchText = "Alpha";
            vm.ApplyFilter();
            Assert.Contains(vm.FilteredAccounts, a => a.Username == "Alpha");
            Assert.DoesNotContain(vm.FilteredAccounts, a => a.Username == "Beta");

            // Clear search
            vm.SearchText = string.Empty;
            vm.ApplyFilter();
            Assert.Equal(3, vm.FilteredAccounts.Count);

            // Filter by group
            vm.SelectedGroup = "GroupB";
            Assert.Single(vm.FilteredAccounts);
            Assert.Equal("Gamma", vm.FilteredAccounts[0].Username);

            vm.SelectedGroup = "All Groups";
            Assert.Equal(3, vm.FilteredAccounts.Count);
        }
    }
}
