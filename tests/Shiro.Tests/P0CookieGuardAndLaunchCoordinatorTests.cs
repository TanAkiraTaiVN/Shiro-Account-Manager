#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P0CookieGuardAndLaunchCoordinatorTests : IDisposable
    {
        private readonly bool _origRememberWindow = RobloxWatcher.RememberWindowPositions;

        public P0CookieGuardAndLaunchCoordinatorTests()
        {
            AccountLaunchService.TestAdjustWindowPositionOverride = (_, _, _) => Task.CompletedTask;
            AccountLaunchService.TestMonitorNativeAuthRateLimitOverride = (_, _, _) => Task.CompletedTask;
        }

        public void Dispose()
        {
            RobloxWatcher.RememberWindowPositions = _origRememberWindow;
            AccountLaunchService.TestGetAuthTicketOverride = null;
            AccountLaunchService.TestStartNativeGameOverride = null;
            AccountLaunchService.TestStartNativeHomeOverride = null;
            AccountLaunchService.TestAdjustWindowPositionOverride = null;
            AccountLaunchService.TestMonitorNativeAuthRateLimitOverride = null;
            LaunchCoordinator.TestWaitForClientUnlockedOverride = null;
            LaunchQueuePacingHelper.TestAsyncJoinTimeoutOverride = null;
            RobloxProcessService.TestGetRunningRobloxPidsOverride = null;
            RobloxDirectLauncher.ResetCookieGuardStateForTest();
            AutoReconnectService.Instance.Clear();
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
        }

        [Fact]
        public async Task LaunchCoordinator_EnqueueAsync_DoesNotDisposeCancellationTokenOfPostLaunchMonitors()
        {
            var monitorEvents = new List<string>();
            var logger = Substitute.For<IAppLogger>();
            logger.When(l => l.Debug(Arg.Any<string>())).Do(ci =>
            {
                lock (monitorEvents)
                {
                    monitorEvents.Add(ci.Arg<string>());
                }
            });
            logger.When(l => l.Warn(Arg.Any<string>())).Do(ci =>
            {
                lock (monitorEvents)
                {
                    monitorEvents.Add(ci.Arg<string>());
                }
            });
            using var loggerScope = AppServices.Override(logger);

            var bridge = Substitute.For<IHostUiBridgeService>();
            using var bridgeScope = AppServices.Override(bridge);

            RobloxWatcher.RememberWindowPositions = true;
            var batchCancelledGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int completedMonitors = 0;
            AccountLaunchService.TestGetAuthTicketOverride = (_, _) => Task.FromResult((true, "ticket-mon-1"));
            AccountLaunchService.TestStartNativeGameOverride = (_, _, _, _, _, _, _) => Task.FromResult(999991);
            LaunchCoordinator.TestWaitForClientUnlockedOverride = (_, _, _) => Task.FromResult(true);
            AccountLaunchService.TestMonitorNativeAuthRateLimitOverride = async (_, _, ct) =>
            {
                await batchCancelledGate.Task.ConfigureAwait(false);
                await Task.Delay(25, ct).ConfigureAwait(false);
                Interlocked.Increment(ref completedMonitors);
            };
            AccountLaunchService.TestAdjustWindowPositionOverride = async (_, _, ct) =>
            {
                await batchCancelledGate.Task.ConfigureAwait(false);
                await Task.Delay(25, ct).ConfigureAwait(false);
                Interlocked.Increment(ref completedMonitors);
            };

            var account1 = new Account
            {
                Username = "MonitorTokenUser1",
                UserID = 1001,
                Valid = true,
                BrowserTrackerID = "123456789012"
            };
            account1.SetField("Window_Position_X", "100");
            account1.SetField("Window_Position_Y", "100");
            account1.SetField("Window_Width", "800");
            account1.SetField("Window_Height", "600");

            var account2 = new Account
            {
                Username = "MonitorTokenUser2",
                UserID = 1002,
                Valid = true
            };

            using var batchCts = new CancellationTokenSource();
            var coordinator = new LaunchCoordinator((req, ct) =>
            {
                if (req.Account.Username == "MonitorTokenUser2")
                {
                    batchCts.Cancel();
                    ct.ThrowIfCancellationRequested();
                }

                return AccountLaunchService.Instance.JoinServerResultAsync(req.Account, req.PlaceId, cancellationToken: ct);
            });

            try
            {
                var batch = await coordinator.EnqueueAsync(
                    new[]
                    {
                        new LaunchRequest(account1, 1818, string.Empty, false, false),
                        new LaunchRequest(account2, 1818, string.Empty, false, false)
                    },
                    delaySeconds: 1,
                    asyncJoin: false,
                    progress: null,
                    cancellationToken: batchCts.Token);

                Assert.True(batch.Cancelled);
                Assert.Equal(LaunchOutcome.Success, batch.Results[0].Status);

                batchCancelledGate.TrySetResult();
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref completedMonitors) == 2, TimeSpan.FromSeconds(3)));

                lock (monitorEvents)
                {
                    Assert.DoesNotContain(monitorEvents, m => m.Contains("Roblox window positioning cancelled", StringComparison.OrdinalIgnoreCase));
                    Assert.DoesNotContain(monitorEvents, m => m.Contains("native auth rate-limit monitor cancelled", StringComparison.OrdinalIgnoreCase));
                    Assert.DoesNotContain(monitorEvents, m => m.Contains("Roblox window positioning failed", StringComparison.OrdinalIgnoreCase));
                    Assert.DoesNotContain(monitorEvents, m => m.Contains("native auth rate-limit monitor failed", StringComparison.OrdinalIgnoreCase));
                }
            }
            finally
            {
                batchCancelledGate.TrySetResult();
            }
        }

        [Fact]
        public async Task LaunchCoordinator_EnqueueAsync_SignalsNextLaunchAccount_OnlyAfterExecuteLaunchSettles()
        {
            var bridge = Substitute.For<IHostUiBridgeService>();
            using var bridgeScope = AppServices.Override(bridge);

            AccountLaunchService.TestGetAuthTicketOverride = (_, _) => Task.FromResult((true, "ticket-seq-1"));
            AccountLaunchService.TestStartNativeGameOverride = (_, _, _, _, _, _, _) => Task.FromResult(55111);
            AccountLaunchService.TestStartNativeHomeOverride = (_, _, _, _, _, _) => Task.FromResult(55222);
            LaunchCoordinator.TestWaitForClientUnlockedOverride = (_, _, _) => Task.FromResult(true);

            bool executeSettled = false;
            var settledAtSignal = new List<bool>();
            bridge.When(b => b.SignalNextLaunchAccount()).Do(_ => settledAtSignal.Add(executeSettled));

            var gameCoordinator = new LaunchCoordinator(async (req, ct) =>
            {
                executeSettled = false;
                var result = await AccountLaunchService.Instance.JoinServerResultAsync(req.Account, req.PlaceId, cancellationToken: ct);
                await Task.Delay(25, ct);
                executeSettled = true;
                return result;
            });

            var gameAcc = new Account { Username = "SeqGameUser", UserID = 2001, Valid = true };
            var gameBatch = await gameCoordinator.EnqueueAsync(
                new[] { new LaunchRequest(gameAcc, 1818, string.Empty, false, false) },
                delaySeconds: 1,
                asyncJoin: false,
                progress: null);

            Assert.Single(gameBatch.Results);
            Assert.Equal(LaunchOutcome.Success, gameBatch.Results[0].Status);
            Assert.Single(settledAtSignal);
            Assert.True(settledAtSignal[0]);

            settledAtSignal.Clear();
            var homeCoordinator = new LaunchCoordinator(async (req, ct) =>
            {
                executeSettled = false;
                var result = await AccountLaunchService.Instance.LaunchAppHomeResultAsync(req.Account, preferChrome: false, cancellationToken: ct);
                await Task.Delay(25, ct);
                executeSettled = true;
                return result;
            });

            var homeAcc = new Account { Username = "SeqHomeUser", UserID = 2002, Valid = true };
            var homeBatch = await homeCoordinator.EnqueueAsync(
                new[] { new LaunchRequest(homeAcc, 0, string.Empty, false, false, openHome: true) },
                delaySeconds: 1,
                asyncJoin: false,
                progress: null);

            Assert.Single(homeBatch.Results);
            Assert.Equal(LaunchOutcome.Success, homeBatch.Results[0].Status);
            Assert.Single(settledAtSignal);
            Assert.True(settledAtSignal[0]);
        }

        [Fact]
        public async Task AccountLaunchService_StandaloneDirectCalls_StillSignalNextLaunchAccount()
        {
            var bridge = Substitute.For<IHostUiBridgeService>();
            using var bridgeScope = AppServices.Override(bridge);

            AccountLaunchService.TestGetAuthTicketOverride = (_, _) => Task.FromResult((true, "ticket-direct-1"));
            AccountLaunchService.TestStartNativeGameOverride = (_, _, _, _, _, _, _) => Task.FromResult(66111);
            AccountLaunchService.TestStartNativeHomeOverride = (_, _, _, _, _, _) => Task.FromResult(66222);

            var acc = new Account { Username = "StandaloneUser", UserID = 3001, Valid = true };
            var gameRes = await AccountLaunchService.Instance.JoinServerResultAsync(acc, 1818);
            Assert.Equal(LaunchOutcome.Success, gameRes.Outcome);
            bridge.Received(1).SignalNextLaunchAccount();

            var homeRes = await AccountLaunchService.Instance.LaunchAppHomeResultAsync(acc);
            Assert.Equal(LaunchOutcome.Success, homeRes.Outcome);
            bridge.Received(2).SignalNextLaunchAccount();

            AccountLaunchService.TestStartNativeHomeOverride = (_, _, _, _, _, _) => throw new InvalidOperationException("sim home err");
            var failHomeRes = await AccountLaunchService.Instance.LaunchAppHomeResultAsync(acc);
            Assert.Equal(LaunchOutcome.Failed, failHomeRes.Outcome);
            bridge.Received(3).SignalNextLaunchAccount();
        }

        [Fact]
        public async Task LaunchCoordinator_EnqueueAsync_WithoutCookieInjection_DoesNotSchedulePostSpawnRestore()
        {
            var restoreCtsField = typeof(RobloxDirectLauncher).GetField("_restoreCts", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(restoreCtsField);

            string isolated = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Shiro-NoCookie-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(isolated);
            RobloxDirectLauncher.ResetCookieGuardStateForTest();
            RobloxDirectLauncher.TestDefaultLocalStorageDirOverride = isolated;
            RobloxDirectLauncher.RestoreOriginalCookies();
            var bridge = Substitute.For<IHostUiBridgeService>();
            using var bridgeScope = AppServices.Override(bridge);

            AccountLaunchService.TestGetAuthTicketOverride = (_, _) => Task.FromResult((true, "ticket-nocookie-1"));
            AccountLaunchService.TestStartNativeGameOverride = (_, _, _, _, _, _, _) => Task.FromResult(424242);
            LaunchCoordinator.TestWaitForClientUnlockedOverride = (_, _, _) => Task.FromResult(true);

            var acc = new Account { Username = "TicketOnlyUser", UserID = 4001, Valid = true };
            var coordinator = new LaunchCoordinator((req, ct) =>
                AccountLaunchService.Instance.JoinServerResultAsync(req.Account, req.PlaceId, cancellationToken: ct));

            try
            {
                var batch = await coordinator.EnqueueAsync(
                    new[] { new LaunchRequest(acc, 1818, string.Empty, false, false) },
                    delaySeconds: 1,
                    asyncJoin: false,
                    progress: null);

                Assert.Single(batch.Results);
                Assert.Equal(LaunchOutcome.Success, batch.Results[0].Status);
                Assert.Equal(424242, acc.LastLaunchedProcessId);
                Assert.Null(restoreCtsField!.GetValue(null));
            }
            finally
            {
                RobloxDirectLauncher.ResetCookieGuardStateForTest();
                if (System.IO.Directory.Exists(isolated))
                {
                    try { System.IO.Directory.Delete(isolated, true); }
                    catch (System.IO.IOException) { }
                }
            }
        }

        [Fact]
        public async Task LaunchCoordinator_EnqueueAsync_PassesNonBatchTokensToPostLaunchMonitors_ForGameAndHome()
        {
            var bridge = Substitute.For<IHostUiBridgeService>();
            using var bridgeScope = AppServices.Override(bridge);

            var capturedTokens = new List<CancellationToken>();
            var monitorGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int completedMonitors = 0;

            AccountLaunchService.TestGetAuthTicketOverride = (_, _) => Task.FromResult((true, "ticket-hooks-1"));
            AccountLaunchService.TestStartNativeGameOverride = (_, _, _, _, _, _, _) => Task.FromResult(77111);
            AccountLaunchService.TestStartNativeHomeOverride = (_, _, _, _, _, _) => Task.FromResult(77222);
            LaunchCoordinator.TestWaitForClientUnlockedOverride = (_, _, _) => Task.FromResult(true);

            AccountLaunchService.TestMonitorNativeAuthRateLimitOverride = async (_, _, ct) =>
            {
                lock (capturedTokens)
                    capturedTokens.Add(ct);
                await monitorGate.Task.ConfigureAwait(false);
                await Task.Delay(5, ct).ConfigureAwait(false);
                Interlocked.Increment(ref completedMonitors);
            };
            AccountLaunchService.TestAdjustWindowPositionOverride = async (_, _, ct) =>
            {
                lock (capturedTokens)
                    capturedTokens.Add(ct);
                await monitorGate.Task.ConfigureAwait(false);
                await Task.Delay(5, ct).ConfigureAwait(false);
                Interlocked.Increment(ref completedMonitors);
            };

            var coordinator = new LaunchCoordinator((req, ct) => req.OpenHome
                ? AccountLaunchService.Instance.LaunchAppHomeResultAsync(req.Account, cancellationToken: ct)
                : AccountLaunchService.Instance.JoinServerResultAsync(req.Account, req.PlaceId, cancellationToken: ct));

            var batch = await coordinator.EnqueueAsync(
                new[]
                {
                    new LaunchRequest(new Account { Username = "HookGameUser", UserID = 5001, Valid = true }, 1818, string.Empty, false, false),
                    new LaunchRequest(new Account { Username = "HookHomeUser", UserID = 5002, Valid = true }, 0, string.Empty, false, false, openHome: true)
                },
                delaySeconds: 1,
                asyncJoin: false,
                progress: null);

            Assert.Equal(2, batch.Results.Count);
            Assert.All(batch.Results, r => Assert.Equal(LaunchOutcome.Success, r.Status));

            // Now EnqueueAsync has returned and disposed linkedCancellation; resume the monitors.
            monitorGate.TrySetResult();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref completedMonitors) == 4, TimeSpan.FromSeconds(3)));

            lock (capturedTokens)
            {
                Assert.Equal(4, capturedTokens.Count);
                Assert.All(capturedTokens, t =>
                {
                    Assert.False(t.CanBeCanceled);
                    Assert.False(t.IsCancellationRequested);
                });
            }
        }

        [Fact]
        public async Task LaunchCoordinator_EnqueueAsync_AsyncJoin_SignalsNextAfterExecuteLaunchSettles()
        {
            var ini = new IniFile();
            ini["General"].Set(AppSettingsKeys.General.SmartFastLaunch, "false");
            using var settingsScope = AppServices.Override(ini["General"]);

            var bridge = new HostUiBridgeService();
            using var bridgeScope = AppServices.Override<IHostUiBridgeService>(bridge);

            AccountLaunchService.TestGetAuthTicketOverride = (_, _) => Task.FromResult((true, "ticket-asyncjoin-1"));
            AccountLaunchService.TestStartNativeGameOverride = (_, _, _, _, _, _, _) => Task.FromResult(88111);
            LaunchQueuePacingHelper.TestAsyncJoinTimeoutOverride = TimeSpan.FromSeconds(5);

            var events = new List<string>();
            LaunchCoordinator? coordinator = null;
            bridge.SignalNextLaunchAccountHandler = () =>
            {
                events.Add("signal");
                coordinator?.SignalNext();
            };

            coordinator = new LaunchCoordinator(async (req, ct) =>
            {
                events.Add("start:" + req.Account.Username);
                var res = await AccountLaunchService.Instance.JoinServerResultAsync(req.Account, req.PlaceId, cancellationToken: ct);
                await Task.Delay(15, ct);
                events.Add("settled:" + req.Account.Username);
                return res;
            });

            var batch = await coordinator.EnqueueAsync(
                new[]
                {
                    new LaunchRequest(new Account { Username = "AsyncU1", UserID = 6001, Valid = true }, 1818, string.Empty, false, false),
                    new LaunchRequest(new Account { Username = "AsyncU2", UserID = 6002, Valid = true }, 1818, string.Empty, false, false)
                },
                delaySeconds: 1,
                asyncJoin: true,
                progress: null);

            Assert.Equal(2, batch.Results.Count);
            Assert.All(batch.Results, r => Assert.Equal(LaunchOutcome.Success, r.Status));
            Assert.Equal(
                new[] { "start:AsyncU1", "settled:AsyncU1", "signal", "start:AsyncU2", "settled:AsyncU2", "signal" },
                events);
        }

        [Fact]
        public async Task LaunchCoordinator_EnqueueAsync_WithCookieInjection_SchedulesPostSpawnRestore()
        {
            string isolated = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Shiro-CoordInject-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(isolated);
            RobloxDirectLauncher.ResetCookieGuardStateForTest();

            try
            {
                RobloxDirectLauncher.TestDefaultLocalStorageDirOverride = isolated;
                RobloxDirectLauncher.TestIsAnyRobloxRunningOverride = () => false;
                RobloxDirectLauncher.TestCookieGuardWaitForClientUnlockedOverride = (_, _, _) => Task.FromResult(true);
                RobloxDirectLauncher.TestPostSpawnSettleDelayMsOverride = 10;

                var bridge = Substitute.For<IHostUiBridgeService>();
                using var bridgeScope = AppServices.Override(bridge);
                LaunchCoordinator.TestWaitForClientUnlockedOverride = (_, _, _) => Task.FromResult(true);

                var coordinator = new LaunchCoordinator((req, _) =>
                {
                    bool injected = RobloxDirectLauncher.InjectAccountCookieToLocalStorage(
                        "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('X', 120),
                        browserTrackerId: "123456789012",
                        localStorageDirectoryOverride: null,
                        username: req.Account.Username);
                    Assert.True(injected);
                    req.Account.LastLaunchedProcessId = 91919;
                    return Task.FromResult(LaunchResult.Success());
                });

                var batch = await coordinator.EnqueueAsync(
                    new[] { new LaunchRequest(new Account { Username = "InjectedUser", UserID = 7001, Valid = true }, 1818, string.Empty, false, false) },
                    delaySeconds: 1,
                    asyncJoin: false,
                    progress: null);

                Assert.Single(batch.Results);
                Assert.Equal(LaunchOutcome.Success, batch.Results[0].Status);

                bool restored = SpinWait.SpinUntil(() => !RobloxDirectLauncher.HasPendingCookieBackup, TimeSpan.FromSeconds(3));
                Assert.True(restored);
                Assert.False(System.IO.File.Exists(System.IO.Path.Combine(isolated, "RobloxCookies.dat")));
                Assert.False(System.IO.File.Exists(System.IO.Path.Combine(isolated, "RobloxCookies.dat.shiro-none")));
            }
            finally
            {
                RobloxDirectLauncher.ResetCookieGuardStateForTest();
                if (System.IO.Directory.Exists(isolated))
                {
                    try { System.IO.Directory.Delete(isolated, true); }
                    catch (System.IO.IOException) { }
                }
            }
        }
    }
}
