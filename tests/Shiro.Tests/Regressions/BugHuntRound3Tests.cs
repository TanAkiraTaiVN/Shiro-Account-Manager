#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
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
    public sealed class BugHuntRound3Tests
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

        [Fact]
        public async Task AccountRuntimeStateStore_Flush_DoesNotDropConcurrentMutationDuringDiskWrite()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"ShiroR3RuntimeState_{Guid.NewGuid():N}");
            using var storageScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
            AccountRuntimeStateStore.ResetInMemoryCacheForTests();

            try
            {
                Directory.CreateDirectory(tempDir);
                var acc = new Account
                {
                    UserID = 55667788L,
                    Username = "RaceUser",
                    LastUse = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)
                };

                int nextX = 10;
                for (int round = 0; round < 25; round++)
                {
                    int stopWriter = 0;
                    int latestWrittenX = Volatile.Read(ref nextX);

                    AccountRuntimeStateStore.RecordWindowBounds(acc, latestWrittenX, 50, 800, 600);

                    var writerThread = new Thread(() =>
                    {
                        while (Volatile.Read(ref stopWriter) == 0)
                        {
                            int val = Interlocked.Increment(ref nextX);
                            AccountRuntimeStateStore.RecordWindowBounds(acc, val, 50, 800, 600);
                            Volatile.Write(ref latestWrittenX, val);
                        }
                    })
                    {
                        IsBackground = true
                    };

                    var flushTask = Task.Run(() => AccountRuntimeStateStore.Flush());
                    writerThread.Start();
                    await Task.Delay(2);
                    Volatile.Write(ref stopWriter, 1);
                    writerThread.Join();
                    await flushTask;

                    // Force a final flush and reload cleanly from disk to verify no dirty update was lost.
                    AccountRuntimeStateStore.Flush();
                    AccountRuntimeStateStore.ResetInMemoryCacheForTests();

                    var reloaded = new Account { UserID = acc.UserID, Username = acc.Username };
                    AccountRuntimeStateStore.HydrateAccounts(new[] { reloaded });

                    int expectedFinalX = Volatile.Read(ref nextX);
                    Assert.Equal(expectedFinalX.ToString(CultureInfo.InvariantCulture), reloaded.GetField("Window_Position_X"));
                }
            }
            finally
            {
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task ModernPeriodicTask_StopDuringActiveTick_DoesNotDisposeTokenInUse()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tickDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception? capturedException = null;

            using var periodic = new ModernPeriodicTask(
                TimeSpan.FromMilliseconds(15),
                async token =>
                {
                    entered.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                    try
                    {
                        _ = token.WaitHandle;
                        await Task.Delay(25, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected clean cancellation without ObjectDisposedException.
                    }
                    catch (Exception ex)
                    {
                        capturedException = ex;
                    }
                    finally
                    {
                        tickDone.TrySetResult();
                    }
                },
                enabled: true);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            periodic.Stop();
            release.TrySetResult();
            await tickDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(capturedException);
        }

        [Fact]
        public void ModernPeriodicTask_StartOrEnableAfterDispose_RemainsDisabled()
        {
            var task = new ModernPeriodicTask(TimeSpan.FromMilliseconds(50), _ => Task.CompletedTask, enabled: false);
            task.Dispose();

            task.Start();
            bool afterStart = task.Enabled;

            task.Enabled = true;
            bool afterEnabledSetter = task.Enabled;

            task.Restart(TimeSpan.FromMilliseconds(25));
            bool afterRestart = task.Enabled;

            Assert.False(afterStart);
            Assert.False(afterEnabledSetter);
            Assert.False(afterRestart);
        }

        [Fact]
        public void DiscordRpcService_UpdateCurrentPresence_WhenPlaceIdExplicitlyCleared_DoesNotIncludeJoinButton()
        {
            string prevPlace = ServerBrowserStateService.Default.CurrentPlaceId;
            string prevJob = ServerBrowserStateService.Default.CurrentJobId;
            var prevPipes = DiscordRpcService.TestAvailablePipesOverride;

            try
            {
                DiscordRpcService.TestAvailablePipesOverride = () => Array.Empty<int>();
                ServerBrowserStateService.Default.CurrentPlaceId = string.Empty;
                ServerBrowserStateService.Default.CurrentJobId = "job-cleared-test";

                var logger = Substitute.For<IAppLogger>();
                var settings = Substitute.For<ISettingsService>();
                settings.Get<bool>(AppSettingsKeys.General.DiscordRpcEnabled, true).Returns(true);
                settings.Get<bool>(AppSettingsKeys.General.DiscordRpcJoinCurrentServer, true).Returns(true);
                settings.Get<bool>(AppSettingsKeys.General.DiscordRpcShowGame, true).Returns(true);

                using var service = new DiscordRpcService(logger, settings);
                service.UpdateCurrentPresence();

                FieldInfo? lastActivityField = typeof(DiscordRpcService).GetField("_lastActivity", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(lastActivityField);
                var activity = lastActivityField!.GetValue(service) as DiscordActivityItem;
                Assert.NotNull(activity);
                Assert.True(activity!.Buttons == null || activity.Buttons.Count == 0,
                    "Cleared PlaceId must not emit fallback Join Server button in Discord Rich Presence.");
            }
            finally
            {
                DiscordRpcService.TestAvailablePipesOverride = prevPipes;
                ServerBrowserStateService.Default.CurrentPlaceId = prevPlace;
                ServerBrowserStateService.Default.CurrentJobId = prevJob;
            }
        }

        [Fact]
        public async Task DiscordRpcService_DisposeDuringActivePipeGateUse_DoesNotThrowObjectDisposedOnPipeGate()
        {
            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            var service = new DiscordRpcService(logger, settings);

            FieldInfo? gateField = typeof(DiscordRpcService).GetField("_pipeGate", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo? writeMethod = typeof(DiscordRpcService).GetMethod("WriteFrameBytesAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(gateField);
            Assert.NotNull(writeMethod);

            var gate = (SemaphoreSlim)gateField!.GetValue(service)!;
            await gate.WaitAsync();
            bool gateHeld = true;

            try
            {
                Task inFlightWrite = (Task)writeMethod!.Invoke(service, new object[] { new byte[] { 1, 2, 3, 4 }, CancellationToken.None })!;
                await Task.Delay(25);

                service.Dispose();

                Exception? releaseException = Record.Exception(() =>
                {
                    gate.Release();
                    gateHeld = false;
                });

                Exception? writeException = await Record.ExceptionAsync(() => inFlightWrite.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.Null(releaseException);
                Assert.IsNotType<TimeoutException>(writeException);
                Assert.IsNotType<ObjectDisposedException>(writeException);
            }
            finally
            {
                if (gateHeld)
                {
                    SafeExecuteHelper.Try(() => gate.Release());
                }
                SafeExecuteHelper.Try(service.Dispose);
            }
        }

        [Fact]
        public async Task RobloxPlaceHelper_GetRandomJobId_ConcurrentCachedServersMutation_DoesNotThrow()
        {
            var prevCached = ServerBrowserStateService.Default.CachedServers;
            var handler = new DelegateHttpHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{invalid-json", Encoding.UTF8, "application/json")
                }));

            using var apiOverride = AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));
            var sharedList = new List<ServerData>();
            for (int i = 0; i < 20; i++)
                sharedList.Add(new ServerData { id = $"srv-{i}", playing = 3, maxPlayers = 10 });
            ServerBrowserStateService.Default.CachedServers = sharedList;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                var mutator = Task.Run(() =>
                {
                    int counter = 0;
                    while (!cts.IsCancellationRequested)
                    {
                        lock (sharedList)
                        {
                            sharedList.Clear();
                            for (int i = 0; i < 20; i++)
                                sharedList.Add(new ServerData { id = $"srv-{counter}-{i}", playing = 4, maxPlayers = 12 });
                        }
                        counter++;
                    }
                });

                Exception? readerException = null;
                var reader = Task.Run(async () =>
                {
                    try
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            string id = await RobloxPlaceHelper.GetRandomJobId(189707, chooseLowestServer: false, cancellationToken: cts.Token);
                            _ = id;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when cts expires.
                    }
                    catch (Exception ex)
                    {
                        readerException = ex;
                    }
                });

                await Task.WhenAll(mutator, reader);
                Assert.Null(readerException);
            }
            finally
            {
                ServerBrowserStateService.Default.CachedServers = prevCached;
            }
        }

        [Fact]
        public void ServerListViewModel_WatcherSettings_SerializeWithInvariantCultureUnderCommaLocale()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"ShiroR3WatcherLocale_{Guid.NewGuid():N}");
            using var storageScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);

            CultureInfo prevCulture = CultureInfo.CurrentCulture;
            CultureInfo prevUiCulture = CultureInfo.CurrentUICulture;
            var watcher = AppServices.WatcherSettings;
            string? prevMemory = watcher?.Get("MemoryLowValue");
            string? prevTimeout = watcher?.Get("NoConnectionTimeout");

            try
            {
                CultureInfo viVn = CultureInfo.GetCultureInfo("vi-VN");
                CultureInfo.CurrentCulture = viVn;
                CultureInfo.CurrentUICulture = viVn;

                var vm = new ServerListViewModel(null, Substitute.For<IAppLogger>(), Substitute.For<ISettingsService>());
                vm.MemoryThresholdMb = 200.5m;
                vm.ConnectionTimeoutSec = 45.25m;

                Assert.Equal("200.5", watcher?.Get("MemoryLowValue"));
                Assert.Equal("45.25", watcher?.Get("NoConnectionTimeout"));
                Assert.Equal(200.5m, watcher?.Get<decimal>("MemoryLowValue"));
                Assert.Equal(45.25d, watcher?.Get<double>("NoConnectionTimeout"));

                var reloaded = new ServerListViewModel(null, Substitute.For<IAppLogger>(), Substitute.For<ISettingsService>());
                Assert.Equal(200.5m, reloaded.MemoryThresholdMb);
                Assert.Equal(45.25m, reloaded.ConnectionTimeoutSec);
            }
            finally
            {
                CultureInfo.CurrentCulture = prevCulture;
                CultureInfo.CurrentUICulture = prevUiCulture;
                watcher?.Set("MemoryLowValue", prevMemory);
                watcher?.Set("NoConnectionTimeout", prevTimeout);
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task ToastService_Show_WithNonFiniteOrExtremeDuration_DoesNotFaultBackgroundTask()
        {
            ToastService.ClearAll();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var unobserved = new List<Exception>();
            void Handler(object? sender, UnobservedTaskExceptionEventArgs args)
            {
                lock (unobserved)
                {
                    unobserved.Add(args.Exception);
                }
                args.SetObserved();
            }

            TaskScheduler.UnobservedTaskException += Handler;
            try
            {
                ToastService.ShowInfo("InfiniteToast", "Should not crash delay", double.PositiveInfinity);
                ToastService.ShowWarning("ExtremeToast", "Should clamp safely", double.MaxValue);

                await Task.Delay(80);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                lock (unobserved)
                {
                    Assert.Empty(unobserved);
                }
                Assert.Contains(ToastService.ActiveToasts, t => t.Title == "InfiniteToast");
                Assert.Contains(ToastService.ActiveToasts, t => t.Title == "ExtremeToast");
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= Handler;
                ToastService.ClearAll();
            }
        }

        [Fact]
        public async Task Batch_GetImage_ChunkFailure_ClearsRemainingPendingBatchItems()
        {
            string? prevDisableImages = AppServices.GeneralSettings?.Get<string>("DisableImages");
            var batchRequestBodies = new List<string>();
            int callIndex = 0;

            var handler = new DelegateHttpHandler(async (req, _) =>
            {
                int current = Interlocked.Increment(ref callIndex);
                string body = req.Content != null ? await req.Content.ReadAsStringAsync().ConfigureAwait(false) : string.Empty;
                lock (batchRequestBodies)
                {
                    batchRequestBodies.Add(body);
                }

                if (current == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("server error", Encoding.UTF8, "text/plain")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"data\":[{\"requestId\":\"88999999:undefined:Avatar:150x150::regular\",\"targetId\":88999999,\"errorCode\":0,\"imageUrl\":\"https://rbx/88999999.png\"}]}",
                        Encoding.UTF8,
                        "application/json")
                };
            });

            var mockApi = Substitute.For<IRobloxApiClient>();
            mockApi.Thumbnails.Returns(new RestClient(new HttpClient(handler), "https://thumbnails.roblox.com/"));
            using var apiOverride = AppServices.Override<IRobloxApiClient>(mockApi);

            try
            {
                AppServices.GeneralSettings?.Set("DisableImages", "false");

                var batch1Tasks = Enumerable.Range(1, 105)
                    .Select(i => Batch.GetImage(88_000_000L + i, "Avatar"))
                    .ToArray();

                await Assert.ThrowsAsync<HttpRequestException>(() => Task.WhenAll(batch1Tasks));

                string img = await Batch.GetImage(88_999_999L, "Avatar");
                Assert.Equal("https://rbx/88999999.png", img);

                Assert.Equal(2, batchRequestBodies.Count);
                Assert.Contains("88999999", batchRequestBodies[1]);
                Assert.DoesNotContain("88000101", batchRequestBodies[1]);
                Assert.DoesNotContain("88000105", batchRequestBodies[1]);
            }
            finally
            {
                AppServices.GeneralSettings?.Set("DisableImages", prevDisableImages ?? "false");
            }
        }

        [Fact]
        public async Task Batch_GetGameIcon_Exception_ClearsRemainingPendingPlaceItems()
        {
            Account? prevValidAccount = AppSettings.LastValidAccount;
            string? prevDisableImages = AppServices.GeneralSettings?.Get<string>("DisableImages");
            var placeQueries = new List<string>();
            int gamesGetterCalls = 0;

            var handler = new DelegateHttpHandler((req, _) =>
            {
                string url = req.RequestUri?.ToString() ?? string.Empty;
                if (url.Contains("multiget-place-details", StringComparison.OrdinalIgnoreCase))
                {
                    lock (placeQueries)
                    {
                        placeQueries.Add(url);
                    }

                    if (placeQueries.Count == 1)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                    }

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("[{\"placeId\":88999111,\"universeId\":77999111,\"name\":\"CleanPlace\"}]", Encoding.UTF8, "application/json")
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"data\":[{\"requestId\":\"77999111:undefined:GameIcon:512x512:png:regular\",\"targetId\":77999111,\"errorCode\":0,\"imageUrl\":\"https://rbx/icon77999111.png\"}]}",
                        Encoding.UTF8,
                        "application/json")
                });
            });

            var gamesClient = new RestClient(new HttpClient(handler), "https://games.roblox.com/");
            var mockApi = Substitute.For<IRobloxApiClient>();
            mockApi.Games.Returns(_ =>
            {
                int call = Interlocked.Increment(ref gamesGetterCalls);
                return call == 2
                    ? throw new InvalidOperationException("simulated games client loss")
                    : gamesClient;
            });
            mockApi.Thumbnails.Returns(new RestClient(new HttpClient(handler), "https://thumbnails.roblox.com/"));
            using var apiOverride = AppServices.Override<IRobloxApiClient>(mockApi);

            try
            {
                AppServices.GeneralSettings?.Set("DisableImages", "false");
                AppSettings.LastValidAccount = new Account { SecurityToken = "dummy_token" };

                var batch1Tasks = Enumerable.Range(1, 105)
                    .Select(i => Batch.GetGameIcon(88_100_000L + i))
                    .ToArray();

                await Assert.ThrowsAsync<InvalidOperationException>(() => Task.WhenAll(batch1Tasks));

                string icon = await Batch.GetGameIcon(88_999_111L);
                Assert.Equal("https://rbx/icon77999111.png", icon);

                Assert.Equal(2, placeQueries.Count);
                Assert.Contains("88999111", placeQueries[1]);
                Assert.DoesNotContain("88100101", placeQueries[1]);
                Assert.DoesNotContain("88100105", placeQueries[1]);
            }
            finally
            {
                AppSettings.LastValidAccount = prevValidAccount;
                AppServices.GeneralSettings?.Set("DisableImages", prevDisableImages ?? "false");
            }
        }
    }
}
