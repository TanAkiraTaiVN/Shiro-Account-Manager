#nullable enable
using System;
using System.Collections.Generic;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class LaunchCoordinatorHelpersBehaviorTests
    {
        [Fact]
        public void GetSmartLaunchMinDelayMs_NoJitter_CalculatesExactMilliseconds()
        {
            // Clamped to min 1 second
            int delay1 = LaunchCoordinator.GetSmartLaunchMinDelayMs(configuredSecondsOverride: 0, includeJitter: false);
            Assert.Equal(1000, delay1);

            // Normal value 5 seconds
            int delay5 = LaunchCoordinator.GetSmartLaunchMinDelayMs(configuredSecondsOverride: 5, includeJitter: false);
            Assert.Equal(5000, delay5);

            // Clamped to max 60 seconds
            int delay75 = LaunchCoordinator.GetSmartLaunchMinDelayMs(configuredSecondsOverride: 75, includeJitter: false);
            Assert.Equal(60000, delay75);
        }

        [Fact]
        public void GetSmartLaunchMinDelayMs_WithJitter_AddsPositiveJitter()
        {
            int delayWithJitter = LaunchCoordinator.GetSmartLaunchMinDelayMs(configuredSecondsOverride: 10, includeJitter: true);
            Assert.InRange(delayWithJitter, 10000, 11500);
        }

        [Fact]
        public void AnalyzeClientLogLines_NullOrEmptyLines_ReturnsPending()
        {
            Assert.Equal(ClientGameJoinState.Pending, LaunchCoordinator.AnalyzeClientLogLines(null!));
            Assert.Equal(ClientGameJoinState.Pending, LaunchCoordinator.AnalyzeClientLogLines(Array.Empty<string>()));
            Assert.Equal(ClientGameJoinState.Pending, LaunchCoordinator.AnalyzeClientLogLines(new[] { "", "   ", null! }));
        }

        [Fact]
        public void AnalyzeClientLogLines_JoinedGameLine_ReturnsJoinedGame()
        {
            var lines = new[]
            {
                "Some irrelevant log line",
                "Connected to server at 127.0.0.1:50000"
            };

            var state = LaunchCoordinator.AnalyzeClientLogLines(lines);
            Assert.Equal(ClientGameJoinState.JoinedGame, state);
        }

        [Fact]
        public void AnalyzeClientLogLines_ChallengedLine_ReturnsChallenged()
        {
            var lines = new[]
            {
                "[FLog::Network] Client requested game join",
                "challengedByGcs detected"
            };

            var state = LaunchCoordinator.AnalyzeClientLogLines(lines);
            Assert.Equal(ClientGameJoinState.Challenged, state);
        }

        [Fact]
        public void AnalyzeClientLogLines_ChallengeAbandoned_ReturnsChallengeFailedToLuaApp()
        {
            var lines = new[]
            {
                "challengedByGcs detected",
                "{\"feature\":\"challengeAbandoned\"}"
            };

            var state = LaunchCoordinator.AnalyzeClientLogLines(lines);
            Assert.Equal(ClientGameJoinState.ChallengeFailedToLuaApp, state);
        }

        [Fact]
        public void AnalyzeClientLogLines_ChallengeCompletedThenJoined_ReturnsJoinedGame()
        {
            var lines = new[]
            {
                "challengedByGcs detected",
                "{\"feature\":\"challengeCompleted\"}",
                "Connected to server at 127.0.0.1:50000"
            };

            var state = LaunchCoordinator.AnalyzeClientLogLines(lines);
            Assert.Equal(ClientGameJoinState.JoinedGame, state);
        }

        [Fact]
        public void RequestGlobalCancel_InvokesSubscribersWithoutThrowing()
        {
            bool cancelFired = false;
            Action handler = () => cancelFired = true;

            LaunchCoordinator.GlobalCancelRequested += handler;
            try
            {
                LaunchCoordinator.RequestGlobalCancel();
                Assert.True(cancelFired);
            }
            finally
            {
                LaunchCoordinator.GlobalCancelRequested -= handler;
            }
        }

        [Fact]
        public void TryFindProcessPlayerLogPath_InvalidPid_ReturnsNull()
        {
            Assert.Null(LaunchCoordinator.TryFindProcessPlayerLogPath(-1, null));
            Assert.Null(LaunchCoordinator.TryFindProcessPlayerLogPath(0, "tracker123"));
            Assert.Null(LaunchCoordinator.TryFindProcessPlayerLogPath(99999999, "tracker123"));
        }

        [Fact]
        public void InspectProcessGameJoinState_InvalidOrDeadPid_ReturnsProcessExited()
        {
            string? cachedPath = null;
            var stateNegative = LaunchCoordinator.InspectProcessGameJoinState(-1, null, ref cachedPath);
            Assert.Equal(ClientGameJoinState.ProcessExited, stateNegative);

            var stateDead = LaunchCoordinator.InspectProcessGameJoinState(99999999, null, ref cachedPath);
            Assert.Equal(ClientGameJoinState.ProcessExited, stateDead);
        }

        [Fact]
        public void InspectProcessGameJoinState_ValidProcessWithTemporaryLogFile_ParsesStateProperly()
        {
            int currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            string tempLogFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"shiro_test_log_{Guid.NewGuid():N}.log");

            try
            {
                System.IO.File.WriteAllLines(tempLogFile, new[]
                {
                    "2026-11-04T10:00:00Z [FLog::Network] Client requested game join",
                    "2026-11-04T10:00:01Z Connected to server at 127.0.0.1:60000"
                });

                string? cachedPath = tempLogFile;
                var state = LaunchCoordinator.InspectProcessGameJoinState(currentPid, null, ref cachedPath);
                Assert.Equal(ClientGameJoinState.JoinedGame, state);
                Assert.Equal(tempLogFile, cachedPath);
            }
            finally
            {
                if (System.IO.File.Exists(tempLogFile))
                    System.IO.File.Delete(tempLogFile);
            }
        }

        [Fact]
        public void InspectProcessGameJoinState_ValidProcessWithChallengedLogFile_ParsesChallenged()
        {
            int currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            string tempLogFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"shiro_test_log_{Guid.NewGuid():N}.log");

            try
            {
                System.IO.File.WriteAllLines(tempLogFile, new[]
                {
                    "[FLog::Network] Client requested game join",
                    "challengedByGcs detected on channel"
                });

                string? cachedPath = tempLogFile;
                var state = LaunchCoordinator.InspectProcessGameJoinState(currentPid, null, ref cachedPath);
                Assert.Equal(ClientGameJoinState.Challenged, state);
            }
            finally
            {
                if (System.IO.File.Exists(tempLogFile))
                    System.IO.File.Delete(tempLogFile);
            }
        }
    }
}
