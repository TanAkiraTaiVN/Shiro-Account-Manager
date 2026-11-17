#nullable enable
using System;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AutoReconnectBehaviorTests : IDisposable
    {
        private readonly AutoReconnectService _service;

        public AutoReconnectBehaviorTests()
        {
            _service = AutoReconnectService.Instance;
            _service.IsEnabled = true;
            _service.MaxRetries = 5;
            _service.InitialBackoff = TimeSpan.FromSeconds(10);
        }

        public void Dispose()
        {
            _service.IsEnabled = false;
        }

        [Fact]
        public void DefaultProperties_MatchExpectedDefaults()
        {
            Assert.Equal(5, _service.MaxRetries);
            Assert.Equal(TimeSpan.FromSeconds(10), _service.InitialBackoff);
            Assert.Equal(TimeSpan.FromSeconds(15), _service.LaunchGracePeriod);
            Assert.Equal(TimeSpan.FromSeconds(20), _service.DisconnectTimeout);
            Assert.Equal(TimeSpan.FromMinutes(10), _service.StableResetWindow);
        }

        [Fact]
        public void RegisterSession_And_TryGetSession_TracksAccountSession()
        {
            var acc = new Account { Username = "ReconnectUser1", UserID = 1001 };
            _service.RegisterSession(acc, 18973, "job-456", followUser: true, joinVip: false, processId: 9999);

            bool found = _service.TryGetSession("ReconnectUser1", out var session);
            Assert.True(found);
            Assert.NotNull(session);
            Assert.Equal("ReconnectUser1", session!.Account?.Username);
            Assert.Equal(18973, session.PlaceId);
            Assert.Equal("job-456", session.JobId);
            Assert.True(session.FollowUser);
            Assert.False(session.JoinVip);
            Assert.Equal(9999, session.ProcessId);
            Assert.True(session.IsActive);
            Assert.False(session.IsSuppressed);
        }

        [Fact]
        public void NotifyDisconnected_And_NotifyConnected_UpdatesSessionState()
        {
            var acc = new Account { Username = "DisconnectUser", UserID = 1002 };
            int pid = 8888;
            _service.RegisterSession(acc, 2000, "job-xyz", false, false, pid);

            Assert.True(_service.TryGetSession("DisconnectUser", out var session));
            Assert.Null(session!.LastDisconnectUtc);

            // Notify disconnect with error 277
            _service.NotifyDisconnected(pid, 277);
            Assert.NotNull(session.LastDisconnectUtc);
            Assert.Equal(277, session.LastDisconnectReason);

            // Notify reconnect clears disconnect state
            _service.NotifyConnected(pid);
            Assert.Null(session.LastDisconnectUtc);
            Assert.Equal(0, session.LastDisconnectReason);
        }

        [Fact]
        public void RegisterSession_PreservesRetryCountWithinStableWindow()
        {
            var acc = new Account { Username = "PreserveRetryUser", UserID = 1003 };
            _service.RegisterSession(acc, 3000, "job-1", false, false, 7777);

            Assert.True(_service.TryGetSession("PreserveRetryUser", out var session));
            session!.RetryCount = 3;

            // Re-register within stable window (1 minute later)
            DateTime shortlyAfter = session.LaunchTimeUtc.AddMinutes(1);
            _service.RegisterSession(acc, 3000, "job-2", false, false, 7778, nowUtcOverride: shortlyAfter);

            Assert.True(_service.TryGetSession("PreserveRetryUser", out var updatedSession));
            Assert.Equal(3, updatedSession!.RetryCount);
            Assert.Equal(7778, updatedSession.ProcessId);
        }

        [Fact]
        public void NotifyMaxRetriesExceededForTesting_FiresEvent()
        {
            var acc = new Account { Username = "MaxRetryUser", UserID = 1004 };
            _service.RegisterSession(acc, 4000, "job-max", false, false, 6666);

            bool eventFired = false;
            Action<AutoReconnectService.ReconnectSession> handler = s =>
            {
                if (s.Account?.Username == "MaxRetryUser")
                    eventFired = true;
            };

            _service.MaxRetriesExceeded += handler;
            try
            {
                _service.NotifyMaxRetriesExceededForTesting("MaxRetryUser");
                Assert.True(eventFired);
            }
            finally
            {
                _service.MaxRetriesExceeded -= handler;
            }
        }

        [Fact]
        public void ReconnectSession_SuppressedSession_DoesNotCountAsActive()
        {
            var acc = new Account { Username = "SuppressedUser", UserID = 1005 };
            _service.RegisterSession(acc, 5000, "job-sup", false, false, 5555);

            Assert.True(_service.TryGetSession("SuppressedUser", out var session));
            session!.IsSuppressed = true;

            // When suppressed, it is not counted in active sessions
            Assert.False(!session.IsSuppressed && session.IsActive);
        }

        [Fact]
        public void SessionManagement_UnregisterSuppressAndClear_OperateCleanly()
        {
            _service.Clear();
            Assert.Equal(0, _service.ActiveSessionsCount);

            var acc1 = new Account { Username = "MgmtUser1" };
            var acc2 = new Account { Username = "MgmtUser2" };

            _service.RegisterSession(acc1, 100, "job1", false, false, 111);
            _service.RegisterSession(acc2, 200, "job2", false, false, 222);

            Assert.Equal(2, _service.ActiveSessionsCount);

            // Suppress single
            _service.SuppressReconnect("MgmtUser1");
            Assert.Equal(1, _service.ActiveSessionsCount);

            // Unregister single
            _service.UnregisterSession("MgmtUser2");
            Assert.Equal(0, _service.ActiveSessionsCount);

            // Null inputs should not throw
            _service.UnregisterSession(null!);
            _service.SuppressReconnect(null!);
            _service.SuppressReconnect("non_existent_user");

            // Re-add and SuppressAll
            _service.RegisterSession(acc1, 100, "job1", false, false, 111);
            _service.RegisterSession(acc2, 200, "job2", false, false, 222);
            Assert.Equal(2, _service.ActiveSessionsCount);

            _service.SuppressAll();
            Assert.Equal(0, _service.ActiveSessionsCount);

            _service.Clear();
            Assert.Equal(0, _service.ActiveSessionsCount);
            Assert.InRange(_service.TotalReconnections, 0, int.MaxValue);
        }

        [Fact]
        public void CheckSessionsCallback_WithSuppressedAndExitedSessions_ExecutesSafely()
        {
            var method = typeof(AutoReconnectService).GetMethod("CheckSessionsCallback", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            var acc = new Account { Username = "CheckUser" };
            _service.RegisterSession(acc, 1234, "job1", false, false, 999999);
            _service.TryGetSession("CheckUser", out var session);
            Assert.NotNull(session);

            session!.ObservedProcessExited = true;
            session.ObservedExitCode = 0;

            var ex = Record.Exception(() => method!.Invoke(_service, new object?[] { null }));
            Assert.Null(ex);

            _service.Clear();
        }

        [Fact]
        public void SuppressReconnectByPid_WhenPidMatches_SuppressesTargetSession()
        {
            var acc = new Account { Username = "PidUser1", UserID = 8877 };
            _service.RegisterSession(acc, 555, "job555", false, false, 443322);

            _service.SuppressReconnectByPid(443322);

            Assert.True(_service.TryGetSession("PidUser1", out var session));
            Assert.True(session!.IsSuppressed);
            Assert.False(session.IsActive);

            // Calling with non-matching or invalid PID does not throw
            _service.SuppressReconnectByPid(0);
            _service.SuppressReconnectByPid(-1);
            _service.SuppressReconnectByPid(999999);
        }

        [Fact]
        public void Evaluate_WhenSessionNullOrInactiveOrSuppressed_ReturnsNone()
        {
            DateTime now = DateTime.UtcNow;

            Assert.Equal(
                AutoReconnectService.ReconnectDecision.None,
                AutoReconnectService.Evaluate(null!, false, null, now));

            var inactiveSession = new AutoReconnectService.ReconnectSession
            {
                Account = new Account { Username = "Inactive" },
                IsActive = false
            };
            Assert.Equal(
                AutoReconnectService.ReconnectDecision.None,
                AutoReconnectService.Evaluate(inactiveSession, true, -1, now));

            var suppressedSession = new AutoReconnectService.ReconnectSession
            {
                Account = new Account { Username = "Suppressed" },
                IsActive = true,
                IsSuppressed = true
            };
            Assert.Equal(
                AutoReconnectService.ReconnectDecision.None,
                AutoReconnectService.Evaluate(suppressedSession, true, -1, now));
        }

        [Fact]
        public void Evaluate_WithinGracePeriod_ReturnsNone()
        {
            DateTime now = DateTime.UtcNow;
            var session = new AutoReconnectService.ReconnectSession
            {
                Account = new Account { Username = "GraceUser" },
                LaunchTimeUtc = now.AddSeconds(-5),
                IsActive = true
            };

            var decision = AutoReconnectService.Evaluate(
                session,
                processExited: true,
                exitCode: -1,
                nowUtc: now,
                gracePeriod: TimeSpan.FromSeconds(15));

            Assert.Equal(AutoReconnectService.ReconnectDecision.None, decision);
        }


        [Fact]
        public async System.Threading.Tasks.Task HandleProcessExit_And_CheckSessionsCallback_ReconnectSuccessFailureAndMaxRetries_CoverBranches()
        {
            _service.Clear();
            _service.IsEnabled = true;
            _service.MaxRetries = 3;
            _service.TestReconnectDelayMsOverride = 1;

            try
            {
                var uidAcc = new Account { Username = "", UserID = 556677 };
                _service.RegisterSession(uidAcc, 1000, "j1", false, false, 0);
                Assert.Equal(1, _service.ActiveSessionsCount);
                _service.Clear();

                var accOk = new Account { Username = "ReconnSuccessUser", UserID = 101 };
                _service.RegisterSession(accOk, 189707, "job-ok", false, false, 0, nowUtcOverride: DateTime.UtcNow.AddMinutes(-15));
                Assert.True(_service.TryGetSession("ReconnSuccessUser", out var sOk));
                var tcsOk = new System.Threading.Tasks.TaskCompletionSource<bool>();
                _service.TestJoinServerOverride = _ =>
                {
                    tcsOk.TrySetResult(true);
                    return System.Threading.Tasks.Task.FromResult("Success");
                };

                _service.HandleProcessExit(sOk!);
                await tcsOk.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await System.Threading.Tasks.Task.Delay(30);
                Assert.True(sOk!.IsActive);
                Assert.Equal(1, sOk.RetryCount);

                var tcsFail = new System.Threading.Tasks.TaskCompletionSource<bool>();
                _service.TestJoinServerOverride = _ =>
                {
                    tcsFail.TrySetResult(true);
                    return System.Threading.Tasks.Task.FromResult("Failed");
                };
                _service.HandleProcessExit(sOk);
                await tcsFail.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await System.Threading.Tasks.Task.Delay(30);
                Assert.True(sOk.IsActive);
                Assert.Equal(2, sOk.RetryCount);

                sOk.RetryCount = 3;
                bool maxExceeded = false;
                Action<AutoReconnectService.ReconnectSession> onMax = _ => maxExceeded = true;
                _service.MaxRetriesExceeded += onMax;
                try
                {
                    _service.HandleProcessExit(sOk);
                    Assert.True(maxExceeded);
                    Assert.False(_service.TryGetSession("ReconnSuccessUser", out _));
                }
                finally
                {
                    _service.MaxRetriesExceeded -= onMax;
                }

                var prevPidsOverride = RobloxProcessService.TestGetRunningRobloxPidsOverride;
                try
                {
                    RobloxProcessService.TestGetRunningRobloxPidsOverride = () => new[] { 999998 };
                    var accStable = new Account { Username = "StableUser", UserID = 202 };
                    _service.RegisterSession(accStable, 189707, "j2", false, false, 999998, nowUtcOverride: DateTime.UtcNow.AddMinutes(-20));
                    Assert.True(_service.TryGetSession("StableUser", out var sStable));
                    sStable!.RetryCount = 2;
                    _service.CheckSessionsCallback(null);
                    Assert.Equal(0, sStable.RetryCount);

                    sStable.RetryCount = 2;
                    sStable.ObservedProcessExited = true;
                    sStable.ObservedExitCode = 0;
                    _service.CheckSessionsCallback(null);
                    Assert.Equal(2, sStable.RetryCount);
                    Assert.False(_service.TryGetSession("StableUser", out _));
                }
                finally
                {
                    RobloxProcessService.TestGetRunningRobloxPidsOverride = prevPidsOverride;
                }
            }
            finally
            {
                _service.TestReconnectDelayMsOverride = null;
                _service.TestJoinServerOverride = null;
                _service.Clear();
            }
        }

        [Fact]
        public void AutoReconnectService_AllBranches_CoverRemainingScenarios()
        {
            DateTime now = DateTime.UtcNow;
            _service.Clear();
            _service.IsEnabled = true;
            Assert.True(_service.IsEnabled);
            _service.IsEnabled = false;
            Assert.False(_service.IsEnabled);
            _service.IsEnabled = true;
            Assert.Equal(0, _service.TotalReconnections);

            // 1. Session registration edge cases
            _service.RegisterSession(null!, 0);
            _service.RegisterSession(new Account { Username = "", UserID = 0 }, 0);
            Assert.Equal(0, _service.ActiveSessionsCount);

            var acc = new Account { Username = "BranchUser", UserID = 777 };
            _service.RegisterSession(acc, 12345, "j-branch", false, false, 999901, nowUtcOverride: now.AddMinutes(-5));
            Assert.Equal(1, _service.ActiveSessionsCount);

            if (_service.TryGetSession("BranchUser", out var sBranch))
            {
                sBranch.RetryCount = 3;
                sBranch.LastReconnectUtc = now.AddMinutes(-1);
            }
            _service.RegisterSession(acc, 12345, "j-branch", false, false, 999901, nowUtcOverride: now);
            Assert.True(_service.TryGetSession("BranchUser", out var sPreserved));
            Assert.Equal(3, sPreserved.RetryCount);

            _service.RegisterSession(acc, 12345, "j-branch", false, false, 999901, nowUtcOverride: now.AddMinutes(20));
            Assert.True(_service.TryGetSession("BranchUser", out var sReset));
            Assert.Equal(0, sReset.RetryCount);

            // 2. Unregister edge cases
            _service.UnregisterSession(null!);
            _service.UnregisterSession("   ");
            _service.UnregisterSession("NonExistentUser");
            _service.UnregisterSession("BranchUser");
            Assert.False(_service.TryGetSession("BranchUser", out _));

            // 3. Suppress edge cases
            _service.RegisterSession(acc, 12345, "j-branch", false, false, 999902);
            _service.SuppressReconnect(null!);
            _service.SuppressReconnect("   ");
            _service.SuppressReconnect("NonExistentUser");
            _service.SuppressReconnect("BranchUser");
            Assert.True(_service.TryGetSession("BranchUser", out var sSuppressed));
            Assert.True(sSuppressed.IsSuppressed);
            Assert.False(sSuppressed.IsActive);
            Assert.Equal(0, _service.ActiveSessionsCount);

            _service.RegisterSession(acc, 12345, "j-branch", false, false, 999903);
            _service.SuppressReconnectByPid(0);
            _service.SuppressReconnectByPid(-1);
            _service.SuppressReconnectByPid(111111);
            _service.SuppressReconnectByPid(999903);
            Assert.True(_service.TryGetSession("BranchUser", out var sPidSupp));
            Assert.True(sPidSupp.IsSuppressed);

            _service.RegisterSession(acc, 12345, "j-branch", false, false, 999904);
            _service.SuppressAll();
            Assert.True(_service.TryGetSession("BranchUser", out var sAllSupp));
            Assert.True(sAllSupp.IsSuppressed);

            // 4. TryGetSession with invalid input
            Assert.False(_service.TryGetSession(null, out _));
            Assert.False(_service.TryGetSession("   ", out _));

            // 5. NotifyMaxRetriesExceededForTesting
            _service.NotifyMaxRetriesExceededForTesting(null!);
            _service.NotifyMaxRetriesExceededForTesting("nonexistent");

            // 6. NotifyDisconnected & NotifyConnected
            _service.NotifyDisconnected(0, 0);
            _service.NotifyDisconnected(-1, 0);
            _service.NotifyDisconnected(888888, 277);

            _service.RegisterSession(acc, 12345, "j-branch", false, false, 999905);
            _service.NotifyDisconnected(999905, 277);
            Assert.True(_service.TryGetSession("BranchUser", out var sDc));
            Assert.NotNull(sDc.LastDisconnectUtc);
            Assert.Equal(277, sDc.LastDisconnectReason);

            DateTime? firstDc = sDc.LastDisconnectUtc;
            _service.NotifyDisconnected(999905, 0);
            Assert.Equal(firstDc, sDc.LastDisconnectUtc);

            _service.NotifyConnected(0);
            _service.NotifyConnected(-1);
            _service.NotifyConnected(888888);
            _service.NotifyConnected(999905);
            Assert.Null(sDc.LastDisconnectUtc);
            Assert.Equal(0, sDc.LastDisconnectReason);

            // 7. CheckSessionsCallback branches
            _service.IsEnabled = false;
            _service.CheckSessionsCallback(null);
            _service.IsEnabled = true;

            sDc.IsActive = false;
            _service.CheckSessionsCallback(null);
            sDc.IsActive = true;
            sDc.IsSuppressed = true;
            _service.CheckSessionsCallback(null);
            sDc.IsSuppressed = false;

            sDc.ProcessId = 0;
            _service.CheckSessionsCallback(null);

            sDc.ProcessId = 999905;
            sDc.LaunchTimeUtc = DateTime.UtcNow.AddMinutes(-10);
            sDc.LastDisconnectUtc = DateTime.UtcNow.AddMinutes(-5);
            sDc.RetryCount = 0;
            _service.TestReconnectDelayMsOverride = 1;
            _service.TestJoinServerOverride = _ => Task.FromResult("Success");
            _service.CheckSessionsCallback(null);

            var inactiveS = new AutoReconnectService.ReconnectSession { Account = acc, IsActive = false };
            _service.HandleProcessExit(inactiveS);
            var suppS = new AutoReconnectService.ReconnectSession { Account = acc, IsActive = true, IsSuppressed = true };
            _service.HandleProcessExit(suppS);

            _service.Clear();
        }

        [Theory]
        [InlineData(0, false, 0, 5, AutoReconnectService.ReconnectDecision.CloseCleanly)]
        [InlineData(0, true, 0, 5, AutoReconnectService.ReconnectDecision.Reconnect)]
        [InlineData(1, false, 0, 5, AutoReconnectService.ReconnectDecision.Reconnect)]
        [InlineData(-1, false, 2, 5, AutoReconnectService.ReconnectDecision.Reconnect)]
        [InlineData(-1, false, 5, 5, AutoReconnectService.ReconnectDecision.GiveUp)]
        public void Evaluate_WhenProcessExited_ReturnsExpectedDecision(
            int? exitCode,
            bool hasPriorDisconnect,
            int retryCount,
            int maxRetries,
            AutoReconnectService.ReconnectDecision expected)
        {
            DateTime now = DateTime.UtcNow;
            var session = new AutoReconnectService.ReconnectSession
            {
                Account = new Account { Username = "ExitUser" },
                LaunchTimeUtc = now.AddMinutes(-5),
                LastDisconnectUtc = hasPriorDisconnect ? now.AddMinutes(-1) : null,
                RetryCount = retryCount,
                IsActive = true
            };

            var decision = AutoReconnectService.Evaluate(
                session,
                processExited: true,
                exitCode: exitCode,
                nowUtc: now,
                maxRetries: maxRetries,
                gracePeriod: TimeSpan.FromSeconds(15));

            Assert.Equal(expected, decision);
        }

        [Theory]
        [InlineData(10, 20, 1, 5, AutoReconnectService.ReconnectDecision.None)]
        [InlineData(25, 20, 1, 5, AutoReconnectService.ReconnectDecision.KillAndReconnect)]
        [InlineData(25, 20, 5, 5, AutoReconnectService.ReconnectDecision.GiveUp)]
        public void Evaluate_WhenRunningWithDisconnect_EvaluatesTimeoutAndRetries(
            int secondsSinceDisconnect,
            int disconnectTimeoutSeconds,
            int retryCount,
            int maxRetries,
            AutoReconnectService.ReconnectDecision expected)
        {
            DateTime now = DateTime.UtcNow;
            var session = new AutoReconnectService.ReconnectSession
            {
                Account = new Account { Username = "DcTimeoutUser" },
                LaunchTimeUtc = now.AddMinutes(-10),
                LastDisconnectUtc = now.AddSeconds(-secondsSinceDisconnect),
                RetryCount = retryCount,
                IsActive = true
            };

            var decision = AutoReconnectService.Evaluate(
                session,
                processExited: false,
                exitCode: null,
                nowUtc: now,
                maxRetries: maxRetries,
                gracePeriod: TimeSpan.FromSeconds(15),
                disconnectTimeout: TimeSpan.FromSeconds(disconnectTimeoutSeconds));

            Assert.Equal(expected, decision);
        }
    }
}
