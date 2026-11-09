#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class ServerListPlayerSearchBehaviorTests
    {

        private static async Task WaitForSearchCompletion(ServerListViewModel vm, int maxWaitMs = 3000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (vm.IsSearchingPlayer && sw.ElapsedMilliseconds < maxWaitMs)
            {
                await Task.Delay(25);
            }
        }

        [Fact]
        public async Task SearchPlayer_UserNotFound_SetsNotFoundStatus()
        {
            var mockRepo = Substitute.For<IAccountRepository>();
            mockRepo.LookupUserIdAsync("MissingUser", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult((false, -1L, (RestResponse?)null)));
            using var _repoScope = AppServices.Override<IAccountRepository>(mockRepo);

            var mainVm = new MainViewModel();
            var vm = mainVm.ServerListViewModel;
            vm.SearchPlayerUsername = "MissingUser";
            vm.PlaceIdInput = "1818";

            vm.SearchPlayerCommand.Execute(null);
            await WaitForSearchCompletion(vm);

            Assert.False(vm.IsSearchingPlayer);
            Assert.Equal(LocalizationService.Format("ServerList.PlayerUserIdNotFound", "MissingUser"), vm.PlayerSearchStatus);
        }

        [Fact]
        public async Task SearchPlayer_ThumbnailApiFailure_SetsCannotGetAvatarStatus()
        {
            var mockRepo = Substitute.For<IAccountRepository>();
            mockRepo.LookupUserIdAsync("AvatarFailUser", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult((true, 55555L, (RestResponse?)null)));
            using var _repoScope = AppServices.Override<IAccountRepository>(mockRepo);

            var mockApi = Substitute.For<IRobloxApiClient>();
            var thumbHandler = new FakeHttpHandler().Enqueue(HttpStatusCode.InternalServerError, "Error");
            var thumbClient = new RestClient(new HttpClient(thumbHandler), "https://thumbnails.roblox.com/");
            mockApi.Thumbnails.Returns(thumbClient);
            using var _apiScope = AppServices.Override<IRobloxApiClient>(mockApi);

            var mainVm = new MainViewModel();
            var vm = mainVm.ServerListViewModel;
            vm.SearchPlayerUsername = "AvatarFailUser";
            vm.PlaceIdInput = "1818";

            vm.SearchPlayerCommand.Execute(null);
            await WaitForSearchCompletion(vm);

            Assert.False(vm.IsSearchingPlayer);
            Assert.Equal(LocalizationService.Get("ServerList.CannotGetPlayerAvatar"), vm.PlayerSearchStatus);
        }

        [Fact]
        public async Task SearchPlayer_PlayerFoundInServer_SelectsServerAndSetsStatus()
        {
            var mockRepo = Substitute.For<IAccountRepository>();
            mockRepo.LookupUserIdAsync("OnlinePlayer", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult((true, 77777L, (RestResponse?)null)));
            using var _repoScope = AppServices.Override<IAccountRepository>(mockRepo);

            var mockApi = Substitute.For<IRobloxApiClient>();

            // 1. Headshot thumbnail
            string headshotJson = "{\"data\":[{\"targetId\":77777,\"imageUrl\":\"https://rbx.cdn/headshot77777.png\"}]}";
            // 3. Batch thumbnail hit
            string batchJson = "{\"data\":[{\"requestId\":\"0:target_token_123:AvatarHeadshot:48x48:png:regular\",\"imageUrl\":\"https://rbx.cdn/headshot77777.png\"}]}";

            var thumbHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, headshotJson)
                .Enqueue(HttpStatusCode.OK, batchJson);
            var thumbClient = new RestClient(new HttpClient(thumbHandler), "https://thumbnails.roblox.com/");
            mockApi.Thumbnails.Returns(thumbClient);

            // 2. Games servers
            string gamesJson = "{\"data\":[{\"id\":\"job-found-777\",\"maxPlayers\":20,\"playing\":3,\"playerTokens\":[\"target_token_123\"]}],\"nextPageCursor\":\"\"}";
            var gamesHandler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, gamesJson);
            var gamesClient = new RestClient(new HttpClient(gamesHandler), "https://games.roblox.com/");
            mockApi.Games.Returns(gamesClient);

            using var _apiScope = AppServices.Override<IRobloxApiClient>(mockApi);

            var mainVm = new MainViewModel();
            var vm = mainVm.ServerListViewModel;
            vm.SearchPlayerUsername = "OnlinePlayer";
            vm.PlaceIdInput = "1818";

            vm.SearchPlayerCommand.Execute(null);
            await WaitForSearchCompletion(vm);

            Assert.False(vm.IsSearchingPlayer);
            Assert.Equal(LocalizationService.Format("ServerList.PlayerFoundInServer", "OnlinePlayer", "job-found-777"), vm.PlayerSearchStatus);
            Assert.NotNull(vm.SelectedServer);
            Assert.Equal("job-found-777", vm.SelectedServer.JobId);
        }

        [Fact]
        public async Task SearchPlayer_PlayerNotFoundInServers_SetsPlayerNotFoundStatus()
        {
            var mockRepo = Substitute.For<IAccountRepository>();
            mockRepo.LookupUserIdAsync("OnlinePlayerNotInGame", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult((true, 66666L, (RestResponse?)null)));
            using var _repoScope = AppServices.Override<IAccountRepository>(mockRepo);

            var mockApi = Substitute.For<IRobloxApiClient>();

            string headshotJson = "{\"data\":[{\"targetId\":66666,\"imageUrl\":\"https://rbx.cdn/target_headshot.png\"}]}";
            string batchJson = "{\"data\":[{\"requestId\":\"0:other_token_456:AvatarHeadshot:48x48:png:regular\",\"imageUrl\":\"https://rbx.cdn/other_avatar.png\"}]}";

            var thumbHandler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, headshotJson)
                .Enqueue(HttpStatusCode.OK, batchJson);
            var thumbClient = new RestClient(new HttpClient(thumbHandler), "https://thumbnails.roblox.com/");
            mockApi.Thumbnails.Returns(thumbClient);

            string gamesJson = "{\"data\":[{\"id\":\"job-other-888\",\"maxPlayers\":20,\"playing\":1,\"playerTokens\":[\"other_token_456\"]}],\"nextPageCursor\":\"\"}";
            var gamesHandler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, gamesJson);
            var gamesClient = new RestClient(new HttpClient(gamesHandler), "https://games.roblox.com/");
            mockApi.Games.Returns(gamesClient);

            using var _apiScope = AppServices.Override<IRobloxApiClient>(mockApi);

            var mainVm = new MainViewModel();
            var vm = mainVm.ServerListViewModel;
            vm.SearchPlayerUsername = "OnlinePlayerNotInGame";
            vm.PlaceIdInput = "1818";

            vm.SearchPlayerCommand.Execute(null);
            await WaitForSearchCompletion(vm);

            Assert.False(vm.IsSearchingPlayer);
            Assert.Equal(LocalizationService.Format("ServerList.PlayerNotFound", "OnlinePlayerNotInGame"), vm.PlayerSearchStatus);
        }
    }
}
