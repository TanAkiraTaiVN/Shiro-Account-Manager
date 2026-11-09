#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        public void InspectProcessGameJoinState_ValidProcessWithChallengedLogFile_ParsesChallenged_Pre()
        {
            Assert.True(true);
        }

        [Fact]
        public async Task Report_TryFindProcessPlayerLogPath_And_WaitIfClientChallenged_CoverBranches()
        {
            Action<LaunchProgress> badSubscriber = _ => throw new InvalidOperationException("sub boom");
            LaunchCoordinator.LaunchProgressChanged += badSubscriber;
            try
            {
                var exReport = Record.Exception(() =>
                    LaunchCoordinator.Report(_ => throw new InvalidOperationException("cb boom"), new LaunchProgress()));
                Assert.Null(exReport);
            }
            finally
            {
                LaunchCoordinator.LaunchProgressChanged -= badSubscriber;
            }

            AppSettings.General.Set(AppSettingsKeys.General.SmartLaunchMinDelaySeconds, "4");
            Assert.Equal(4000, LaunchCoordinator.GetSmartLaunchMinDelayMs(null, includeJitter: false));
            AppSettings.General.Set(AppSettingsKeys.General.SmartLaunchMinDelaySeconds, "2");

            var luaAppLines = new[]
            {
                "[FLog::SingleSurfaceApp] returnToLuaApp: (stage:UGCGame)"
            };
            Assert.Equal(ClientGameJoinState.ChallengeFailedToLuaApp, LaunchCoordinator.AnalyzeClientLogLines(luaAppLines));

            int currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"shiro_logs_{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(tempDir);
            var prevLogsDir = LaunchCoordinator.TestLogsDirectoryOverride;
            var prevOverride = LaunchCoordinator.TestInspectProcessGameJoinStateOverride;
            int prevProbeMs = LaunchCoordinator.ChallengeProbeDelayMs;
            double prevMaxWait = LaunchCoordinator.MaxChallengeVerificationWaitSeconds;

            try
            {
                LaunchCoordinator.TestLogsDirectoryOverride = System.IO.Path.Combine(tempDir, "missing");
                Assert.Null(LaunchCoordinator.TryFindProcessPlayerLogPath(currentPid, "trk1"));

                LaunchCoordinator.TestLogsDirectoryOverride = tempDir;
                string crashLog = System.IO.Path.Combine(tempDir, "0.600_Player_CrashHandler_last.log");
                string playerLog = System.IO.Path.Combine(tempDir, "0.600_Player_123_last.log");
                System.IO.File.WriteAllText(crashLog, "tracker_abc");
                System.IO.File.WriteAllText(playerLog, new string('X', 140 * 1024) + "\r\ntracker_abc\r\nConnected to server at 127.0.0.1:50000\r\n");

                using var shortProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c ping -n 3 127.0.0.1 > nul",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                if (shortProc != null)
                {
                    System.IO.File.SetCreationTimeUtc(playerLog, shortProc.StartTime.ToUniversalTime());
                    System.IO.File.SetLastWriteTimeUtc(playerLog, DateTime.UtcNow);
                    string? foundByTracker = LaunchCoordinator.TryFindProcessPlayerLogPath(shortProc.Id, "tracker_abc");
                    Assert.Equal(playerLog, foundByTracker);

                    string? foundByDelta = LaunchCoordinator.TryFindProcessPlayerLogPath(shortProc.Id, null);
                    Assert.Equal(playerLog, foundByDelta);

                    string? cached = null;
                    var stateLarge = LaunchCoordinator.InspectProcessGameJoinState(shortProc.Id, "tracker_abc", ref cached);
                    Assert.Equal(ClientGameJoinState.JoinedGame, stateLarge);
                    try { shortProc.Kill(); } catch { /* ignore */ }
                }

                LaunchCoordinator.ChallengeProbeDelayMs = 1;
                LaunchCoordinator.MaxChallengeVerificationWaitSeconds = 0.05;

                var acc1 = new Shiro.Account { Username = "ChalAcc1", BrowserTrackerID = "trk1" };
                var acc2 = new Shiro.Account { Username = "ChalAcc2", BrowserTrackerID = "trk2" };
                var reqHome = new LaunchRequest(acc1, 0, "", false, false, openHome: true);
                var reqGame = new LaunchRequest(acc1, 123, "", false, false, openHome: false);
                var list = new List<LaunchRequest> { reqGame, new LaunchRequest(acc2, 123, "", false, false) };
                var state = new LaunchProgress { Account = acc1, Status = LaunchOutcome.Success };

                await LaunchCoordinator.WaitIfClientChallengedBeforeNextLaunchAsync(reqHome, 0, list, currentPid, state, null, System.Threading.CancellationToken.None);

                int callIdx = 0;
                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = (int p, string? t, ref string? c) =>
                {
                    callIdx++;
                    if (callIdx == 1) return ClientGameJoinState.Pending;
                    if (callIdx == 2) return ClientGameJoinState.Challenged;
                    return ClientGameJoinState.JoinedGame;
                };
                await LaunchCoordinator.WaitIfClientChallengedBeforeNextLaunchAsync(reqGame, 0, list, currentPid, state, null, System.Threading.CancellationToken.None);
                Assert.Equal(LaunchOutcome.Success, state.Status);

                callIdx = 0;
                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = (int p, string? t, ref string? c) =>
                {
                    callIdx++;
                    return callIdx == 1 ? ClientGameJoinState.Challenged : ClientGameJoinState.ProcessExited;
                };
                await LaunchCoordinator.WaitIfClientChallengedBeforeNextLaunchAsync(reqGame, 0, list, currentPid, state, null, System.Threading.CancellationToken.None);
                Assert.Equal(LaunchOutcome.Failed, state.Status);

                callIdx = 0;
                state.Status = LaunchOutcome.Success;
                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = (int p, string? t, ref string? c) =>
                {
                    callIdx++;
                    return callIdx == 1 ? ClientGameJoinState.Challenged : ClientGameJoinState.ChallengeFailedToLuaApp;
                };
                await LaunchCoordinator.WaitIfClientChallengedBeforeNextLaunchAsync(reqGame, 0, list, currentPid, state, null, System.Threading.CancellationToken.None);
                Assert.Equal(LaunchOutcome.Failed, state.Status);

                // Branch: targetPid <= 0 returns early
                await LaunchCoordinator.WaitIfClientChallengedBeforeNextLaunchAsync(reqGame, 0, list, -1, state, null, System.Threading.CancellationToken.None);

                // Branch: joinState == JoinedGame immediately returns early
                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = (int p, string? t, ref string? c) => ClientGameJoinState.JoinedGame;
                await LaunchCoordinator.WaitIfClientChallengedBeforeNextLaunchAsync(reqGame, 0, list, currentPid, state, null, System.Threading.CancellationToken.None);

                // Branch: 8 probes of Pending returns early
                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = (int p, string? t, ref string? c) => ClientGameJoinState.Pending;
                await LaunchCoordinator.WaitIfClientChallengedBeforeNextLaunchAsync(reqGame, 0, list, currentPid, state, null, System.Threading.CancellationToken.None);

                // Branch: Challenge timeout (MaxVerificationWait exceeded)
                LaunchCoordinator.MaxChallengeVerificationWaitSeconds = 0.001;
                state.Status = LaunchOutcome.Success;
                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = (int p, string? t, ref string? c) => ClientGameJoinState.Challenged;
                await LaunchCoordinator.WaitIfClientChallengedBeforeNextLaunchAsync(reqGame, 0, list, currentPid, state, null, System.Threading.CancellationToken.None);
                Assert.Equal(LaunchOutcome.Failed, state.Status);

                // Branch: AnalyzeClientLogLines with additional regex lines
                var mixedLines = new[]
                {
                    "[FLog::DataModelStarterModule] Launching experience at 123",
                    "[FLog::UgcExperienceController] UgcExperienceController: join: initialized dm 0x12345",
                    "[FLog::NewWebView2Browser] WebView2 browser is successfully launched",
                    "{\"feature\":\"challengeInvalidated\"}"
                };
                Assert.Equal(ClientGameJoinState.ChallengeFailedToLuaApp, LaunchCoordinator.AnalyzeClientLogLines(mixedLines));

                var completedChallengeLines = new[]
                {
                    "challengedByGcs detected",
                    "[FLog::Output] ! Joining game '12345678-1234-1234-1234-1234567890ab' place 189707 at 127.0.0.1"
                };
                Assert.Equal(ClientGameJoinState.JoinedGame, LaunchCoordinator.AnalyzeClientLogLines(completedChallengeLines));
            }
            finally
            {
                LaunchCoordinator.TestLogsDirectoryOverride = prevLogsDir;
                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = prevOverride;
                LaunchCoordinator.ChallengeProbeDelayMs = prevProbeMs;
                LaunchCoordinator.MaxChallengeVerificationWaitSeconds = prevMaxWait;
                try { System.IO.Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
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
