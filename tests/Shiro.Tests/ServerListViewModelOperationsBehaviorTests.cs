#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class ServerListViewModelOperationsBehaviorTests
    {
        private class TestHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
            public TestHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_responder(request));
            }
        }

        [Fact]
        public async Task FetchVipServersAsync_CoversAllFallbackAndErrorBranches()
        {
            using var restClient = new RestClient("https://games.roblox.com");
            var acc = new Account { Username = "VipUser", SecurityToken = "valid-tok", Valid = true };

            var origFactory = Account.TestHttpClientFactory;
            try
            {
                // 1. Response with variety of VIP server fallback fields (accessCode, id, name, empty)
                string vipJson = @"{
                    ""data"": [
                        { ""accessCode"": """", ""id"": ""id-only-1"", ""name"": """", ""region"": ""EU-Central"", ""ping"": 50, ""maxPlayers"": 10, ""playing"": 2, ""ip"": ""5.6.7.8"" },
                        { ""accessCode"": """", ""id"": """", ""name"": ""CustomVIPName"", ""region"": """", ""ping"": null, ""maxPlayers"": 8, ""playing"": 1, ""ip"": null },
                        { ""accessCode"": """", ""id"": """", ""name"": """", ""region"": null, ""ping"": null, ""maxPlayers"": 5, ""playing"": 0, ""ip"": null },
                        { ""accessCode"": ""code-xyz"", ""id"": """", ""name"": """", ""region"": ""US-East"", ""ping"": 20, ""maxPlayers"": 12, ""playing"": 6, ""ip"": ""1.2.3.4"" }
                    ]
                }";

                Account.TestHttpClientFactory = _ => new HttpClient(new TestHttpHandler(req =>
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(vipJson)
                    };
                }));

                var list = await ServerListViewModel.TestFetchVipServersAsync(restClient, acc, 12345, CancellationToken.None);
                Assert.NotNull(list);
                Assert.Equal(4, list.Count);
                Assert.Equal("id-only-1", list[0].JobId);
                Assert.Equal("EU-Central", list[0].Region);
                Assert.Equal(50, list[0].Ping);

                Assert.Equal("CustomVIPName", list[1].JobId);
                Assert.Equal("VIP Private", list[1].Region);
                Assert.Equal(-1, list[1].Ping);

                Assert.Equal("VIP Server", list[2].JobId);
                Assert.Equal("VIP Private", list[2].Region);

                Assert.Equal("VIP:code-xyz", list[3].JobId);
                Assert.Equal("US-East", list[3].Region);
                Assert.Equal(20, list[3].Ping);

                // 2. Empty content / unsuccessful response => returns null
                Account.TestHttpClientFactory = _ => new HttpClient(new TestHttpHandler(req =>
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
                }));

                var listEmpty = await ServerListViewModel.TestFetchVipServersAsync(restClient, acc, 12345, CancellationToken.None);
                Assert.Null(listEmpty);

                // 3. Throws exception in execution => caught and returns null
                Account.TestHttpClientFactory = _ => throw new InvalidOperationException("Simulated Rest failure");
                var listException = await ServerListViewModel.TestFetchVipServersAsync(restClient, acc, 12345, CancellationToken.None);
                Assert.Null(listException);
            }
            finally
            {
                Account.TestHttpClientFactory = origFactory;
            }
        }

        [Fact]
        public async Task LoadRegionCoreAsync_And_LoadAllRegions_CoverRemainingBranches()
        {
            var mainVm = new MainViewModel();
            var vm = new ServerListViewModel(mainVm);
            vm.PlaceIdInput = "123456";

            var server = new ServerItemViewModel
            {
                JobId = "test-job-fail",
                Type = "Public",
                RawData = new ServerData { id = "test-job-fail" }
            };

            var acc = new Account { Username = "TestUser", SecurityToken = "tok", Valid = true };
            mainVm.Accounts.Add(new AccountViewModel(acc));
            mainVm.Accounts[0].IsSelected = true;

            var queryMock = Substitute.For<IRobloxServerQueryService>();
            using var _ = AppServices.Override<IRobloxServerQueryService>(queryMock);

            try
            {
                // 1. Result.Success = false with custom Region
                queryMock.QueryServerRegionAndPingAsync(Arg.Any<Account>(), Arg.Any<long>(), "test-job-fail", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new ServerRegionPingResult
                    {
                        Success = false,
                        Region = "CustomErrorRegion"
                    }));

                await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(server);
                Assert.Equal("CustomErrorRegion", server.Region);

#pragma warning disable CS8625
                // 2. Result.Success = false with empty Region (falls back to UnknownRegion)
                queryMock.QueryServerRegionAndPingAsync(Arg.Any<Account>(), Arg.Any<long>(), "test-job-fail", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new ServerRegionPingResult
                    {
                        Success = false,
                        Region = null
                    }));

                await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(server);
                Assert.Equal(LocalizationService.Get("ServerQuery.UnknownRegion"), server.Region);

                // 3. Result.Success = true with all null properties
                queryMock.QueryServerRegionAndPingAsync(Arg.Any<Account>(), Arg.Any<long>(), "test-job-fail", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new ServerRegionPingResult
                    {
                        Success = true,
                        Ip = null,
                        PingMs = null,
                        CountryCode = null,
                        FlagEmoji = null,
                        Region = null
                    }));

                await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(server);
                Assert.Equal(string.Empty, server.Ip);
                Assert.Equal(string.Empty, server.CountryCode);
                Assert.Equal(string.Empty, server.FlagEmoji);
                Assert.Equal(string.Empty, server.Region);
#pragma warning restore CS8625

                // 4. Exception thrown in query service (caught by general catch)
                queryMock.QueryServerRegionAndPingAsync(Arg.Any<Account>(), Arg.Any<long>(), "test-job-fail", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns<Task<ServerRegionPingResult>>(_ => throw new HttpRequestException("Server region lookup failed"));

                await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(server);
                Assert.Equal(LocalizationService.Get("MissingAssets.PriceLoadError"), server.Region);

                // 5. LoadRegion with PingAsc / PingDesc sort mode to trigger ApplyServerSortAndFilter
                vm.SortMode = ServerSortMode.PingAsc;
                queryMock.QueryServerRegionAndPingAsync(Arg.Any<Account>(), Arg.Any<long>(), "test-job-fail", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new ServerRegionPingResult
                    {
                        Success = true,
                        Ip = "10.0.0.1",
                        PingMs = 15,
                        CountryCode = "US",
                        FlagEmoji = "🇺🇸",
                        Region = "Virginia"
                    }));

                await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(server);
                Assert.Equal(15, server.Ping);

                // 6. LoadRegion with null server / null RawData
                await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(null);
                await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(new ServerItemViewModel());

                // 7. LoadRegion with placeId <= 0
                vm.PlaceIdInput = "0";
                mainVm.PlaceId = "0";
                await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(server);

                // 8. LoadRegion when acc == null
                mainVm.Accounts.Clear();
                AppServices.SelectionState.SelectedAccount = null;
                AppServices.SelectionState.LastValidAccount = null;
                await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(server);
                Assert.Equal(LocalizationService.Get("ServerQuery.FetchingRegion"), server.Region);

                // 9. LoadAllRegionsAsync with 0 pending servers
                vm.SetRawServers(new[]
                {
                    new ServerItemViewModel { JobId = "s1", Region = "AlreadyLoaded", Ip = "1.1.1.1" }
                });
                await ((AsyncRelayCommand)vm.LoadAllRegionsCommand).ExecuteAsync(null);
                Assert.Equal(LocalizationService.Get("ServerList.RegionsLoadedComplete"), vm.LoadingStatus);

                // 10. LoadAllRegionsAsync with cancellation
                vm.SetRawServers(new[]
                {
                    new ServerItemViewModel { JobId = "s2", Region = "", Ip = "", RawData = new ServerData { id = "s2" } }
                });
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                queryMock.QueryServerRegionAndPingAsync(Arg.Any<Account>(), Arg.Any<long>(), "s2", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns<Task<ServerRegionPingResult>>(_ => throw new OperationCanceledException(cts.Token));

                // Re-add account so acc != null
                mainVm.Accounts.Add(new AccountViewModel(acc));
                mainVm.Accounts[0].IsSelected = true;
                vm.PlaceIdInput = "123456";
                vm.SortMode = ServerSortMode.PingDesc;

                await ((AsyncRelayCommand)vm.LoadAllRegionsCommand).ExecuteAsync(null);
            }
            finally
            {
            }
        }

        [Fact]
        public async Task ResolveAuthAccount_ChecksSelectionState_Fallbacks()
        {
            var mainVm = new MainViewModel();
            var vm = new ServerListViewModel(mainVm);

            mainVm.Accounts.Clear();

            // 1. AppServices.SelectionState.SelectedAccount valid
            var acc1 = new Account { Username = "SelUser", Valid = true, SecurityToken = "tok" };
            AppServices.SelectionState.SelectedAccount = acc1;
            AppServices.SelectionState.LastValidAccount = null;

            var s = new ServerItemViewModel { JobId = "j1", RawData = new ServerData { id = "j1" } };
            await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(s);
            Assert.Equal("j1", s.JobId);
            Assert.False(s.IsBusyPinging);

            // 2. AppServices.SelectionState.LastValidAccount valid
            AppServices.SelectionState.SelectedAccount = null;
            AppServices.SelectionState.LastValidAccount = acc1;
            await ((AsyncRelayCommand)vm.LoadRegionCommand).ExecuteAsync(s);
            Assert.Equal("j1", s.JobId);
            Assert.False(s.IsBusyPinging);

            // Reset
            AppServices.SelectionState.SelectedAccount = null;
            AppServices.SelectionState.LastValidAccount = null;
        }

        [Fact]
        public void SelectServerJobId_HandlesVariousParamTypes()
        {
            var mainVm = new MainViewModel();
            var vm = new ServerListViewModel(mainVm);
            vm.PlaceIdInput = "998877";

            // 1. Invalid param types do not set JobId
            vm.SelectServerJobIdCommand.Execute(null);
            Assert.Equal(string.Empty, mainVm.JobId);

            vm.SelectServerJobIdCommand.Execute(12345);
            Assert.Equal(string.Empty, mainVm.JobId);

            // 2. ServerItemViewModel with standard public server
            var pubServer = new ServerItemViewModel { JobId = "job-std-1", Type = "Public", RawData = new ServerData { id = "job-std-1" } };
            vm.SelectServerJobIdCommand.Execute(pubServer);
            Assert.Equal("job-std-1", mainVm.JobId);
            Assert.Equal("998877", mainVm.PlaceId);
            Assert.Equal(LocalizationService.Format("ServerList.JobIdSelectedAndCopied", "job-std-1"), vm.LoadingStatus);

            // 3. ServerItemViewModel with VIP server and accessCode
            var vipServer = new ServerItemViewModel { JobId = "raw-id", Type = "VIP", RawData = new ServerData { accessCode = "secret-access" } };
            vm.SelectServerJobIdCommand.Execute(vipServer);
            Assert.Equal("VIP:secret-access", mainVm.JobId);
            Assert.Equal(LocalizationService.Format("ServerList.JobIdSelectedAndCopied", "VIP:secret-access"), vm.LoadingStatus);
        }
    }
}
