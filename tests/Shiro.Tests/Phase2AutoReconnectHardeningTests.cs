#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class Phase2AutoReconnectHardeningTests
    {
        [Fact]
        public void IsAccountEligibleForReconnect_RejectsNullExpiredAndBannedAccounts()
        {
            Assert.False(AutoReconnectService.IsAccountEligibleForReconnect(null));
            Assert.False(AutoReconnectService.IsAccountEligibleForReconnect(new Account { Valid = false }));
            Assert.False(AutoReconnectService.IsAccountEligibleForReconnect(new Account { Valid = true, AccountStatusIssue = "Banned" }));
            Assert.False(AutoReconnectService.IsAccountEligibleForReconnect(new Account { Valid = false, AccountStatusIssue = "Banned" }));
            Assert.True(AutoReconnectService.IsAccountEligibleForReconnect(new Account { Valid = true }));
        }

        [Fact]
        public async Task HandleProcessExit_WhenAccountExpiredOrBanned_SuppressesWithoutAttemptingReconnect()
        {
            var ini = new IniFile();
            var general = ini["General"];
            using var settingsScope = AppServices.Override<ISettingsService>(general);
            using var service = new AutoReconnectService(TimeSpan.FromHours(1))
            {
                IsEnabled = true,
                TestReconnectDelayMsOverride = 1
            };

            int reconnectAttempts = 0;
            int joinCalls = 0;
            service.ReconnectAttempted += (_, _) => Interlocked.Increment(ref reconnectAttempts);
            service.TestJoinServerWithTokenOverride = (_, _) =>
            {
                Interlocked.Increment(ref joinCalls);
                return Task.FromResult("Success");
            };

            var expiredAcc = new Account { Username = "ExpiredReconnectAcc", UserID = 81001, Valid = false };
            var warnedAcc = new Account { Username = "WarnedReconnectAcc", UserID = 81002, Valid = true, AccountStatusIssue = "Warned/Locked" };

            service.RegisterSession(expiredAcc, 1818, processId: 0, nowUtcOverride: DateTime.UtcNow.AddMinutes(-2));
            service.RegisterSession(warnedAcc, 1818, processId: 0, nowUtcOverride: DateTime.UtcNow.AddMinutes(-2));

            Assert.True(service.TryGetAccountSession(expiredAcc, out var expiredSession));
            Assert.True(service.TryGetAccountSession(warnedAcc, out var warnedSession));

            service.HandleProcessExit(expiredSession!);
            service.HandleProcessExit(warnedSession!);

            await Task.Delay(100);

            Assert.Equal(0, reconnectAttempts);
            Assert.Equal(0, joinCalls);
            Assert.False(service.TryGetAccountSession(expiredAcc, out _));
            Assert.False(service.TryGetAccountSession(warnedAcc, out _));
            Assert.True(expiredSession!.IsSuppressed);
            Assert.False(expiredSession.IsActive);
            Assert.Equal(0, expiredSession.RetryCount);
            Assert.True(warnedSession!.IsSuppressed);
            Assert.False(warnedSession.IsActive);
            Assert.Equal(0, warnedSession.RetryCount);
        }

        [Fact]
        public async Task HandleProcessExit_WhenAccountInvalidatedDuringBackoff_AbortsBeforeJoinServer()
        {
            var ini = new IniFile();
            var general = ini["General"];
            using var settingsScope = AppServices.Override<ISettingsService>(general);
            using var service = new AutoReconnectService(TimeSpan.FromHours(1))
            {
                IsEnabled = true,
                TestReconnectDelayMsOverride = 60
            };

            int joinCalls = 0;
            service.TestJoinServerWithTokenOverride = (_, _) =>
            {
                Interlocked.Increment(ref joinCalls);
                return Task.FromResult("Success");
            };

            var acc = new Account { Username = "InvalidatedDuringBackoffAcc", UserID = 81003, Valid = true };
            service.RegisterSession(acc, 1818, processId: 0, nowUtcOverride: DateTime.UtcNow.AddMinutes(-2));
            Assert.True(service.TryGetAccountSession(acc, out var session));

            service.HandleProcessExit(session!);
            acc.Valid = false;

            await Task.Delay(200);
            Assert.True(SpinWait.SpinUntil(() => !service.TryGetAccountSession(acc, out _), TimeSpan.FromMilliseconds(500)));

            Assert.Equal(0, joinCalls);
            Assert.False(service.TryGetAccountSession(acc, out _));
            Assert.True(session!.IsSuppressed);
            Assert.False(session.IsActive);
        }

        [Fact]
        public async Task HandleProcessExit_PropagatesCancelableTokenToJoinServer_CanceledOnSuppressOrShutdown()
        {
            // Case 1: SuppressReconnect cancels the token passed to JoinServer
            {
                var ini = new IniFile();
                var general = ini["General"];
                using var settingsScope = AppServices.Override<ISettingsService>(general);
                using var service = new AutoReconnectService(TimeSpan.FromHours(1))
                {
                    IsEnabled = true,
                    TestReconnectDelayMsOverride = 1
                };

                var tokenCaptured = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
                var joinCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                service.TestJoinServerWithTokenOverride = async (_, ct) =>
                {
                    tokenCaptured.TrySetResult(ct);
                    try
                    {
                        await Task.Delay(5000, ct).ConfigureAwait(false);
                        return "Success";
                    }
                    catch (OperationCanceledException)
                    {
                        joinCanceled.TrySetResult(true);
                        throw;
                    }
                };

                var acc = new Account { Username = "SuppressJoinTokenUser", UserID = 81004, Valid = true };
                service.RegisterSession(acc, 1818, processId: 0, nowUtcOverride: DateTime.UtcNow.AddMinutes(-2));
                Assert.True(service.TryGetAccountSession(acc, out var session));

                service.HandleProcessExit(session!);
                CancellationToken capturedToken = await tokenCaptured.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(capturedToken.CanBeCanceled);
                Assert.False(capturedToken.IsCancellationRequested);

                service.SuppressReconnect(acc.Username);

                bool canceled = await joinCanceled.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
                Assert.True(canceled);
                Assert.True(capturedToken.IsCancellationRequested);
                Assert.True(session!.IsSuppressed);
                Assert.False(session.NeedsRetry);
            }

            // Case 2: StopForShutdown cancels the token passed to JoinServer
            {
                var ini = new IniFile();
                var general = ini["General"];
                using var settingsScope = AppServices.Override<ISettingsService>(general);
                using var service = new AutoReconnectService(TimeSpan.FromHours(1))
                {
                    IsEnabled = true,
                    TestReconnectDelayMsOverride = 1
                };

                var tokenCaptured = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
                var joinCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                service.TestJoinServerWithTokenOverride = async (_, ct) =>
                {
                    tokenCaptured.TrySetResult(ct);
                    try
                    {
                        await Task.Delay(5000, ct).ConfigureAwait(false);
                        return "Success";
                    }
                    catch (OperationCanceledException)
                    {
                        joinCanceled.TrySetResult(true);
                        throw;
                    }
                };

                var acc = new Account { Username = "ShutdownJoinTokenUser", UserID = 81005, Valid = true };
                service.RegisterSession(acc, 1818, processId: 0, nowUtcOverride: DateTime.UtcNow.AddMinutes(-2));
                Assert.True(service.TryGetAccountSession(acc, out var session));

                service.HandleProcessExit(session!);
                CancellationToken capturedToken = await tokenCaptured.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(capturedToken.CanBeCanceled);
                Assert.False(capturedToken.IsCancellationRequested);

                service.StopForShutdown();

                bool canceled = await joinCanceled.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
                Assert.True(canceled);
                Assert.True(capturedToken.IsCancellationRequested);
                Assert.True(session!.IsSuppressed);
                Assert.False(session.NeedsRetry);
            }
        }

        [Fact]
        public async Task ObserveProcessExitImmediateAsync_WhenSuppressedOrShutdown_CancelsWaitWithoutWaitingForProcessExit()
        {
            string cmdPath = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            static Process StartHoldProcess(string exe) => Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "/c \"more >nul\"",
                RedirectStandardInput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            })!;

            // Case 1: SuppressReconnect wakes WaitForExitAsync immediately
            using (var proc1 = StartHoldProcess(cmdPath))
            {
                try
                {
                    var ini = new IniFile();
                    var general = ini["General"];
                    using var settingsScope = AppServices.Override<ISettingsService>(general);
                    using var service = new AutoReconnectService(TimeSpan.FromHours(1))
                    {
                        IsEnabled = true,
                        LaunchGracePeriod = TimeSpan.Zero
                    };

                    int reconnectAttempts = 0;
                    service.ReconnectAttempted += (_, _) => Interlocked.Increment(ref reconnectAttempts);

                    var acc1 = new Account { Username = "ObserveSuppressUser", UserID = 81006, Valid = true };
                    service.RegisterSession(acc1, 1818, processId: proc1.Id, nowUtcOverride: DateTime.UtcNow.AddMinutes(-2));
                    Assert.True(service.TryGetAccountSession(acc1, out var session1));
                    Assert.NotNull(session1!.ObserveExitTask);
                    Assert.False(session1.ObserveExitTask!.IsCompleted);

                    service.SuppressReconnect(acc1.Username);

                    await session1.ObserveExitTask.WaitAsync(TimeSpan.FromMilliseconds(500));
                    Assert.True(session1.ObserveExitTask.IsCompletedSuccessfully);
                    Assert.False(proc1.HasExited);
                    Assert.Equal(0, reconnectAttempts);
                }
                finally
                {
                    try { if (!proc1.HasExited) proc1.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { /* Process already exited before cleanup. */ }
                }
            }

            // Case 2: StopForShutdown wakes WaitForExitAsync immediately
            using (var proc2 = StartHoldProcess(cmdPath))
            {
                try
                {
                    var ini = new IniFile();
                    var general = ini["General"];
                    using var settingsScope = AppServices.Override<ISettingsService>(general);
                    using var service = new AutoReconnectService(TimeSpan.FromHours(1))
                    {
                        IsEnabled = true,
                        LaunchGracePeriod = TimeSpan.Zero
                    };

                    int reconnectAttempts = 0;
                    service.ReconnectAttempted += (_, _) => Interlocked.Increment(ref reconnectAttempts);

                    var acc2 = new Account { Username = "ObserveShutdownUser", UserID = 81007, Valid = true };
                    service.RegisterSession(acc2, 1818, processId: proc2.Id, nowUtcOverride: DateTime.UtcNow.AddMinutes(-2));
                    Assert.True(service.TryGetAccountSession(acc2, out var session2));
                    Assert.NotNull(session2!.ObserveExitTask);
                    Assert.False(session2.ObserveExitTask!.IsCompleted);

                    service.StopForShutdown();

                    await session2.ObserveExitTask.WaitAsync(TimeSpan.FromMilliseconds(500));
                    Assert.True(session2.ObserveExitTask.IsCompletedSuccessfully);
                    Assert.False(proc2.HasExited);
                    Assert.Equal(0, reconnectAttempts);
                }
                finally
                {
                    try { if (!proc2.HasExited) proc2.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { /* Process already exited before cleanup. */ }
                }
            }
        }

        [Fact]
        public async Task HandleProcessExit_WhenTokenDisposedDuringSuppressOrShutdown_DoesNotFlipSessionToNeedsRetry()
        {
            foreach (bool useStopForShutdown in new[] { false, true })
            {
                var ini = new IniFile();
                var general = ini["General"];
                using var settingsScope = AppServices.Override<ISettingsService>(general);
                using var service = new AutoReconnectService(TimeSpan.FromHours(1))
                {
                    IsEnabled = true,
                    TestReconnectDelayMsOverride = 1
                };

                var joinEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var joinExited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool odeObserved = false;

                service.TestJoinServerWithTokenOverride = async (s, ct) =>
                {
                    joinEntered.TrySetResult(ct);
                    await cancelCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    try
                    {
                        var waitHandle = ct.WaitHandle;
                        GC.KeepAlive(waitHandle);
                        return "Success";
                    }
                    catch (ObjectDisposedException)
                    {
                        odeObserved = true;
                        throw;
                    }
                    finally
                    {
                        joinExited.TrySetResult(true);
                    }
                };

                var acc = new Account { Username = useStopForShutdown ? "OdeShutdownUser" : "OdeSuppressUser", UserID = 81008, Valid = true };
                service.RegisterSession(acc, 1818, processId: 0, nowUtcOverride: DateTime.UtcNow.AddMinutes(-2));
                Assert.True(service.TryGetAccountSession(acc, out var session));

                service.HandleProcessExit(session!);
                await joinEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

                if (useStopForShutdown)
                    service.StopForShutdown();
                else
                    service.SuppressReconnect(acc.Username);

                cancelCompleted.TrySetResult(true);
                await joinExited.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await Task.Delay(50);

                Assert.True(odeObserved);
                Assert.True(session!.IsSuppressed);
                Assert.False(session.NeedsRetry);
                Assert.False(session.IsActive);
            }
        }

        [Fact]
        public async Task ResetReconnectToken_WhenSuppressedSequentiallyOrConcurrently_NeverReturnsUncanceledToken()
        {
            var suppressedSession = new AutoReconnectService.ReconnectSession
            {
                Account = new Account { Username = "SuppressedResetUser", UserID = 81009 }
            };
            suppressedSession.MarkSuppressedAndCancel();

            CancellationToken tokenAfterSuppress = suppressedSession.ResetReconnectToken(CancellationToken.None);
            Assert.True(tokenAfterSuppress.IsCancellationRequested);

            for (int i = 0; i < 400; i++)
            {
                var raceSession = new AutoReconnectService.ReconnectSession
                {
                    Account = new Account { Username = $"RaceResetUser_{i}", UserID = 82000 + i }
                };

                using var barrier = new Barrier(2);
                CancellationToken observedToken = CancellationToken.None;

                var resetTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    observedToken = raceSession.ResetReconnectToken(CancellationToken.None);
                });

                var suppressTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    raceSession.MarkSuppressedAndCancel();
                });

                await Task.WhenAll(resetTask, suppressTask);
                Assert.True(observedToken.IsCancellationRequested);
            }

            // Racing ResetReconnectToken against CancelPendingReconnect/DeactivateAndCancel must never throw ObjectDisposedException.
            for (int i = 0; i < 400; i++)
            {
                var raceSession = new AutoReconnectService.ReconnectSession
                {
                    Account = new Account { Username = $"RaceCancelUser_{i}", UserID = 83000 + i }
                };

                using var barrier = new Barrier(2);
                CancellationToken observedToken = CancellationToken.None;

                var resetTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    observedToken = raceSession.ResetReconnectToken(CancellationToken.None);
                });

                var cancelTask = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    if ((i & 1) == 0)
                        raceSession.CancelPendingReconnect();
                    else
                        raceSession.DeactivateAndCancel();
                });

                await Task.WhenAll(resetTask, cancelTask);
                Assert.True(observedToken.CanBeCanceled);
            }
        }
    }
}
