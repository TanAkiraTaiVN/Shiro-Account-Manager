#nullable enable
using System;
using System.IO;
using Shiro.Classes;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class ServerListViewModelBehaviorTests : IDisposable
    {
        private readonly string _testDir;

        public ServerListViewModelBehaviorTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "Shiro-ServerListVMTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDir))
            {
                try { Directory.Delete(_testDir, true); }
                catch (IOException) { }
            }
        }

        [Fact]
        public void PlayerSearch_InputValidation_SetsAppropriateStatus()
        {
            var mainVm = new MainViewModel();
            var vm = mainVm.ServerListViewModel;

            // 1. Empty username
            vm.SearchPlayerUsername = "";
            vm.SearchPlayerCommand.Execute(null);
            Assert.Contains(vm.PlayerSearchStatus, new[] { "Vui lòng nhập tên người chơi", "Please enter a username", LocalizationService.Get("ServerList.EnterPlayerUsername") });

            // 2. Non-empty username but empty PlaceId
            vm.SearchPlayerUsername = "TestPlayer";
            vm.PlaceIdInput = "";
            vm.SearchPlayerCommand.Execute(null);
            Assert.Contains(vm.PlayerSearchStatus, new[] { "Vui lòng nhập PlaceId", "Please enter a Place ID", LocalizationService.Get("ServerList.EnterPlaceIdFirst") });
        }

        [Fact]
        public void Favorites_AddRemoveAndSelect_WorkInIsolatedStorage()
        {
            using (StoragePaths.OverrideDataDirectoryForTesting(_testDir))
            {
                var mainVm = new MainViewModel();
                var vm = mainVm.ServerListViewModel;

                Assert.True(vm.IsFavoritesEmpty);
                Assert.Empty(vm.Favorites);

                // Add a favorite
                vm.PlaceIdInput = "18973";
                vm.AddFavoriteCommand.Execute(null);

                Assert.False(vm.IsFavoritesEmpty);
                Assert.Single(vm.Favorites);
                Assert.Equal(18973, vm.Favorites[0].PlaceId);

                // Adding duplicate does not add second entry
                vm.AddFavoriteCommand.Execute(null);
                Assert.Single(vm.Favorites);

                // Select favorite updates place input
                var fav = vm.Favorites[0];
                vm.PlaceIdInput = "999";
                vm.SelectFavoriteCommand.Execute(fav);
                Assert.Equal("18973", vm.PlaceIdInput);
                Assert.Equal("18973", mainVm.PlaceId);

                // Remove favorite
                vm.RemoveFavoriteCommand.Execute(fav);
                Assert.True(vm.IsFavoritesEmpty);
                Assert.Empty(vm.Favorites);
            }
        }

        [Fact]
        public void SyncPlaceId_And_ClearRawServers_UpdateCollectionProperties()
        {
            var mainVm = new MainViewModel();
            var vm = mainVm.ServerListViewModel;

            vm.SyncPlaceId("55555");
            Assert.Equal("55555", vm.PlaceIdInput);

            var s1 = new ServerItemViewModel { JobId = "job-1", Type = "Public", Playing = 5 };
            vm.SetRawServers(new[] { s1 });
            Assert.Equal(1, vm.RawServersCount);
            Assert.True(vm.HasServers);
            Assert.False(vm.IsServersEmpty);

            vm.ClearRawServers();
            Assert.Equal(0, vm.RawServersCount);
            Assert.False(vm.HasServers);
            Assert.True(vm.IsServersEmpty);
        }

        [Fact]
        public void GameSearchTerm_And_IsSearchingGames_NotifyChanges()
        {
            var vm = new ServerListViewModel();

            vm.GameSearchTerm = "Blox";
            Assert.Equal("Blox", vm.GameSearchTerm);

            Assert.False(vm.IsSearchingGames);
            Assert.True(vm.IsSearchGamesEmpty);

            vm.IsSearchingGames = true;
            Assert.True(vm.IsSearchingGames);
            Assert.False(vm.IsSearchGamesEmpty);
        }

        [Fact]
        public void SelectGame_And_LaunchGame_WithGameSearchItem_UpdatesViewModelState()
        {
            using (StoragePaths.OverrideDataDirectoryForTesting(_testDir))
            {
                var mainVm = new MainViewModel();
                var vm = mainVm.ServerListViewModel;

                var gameItem = new GameSearchItemViewModel
                {
                    PlaceId = 987654321,
                    Name = "Awesome Tycoon",
                    PlayerCount = 1200,
                    ThumbnailUrl = "https://tr.rbxcdn.com/test-thumb"
                };

                // SelectGame
                vm.SelectGameCommand.Execute(gameItem);
                Assert.Equal("987654321", vm.PlaceIdInput);
                Assert.Equal("987654321", mainVm.PlaceId);
                Assert.Equal(0, vm.SelectedSubTabIndex);

                // LaunchGame with no selected accounts sets warning status
                vm.LaunchGameCommand.Execute(gameItem);
                Assert.Equal(LocalizationService.Get("ServerList.SelectAccountBeforePlay"), vm.LoadingStatus);

                // AddFavorite with GameSearchItemViewModel
                vm.AddFavoriteCommand.Execute(gameItem);
                Assert.Single(vm.Favorites);
                Assert.Equal(987654321, vm.Favorites[0].PlaceId);
                Assert.Equal("Awesome Tycoon", vm.Favorites[0].Name);

                // Adding same game again sets GameAlreadyFavorite warning
                vm.AddFavoriteCommand.Execute(gameItem);
                Assert.Single(vm.Favorites);
                Assert.Equal(LocalizationService.Get("ServerList.GameAlreadyFavorite"), vm.LoadingStatus);
            }
        }

        [Fact]
        public void AddFavorite_InvalidOrEmptyPlaceId_DoesNotAdd()
        {
            using (StoragePaths.OverrideDataDirectoryForTesting(_testDir))
            {
                var vm = new ServerListViewModel();
                int initialCount = vm.Favorites.Count;

                vm.PlaceIdInput = "";
                vm.AddFavoriteCommand.Execute(null);
                Assert.Equal(initialCount, vm.Favorites.Count);

                vm.PlaceIdInput = "not-a-number";
                vm.AddFavoriteCommand.Execute(null);
                Assert.Equal(initialCount, vm.Favorites.Count);
            }
        }

        [Fact]
        public void SelectServerJobId_And_CopyJobId_HandlesPublicAndVipServers()
        {
            var mainVm = new MainViewModel();
            var vm = mainVm.ServerListViewModel;
            vm.PlaceIdInput = "1818";

            // 1. Public server
            var publicServer = new ServerItemViewModel
            {
                JobId = "public-job-id-12345",
                Type = "Public"
            };

            vm.SelectServerJobIdCommand.Execute(publicServer);
            Assert.Equal("public-job-id-12345", mainVm.JobId);
            Assert.Contains("public-job-id-12345", vm.LoadingStatus);

            vm.CopyJobIdCommand.Execute(publicServer);
            Assert.Contains("public-job-id-12345", vm.LoadingStatus);

            // 2. VIP server with accessCode
            var vipServer = new ServerItemViewModel
            {
                JobId = "vip-job-id",
                Type = "VIP",
                RawData = new ServerData { accessCode = "vip-secret-code-99" }
            };

            vm.SelectServerJobIdCommand.Execute(vipServer);
            Assert.Equal("VIP:vip-secret-code-99", mainVm.JobId);
            Assert.Contains("VIP:vip-secret-code-99", vm.LoadingStatus);

            vm.CopyJobIdCommand.Execute(vipServer);
            Assert.Contains("VIP:vip-secret-code-99", vm.LoadingStatus);
        }

        [Fact]
        public async Task JoinServerCommand_Validation_ChecksPlaceIdAndSelectedAccounts()
        {
            var mainVm = new MainViewModel();
            var vm = mainVm.ServerListViewModel;

            var server = new ServerItemViewModel { JobId = "job-join-test", Type = "Public" };

            // 1. Invalid PlaceId
            vm.PlaceIdInput = "";
            mainVm.PlaceId = "";
            vm.JoinServerCommand.Execute(server);
            Assert.Equal(LocalizationService.Get("ServerList.InvalidPlaceId"), vm.LoadingStatus);

            // 2. Valid PlaceId but No Selected Accounts
            vm.PlaceIdInput = "1818";
            mainVm.PlaceId = "1818";
            vm.JoinServerCommand.Execute(server);
            Assert.Equal(LocalizationService.Get("ServerList.SelectAccountBeforeJoinServer"), vm.LoadingStatus);
        }

        [Fact]
        public async Task LoadAllRegionsCommand_WhenNoServers_ReturnsEarly()
        {
            var vm = new ServerListViewModel();
            vm.ClearRawServers();

            var ex = Record.Exception(() => vm.LoadAllRegionsCommand.Execute(null));
            Assert.Null(ex);
        }
    }
}
