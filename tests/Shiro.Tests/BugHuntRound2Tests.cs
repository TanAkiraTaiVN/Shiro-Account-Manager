#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class BugHuntRound2Tests : IDisposable
    {
        private sealed class DelegateHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

            public DelegateHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                _responder(request, cancellationToken);
        }

        private readonly Func<Account, HttpClient>? _origAccountClientFactory;

        public BugHuntRound2Tests()
        {
            _origAccountClientFactory = RobloxAccountSessionClient.TestHttpClientFactory;
        }

        public void Dispose()
        {
            RobloxAccountSessionClient.TestHttpClientFactory = _origAccountClientFactory;
            SharedHttpClient.ResetTestClient("default");
        }

        [Fact]
        public async Task AvatarService_EnsureAvatarAsync_CancelledFirstCallerDoesNotAbortConcurrentCaller()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"ShiroR2Avatar_{Guid.NewGuid():N}");
            using var storageScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);

            try
            {
                const long userId = 8899776611L;
                var enteredHeadshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseHeadshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                byte[] pngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

                var client = new HttpClient(new DelegateHttpHandler(async (req, ct) =>
                {
                    string url = req.RequestUri?.ToString() ?? string.Empty;
                    if (url.Contains("avatar-headshot", StringComparison.OrdinalIgnoreCase))
                    {
                        enteredHeadshot.TrySetResult();
                        await releaseHeadshot.Task.WaitAsync(ct).ConfigureAwait(false);
                        string json = $"{{\"data\":[{{\"targetId\":{userId},\"state\":\"Completed\",\"imageUrl\":\"https://fake.rbxcdn.com/r2-avatar.png\"}}]}}";
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(json, Encoding.UTF8, "application/json")
                        };
                    }

                    if (url.Contains("r2-avatar.png", StringComparison.OrdinalIgnoreCase))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pngBytes) };
                    }

                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }));
                SharedHttpClient.SetTestClient("default", client);

                using var ctsFirst = new CancellationTokenSource();
                Task<string?> firstTask = AvatarService.EnsureAvatarAsync(userId, ctsFirst.Token);
                await enteredHeadshot.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Task<string?> secondTask = AvatarService.EnsureAvatarAsync(userId, CancellationToken.None);
                ctsFirst.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstTask);

                releaseHeadshot.TrySetResult();
                string? secondPath = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.False(string.IsNullOrEmpty(secondPath));
                Assert.True(File.Exists(secondPath));
            }
            finally
            {
                SharedHttpClient.ResetTestClient("default");
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void AccountViewModel_UpdateModel_SameReference_RaisesPropertyChanged()
        {
            using var suppressSave = Account.BeginSuppressDeferredSaveScope();
            var acc = new Account { Username = "InPlaceUser", Alias = "OldAlias", Valid = true };
            var vm = new AccountViewModel(acc);

            var changed = new List<string?>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            acc.Alias = "UpdatedAlias";
            vm.UpdateModel(acc);

            Assert.NotEmpty(changed);
            Assert.Equal("UpdatedAlias", vm.Alias);
        }

        [Fact]
        public void AccountManager_UpdateAccountView_NotifiesSingleAccountAndRefreshesViewModel()
        {
            using var suppressSave = Account.BeginSuppressDeferredSaveScope();
            var acc = new Account { Username = "SingleRefreshUser", UserID = 556611, Alias = "Before", Group = "Alpha", Valid = true };
            using var mvm = new MainViewModel();
            mvm.LoadAccounts(new[] { acc });
            Assert.Single(mvm.Accounts);

            var cardChanged = new List<string?>();
            mvm.Accounts[0].PropertyChanged += (_, e) => cardChanged.Add(e.PropertyName);

            var bridge = HostUiBridgeService.Default;
            var prevSingle = bridge.RefreshSingleAccountOnUiHandler;
            var prevRefresh = bridge.RefreshAccountViewHandler;
            var mgr = AccountManager.Instance ?? new AccountManager();
            try
            {
                bridge.RefreshSingleAccountOnUiHandler = account => mvm.RefreshAccount(account);
                bridge.RefreshAccountViewHandler = account => mgr.UpdateAccountView(account);

                acc.Alias = "After";
                acc.Group = "Beta";
                bridge.RefreshAccountView(acc);

                Assert.NotEmpty(cardChanged);
                Assert.Equal("After", mvm.Accounts[0].Alias);
                Assert.Contains("Beta", mvm.AvailableGroups);
            }
            finally
            {
                bridge.RefreshSingleAccountOnUiHandler = prevSingle;
                bridge.RefreshAccountViewHandler = prevRefresh;
            }
        }

        [Fact]
        public async Task AccountApiDispatcher_SetRecommendedServer_MissingOrInvalidCurrentPlaceId_Returns400BeforeCsrf()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            const string adminPass = "Round2Pass123";
            string? prevPw = AppSettings.WebSocketPassword;
            string? prevPlaceId = AppServices.ServerBrowserState.CurrentPlaceId;
            var prevAccounts = AppServices.AccountRepository.GetAll();
            bool prevAllowLaunch = AppSettings.WebServer.Get<bool>("AllowLaunchAccount");
            bool prevAllowEdit = AppSettings.WebServer.Get<bool>("AllowAccountEditing");
            var acc = new Account
            {
                Username = "RecServerInvalidPlaceUser",
                UserID = 777111,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.RECSRV.S",
                Valid = true
            };
            AccountManager.ResetAccountsForTests(new[] { acc });

            int csrfCalls = 0;
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new DelegateHttpHandler((_, _) =>
            {
                csrfCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }));

            try
            {
                AppSettings.WebSocketPassword = adminPass;
                AppSettings.WebServer.Set("AllowLaunchAccount", "true");
                AppSettings.WebServer.Set("AllowAccountEditing", "true");
                AppServices.ServerBrowserState.CurrentPlaceId = string.Empty;
                AppServices.ServerBrowserState.CachedServers.Clear();
                AppServices.ServerBrowserState.CachedServers.Add(new ServerData { id = "rec-srv-invalid" });

                int capturedStatus = 200;
                var ctx = new AccountManager.ApiRequestContext
                {
                    AbsolutePath = "/SetRecommendedServer",
                    IsV2 = true,
                    IsLocal = true,
                    Host = "localhost",
                    Origin = string.Empty,
                    Body = string.Empty,
                    GetQuery = k => k switch
                    {
                        "Password" => adminPass,
                        "Account" => "RecServerInvalidPlaceUser",
                        _ => null
                    },
                    GetHeader = _ => null,
                    SetStatusCode = code => capturedStatus = code
                };

                string resp = await mgr.ProcessApiRequestAsync(ctx);
                Assert.Equal(400, capturedStatus);
                Assert.Contains("Invalid PlaceId", resp);
                Assert.Equal(0, csrfCalls);
            }
            finally
            {
                AppSettings.WebSocketPassword = prevPw ?? string.Empty;
                AppSettings.WebServer.Set("AllowLaunchAccount", prevAllowLaunch ? "true" : "false");
                AppSettings.WebServer.Set("AllowAccountEditing", prevAllowEdit ? "true" : "false");
                AppServices.ServerBrowserState.CurrentPlaceId = prevPlaceId ?? string.Empty;
                AppServices.ServerBrowserState.CachedServers.Clear();
                AccountManager.ResetAccountsForTests(prevAccounts);
            }
        }

        [Fact]
        public async Task MissingAssetsViewModel_BuyCancelled_RestoresCanBuyAndBuyButtonText()
        {
            var acc = new Account { Username = "buyer", UserID = 10 };
            var commerce = Substitute.For<IAssetCommerce>();
            var prompt = Substitute.For<IUserPrompt>();
            prompt.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

            var gate = new TaskCompletionSource<(bool Ok, string? Error)>();
            CancellationToken purchaseToken = default;
            commerce.PurchaseAsync(acc, 1, 77, 15, 9, "col-1", "prod-1", "User", Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    purchaseToken = ci.ArgAt<CancellationToken>(8);
                    return gate.Task;
                });

            var vm = new MissingAssetsViewModel(acc, new long[] { 1 }, commerce, prompt);
            var item = new MissingAssetDisplayItem
            {
                AssetId = 1,
                Name = "Cap",
                ProductId = 77,
                SellerId = 9,
                PriceInRobux = 15,
                Cost = 15,
                CanBuy = true,
                IsLoaded = true,
                CollectibleItemId = "col-1",
                CollectibleProductId = "prod-1",
                SellerType = "User",
                BuyButtonText = LocalizationService.Get("MissingAssets.BuyAction")
            };

            Task buyTask = vm.BuyCommand.ExecuteAsync(item);
            vm.OnClosing();
            gate.TrySetCanceled(purchaseToken);
            await buyTask;

            Assert.True(item.CanBuy);
            Assert.Equal(LocalizationService.Get("MissingAssets.BuyAction"), item.BuyButtonText);
        }

        [Fact]
        public async Task AutoReconnectService_Dispose_CancelsPendingReconnectTask()
        {
            using var service = new AutoReconnectService(TimeSpan.FromHours(1))
            {
                IsEnabled = true,
                MaxRetries = 5,
                TestReconnectDelayMsOverride = 10_000,
                TestJoinServerOverride = _ => Task.FromResult("Success")
            };

            var acc = new Account { Username = "DisposeReconnectUser", UserID = 334455 };
            service.RegisterSession(acc, 1818, "job-d", false, false, 0, nowUtcOverride: DateTime.UtcNow.AddMinutes(-15));
            Assert.True(service.TryGetSession("DisposeReconnectUser", out var session));

            service.HandleProcessExit(session!);
            await Task.Delay(20);

            Assert.NotNull(session!.ReconnectCts);
            CancellationToken pendingToken = session.ReconnectCts!.Token;
            Assert.False(pendingToken.IsCancellationRequested);

            service.Dispose();

            Assert.True(pendingToken.IsCancellationRequested);
        }

        [Fact]
        public async Task RobloxProcess_CleanupProcessExit_DoesNotTriggerDisposedExceptionDuringRamTrim()
        {
            var logger = Substitute.For<IAppLogger>();
            var loggedWarningsAndDebugs = new List<string>();
            logger.When(l => l.Warn(Arg.Any<string>())).Do(ci =>
            {
                lock (loggedWarningsAndDebugs) loggedWarningsAndDebugs.Add(ci.Arg<string>());
            });
            logger.When(l => l.Debug(Arg.Any<string>())).Do(ci =>
            {
                lock (loggedWarningsAndDebugs) loggedWarningsAndDebugs.Add(ci.Arg<string>());
            });
            using var logScope = AppServices.Override<IAppLogger>(logger);

            using var proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            })!;

            try
            {
                using var rbx = new RobloxProcess(proc, string.Empty);
                rbx.Dispose();

                var ramTrimMethod = typeof(RobloxProcess).GetMethod(
                    "SchedulePostLaunchRamTrimAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(ramTrimMethod);

                Task ramTrimTask = (Task)ramTrimMethod!.Invoke(rbx, null)!;
                await ramTrimTask.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.DoesNotContain(loggedWarningsAndDebugs, msg =>
                    msg.Contains("disposed", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            }
        }

        [Fact]
        public async Task ServerListViewModel_SearchPlayerAsync_AcceptsFullRobloxUrl()
        {
            using var vm = new ServerListViewModel();
            vm.PlaceIdInput = "https://www.roblox.com/games/1818/Classic-Crossroads";
            vm.SearchPlayerUsername = " ";
            await ((AsyncRelayCommand)vm.SearchPlayerCommand).ExecuteAsync(null);
            Assert.Equal(LocalizationService.Get("ServerList.EnterPlayerUsername"), vm.PlayerSearchStatus);

            vm.SearchPlayerUsername = "TestPlayerNoCall";
            SharedHttpClient.SetTestClient("default", new HttpClient(new DelegateHttpHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))));

            await ((AsyncRelayCommand)vm.SearchPlayerCommand).ExecuteAsync(null);
            Assert.NotEqual(LocalizationService.Get("ServerList.EnterPlaceIdFirst"), vm.PlayerSearchStatus);
        }

        [Fact]
        public async Task ServerListViewModel_SearchGamesAsync_ConcurrentSearchSupersedesOlderResult()
        {
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int callIndex = 0;

            SharedHttpClient.SetTestClient("default", new HttpClient(new DelegateHttpHandler(async (req, ct) =>
            {
                string url = req.RequestUri?.ToString() ?? string.Empty;
                if (url.Contains("omni-search", StringComparison.OrdinalIgnoreCase))
                {
                    int idx = Interlocked.Increment(ref callIndex);
                    if (idx == 1)
                    {
                        firstStarted.TrySetResult();
                        await releaseFirst.Task.WaitAsync(ct).ConfigureAwait(false);
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"searchResults\":[{\"contents\":[{\"rootPlaceId\":111111,\"name\":\"Stale Game\",\"playerCount\":10}]}]}")
                        };
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"searchResults\":[{\"contents\":[{\"rootPlaceId\":222222,\"name\":\"Fresh Game\",\"playerCount\":99}]}]}")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") };
            })));

            using var vm = new ServerListViewModel();
            vm.GameSearchTerm = "first";
            Task t1 = vm.SearchGamesAsync();
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            vm.GameSearchTerm = "second";
            Task t2 = ((AsyncRelayCommand)vm.SearchGamesCommand).ExecuteAsync(null);
            await t2.WaitAsync(TimeSpan.FromSeconds(5));

            releaseFirst.TrySetResult();
            await t1.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.DoesNotContain(vm.SearchGames, g => g.PlaceId == 111111);
            Assert.Contains(vm.SearchGames, g => g.PlaceId == 222222);
        }
    }
}
