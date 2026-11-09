#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
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

        [Fact]
        public async Task SearchGamesAsync_WithSharedHttpClient_PopulatesSearchGamesAndThumbnails()
        {
            var handler = new Shiro.Tests.TestInfrastructure.FakeHttpHandler(req =>
            {
                string url = req.RequestUri?.AbsoluteUri ?? "";
                if (url.Contains("omni-search"))
                {
                    string json = "{\"searchResults\":[{\"contents\":[{\"rootPlaceId\":189707,\"name\":\"Natural Disaster Survival\",\"playerCount\":5000},{\"placeId\":554433,\"name\":\"Tycoon\",\"playerCount\":200}]}]}";
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json) };
                }
                if (url.Contains("gameicons"))
                {
                    string json = "{\"data\":[{\"targetId\":189707,\"imageUrl\":\"https://icon.rbx/189707.png\"},{\"targetId\":554433,\"imageUrl\":\"https://icon.rbx/554433.png\"}]}";
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json) };
                }
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("{}") };
            });

            SharedHttpClient.SetTestClient("default", new System.Net.Http.HttpClient(handler));
            try
            {
                var vm = new ServerListViewModel();
                vm.GameSearchTerm = "Disaster";

                vm.SearchGamesCommand.Execute(null);

                // Give background Task.Run a brief moment
                for (int i = 0; i < 20 && vm.IsSearchingGames; i++)
                {
                    await Task.Delay(50);
                }

                Assert.False(vm.IsSearchingGames);
                Assert.True(vm.SearchGames.Count >= 2);
                Assert.Equal(189707, vm.SearchGames[0].PlaceId);
                Assert.Equal("Natural Disaster Survival", vm.SearchGames[0].Name);
                Assert.Equal("https://icon.rbx/189707.png", vm.SearchGames[0].ThumbnailUrl);
            }
            finally
            {
                SharedHttpClient.ResetTestClient("default");
            }
        }

        [Fact]
        public async Task Favorites_LoadSaveSelectAndLaunch_CoversBranches()
        {
            using (StoragePaths.OverrideDataDirectoryForTesting(_testDir))
            {
                string favPath = StoragePaths.GetUserFile("FavoriteGames.json");
                string initialJson = "[{\"Name\":\"Disaster\",\"PlaceID\":189707,\"PrivateServer\":\"vip-link-1\"},{\"Name\":\"\",\"PlaceID\":554433,\"PrivateServer\":\"\"}]";
                File.WriteAllText(favPath, initialJson);

                var mainVm = new MainViewModel();
                var vm = mainVm.ServerListViewModel;

                Assert.Equal(2, vm.Favorites.Count);
                Assert.Equal("Disaster", vm.Favorites[0].Name);
                Assert.Equal("Place 554433", vm.Favorites[1].Name);

                // LaunchFavoriteCommand without selected accounts
                vm.LaunchFavoriteCommand.Execute(vm.Favorites[0]);
                Assert.Equal(LocalizationService.Get("ServerList.SelectAccountBeforePlay"), vm.LoadingStatus);

                // LaunchFavoriteCommand with selected accounts
                var acc = new Account { Username = "FavPlayer", Valid = true };
                var accVm = new AccountViewModel(acc) { IsSelected = true };
                mainVm.Accounts.Add(accVm);
                mainVm.NotifyCounts();
                vm.LaunchFavoriteCommand.Execute(vm.Favorites[0]);
                Assert.Equal(LocalizationService.Format("ServerList.LaunchingFavoriteGame", "Disaster"), mainVm.StatusMessage);

                // SelectFavoriteCommand
                vm.SelectFavoriteCommand.Execute(vm.Favorites[0]);
                Assert.Equal("189707", vm.PlaceIdInput);
                Assert.Equal("vip-link-1", mainVm.JobId);

                // AddFavorite duplicate
                vm.AddFavoriteCommand.Execute(189707);
                Assert.Equal(LocalizationService.Get("ServerList.GameAlreadyFavorite"), vm.LoadingStatus);

                // AddFavorite new by long placeId
                vm.PlaceIdInput = "778899";
                vm.AddFavoriteCommand.Execute(null);
                Assert.Equal(3, vm.Favorites.Count);

                // RemoveFavorite
                var toRemove = vm.Favorites[0];
                vm.RemoveFavoriteCommand.Execute(toRemove);
                Assert.Equal(2, vm.Favorites.Count);
                Assert.Contains("Disaster", vm.LoadingStatus);
            }
        }

        [Fact]
        public async Task FetchVipServers_And_ServerOperations_Branches()
        {
            var handler = new Shiro.Tests.TestInfrastructure.FakeHttpHandler(req =>
            {
                string url = req.RequestUri?.AbsoluteUri ?? "";
                if (url.Contains("VIP"))
                {
                    string json = "{\"data\":[{\"id\":\"vip-guid-1\",\"accessCode\":\"code-123\",\"maxPlayers\":10,\"playing\":2,\"ping\":45,\"region\":\"US-East\",\"ip\":\"127.0.0.1\"}]}";
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json) };
                }
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new System.Net.Http.StringContent("{}") };
            });

            var prevFactory = Account.TestHttpClientFactory;
            Account.TestHttpClientFactory = _ => new System.Net.Http.HttpClient(handler);
            var restClient = new Shiro.Http.RestClient(new System.Net.Http.HttpClient(handler), "https://games.roblox.com/");
            var acc = new Account { Username = "VipUser", SecurityToken = "_|WARNING:-test" };

            // 1. Success response
            var vips = await ServerListViewModel.TestFetchVipServersAsync(restClient, acc, 189707, CancellationToken.None);
            Assert.NotNull(vips);
            Assert.Single(vips);
            Assert.Equal("VIP:code-123", vips[0].JobId);
            Assert.Equal("VIP", vips[0].Type);
            Assert.Equal(10, vips[0].MaxPlayers);
            Assert.Equal(2, vips[0].Playing);
            Assert.Equal(45, vips[0].Ping);
            Assert.Equal("US-East", vips[0].Region);
            Assert.Equal("127.0.0.1", vips[0].Ip);

            // 2. Error / canceled response
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var canceledVips = await ServerListViewModel.TestFetchVipServersAsync(restClient, acc, 189707, cts.Token);
            Assert.Null(canceledVips);

            // 3. Exception in client
            Account.TestHttpClientFactory = _ => throw new InvalidOperationException("boom");
            var throwingClient = new Shiro.Http.RestClient(new System.Net.Http.HttpClient(new Shiro.Tests.TestInfrastructure.FakeHttpHandler(_ => throw new InvalidOperationException("boom"))), "https://games.roblox.com/");
            var throwVips = await ServerListViewModel.TestFetchVipServersAsync(throwingClient, acc, 189707, CancellationToken.None);
            Assert.Null(throwVips);
            Account.TestHttpClientFactory = prevFactory;
        }

        [Fact]
        public async Task JoinServerCommand_And_LoadRegionCommands_AllBranches()
        {
            var mainVm = new MainViewModel();
            var vm = mainVm.ServerListViewModel;
            vm.PlaceIdInput = "189707";

            // Guard branches
            vm.JoinServerCommand.Execute(null);
            vm.JoinServerCommand.Execute("not-a-server-item");

            var server = new ServerItemViewModel
            {
                JobId = "job-oper-1",
                Type = "Public",
                RawData = new ServerData { id = "job-oper-1" }
            };

            // Join with selected accounts
            var acc = new Account { Username = "JoinUser1", UserID = 111, Valid = true, SecurityToken = "_|WARNING:-valid" };
            var accVm = new AccountViewModel(acc) { IsSelected = true };
            mainVm.Accounts.Add(accVm);
                mainVm.NotifyCounts();

            vm.JoinServerCommand.Execute(server);
            Assert.Equal(LocalizationService.Format("ServerList.JoiningServerWithAccounts", "job-oper-1", 1), vm.LoadingStatus);

            // LoadRegion branches
            vm.LoadRegionCommand.Execute(null);

            var queryMock = NSubstitute.Substitute.For<IRobloxServerQueryService>();
            queryMock.QueryServerRegionAndPingAsync(Arg.Any<Account>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ServerRegionPingResult
                {
                    Success = true,
                    Ip = "1.2.3.4",
                    PingMs = 35,
                    CountryCode = "US",
                    FlagEmoji = "🇺🇸",
                    Region = "North America"
                }));

            using var _ = AppServices.Override<IRobloxServerQueryService>(queryMock);
            await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(server);
            Assert.Equal("1.2.3.4", server.Ip);
            Assert.Equal(35, server.Ping);
            Assert.Equal("North America", server.Region);

            // LoadAllRegionsCommand with servers
            vm.SetRawServers(new[] { server, new ServerItemViewModel { JobId = "job-2", Type = "Public", RawData = new ServerData { id = "job-2" } } });
            await ((AsyncRelayCommand)vm.LoadAllRegionsCommand).ExecuteAsync(null);
            Assert.Equal(LocalizationService.Get("ServerList.RegionsLoadedComplete"), vm.LoadingStatus);
        }
    }
}
