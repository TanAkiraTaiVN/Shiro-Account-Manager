#nullable enable
using Shiro.Classes;
using Xunit;

namespace Shiro.Tests
{
    public class LogBindingAndWatcherTests
    {
        [Theory]
        [InlineData("2026-11-04T10:00:00.000Z,0.1,1234,6 [FLog::Output] Command line: RobloxPlayerBeta.exe --app -t abc -b 112233445566", "112233445566", LogHeaderMatchResult.Matched)]
        [InlineData("2026-11-04T10:00:00.000Z,0.1,1234,6 [FLog::Output] browserTrackerId=112233445566", "112233445566", LogHeaderMatchResult.Matched)]
        [InlineData("2026-11-04T10:00:00.000Z,0.1,1234,6 [FLog::Output] Command line: RobloxPlayerBeta.exe --app -t abc -b 998877665544", "112233445566", LogHeaderMatchResult.Mismatched)]
        [InlineData("2026-11-04T10:00:00.000Z,0.1,1234,6 [FLog::Output] Starting Roblox client...", "112233445566", LogHeaderMatchResult.Inconclusive)]
        [InlineData("", "112233445566", LogHeaderMatchResult.Inconclusive)]
        [InlineData("any header", "", LogHeaderMatchResult.Inconclusive)]
        public void EvaluateLogHeaderForTracker_ClassifiesTrackerOwnershipDeterministically(
            string headerSample,
            string expectedTrackerId,
            LogHeaderMatchResult expected)
        {
            var actual = RobloxProcess.EvaluateLogHeaderForTracker(headerSample, expectedTrackerId);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void AdvanceLogSessionState_TracksConnect_DataModelInit_Pause_AndDisconnectReason()
        {
            var state = new RobloxLogSessionState();

            Assert.Equal(
                RobloxLogLineOutcome.None,
                RobloxProcess.AdvanceLogSessionState(string.Empty, ref state, verifyDataModel: true, exitOnBeta: false));

            Assert.Equal(
                RobloxLogLineOutcome.None,
                RobloxProcess.AdvanceLogSessionState("2026-11-08T10:00:00.100Z [FLog::Output] challengedByGcs", ref state, verifyDataModel: true, exitOnBeta: false));
            Assert.True(state.HasGameJoinChallenge);

            Assert.Equal(
                RobloxLogLineOutcome.None,
                RobloxProcess.AdvanceLogSessionState("2026-11-08T10:00:00.200Z [FLog::Challenge] \"feature\":\"challengeCompleted\"", ref state, verifyDataModel: true, exitOnBeta: false));
            Assert.False(state.HasGameJoinChallenge);

            RobloxProcess.AdvanceLogSessionState("2026-11-08T10:00:00.300Z [FLog::Output] challengedByGcs", ref state, verifyDataModel: true, exitOnBeta: false);
            RobloxProcess.AdvanceLogSessionState("2026-11-08T10:00:00.400Z [FLog::NewWebView2Browser] Security cookie value changed, updating HTTP layer cookie", ref state, verifyDataModel: true, exitOnBeta: false);
            Assert.True(state.HasGameJoinChallenge);
            Assert.True(state.HasSecurityCookieChanged);

            var connectOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:00:01.000Z [FLog::Output] ! Joining game '11111111-2222-3333-4444-555555555555' place 1818 at 128.116.1.1",
                ref state,
                verifyDataModel: true,
                exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.Connected, connectOutcome);
            Assert.True(state.IsConnected);
            Assert.False(state.HasGameJoinChallenge);
            Assert.False(state.HasSecurityCookieChanged);

            var dmInitOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:00:02.000Z [FLog::UgcExperienceController] UgcExperienceController: join: initialized dm 0x27bce3f5100",
                ref state,
                verifyDataModel: true,
                exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.DataModelInitialized, dmInitOutcome);
            Assert.Equal(0x27bce3f5100L, state.CurrentDataModel);
            Assert.False(state.IsDMPaused);

            var duplicateInitOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:00:02.100Z [FLog::UgcExperienceController] UgcExperienceController: join: initialized dm 0x9999",
                ref state,
                verifyDataModel: true,
                exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.None, duplicateInitOutcome);
            Assert.Equal(0x27bce3f5100L, state.CurrentDataModel);

            var modernPauseOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:00:03.000Z [FLog::SurfaceController] SurfaceController[_:1]::pause destroyView:true.",
                ref state,
                verifyDataModel: true,
                exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.DataModelPaused, modernPauseOutcome);
            Assert.True(state.IsDMPaused);

            var stop2ResetOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:00:03.500Z [FLog::SurfaceController] SurfaceController[_:1]::stop",
                ref state,
                verifyDataModel: true,
                exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.DataModelReset, stop2ResetOutcome);
            Assert.Equal(-1L, state.CurrentDataModel);

            var legacyInitOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:00:04.000Z [FLog::UGCGameController] UGCGameController, initialized DataModel(1A2B3C)",
                ref state,
                verifyDataModel: true,
                exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.DataModelInitialized, legacyInitOutcome);
            Assert.Equal(0x1A2B3CL, state.CurrentDataModel);
            Assert.False(state.IsDMPaused);

            Assert.Equal(
                RobloxLogLineOutcome.None,
                RobloxProcess.AdvanceLogSessionState(
                    "2026-11-08T10:00:04.200Z [FLog::SurfaceController] SurfaceController[_:1]::pause dataModel(FFFF), view(v1), destroyView:false.",
                    ref state,
                    verifyDataModel: true,
                    exitOnBeta: false));
            Assert.False(state.IsDMPaused);

            var legacyPauseOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:00:04.500Z [FLog::SurfaceController] SurfaceController[_:1]::pause dataModel(1A2B3C), view(v1), destroyView:false.",
                ref state,
                verifyDataModel: true,
                exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.DataModelPaused, legacyPauseOutcome);
            Assert.True(state.IsDMPaused);

            var disconnectOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:00:05.000Z [FLog::Network] Sending disconnect with reason: 277",
                ref state,
                verifyDataModel: true,
                exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.Disconnected, disconnectOutcome);
            Assert.False(state.IsConnected);
            Assert.Equal(277, state.LastDisconnectReason);
        }

        [Fact]
        public void AdvanceLogSessionState_DisconnectAndConnectOutcomes_DriveAutoReconnectSessionState()
        {
            var state = new RobloxLogSessionState();
            var disconnectOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:00:05.000Z [FLog::Network] Sending disconnect with reason: 277",
                ref state,
                verifyDataModel: true,
                exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.Disconnected, disconnectOutcome);

            using var reconnectSvc = new Shiro.Services.AutoReconnectService();
            var trackedAccount = new Account { Username = "LogStateWatcherUser", UserID = 777001 };
            reconnectSvc.RegisterSession(trackedAccount, placeId: 1818, processId: 0);
            Assert.True(reconnectSvc.TryGetSession("LogStateWatcherUser", out var trackedSession));
            trackedSession!.ProcessId = 42424;

            reconnectSvc.NotifyDisconnected(0);
            reconnectSvc.NotifyConnected(0);
            reconnectSvc.NotifyDisconnected(42424, reason: 0);
            Assert.NotNull(trackedSession.LastDisconnectUtc);
            reconnectSvc.NotifyDisconnected(42424, reason: state.LastDisconnectReason);
            Assert.Equal(277, trackedSession.LastDisconnectReason);

            var connectOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:00:06.000Z Connected to server at 128.116.1.10:53640",
                ref state,
                verifyDataModel: true,
                exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.Connected, connectOutcome);

            reconnectSvc.NotifyConnected(42424);
            Assert.Null(trackedSession.LastDisconnectUtc);
            Assert.Equal(0, trackedSession.LastDisconnectReason);
            reconnectSvc.NotifyConnected(42424);

            reconnectSvc.Dispose();
            reconnectSvc.NotifyDisconnected(42424, state.LastDisconnectReason);
            reconnectSvc.NotifyConnected(42424);
            reconnectSvc.Dispose();
        }

        [Fact]
        public void AdvanceLogSessionState_TriggersKillChallengedOrHijackedClient_WhenCookieChangedAndReturningToLuaApp()
        {
            var hijackedState = new RobloxLogSessionState();
            RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:01:00.000Z Connected to server at 128.116.1.10:53640",
                ref hijackedState,
                verifyDataModel: true,
                exitOnBeta: false);
            RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:01:01.000Z [FLog::UgcExperienceController] UgcExperienceController: join: initialized dm 0x102030",
                ref hijackedState,
                verifyDataModel: true,
                exitOnBeta: false);
            RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:01:02.000Z [FLog::NewWebView2Browser] Security cookie value changed, updating HTTP layer cookie",
                ref hijackedState,
                verifyDataModel: true,
                exitOnBeta: false);

            var hijackedOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:01:03.000Z [FLog::SingleSurfaceApp] setStage: (stage:LuaApp)",
                ref hijackedState,
                verifyDataModel: true,
                exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.KillChallengedOrHijackedClient, hijackedOutcome);
            Assert.Equal(-1L, hijackedState.CurrentDataModel);

            var challengedState = new RobloxLogSessionState();
            RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:02:00.000Z [FLog::Output] challengedByGcs",
                ref challengedState,
                verifyDataModel: true,
                exitOnBeta: false);

            var challengedOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:02:01.000Z [FLog::SingleSurfaceApp] returnToLuaApp: (stage:UGCGame)",
                ref challengedState,
                verifyDataModel: true,
                exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.KillChallengedOrHijackedClient, challengedOutcome);
            Assert.Equal(-1L, challengedState.CurrentDataModel);

            var idleCookieChangeState = new RobloxLogSessionState();
            RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:02:30.000Z [FLog::NewWebView2Browser] Security cookie value changed, updating HTTP layer cookie",
                ref idleCookieChangeState,
                verifyDataModel: true,
                exitOnBeta: false);
            Assert.False(idleCookieChangeState.HasGameJoinChallenge);
            Assert.Equal(0L, idleCookieChangeState.CurrentDataModel);
            Assert.True(idleCookieChangeState.HasSecurityCookieChanged);

            var verifiedIdleReturnOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:02:31.000Z [FLog::SingleSurfaceApp] returnToLuaApp: ... App has been initialized, returning from game.",
                ref idleCookieChangeState,
                verifyDataModel: true,
                exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.None, verifiedIdleReturnOutcome);
            Assert.NotEqual(RobloxLogLineOutcome.KillChallengedOrHijackedClient, verifiedIdleReturnOutcome);

            var unverifiedIdleReturnOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:02:32.000Z [FLog::SingleSurfaceApp] returnToLuaApp: ... App has been initialized, returning from game.",
                ref idleCookieChangeState,
                verifyDataModel: false,
                exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.DataModelReset, unverifiedIdleReturnOutcome);
            Assert.NotEqual(RobloxLogLineOutcome.KillChallengedOrHijackedClient, unverifiedIdleReturnOutcome);
        }

        [Fact]
        public void AdvanceLogSessionState_TriggersKillBetaHomeMenu_WhenExitOnBetaEnabledAndDataModelStopped()
        {
            var state = new RobloxLogSessionState();
            RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:03:00.000Z [FLog::UgcExperienceController] UgcExperienceController: join: initialized dm 0x55AA",
                ref state,
                verifyDataModel: true,
                exitOnBeta: true);
            RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:03:00.500Z [FLog::Output] Active gameplay heartbeat",
                ref state,
                verifyDataModel: true,
                exitOnBeta: true);

            var ignoredReturn = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:03:01.000Z [FLog::SingleSurfaceApp] returnToLuaAppInternal: ... App not yet initialized, returning from game.",
                ref state,
                verifyDataModel: true,
                exitOnBeta: true);
            Assert.Equal(RobloxLogLineOutcome.None, ignoredReturn);
            Assert.Equal(0x55AAL, state.CurrentDataModel);

            RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:03:02.000Z [FLog::UgcExperienceController] UgcExperienceController: leave",
                ref state,
                verifyDataModel: true,
                exitOnBeta: true);

            var killOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:03:03.000Z [FLog::SingleSurfaceApp] returnToLuaAppInternal: ... App has been initialized, returning from game.",
                ref state,
                verifyDataModel: true,
                exitOnBeta: true);
            Assert.Equal(RobloxLogLineOutcome.KillBetaHomeMenu, killOutcome);
            Assert.Equal(-1L, state.CurrentDataModel);

            var legacyResetState = new RobloxLogSessionState();
            RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:04:00.000Z [FLog::UGCGameController] UGCGameController, initialized DataModel(ABCDEF)",
                ref legacyResetState,
                verifyDataModel: true,
                exitOnBeta: false);
            RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:04:01.000Z [FLog::UGCGameController] UGCGameController::leave (blocking:1) dataModel(ABCDEF)",
                ref legacyResetState,
                verifyDataModel: true,
                exitOnBeta: false);

            var resetOutcome = RobloxProcess.AdvanceLogSessionState(
                "2026-11-08T10:04:02.000Z [FLog::SingleSurfaceApp] returnToLuaApp: ... App has been initialized, returning from game.",
                ref legacyResetState,
                verifyDataModel: true,
                exitOnBeta: false);
            Assert.Equal(RobloxLogLineOutcome.DataModelReset, resetOutcome);
            Assert.Equal(-1L, legacyResetState.CurrentDataModel);

            var unverifiedState = new RobloxLogSessionState();
            unverifiedState.CurrentDataModel = 0x777;
            Assert.Equal(
                RobloxLogLineOutcome.KillBetaHomeMenu,
                RobloxProcess.AdvanceLogSessionState(
                    "2026-11-08T10:05:00.000Z [FLog::SingleSurfaceApp] returnToLuaApp: ... App has been initialized, returning from game.",
                    ref unverifiedState,
                    verifyDataModel: false,
                    exitOnBeta: true));
            Assert.Equal(-1L, unverifiedState.CurrentDataModel);
        }

        [Fact]
        public void RobloxWatcher_TryClassifyClientCommandLine_And_TryValidateWindowBoundsChange_HandleEdgeCases()
        {
            Assert.False(RobloxWatcher.TryClassifyClientCommandLine(null, out string nullTracker));
            Assert.Equal(string.Empty, nullTracker);

            Assert.False(RobloxWatcher.TryClassifyClientCommandLine("   ", out string wsTracker));
            Assert.Equal(string.Empty, wsTracker);

            Assert.False(RobloxWatcher.TryClassifyClientCommandLine(
                @"\??\C:\Users\User\AppData\Local\Roblox\Versions\version-abc\RobloxCrashHandler.exe --app -t tok -b 12345678",
                out string ntPathTracker));
            Assert.Equal(string.Empty, ntPathTracker);

            Assert.False(RobloxWatcher.TryClassifyClientCommandLine(
                "\"C:\\RobloxPlayerBeta.exe\" --crashHandler",
                out string helperTracker));
            Assert.Equal(string.Empty, helperTracker);

            Assert.True(RobloxWatcher.TryClassifyClientCommandLine(
                "\"C:\\RobloxPlayerBeta.exe\" --play -t AUTH -j \"https://assetgame.roblox.com/game/PlaceLauncher.ashx\" -b 9988776655",
                out string gameTracker));
            Assert.Equal("9988776655", gameTracker);

            Assert.True(RobloxWatcher.TryClassifyClientCommandLine(
                "\"C:\\RobloxPlayerBeta.exe\" -j \"https://assetgame.roblox.com/game/PlaceLauncher.ashx\"",
                out string noTracker));
            Assert.Equal(string.Empty, noTracker);

            Assert.True(RobloxWatcher.TryClassifyClientCommandLine(
                "\"C:\\RobloxPlayerBeta.exe\" --app -b 4455667788",
                out string appTracker));
            Assert.Equal("4455667788", appTracker);

            var validRect = CreateRect(100, 120, 1100, 820);
            Assert.True(RobloxWatcher.TryValidateWindowBoundsChange(validRect, default, out int width, out int height));
            Assert.Equal(1000, width);
            Assert.Equal(700, height);

            Assert.False(RobloxWatcher.TryValidateWindowBoundsChange(validRect, validRect, out int sameW, out int sameH));
            Assert.Equal(1000, sameW);
            Assert.Equal(700, sameH);

            var resizedRect = CreateRect(100, 120, 1100, 900);
            Assert.True(RobloxWatcher.TryValidateWindowBoundsChange(resizedRect, validRect, out int resizedW, out int resizedH));
            Assert.Equal(1000, resizedW);
            Assert.Equal(780, resizedH);

            Assert.False(RobloxWatcher.TryValidateWindowBoundsChange(CreateRect(0, 0, 199, 500), default, out _, out _));
            Assert.False(RobloxWatcher.TryValidateWindowBoundsChange(CreateRect(0, 0, 500, 199), default, out _, out _));
            Assert.False(RobloxWatcher.TryValidateWindowBoundsChange(CreateRect(-32000, 100, -31000, 800), default, out _, out _));
            Assert.False(RobloxWatcher.TryValidateWindowBoundsChange(CreateRect(100, -10000, 900, -9200), default, out _, out _));
        }

        private static RobloxWatcher.RECT CreateRect(int left, int top, int right, int bottom) =>
            new() { Left = left, Top = top, Right = right, Bottom = bottom };
    }
}
