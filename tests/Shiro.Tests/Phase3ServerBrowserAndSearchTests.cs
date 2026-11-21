#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
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
    public class Phase3ServerBrowserAndSearchTests
    {
        [Fact]
        public async Task LoadRegionCoreAsync_UsesProperServerQueryLocalizationKeys_NotMissingAssetsKey()
        {
            var prevSelected = AppServices.SelectionState.SelectedAccount;
            var prevLastValid = AppServices.SelectionState.LastValidAccount;
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            AppDispatcher.CustomDispatcher = static action => action();

            try
            {
                var queryMock = Substitute.For<IRobloxServerQueryService>();
                queryMock.QueryServerRegionAndPingAsync(
                        Arg.Any<Account>(),
                        Arg.Any<long>(),
                        Arg.Any<string>(),
                        Arg.Any<string?>(),
                        Arg.Any<CancellationToken>())
                    .Returns<Task<ServerRegionPingResult>>(_ => throw new HttpRequestException("Simulated region failure"));
                using var _queryScope = AppServices.Override<IRobloxServerQueryService>(queryMock);

                using var mainVm = new MainViewModel();
                using var vm = new ServerListViewModel(mainVm) { PlaceIdInput = "1818" };
                var server = new ServerItemViewModel
                {
                    JobId = "srv-loc-1",
                    Type = "Public",
                    RawData = new ServerData { id = "srv-loc-1" }
                };

                // 1. Missing account -> ServerQuery.LoginRequired (verified in both English and Vietnamese)
                mainVm.Accounts.Clear();
                AppServices.SelectionState.SelectedAccount = null;
                AppServices.SelectionState.LastValidAccount = null;

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(server);
                string enLoginRequired = LocalizationService.Get("ServerQuery.LoginRequired");
                Assert.Equal("Login required", enLoginRequired);
                Assert.Equal(enLoginRequired, server.Region);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(server);
                string viLoginRequired = LocalizationService.Get("ServerQuery.LoginRequired");
                Assert.Equal("Cần đăng nhập", viLoginRequired);
                Assert.Equal(viLoginRequired, server.Region);

                // 2. Exception -> ServerQuery.RegionLoadError (never MissingAssets.PriceLoadError)
                var acc = new Account { Username = "RegionTester", SecurityToken = "tok", Valid = true };
                AppServices.SelectionState.SelectedAccount = acc;

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(server);
                string enRegionError = LocalizationService.Get("ServerQuery.RegionLoadError");
                Assert.Equal("Load error", enRegionError);
                Assert.Equal(enRegionError, server.Region);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(server);
                string viRegionError = LocalizationService.Get("ServerQuery.RegionLoadError");
                Assert.Equal("Lỗi tải khu vực", viRegionError);
                Assert.Equal(viRegionError, server.Region);
                Assert.NotEqual(LocalizationService.Get("MissingAssets.PriceLoadError"), server.Region);
            }
            finally
            {
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                AppServices.SelectionState.SelectedAccount = prevSelected;
                AppServices.SelectionState.LastValidAccount = prevLastValid;
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public async Task SearchPlayerAsync_EnforcesMaxPlayerSearchPagesCap_AndStopsScanning()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            AppDispatcher.CustomDispatcher = static action => action();

            try
            {
                Assert.Equal(15, ServerBrowserTabViewModel.MaxPlayerSearchPages);

                var mockRepo = Substitute.For<IAccountRepository>();
                mockRepo.LookupUserIdAsync("TargetPlayer", Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult((true, 99999L, (RestResponse?)null)));
                using var _repoScope = AppServices.Override<IAccountRepository>(mockRepo);

                int publicServerRequests = 0;
                const int TotalMockPages = 25;
                const int ServersPerPage = 10;

                var gamesHandler = new FakeHttpHandler(_ =>
                {
                    int page = Interlocked.Increment(ref publicServerRequests);
                    var sb = new StringBuilder();
                    sb.Append("{\"data\":[");
                    for (int i = 0; i < ServersPerPage; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"id\":\"job-p").Append(page).Append('-').Append(i)
                          .Append("\",\"maxPlayers\":12,\"playing\":1,\"playerTokens\":[\"tok_p")
                          .Append(page).Append('_').Append(i).Append("\"]}");
                    }
                    string nextCursor = page < TotalMockPages ? $"page_{page + 1}" : string.Empty;
                    sb.Append("],\"nextPageCursor\":\"").Append(nextCursor).Append("\"}");

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json")
                    };
                });

                var thumbHandler = new FakeHttpHandler(req =>
                {
                    string uri = req.RequestUri?.ToString() ?? string.Empty;
                    if (uri.Contains("avatar-headshot", StringComparison.OrdinalIgnoreCase))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"data\":[{\"targetId\":99999,\"imageUrl\":\"https://rbx.cdn/target99999.png\"}]}", Encoding.UTF8, "application/json")
                        };
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":[{\"requestId\":\"none\",\"imageUrl\":\"https://rbx.cdn/other.png\"}]}", Encoding.UTF8, "application/json")
                    };
                });

                using var gamesClient = new RestClient(new HttpClient(gamesHandler), "https://games.roblox.com/");
                using var thumbClient = new RestClient(new HttpClient(thumbHandler), "https://thumbnails.roblox.com/");
                var mockApi = Substitute.For<IRobloxApiClient>();
                mockApi.Games.Returns(gamesClient);
                mockApi.Thumbnails.Returns(thumbClient);
                using var _apiScope = AppServices.Override<IRobloxApiClient>(mockApi);

                using var mainVm = new MainViewModel();
                var vm = mainVm.ServerListViewModel;
                vm.SearchPlayerUsername = "TargetPlayer";
                vm.PlaceIdInput = "1818";

                await ((AsyncRelayCommand)vm.SearchPlayerCommand).ExecuteAsync(null);

                Assert.Equal(ServerBrowserTabViewModel.MaxPlayerSearchPages, publicServerRequests);
                Assert.False(vm.IsSearchingPlayer);

                int expectedScannedServers = ServerBrowserTabViewModel.MaxPlayerSearchPages * ServersPerPage;
                string expectedStatus = LocalizationService.Format("ServerList.PlayerSearchPageLimitReached", "TargetPlayer", expectedScannedServers);
                Assert.NotEqual("ServerList.PlayerSearchPageLimitReached", expectedStatus);
                Assert.Equal(expectedStatus, vm.PlayerSearchStatus);
            }
            finally
            {
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public async Task SearchPlayerAsync_ThrottlesUiStatusPostsPerBatch()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            int totalDispatcherPosts = 0;
            int scanStatusPosts = 0;

            try
            {
                Assert.Equal(100, ServerBrowserTabViewModel.PlayerSearchStatusThrottleMs);

                var mockRepo = Substitute.For<IAccountRepository>();
                mockRepo.LookupUserIdAsync("ThrottleUser", Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult((true, 88888L, (RestResponse?)null)));
                using var _repoScope = AppServices.Override<IAccountRepository>(mockRepo);

                var sb = new StringBuilder();
                sb.Append("{\"data\":[");
                for (int i = 0; i < 100; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"id\":\"job-throttle-").Append(i)
                      .Append("\",\"maxPlayers\":10,\"playing\":1,\"playerTokens\":[\"tok_")
                      .Append(i).Append("\"]}");
                }
                sb.Append("],\"nextPageCursor\":\"\"}");

                var gamesHandler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, sb.ToString());
                var thumbHandler = new FakeHttpHandler(req =>
                {
                    string uri = req.RequestUri?.ToString() ?? string.Empty;
                    if (uri.Contains("avatar-headshot", StringComparison.OrdinalIgnoreCase))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"data\":[{\"targetId\":88888,\"imageUrl\":\"https://rbx.cdn/target88888.png\"}]}", Encoding.UTF8, "application/json")
                        };
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
                    };
                });

                using var gamesClient = new RestClient(new HttpClient(gamesHandler), "https://games.roblox.com/");
                using var thumbClient = new RestClient(new HttpClient(thumbHandler), "https://thumbnails.roblox.com/");
                var mockApi = Substitute.For<IRobloxApiClient>();
                mockApi.Games.Returns(gamesClient);
                mockApi.Thumbnails.Returns(thumbClient);
                using var _apiScope = AppServices.Override<IRobloxApiClient>(mockApi);

                using var mainVm = new MainViewModel();
                var vm = mainVm.ServerListViewModel;
                vm.SearchPlayerUsername = "ThrottleUser";
                vm.PlaceIdInput = "1818";

                string notFoundText = LocalizationService.Format("ServerList.PlayerNotFound", "ThrottleUser");
                AppDispatcher.CustomDispatcher = action =>
                {
                    Interlocked.Increment(ref totalDispatcherPosts);
                    string before = vm.PlayerSearchStatus;
                    action();
                    if (!string.Equals(vm.PlayerSearchStatus, before, StringComparison.Ordinal) &&
                        !string.Equals(vm.PlayerSearchStatus, notFoundText, StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref scanStatusPosts);
                    }
                };

                await ((AsyncRelayCommand)vm.SearchPlayerCommand).ExecuteAsync(null);

                Assert.False(vm.IsSearchingPlayer);
                Assert.Equal(notFoundText, vm.PlayerSearchStatus);
                Assert.InRange(scanStatusPosts, 1, 5);
                Assert.InRange(totalDispatcherPosts, 2, 5);
            }
            finally
            {
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }
    }
}
