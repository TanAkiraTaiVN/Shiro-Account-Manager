#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P0AsyncViewModelConcurrencyTests : IDisposable
    {
        private const string FirstSearchJson =
            "{\"searchResults\":[{\"contents\":[{\"rootPlaceId\":111111,\"name\":\"First Game\",\"playerCount\":10}]}]}";
        private const string SecondSearchJson =
            "{\"searchResults\":[{\"contents\":[{\"rootPlaceId\":222222,\"name\":\"Second Game\",\"playerCount\":20}]}]}";
        private const string EmptyIconsJson = "{\"data\":[]}";

        private readonly Action<Action>? _originalDispatcher = AppDispatcher.CustomDispatcher;

        public void Dispose()
        {
            AppDispatcher.CustomDispatcher = _originalDispatcher;
            SharedHttpClient.ResetTestClient("default");
        }

        [Fact]
        public async Task ServerListViewModel_SearchGamesAsync_WhenDisposedInFlight_ResetsIsSearchingGamesToFalse()
        {
            AppDispatcher.CustomDispatcher = action => action();
            var requestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var handler = new FakeHttpHandler(async (req, ct) =>
            {
                requestEntered.TrySetResult();
                await releaseGate.Task.WaitAsync(ct).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(FirstSearchJson)
                };
            });

            SharedHttpClient.SetTestClient("default", new HttpClient(handler));
            using var vm = new ServerListViewModel();
            vm.GameSearchTerm = "obby";

            Task searchTask = vm.SearchGamesAsync();
            await requestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsSearchingGames);

            vm.Dispose();
            await searchTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(vm.IsSearchingGames);
            Assert.True(vm.IsSearchGamesEmpty);
        }

        [Fact]
        public async Task ServerListViewModel_SearchGamesAsync_WhenSuperseded_KeepsSpinnerUntilNewerSearchCompletes()
        {
            AppDispatcher.CustomDispatcher = action => action();
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int omniCalls = 0;

            var handler = new FakeHttpHandler(async (req, ct) =>
            {
                string url = req.RequestUri?.AbsoluteUri ?? string.Empty;
                if (url.Contains("omni-search", StringComparison.OrdinalIgnoreCase))
                {
                    int idx = Interlocked.Increment(ref omniCalls);
                    if (idx == 1)
                    {
                        firstStarted.TrySetResult();
                        await releaseFirst.Task.WaitAsync(ct).ConfigureAwait(false);
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FirstSearchJson) };
                    }

                    secondStarted.TrySetResult();
                    await releaseSecond.Task.WaitAsync(ct).ConfigureAwait(false);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SecondSearchJson) };
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(EmptyIconsJson) };
            });

            SharedHttpClient.SetTestClient("default", new HttpClient(handler));
            using var vm = new ServerListViewModel();
            vm.GameSearchTerm = "first";

            Task t1 = vm.SearchGamesAsync();
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.IsSearchingGames);

            vm.GameSearchTerm = "second";
            Task t2 = vm.SearchGamesAsync();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            releaseFirst.TrySetResult();
            await t1.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.IsSearchingGames);

            releaseSecond.TrySetResult();
            await t2.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(vm.IsSearchingGames);
            Assert.Single(vm.SearchGames);
            Assert.Equal(222222, vm.SearchGames[0].PlaceId);
        }

        [Fact]
        public async Task MissingAssetsViewModel_BuyAsync_DoesNotCancelInFlightLoad_AndOnClosingCancelsBoth()
        {
            AppDispatcher.CustomDispatcher = action => action();
            var account = new Account { Username = "buyer", UserID = 10 };
            var commerce = Substitute.For<IAssetCommerce>();
            var prompt = Substitute.For<IUserPrompt>();
            prompt.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

            var loadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var loadGate = new TaskCompletionSource<(bool Ok, string Content)>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken loadToken = default;

            commerce.GetAssetDetailsAsync(account, 1, Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    loadToken = ci.ArgAt<CancellationToken>(2);
                    loadEntered.TrySetResult();
                    return loadGate.Task;
                });

            var purchaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var purchaseGate = new TaskCompletionSource<(bool Ok, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken purchaseToken = default;

            commerce.PurchaseAsync(
                    account,
                    2,
                    77,
                    15,
                    9,
                    "col-1",
                    "prod-1",
                    "User",
                    Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    purchaseToken = ci.ArgAt<CancellationToken>(8);
                    purchaseEntered.TrySetResult();
                    return purchaseGate.Task;
                });

            var vm = new MissingAssetsViewModel(account, new long[] { 1 }, commerce, prompt);
            Task loadTask = vm.LoadCommand.ExecuteAsync(null);
            await loadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var loadedItem = new MissingAssetDisplayItem
            {
                AssetId = 2,
                Name = "Cap",
                ProductId = 77,
                SellerId = 9,
                PriceInRobux = 15,
                Cost = 15,
                CanBuy = true,
                IsLoaded = true,
                CollectibleItemId = "col-1",
                CollectibleProductId = "prod-1",
                SellerType = "User"
            };

            Task buyTask = vm.BuyCommand.ExecuteAsync(loadedItem);
            await purchaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(loadToken.IsCancellationRequested);
            Assert.False(purchaseToken.IsCancellationRequested);

            vm.OnClosing();

            Assert.True(loadToken.IsCancellationRequested);
            Assert.True(purchaseToken.IsCancellationRequested);

            loadGate.TrySetCanceled(loadToken);
            purchaseGate.TrySetCanceled(purchaseToken);
            await Task.WhenAll(loadTask, buyTask).WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task ProxyManagerViewModel_TestAllAsync_GuardsMutatingAndSaveCommandsWhileBusy_AndSnapshotsProxies()
        {
            var store = Substitute.For<IProxyStore>();
            var p1 = new ProxyItem { Protocol = "http", Host = "1.1.1.1", Port = 80, LatencyMs = -1 };
            var p2 = new ProxyItem { Protocol = "http", Host = "2.2.2.2", Port = 80, LatencyMs = -2 };
            store.Load().Returns(new List<ProxyItem> { p1, p2 });
            store.Parse(Arg.Any<string?>()).Returns(ci => ProxyManagerService.ParseProxy(ci.Arg<string?>() ?? string.Empty));

            var testStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var testGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IEnumerable<ProxyItem>? capturedProxies = null;

            store.TestAllAsync(Arg.Any<IEnumerable<ProxyItem>>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    capturedProxies = ci.ArgAt<IEnumerable<ProxyItem>>(0);
                    testStarted.TrySetResult();
                    return testGate.Task;
                });

            var vm = new ProxyManagerViewModel(store);
            bool closeRequested = false;
            vm.CloseRequested += (_, _) => closeRequested = true;

            Task testTask = vm.TestAllAsync();
            await testStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsBusy);
            Assert.False(vm.AddProxyCommand.CanExecute(null));
            Assert.False(vm.DeleteProxyCommand.CanExecute(p1));
            Assert.False(vm.RemoveDeadCommand.CanExecute(null));
            Assert.False(vm.SaveCommand.CanExecute(null));
            Assert.IsNotType<ObservableCollection<ProxyItem>>(capturedProxies);
            Assert.Equal(2, capturedProxies!.Count());

            vm.NewProxyText = "8.8.8.8:8080";
            vm.AddProxyCommand.Execute(null);
            vm.DeleteProxyCommand.Execute(p1);
            vm.RemoveDeadCommand.Execute(null);
            vm.SaveCommand.Execute(null);

            Assert.Equal(2, vm.Proxies.Count);
            Assert.Equal("8.8.8.8:8080", vm.NewProxyText);
            Assert.False(closeRequested);
            store.DidNotReceive().Save(Arg.Any<IEnumerable<ProxyItem>>());

            testGate.SetResult();
            await testTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(vm.IsBusy);
            Assert.True(vm.AddProxyCommand.CanExecute(null));
            Assert.True(vm.DeleteProxyCommand.CanExecute(p1));
            Assert.True(vm.RemoveDeadCommand.CanExecute(null));
            Assert.True(vm.SaveCommand.CanExecute(null));

            vm.RemoveDeadCommand.Execute(null);
            Assert.Single(vm.Proxies);
            Assert.Same(p1, vm.Proxies[0]);

            vm.AddProxyCommand.Execute(null);
            Assert.Equal(2, vm.Proxies.Count);

            vm.SaveCommand.Execute(null);
            Assert.True(closeRequested);
            store.Received(1).Save(vm.Proxies);
        }
    }
}
