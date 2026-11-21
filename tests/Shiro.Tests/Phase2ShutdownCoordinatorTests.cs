#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class Phase2ShutdownCoordinatorTests
    {
        private sealed class CustomParticipant : IShutdownParticipant
        {
            private readonly Action _action;

            public CustomParticipant(ShutdownPhase phase, string name, Action action)
            {
                Phase = phase;
                Name = name;
                _action = action;
            }

            public ShutdownPhase Phase { get; }
            public string Name { get; }
            public void ExecuteShutdown() => _action();
        }

        [Fact]
        public void ShutdownCoordinator_ExecutesPhasesInOrder_IsolatesExceptions_AndIsIdempotent()
        {
            var executed = new List<string>();
            var coordinator = new ShutdownCoordinator();

            coordinator.Register(ShutdownPhase.ClearSecretsAndPools, "clear-1", () => executed.Add("clear-1"));
            coordinator.Register(ShutdownPhase.StopServersAndIntegrations, "servers-1", () => executed.Add("servers-1"));
            coordinator.Register(ShutdownPhase.StopMonitors, "monitors-1", () => executed.Add("monitors-1"));
            coordinator.Register(ShutdownPhase.StopMonitors, "monitors-throws", () =>
            {
                executed.Add("monitors-throws");
                throw new InvalidOperationException("simulated shutdown error");
            });
            coordinator.Register(new CustomParticipant(ShutdownPhase.FlushPersistence, "flush-1", () => executed.Add("flush-1")));
            coordinator.Register(ShutdownPhase.StopMonitors, "monitors-2", () => executed.Add("monitors-2"));

            IReadOnlyList<string> firstRun = coordinator.ExecuteAll();
            string[] expectedOrder =
            {
                "monitors-1",
                "monitors-throws",
                "monitors-2",
                "servers-1",
                "flush-1",
                "clear-1"
            };

            Assert.Equal(expectedOrder, executed);
            Assert.Equal(expectedOrder, firstRun);

            IReadOnlyList<string> secondRun = coordinator.ExecuteAll();
            Assert.Empty(secondRun);
            Assert.Equal(6, executed.Count);
        }

        [Fact]
        public async Task AppBootstrap_DisposeSynchronously_ExecutesAsyncShutdownCallbacks()
        {
            bool callbackRan = false;
            var bootstrap = new AppBootstrap();
            await bootstrap.InitializeAsync(
                shutdownHostAsync: async () =>
                {
                    await Task.Yield();
                    callbackRan = true;
                });

            bootstrap.DisposeSynchronously();

            Assert.True(callbackRan);
            Assert.Throws<ObjectDisposedException>(() => { _ = bootstrap.Services; });
        }

        [Fact]
        public async Task AccountManager_ShutdownBackend_ConcurrentCalls_SerializeAndRunOnce()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroPhase2Shutdown_" + Guid.NewGuid().ToString("N"));
            var prevInstance = AccountManager.Instance;
            var prevIni = AppSettings.ActiveIniFile;
            bool prevSuppress = AccountManager.SuppressExitOnShutdown;
            bool prevPersistenceShutdown = AppServices.AccountPersistence.IsShutdownRequested;
            AccountManager? manager = null;

            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    AccountManager.SuppressExitOnShutdown = true;
                    manager = new AccountManager();

                    const int callers = 6;
                    using var barrier = new Barrier(callers);
                    var observedDisposed = new bool[callers];
                    var tasks = new Task[callers];

                    for (int i = 0; i < callers; i++)
                    {
                        int idx = i;
                        tasks[i] = Task.Run(() =>
                        {
                            barrier.SignalAndWait();
                            manager.ShutdownBackend();
                            observedDisposed[idx] = manager.IsDisposed;
                        });
                    }

                    Exception? ex = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
                    Assert.Null(ex);
                    Assert.True(manager.IsDisposed);
                    Assert.All(observedDisposed, flag => Assert.True(flag));
                }
            }
            finally
            {
                manager?.Dispose();
                AppServices.AccountPersistence.IsShutdownRequested = prevPersistenceShutdown;
                AppSettings.ActiveIniFile = prevIni;
                AccountManager.Instance = prevInstance;
                prevInstance?.WireStateAndHostBridgeServices();
                AccountManager.SuppressExitOnShutdown = prevSuppress;
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Fact]
        public async Task BackgroundServices_ShutdownAndReenterCleanly()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroPhase2Bg_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    // 1. AccountPersistenceService.StopDeferredSave() + re-entry
                    var repo = new AccountRepository();
                    var persistence = new AccountPersistenceService(repo);
                    FieldInfo? deferredTimerField = typeof(AccountPersistenceService).GetField(
                        "_deferredSaveTimer", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(deferredTimerField);

                    persistence.QueueDeferredSave();
                    Assert.NotNull(deferredTimerField!.GetValue(persistence));

                    persistence.StopDeferredSave();
                    Assert.True(persistence.IsShutdownRequested);
                    Assert.Null(deferredTimerField.GetValue(persistence));

                    persistence.QueueDeferredSave();
                    Assert.Null(deferredTimerField.GetValue(persistence));

                    persistence.IsShutdownRequested = false;
                    persistence.QueueDeferredSave();
                    Assert.False(persistence.IsShutdownRequested);
                    Assert.NotNull(deferredTimerField.GetValue(persistence));
                    persistence.StopDeferredSave();

                    // 2. AccountRuntimeStateStore.Shutdown() + re-entry
                    AccountRuntimeStateStore.ResetInMemoryCacheForTests();
                    FieldInfo? runtimeTimerField = typeof(AccountRuntimeStateStore).GetField(
                        "_debounceTimer", BindingFlags.Static | BindingFlags.NonPublic);
                    Assert.NotNull(runtimeTimerField);

                    var runtimeAccount = new Account { UserID = 771001, Username = "RuntimeShutdownUser" };
                    AccountRuntimeStateStore.RecordLastUse(runtimeAccount, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
                    Assert.NotNull(runtimeTimerField!.GetValue(null));

                    bool flushed = AccountRuntimeStateStore.Shutdown();
                    Assert.True(flushed);
                    Assert.Null(runtimeTimerField.GetValue(null));
                    Assert.True(File.Exists(StoragePaths.AccountRuntimeStateFile));

                    AccountRuntimeStateStore.RecordLastUse(runtimeAccount, new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc));
                    Assert.NotNull(runtimeTimerField.GetValue(null));
                    AccountRuntimeStateStore.Shutdown();
                }

                // 3. Win32TrayHelper.CancelPendingRefresh()
                Win32TrayHelper.QueueRefreshNotificationArea(delayMs: 5000);
                Exception? trayEx = Record.Exception(() => Win32TrayHelper.CancelPendingRefresh());
                Assert.Null(trayEx);
                FieldInfo? trayCtsField = typeof(Win32TrayHelper).GetField(
                    "_refreshTrayCts", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(trayCtsField);
                Assert.Null(trayCtsField!.GetValue(null));

                // 4. RobloxAudioService.Shutdown() + MTA re-entry
                RobloxAudioService.SeedDummyCachedAudioSessionForTest(999901);
                Assert.True(RobloxAudioService.HasCachedAudioSessionForTest(999901));
                var ctx1 = RobloxAudioService.ProbeWasapiExecutionContext();
                Assert.Equal(ApartmentState.MTA, ctx1.Apartment);

                RobloxAudioService.Shutdown();
                Assert.False(RobloxAudioService.HasCachedAudioSessionForTest(999901));

                var ctx2 = RobloxAudioService.ProbeWasapiExecutionContext();
                Assert.Equal(ApartmentState.MTA, ctx2.Apartment);
                Assert.NotEqual(ctx1.ThreadId, ctx2.ThreadId);
                RobloxAudioService.Shutdown();

                // 5. WebhookNotificationService.Shutdown(200) + re-entry
                var dispatched = new List<string>();
                void OnAlert(WebhookAlert alert)
                {
                    if (!string.IsNullOrEmpty(alert.AccountUsername))
                        dispatched.Add(alert.AccountUsername);
                }

                WebhookNotificationService.AlertDispatched += OnAlert;
                try
                {
                    WebhookNotificationService.EnqueueAlert(new WebhookAlert
                    {
                        AccountUsername = "HookShutdownBefore",
                        EventType = WebhookEventType.Info,
                        Description = "before"
                    });
                    WebhookNotificationService.Shutdown(200);

                    WebhookNotificationService.EnqueueAlert(new WebhookAlert
                    {
                        AccountUsername = "HookShutdownAfter",
                        EventType = WebhookEventType.Info,
                        Description = "after"
                    });
                    Assert.Equal(new[] { "HookShutdownBefore", "HookShutdownAfter" }, dispatched);
                    WebhookNotificationService.Shutdown(200);
                }
                finally
                {
                    WebhookNotificationService.AlertDispatched -= OnAlert;
                }

                // 6a. AutoReconnectService.StopForShutdown()
                using (var reconnect = new AutoReconnectService(TimeSpan.FromSeconds(30)))
                {
                    CancellationToken tokenBefore = reconnect.LifecycleToken;
                    Assert.False(tokenBefore.IsCancellationRequested);

                    var reconnectAccount = new Account { UserID = 771002, Username = "ReconnectShutdownUser" };
                    reconnect.RegisterSession(reconnectAccount, placeId: 1818, processId: 0);
                    Assert.Equal(1, reconnect.ActiveSessionsCount);

                    reconnect.StopForShutdown();
                    Assert.True(tokenBefore.IsCancellationRequested);
                    Assert.Equal(0, reconnect.ActiveSessionsCount);

                    CancellationToken tokenAfter = reconnect.LifecycleToken;
                    Assert.False(tokenAfter.IsCancellationRequested);
                    reconnect.RegisterSession(reconnectAccount, placeId: 1818, processId: 0);
                    Assert.Equal(1, reconnect.ActiveSessionsCount);
                }

                // 6b. LaunchCoordinator.Dispose() when idle and when an enqueue is active
                var idleCoord = new LaunchCoordinator((_, _) => Task.FromResult(LaunchResult.Success()));
                idleCoord.Dispose();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => idleCoord.EnqueueAsync(
                    new[] { new LaunchRequest(new Account { UserID = 771003, Username = "IdleCoordUser" }, 1818, string.Empty, false, false) },
                    delaySeconds: 1,
                    asyncJoin: false,
                    progress: null));

                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var activeCoord = new LaunchCoordinator(async (_, ct) =>
                {
                    entered.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return LaunchResult.Success();
                });

                Task<LaunchBatchResult> enqueueTask = activeCoord.EnqueueAsync(
                    new[] { new LaunchRequest(new Account { UserID = 771004, Username = "ActiveCoordUser" }, 1818, string.Empty, false, false) },
                    delaySeconds: 1,
                    asyncJoin: false,
                    progress: null);

                await entered.Task;
                activeCoord.Dispose();
                release.TrySetResult();

                LaunchBatchResult batch = await enqueueTask;
                Assert.True(batch.Cancelled);
                Assert.Single(batch.Results);
                Assert.Equal(LaunchOutcome.Cancelled, batch.Results[0].Status);
            }
            finally
            {
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();
                RobloxDirectLauncher.ResetCookieGuardStateForTest();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Fact]
        public void AutoReconnectService_LifecycleToken_RemainsLinkableAfterStopForShutdownAndDispose()
        {
            using var service = new AutoReconnectService(TimeSpan.FromSeconds(30));
            CancellationToken tokenBeforeStop = service.LifecycleToken;
            Assert.False(tokenBeforeStop.IsCancellationRequested);

            service.StopForShutdown();
            Assert.True(tokenBeforeStop.IsCancellationRequested);

            using (var linkedAfterStop = CancellationTokenSource.CreateLinkedTokenSource(tokenBeforeStop))
            {
                Assert.True(linkedAfterStop.IsCancellationRequested);
            }

            bool callbackRan = false;
            using (tokenBeforeStop.Register(() => callbackRan = true))
            {
                Assert.True(callbackRan);
            }

            CancellationToken tokenBeforeDispose = service.LifecycleToken;
            Assert.False(tokenBeforeDispose.IsCancellationRequested);

            service.Dispose();
            Assert.True(tokenBeforeDispose.IsCancellationRequested);

            using (var linkedAfterDispose = CancellationTokenSource.CreateLinkedTokenSource(tokenBeforeDispose))
            {
                Assert.True(linkedAfterDispose.IsCancellationRequested);
            }

            var session = new AutoReconnectService.ReconnectSession
            {
                Account = new Account { UserID = 771005, Username = "LinkTokenUser" }
            };
            CancellationToken linkedSessionToken = session.ResetReconnectToken(tokenBeforeDispose);
            Assert.True(linkedSessionToken.IsCancellationRequested);
            session.CancelPendingReconnect();

            var disposedExternalCts = new CancellationTokenSource();
            CancellationToken disposedExternalToken = disposedExternalCts.Token;
            disposedExternalCts.Cancel();
            disposedExternalCts.Dispose();
            CancellationToken fallbackToken = session.ResetReconnectToken(disposedExternalToken);
            Assert.True(fallbackToken.IsCancellationRequested);
            session.CancelPendingReconnect();
        }
    }
}
