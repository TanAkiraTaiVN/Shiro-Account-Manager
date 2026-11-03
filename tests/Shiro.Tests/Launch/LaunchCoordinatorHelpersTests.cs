#nullable enable
using System;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests.Launch
{
    public sealed class LaunchCoordinatorHelpersTests
    {
        [Fact]
        public void AnalyzeClientLogLines_DetectsJoinedAndDisconnected()
        {
            var joinedLines = new[]
            {
                "2026-11-03T10:00:00.000Z,1.0,Info [FLog::Output] ! Joining game '11111111-2222-3333-4444-555555555555' place 12345 at 127.0.0.1",
                "Connected to server at 127.0.0.1:5000"
            };
            var stateJoined = LaunchCoordinator.AnalyzeClientLogLines(joinedLines);
            Assert.Equal(ClientGameJoinState.JoinedGame, stateJoined);

            var challengedLines = new[]
            {
                "2026-11-03T10:00:00.000Z,1.0,Info [FLog::GameJoin] challengedByGcs"
            };
            var stateChallenged = LaunchCoordinator.AnalyzeClientLogLines(challengedLines);
            Assert.Equal(ClientGameJoinState.Challenged, stateChallenged);

            var challengeFailedLines = new[]
            {
                "2026-11-03T10:00:00.000Z,1.0,Info [FLog::GameJoin] challengedByGcs",
                "2026-11-03T10:00:01.000Z,1.0,Info [FLog::SingleSurfaceApp] returnToLuaApp: ... App not yet initialized, returning from game."
            };
            var stateFailed = LaunchCoordinator.AnalyzeClientLogLines(challengeFailedLines);
            Assert.Equal(ClientGameJoinState.ChallengeFailedToLuaApp, stateFailed);

            var pendingLines = new[]
            {
                "2026-11-03T10:00:00.000Z,1.0,Info [FLog::Output] Startup output line"
            };
            var statePending = LaunchCoordinator.AnalyzeClientLogLines(pendingLines);
            Assert.Equal(ClientGameJoinState.Pending, statePending);
        }

        [Fact]
        public void GetSmartLaunchMinDelayMs_ClampsConfiguredSeconds()
        {
            int minDelay = LaunchCoordinator.GetSmartLaunchMinDelayMs(0, includeJitter: false);
            Assert.Equal(1000, minDelay);

            int normalDelay = LaunchCoordinator.GetSmartLaunchMinDelayMs(5, includeJitter: false);
            Assert.Equal(5000, normalDelay);

            int maxDelay = LaunchCoordinator.GetSmartLaunchMinDelayMs(99999, includeJitter: false);
            Assert.Equal(60000, maxDelay);
        }
    }
}
