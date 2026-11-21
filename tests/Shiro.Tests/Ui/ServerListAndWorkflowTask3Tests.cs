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

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public sealed class ServerListAndWorkflowTask3Tests
    {
        private const string RetryServerListJson = "{\"data\":[{\"id\":\"retry-job-1\",\"maxPlayers\":10,\"playing\":3}]}";
        private const string AvatarHeadshotJson = "{\"data\":[{\"imageUrl\":\"https://tr.rbxcdn.com/target.png\"}]}";
        private const string EmptyBatchAvatarJson = "{\"data\":[]}";
        private const string PlayerSearchServersJson = "{\"data\":[{\"id\":\"srv-1\",\"maxPlayers\":10,\"playing\":2,\"playerTokens\":[\"tok1\"]}]}";

        private static HttpResponseMessage CreateJsonOk(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json) };

        [Fact]
        public async Task RefreshServersAsync_OnException_ResetsLastLoadedPlaceIdSoRetryProceeds()
        {
            bool shouldFail = true;
            int retrySuccessCalls = 0;
            var handler = new FakeHttpHandler(_ =>
            {
                Interlocked.Increment(ref retrySuccessCalls);
                return CreateJsonOk(RetryServerListJson);
            });

            var fakeRest = new RestClient(new HttpClient(handler), "https://games.roblox.com/");
            var mockApi = Substitute.For<IRobloxApiClient>();
            mockApi.Games.Returns(_ =>
            {
                if (shouldFail)
                    throw new InvalidOperationException("games client init failed");
                return fakeRest;
            });
            using var apiScope = AppServices.Override<IRobloxApiClient>(mockApi);

            using var vm = new ServerListViewModel();
            vm.PlaceIdInput = "189707";
            await vm.RefreshServersAsync();
            Assert.Equal(0, vm.RawServersCount);

            shouldFail = false;
            vm.SetRawServers(new[] { new ServerItemViewModel { JobId = "stale-job", Type = "Public" } });
            vm.EnsureServersLoadedForCurrentPlaceId();

            for (int i = 0; i < 40 && Volatile.Read(ref retrySuccessCalls) == 0; i++)
            {
                await Task.Delay(25);
            }

            Assert.True(Volatile.Read(ref retrySuccessCalls) > 0);
        }

        [Fact]
        public async Task SearchPlayerAsync_DispatchesScanStatusViaAppDispatcher()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            int inDispatchDepth = 0;
            int statusUpdatedInsideDispatch = 0;
            int statusUpdatedOutsideDispatch = 0;
            int uiThreadId = Environment.CurrentManagedThreadId;
            AppDispatcher.CustomDispatcher = action =>
            {
                Interlocked.Increment(ref inDispatchDepth);
                try { action(); }
                finally { Interlocked.Decrement(ref inDispatchDepth); }
            };

            var mockRepo = Substitute.For<IAccountRepository>();
            mockRepo.LookupUserIdAsync("TargetPlayer", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<(bool, long, RestResponse?)>((true, 424242L, new RestResponse())));
            using var repoScope = AppServices.Override<IAccountRepository>(mockRepo);

            var thumbHandler = new FakeHttpHandler(async (req, _) =>
            {
                await Task.Delay(5).ConfigureAwait(false);
                string path = req.RequestUri?.AbsolutePath ?? "";
                if (path.Contains("avatar-headshot"))
                    return CreateJsonOk(AvatarHeadshotJson);
                return CreateJsonOk(EmptyBatchAvatarJson);
            });
            var gamesHandler = new FakeHttpHandler(async (_, _) =>
            {
                await Task.Delay(5).ConfigureAwait(false);
                return CreateJsonOk(PlayerSearchServersJson);
            });

            var mockApi = Substitute.For<IRobloxApiClient>();
            mockApi.Thumbnails.Returns(new RestClient(new HttpClient(thumbHandler), "https://thumbnails.roblox.com/"));
            mockApi.Games.Returns(new RestClient(new HttpClient(gamesHandler), "https://games.roblox.com/"));
            using var apiScope = AppServices.Override<IRobloxApiClient>(mockApi);

            try
            {
                using var vm = new ServerListViewModel();
                vm.PlaceIdInput = "189707";
                vm.SearchPlayerUsername = "TargetPlayer";
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ServerListViewModel.PlayerSearchStatus)
                        && Environment.CurrentManagedThreadId != uiThreadId)
                    {
                        if (Volatile.Read(ref inDispatchDepth) > 0)
                            Interlocked.Increment(ref statusUpdatedInsideDispatch);
                        else
                            Interlocked.Increment(ref statusUpdatedOutsideDispatch);
                    }
                };
                await ((AsyncRelayCommand)vm.SearchPlayerCommand).ExecuteAsync(null);
                Assert.Equal(0, Volatile.Read(ref statusUpdatedOutsideDispatch));
                Assert.True(Volatile.Read(ref statusUpdatedInsideDispatch) > 0);
            }
            finally
            {
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public async Task MainViewModel_SearchText_DebounceAndDispose_IgnoreStaleFilterWork()
        {
            var vm = new MainViewModel();
            vm.LoadAccounts(new[]
            {
                new Account { Username = "AlphaOne", Valid = true },
                new Account { Username = "BetaTwo", Valid = true }
            });

            vm.SearchText = "Alpha";
            vm.SearchText = string.Empty;
            await Task.Delay(200);
            Assert.Equal(2, vm.FilteredAccounts.Count);

            vm.SearchText = "Beta";
            vm.Dispose();
            await Task.Delay(200);
            Assert.Equal(2, vm.FilteredAccounts.Count);
        }

        [Fact]
        public async Task AccountWorkflowService_RemoveSelectedAccounts_SyncsLookupIndex()
        {
            var dlg = Substitute.For<IDialogService>();
            dlg.ShowConfirmationAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
            using var dlgScope = AppServices.Override<IDialogService>(dlg);

            var mockRepo = Substitute.For<IAccountRepository>();
            mockRepo.RemoveRange(Arg.Any<System.Collections.Generic.IEnumerable<Account>>()).Returns(1);
            using var repoScope = AppServices.Override<IAccountRepository>(mockRepo);

            using var mainVm = new MainViewModel();
            var acc = new Account { Username = "BatchRemoved", UserID = 556677, Valid = true };
            mainVm.LoadAccounts(new[] { acc });
            mainVm.Accounts[0].IsSelected = true;

            await AccountWorkflowService.Default.RemoveSelectedAccountsAsync(mainVm, _ => { });

            Assert.Empty(mainVm.Accounts);
            Assert.Null(mainVm.TryGetAccountViewModel(acc));
            Assert.Null(mainVm.TryGetAccountViewModel(new Account { UserID = 556677 }));
            Assert.Null(mainVm.TryGetAccountViewModel(new Account { Username = "BatchRemoved" }));
        }
    }
}
