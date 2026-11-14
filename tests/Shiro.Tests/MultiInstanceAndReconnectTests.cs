using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class MultiInstanceAndReconnectTests
    {
        [Fact]
        public void AutoReconnect_Evaluate_CleanExitZeroWithoutDisconnect_ClosesCleanly()
        {
            DateTime launch = new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);
            DateTime now = launch.AddSeconds(45);
            var session = new AutoReconnectService.ReconnectSession
            {
                Account = new Account { Username = "TestUser1" },
                PlaceId = 12345,
                ProcessId = 4321,
                LaunchTimeUtc = launch,
                IsActive = true
            };

            var decision = AutoReconnectService.Evaluate(session, processExited: true, exitCode: 0, nowUtc: now);
            Assert.Equal(AutoReconnectService.ReconnectDecision.CloseCleanly, decision);
        }

        [Fact]
        public void AutoReconnect_Evaluate_ExitZeroAfterDisconnect_Reconnects()
        {
            DateTime launch = new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);
            DateTime now = launch.AddSeconds(45);
            var session = new AutoReconnectService.ReconnectSession
            {
                Account = new Account { Username = "TestUser2" },
                PlaceId = 12345,
                ProcessId = 4321,
                LaunchTimeUtc = launch,
                LastDisconnectUtc = now.AddSeconds(-5),
                LastDisconnectReason = 277,
                IsActive = true
            };

            var decision = AutoReconnectService.Evaluate(session, processExited: true, exitCode: 0, nowUtc: now);
            Assert.Equal(AutoReconnectService.ReconnectDecision.Reconnect, decision);
        }

        [Fact]
        public void AutoReconnect_Evaluate_DisconnectedOver20SecondsWhileRunning_KillsAndReconnects()
        {
            DateTime launch = new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);
            DateTime now = launch.AddSeconds(60);
            var session = new AutoReconnectService.ReconnectSession
            {
                Account = new Account { Username = "TestUser3" },
                PlaceId = 12345,
                ProcessId = 4321,
                LaunchTimeUtc = launch,
                LastDisconnectUtc = now.AddSeconds(-25),
                LastDisconnectReason = 268,
                IsActive = true
            };

            var decision = AutoReconnectService.Evaluate(session, processExited: false, exitCode: null, nowUtc: now);
            Assert.Equal(AutoReconnectService.ReconnectDecision.KillAndReconnect, decision);
        }

        [Fact]
        public void AutoReconnect_Evaluate_ExceedingMaxRetries_GivesUp()
        {
            DateTime launch = new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);
            DateTime now = launch.AddSeconds(60);
            var session = new AutoReconnectService.ReconnectSession
            {
                Account = new Account { Username = "TestUser4" },
                PlaceId = 12345,
                ProcessId = 4321,
                RetryCount = 5,
                LaunchTimeUtc = launch,
                IsActive = true
            };

            var decision = AutoReconnectService.Evaluate(session, processExited: true, exitCode: 1, nowUtc: now, maxRetries: 5);
            Assert.Equal(AutoReconnectService.ReconnectDecision.GiveUp, decision);
        }

        [Fact]
        public void AutoReconnect_RegisterSession_PreservesRetryCountWithinWindow_AndResetsAfterWindow()
        {
            using var service = new AutoReconnectService();
            var account = new Account { Username = "RetryPreserveUser" };
            DateTime t0 = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

            service.RegisterSession(account, 1001, processId: 111, nowUtcOverride: t0);
            Assert.True(service.TryGetSession("RetryPreserveUser", out var s1));
            s1.RetryCount = 3;
            s1.LastReconnectUtc = t0.AddMinutes(1);

            // Re-register 4 minutes later (< 10 min StableResetWindow) -> preserves RetryCount = 3
            service.RegisterSession(account, 1001, processId: 222, nowUtcOverride: t0.AddMinutes(5));
            Assert.True(service.TryGetSession("RetryPreserveUser", out var s2));
            Assert.Equal(3, s2.RetryCount);
            Assert.Equal(222, s2.ProcessId);

            // Re-register 15 minutes after LastReconnectUtc (> 10 min StableResetWindow) -> resets RetryCount = 0
            service.RegisterSession(account, 1001, processId: 333, nowUtcOverride: t0.AddMinutes(16));
            Assert.True(service.TryGetSession("RetryPreserveUser", out var s3));
            Assert.Equal(0, s3.RetryCount);
        }

        [Fact]
        public void AutoReconnect_NotifyDisconnectedAndConnected_TracksAndClearsDisconnectState()
        {
            using var service = new AutoReconnectService();
            var account = new Account { Username = "DisconnectTrackUser" };
            service.RegisterSession(account, 1001, processId: 7777);

            service.NotifyDisconnected(7777, reason: 277);
            Assert.True(service.TryGetSession("DisconnectTrackUser", out var s1));
            Assert.NotNull(s1.LastDisconnectUtc);
            Assert.Equal(277, s1.LastDisconnectReason);

            service.NotifyConnected(7777);
            Assert.Null(s1.LastDisconnectUtc);
            Assert.Equal(0, s1.LastDisconnectReason);
        }

        [Fact]
        public void LaunchCoordinator_GetSmartLaunchMinDelayMs_ClampsAndAddsJitter()
        {
            Assert.Equal(3000, LaunchCoordinator.GetSmartLaunchMinDelayMs(configuredSecondsOverride: 3, includeJitter: false));
            Assert.Equal(1000, LaunchCoordinator.GetSmartLaunchMinDelayMs(configuredSecondsOverride: -5, includeJitter: false));
            Assert.Equal(60000, LaunchCoordinator.GetSmartLaunchMinDelayMs(configuredSecondsOverride: 120, includeJitter: false));

            int withJitter = LaunchCoordinator.GetSmartLaunchMinDelayMs(configuredSecondsOverride: 3, includeJitter: true);
            Assert.InRange(withJitter, 3000, 4500);
        }

        [Fact]
        public async Task WaitForClientUnlockedAsync_NonExistentPid_ReturnsFalsePromptly()
        {
            RobloxSingletonService.Apply(true);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            bool unlocked = await RobloxSingletonService.WaitForClientUnlockedAsync(
                targetPid: int.MaxValue - 1,
                timeout: TimeSpan.FromSeconds(5),
                cancellationToken: cts.Token);

            Assert.False(unlocked);
        }

        [Fact]
        public void ProcessExtensions_GetCommandLine_WorksWithLimitedQueryInformation()
        {
            using var current = Process.GetCurrentProcess();
            string? cmdLine = current.GetCommandLine();

            Assert.False(string.IsNullOrWhiteSpace(cmdLine));
        }

        [Fact]
        public void RobloxProcess_ReadAppendedLines_ReadsSequentiallyWithoutSkippingConcurrentWrites()
        {
            using var ms = new System.IO.MemoryStream();
            byte[] batch1 = System.Text.Encoding.UTF8.GetBytes("line1\nline2\n");
            ms.Write(batch1, 0, batch1.Length);

            long lastPos = 0;
            string[] lines1 = RobloxProcess.ReadAppendedLines(ms, ref lastPos);
            Assert.Contains("line1", lines1);
            Assert.Contains("line2", lines1);
            Assert.Equal(batch1.Length, lastPos);

            // Append second batch and verify no bytes between batch1 and batch2 are skipped
            byte[] batch2 = System.Text.Encoding.UTF8.GetBytes("2026-10-23T09:00:00.000Z,1.0,1,1 [FLog::Network] Sending disconnect with reason: 277\n");
            ms.Write(batch2, 0, batch2.Length);

            string[] lines2 = RobloxProcess.ReadAppendedLines(ms, ref lastPos);
            Assert.Contains(lines2, l => l.Contains("Sending disconnect with reason: 277"));
            Assert.Equal(batch1.Length + batch2.Length, lastPos);
        }

        [Fact]
        public void MemoryOptimizer_ShouldTriggerPeriodicTrim_ExcludesNormalPriorityForegroundClient()
        {
            long mb500 = 500L * 1024 * 1024;
            long mb1500 = 1500L * 1024 * 1024;

            // Foreground client at Normal priority with 500 MB should NOT trigger trim
            Assert.False(MemoryOptimizer.ShouldTriggerPeriodicTrim(
                workingSetBytes: mb500,
                currentPriority: ProcessPriorityClass.Normal,
                isForeground: true,
                lowerPriorityEnabled: true));

            // Foreground client exceeding 1.2 GB SHOULD trigger trim
            Assert.True(MemoryOptimizer.ShouldTriggerPeriodicTrim(
                workingSetBytes: mb1500,
                currentPriority: ProcessPriorityClass.Normal,
                isForeground: true,
                lowerPriorityEnabled: true));

            // Background client at Normal priority SHOULD trigger trim/priority adjustment
            Assert.True(MemoryOptimizer.ShouldTriggerPeriodicTrim(
                workingSetBytes: 200L * 1024 * 1024,
                currentPriority: ProcessPriorityClass.Normal,
                isForeground: false,
                lowerPriorityEnabled: true));
        }

        [Fact]
        public void AntiAfkService_ComputeNextDueUtc_EnforcesMinimumThirtySecondFloor()
        {
            DateTime now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
            DateTime due = AntiAfkService.ComputeNextDueUtc(now, intervalMinutes: 1, jitterSeconds: -60);

            Assert.Equal(now.AddSeconds(30), due);
        }

        [Fact]
        public void RobloxAudioService_ShouldApplySmartMuteState_SkipsRedundantUnfocusedTicksAndDetectsNewClients()
        {
            // No Roblox running -> never enumerate COM audio sessions
            Assert.False(RobloxAudioService.ShouldApplySmartMuteState(0, -1, 0, -1));

            // First check when 2 Roblox clients are in background and user is on Chrome (activePid=0) -> apply once
            Assert.True(RobloxAudioService.ShouldApplySmartMuteState(0, -1, 2, -1));

            // Subsequent checks while still on Chrome (activePid=0) and still 2 Roblox clients -> skip COM!
            Assert.False(RobloxAudioService.ShouldApplySmartMuteState(0, 0, 2, 2));

            // A 3rd Roblox client launches in background while user is still on Chrome -> apply to mute the 3rd client!
            Assert.True(RobloxAudioService.ShouldApplySmartMuteState(0, 0, 3, 2));

            // User switches focus to Roblox PID 4000 -> apply!
            Assert.True(RobloxAudioService.ShouldApplySmartMuteState(4000, 0, 3, 3));

            // User stays on Roblox PID 4000 -> skip!
            Assert.False(RobloxAudioService.ShouldApplySmartMuteState(4000, 4000, 3, 3));
        }

        [Fact]
        public void ProxyBindingService_ParseWebProxy_PreservesColonsInPasswordsAndRejectsInvalidPorts()
        {
            var colonProxy = ProxyBindingService.ParseWebProxy("10.0.0.1:8080:alice:p@ss:word:123") as System.Net.WebProxy;
            Assert.NotNull(colonProxy);
            var creds1 = colonProxy.Credentials?.GetCredential(new Uri("http://10.0.0.1:8080"), "Basic");
            Assert.Equal("alice", creds1?.UserName);
            Assert.Equal("p@ss:word:123", creds1?.Password);

            var uriProxy = ProxyBindingService.ParseWebProxy("http://bob:sec:ret:99@proxy.example.com:3128") as System.Net.WebProxy;
            Assert.NotNull(uriProxy);
            var creds2 = uriProxy.Credentials?.GetCredential(new Uri("http://proxy.example.com:3128"), "Basic");
            Assert.Equal("bob", creds2?.UserName);
            Assert.Equal("sec:ret:99", creds2?.Password);

            var item = ProxyManagerService.ParseProxy("10.0.0.1:8080:alice:p@ss:word:123");
            Assert.NotNull(item);
            Assert.Equal("alice", item.Username);
            Assert.Equal("p@ss:word:123", item.Password);

            Assert.Null(ProxyBindingService.ParseWebProxy("10.0.0.1:99999"));
        }

        [Fact]
        public void RobloxProcessService_SnapshotCache_ReturnsConsistentCountsAndSupportsInvalidation()
        {
            RobloxProcessService.InvalidateSnapshotCache();
            int count1 = RobloxProcessService.GetRunningRobloxCount();
            int count2 = RobloxProcessService.GetRunningRobloxCount();
            var pids = RobloxProcessService.GetRunningRobloxPids();

            Assert.Equal(count1, count2);
            Assert.Equal(count1, pids.Count);
        }

        [Fact]
        public void GameThumbnailService_ClearCache_And_TryGetCachedBitmap_HandlesInvalidPlaceIds()
        {
            GameThumbnailService.ClearCache();
            Assert.False(GameThumbnailService.HasDiskCache(0));
            Assert.False(GameThumbnailService.HasDiskCache(-5));
            Assert.False(GameThumbnailService.TryGetCachedBitmap(0, out var bmp));
            Assert.Null(bmp);
        }

        [Fact]
        public async Task Account_CreateAsync_WithProvidedAccountJson_CreatesValidAccountWithoutNetwork()
        {
            string json = "{\"UserId\":12345678,\"Name\":\"ShiroTestUser\",\"DisplayName\":\"Shiro\"}";
            var acc = await Account.CreateAsync("_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you|_", json);

            Assert.NotNull(acc);
            Assert.True(acc.Valid);
            Assert.Equal(12345678L, acc.UserID);
            Assert.Equal("ShiroTestUser", acc.Username);
        }

        [Fact]
        public void Account_FindRobloxPlayerExecutablePublic_UsesCacheAndSupportsInvalidation()
        {
            Account.InvalidateExecutableCache();
            string? path1 = Account.FindRobloxPlayerExecutablePublic();
            string? path2 = Account.FindRobloxPlayerExecutablePublic();
            Assert.Equal(path1, path2);
        }

        [Fact]
        public void RobloxWatcher_RemoveProcessMetadata_SafelyClearsSeenAndMetadataCache()
        {
            lock (RobloxWatcher.InstanceRegistryLock)
            {
                RobloxWatcher.Seen.Add(999999);
            }
            RobloxWatcher.RemoveProcessMetadata(999999);
            lock (RobloxWatcher.InstanceRegistryLock)
            {
                Assert.DoesNotContain(999999, RobloxWatcher.Seen);
            }
        }

        [Fact]
        public void RobloxWindowService_FindWindowMapForPids_HandlesEmptyAndNonExistentPids()
        {
            var emptyMap = RobloxWindowService.FindWindowMapForPids(new System.Collections.Generic.HashSet<uint>());
            Assert.Empty(emptyMap);

            var bogusMap = RobloxWindowService.FindWindowMapForPids(new System.Collections.Generic.HashSet<uint> { 9999999u });
            Assert.Empty(bogusMap);
        }

        [Fact]
        public void RobloxProcess_ReadAppendedLines_ReadsCompleteContentWithPooledBuffer()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("Line1\nLine2\nLine3\n");
            using var ms = new System.IO.MemoryStream(data);
            long pos = 0;

            string[] lines = RobloxProcess.ReadAppendedLines(ms, ref pos);
            Assert.Equal(data.Length, pos);
            Assert.Contains("Line1", lines);
            Assert.Contains("Line2", lines);
            Assert.Contains("Line3", lines);

            string[] empty = RobloxProcess.ReadAppendedLines(ms, ref pos);
            Assert.Empty(empty);
        }

        [Fact]
        public void RobloxProcess_LogPatterns_DetectsForcedVerificationChallengeAndHijackLines()
        {
            string challenge403 = "2026-11-01T07:25:01.448Z,2.043934,3e8c,6,Warning [FLog::Output] status:403, body:{ \"errors\": [ { \"code\": 0, \"message\": \"challengedByGcs\" } ] }, url:{ \"https://gamejoin.roblox.com/v1/join-game\" }";
            string challengeDisplayed = "2026-11-01T07:25:06.101Z,6.696682,3fc0,6,Info [FLog::WebView2 Wrapper] HandleEvent: message={\"feature\":\"challengeDisplayed\",\"params\":{\"challengeType\":\"captchav2\"}}";
            string challengeCompleted = "2026-11-01T07:26:07.887Z,68.483238,3fc0,6,Info [FLog::WebView2 Wrapper] HandleEvent: message={\"feature\":\"challengeCompleted\",\"params\":{\"challengeType\":\"captchav2\"}}";
            string challengeInvalidated = "2026-11-01T07:25:24.506Z,25.102180,3fc0,6,Info [FLog::WebView2 Wrapper] HandleEvent: message={\"feature\":\"challengeInvalidated\",\"params\":{\"challengeId\":\"abc\"}}";
            string cookieChanged = "2026-11-01T07:25:24.819Z,25.415022,3fc0,6,Info [FLog::NewWebView2Browser] Security cookie value changed, updating HTTP layer cookie";
            string returnToLuaApp0745 = "2026-11-01T07:25:25.134Z,25.729431,3fc0,6,Info [FLog::SingleSurfaceApp] returnToLuaApp: (stage:UGCGame).";
            string pauseFalse0745 = "2026-11-01T07:25:25.134Z,25.729502,3fc0,6,Info [FLog::SurfaceController] SurfaceController[_:1]::pause destroyView:false.";
            string stopBegin0745 = "2026-11-01T07:25:25.134Z,25.729843,3fc0,6,Info [FLog::UgcExperienceController] UgcExperienceController: stop: begin (blocking=false dataModel=0x2be321039d0)";
            string trackerLine = "2026-11-01T07:25:02.423Z,3.018574,56c0,6,Info [FLog::LogWin32BTId] LogWin32BTId, App, cookie Engine => Native set RBXEventTrackerV2 as CreateDate=11/01/2026 02:25:02&rbxid=11800535274&browserid=1793415916069001";

            Assert.True(RobloxProcess.IsGameJoinChallengedLogLine(challenge403));
            Assert.True(RobloxProcess.IsGameJoinChallengedLogLine(challengeDisplayed));
            Assert.True(RobloxProcess.IsChallengeCompletedLogLine(challengeCompleted));
            Assert.True(RobloxProcess.IsChallengeInvalidatedOrAbandonedLogLine(challengeInvalidated));
            Assert.True(RobloxProcess.IsChallengeInvalidatedOrAbandonedLogLine(returnToLuaApp0745));
            Assert.True(RobloxProcess.IsSecurityCookieChangedLogLine(cookieChanged));
            Assert.True(RobloxProcess.IsReturnToLuaAppLine(returnToLuaApp0745, hasActiveGameDataModel: false));
            Assert.Matches(RobloxProcess.ModernDataModelPauseRegex(), pauseFalse0745);
            Assert.Matches(RobloxProcess.ModernDataModelStopRegex(), stopBegin0745);

            Assert.True(RobloxProcess.TryParseCookieTrackerLine(trackerLine, out long uid, out string btid));
            Assert.Equal(11800535274L, uid);
            Assert.Equal("1793415916069001", btid);

            string trackerLineWithRbxUid = "2026-11-01T07:25:02.423Z,3.018574,56c0,6,Info [FLog::LogWin32BTId] LogWin32BTId, App, cookie Engine => Native set RBXEventTrackerV2 as CreateDate=11/01/2026 02:25:02&rbxid=123&rbxuid=11800535274&browserid=1793415916069001";
            Assert.True(RobloxProcess.TryParseCookieTrackerLine(trackerLineWithRbxUid, out long uid2, out string btid2));
            Assert.Equal(11800535274L, uid2);
            Assert.Equal("1793415916069001", btid2);
        }

        [Fact]
        public void LaunchCoordinator_AnalyzeClientLogLines_ClassifiesChallengeLifecycleAccurately()
        {
            var challengedLines = new[]
            {
                "2026-11-01T07:25:00.289Z,0.884,3fc0,6,Info [FLog::UgcExperienceController] UgcExperienceController: join: initialized dm 0x2be321039d0",
                "2026-11-01T07:25:01.448Z,2.043,3e8c,6,Warning [FLog::Output] status:403, body:{ \"message\": \"challengedByGcs\" }, url:{ \"https://gamejoin.roblox.com/v1/join-game\" }",
                "2026-11-01T07:25:06.101Z,6.696,3fc0,6,Info [FLog::WebView2 Wrapper] HandleEvent: message={\"feature\":\"challengeDisplayed\",\"params\":{\"challengeType\":\"captchav2\"}}"
            };
            Assert.Equal(ClientGameJoinState.Challenged, LaunchCoordinator.AnalyzeClientLogLines(challengedLines));

            var failedToLuaAppLines = new[]
            {
                challengedLines[0],
                challengedLines[1],
                challengedLines[2],
                "2026-11-01T07:25:24.506Z,25.102,3fc0,6,Info [FLog::WebView2 Wrapper] HandleEvent: message={\"feature\":\"challengeInvalidated\"}",
                "2026-11-01T07:25:25.134Z,25.729,3fc0,6,Info [FLog::SingleSurfaceApp] returnToLuaApp: (stage:UGCGame)."
            };
            Assert.Equal(ClientGameJoinState.ChallengeFailedToLuaApp, LaunchCoordinator.AnalyzeClientLogLines(failedToLuaAppLines));

            var solvedAndJoinedLines = new[]
            {
                challengedLines[0],
                challengedLines[1],
                challengedLines[2],
                "2026-11-01T07:31:17.399Z,43.470,40dc,6,Info [FLog::WebView2 Wrapper] HandleEvent: message={\"feature\":\"challengeCompleted\"}",
                "2026-11-01T07:31:17.861Z,43.932,3dd0,6,Info [FLog::Output] ! Joining game '9ccfd18c-0a9f-4e64-b5c5-f6b64deefca1' place 109983668079237 at 128.116.51.34"
            };
            Assert.Equal(ClientGameJoinState.JoinedGame, LaunchCoordinator.AnalyzeClientLogLines(solvedAndJoinedLines));

            var launchingExperienceThenChallenged = new[]
            {
                "2026-11-02T08:34:36.093Z,3.093401,74b8,6,Warning [FLog::DataModelStarterModule] Launching experience at 113290951185459",
                "2026-11-02T08:34:38.110Z,5.110469,8304,12 [DFLog::HttpTraceError] status:403 url:{ \"https://gamejoin.roblox.com/v1/join-game\" } challengedByGcs",
                "2026-11-02T08:35:04.063Z,31.063820,74b8,6 [FLog::SingleSurfaceApp] returnToLuaApp: (stage:UGCGame)."
            };
            Assert.Equal(ClientGameJoinState.ChallengeFailedToLuaApp, LaunchCoordinator.AnalyzeClientLogLines(launchingExperienceThenChallenged));

            var joinedThenNormalLeave = new[]
            {
                "2026-11-02T08:34:36.093Z,3.093,74b8,6,Info [FLog::UgcExperienceController] UgcExperienceController: join: initialized dm 0x2be321039d0",
                "2026-11-02T08:34:38.110Z,5.110,74b8,6,Info [FLog::Output] ! Joining game '9ccfd18c-0a9f-4e64-b5c5-f6b64deefca1' place 109983668079237 at 128.116.51.34",
                "2026-11-02T08:40:04.063Z,331.063,74b8,6,Info [FLog::SingleSurfaceApp] returnToLuaAppInternal: ... App has been initialized, returning from game."
            };
            Assert.Equal(ClientGameJoinState.JoinedGame, LaunchCoordinator.AnalyzeClientLogLines(joinedThenNormalLeave));
        }

        [Fact]
        public void RobloxDirectLauncher_CanSyncRotatedCookieForTarget_BlocksMismatchedUserIdOrTrackerId()
        {
            var accountA = new Account
            {
                Username = "AD_Jackpot94",
                UserID = 11800535274L,
                BrowserTrackerID = "175658845055"
            };

            // Exact match -> allowed
            Assert.True(RobloxDirectLauncher.CanSyncRotatedCookieForTarget(
                accountA,
                trackerUserId: 11800535274L,
                trackerBrowserId: "175658845055",
                expectedBrowserId: "175658845055"));

            // Different UserID (e.g. AD_Jackpot95 = 11800534427) -> MUST block cross-contamination!
            Assert.False(RobloxDirectLauncher.CanSyncRotatedCookieForTarget(
                accountA,
                trackerUserId: 11800534427L,
                trackerBrowserId: "175658845055",
                expectedBrowserId: "175658845055"));

            // Unknown UserID (0) but mismatched BrowserTrackerID -> MUST block!
            Assert.False(RobloxDirectLauncher.CanSyncRotatedCookieForTarget(
                accountA,
                trackerUserId: 0L,
                trackerBrowserId: "999999999999",
                expectedBrowserId: "175658845055"));
        }
    }
}
