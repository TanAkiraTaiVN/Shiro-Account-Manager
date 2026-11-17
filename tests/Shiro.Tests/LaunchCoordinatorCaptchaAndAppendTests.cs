#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class LaunchCoordinatorCaptchaAndAppendTests
    {
        private static ISettingsService CreateSettingsWithWaitForCaptcha(bool waitForCaptcha)
        {
            var ini = new IniFile();
            var section = ini["General"];
            section.Set(AppSettingsKeys.General.SmartFastLaunch, "true");
            section.Set(AppSettingsKeys.General.SmartLaunchMinDelaySeconds, "0");
            section.Set(AppSettingsKeys.General.WaitForCaptchaInQueue, waitForCaptcha ? "true" : "false");
            return section;
        }

        [Fact]
        public void LaunchTimingService_RecordsGapAndRolling10mCount_AndEmitsStructuredLog()
        {
            DateTimeOffset now = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
            var logger = Substitute.For<IAppLogger>();
            var loggedMessages = new List<string>();
            logger.When(l => l.Info(Arg.Any<string>()))
                  .Do(ci => loggedMessages.Add(ci.Arg<string>()));

            var tracker = new LaunchTimingService(() => now, logger);

            var first = tracker.RecordStart();
            Assert.Equal(-1, first.GapMs);
            Assert.Equal(1, first.Recent10mCount);

            tracker.FormatAndLog(new LaunchTimingEntry(
                Index: 1,
                Total: 2,
                Username: "TimingUser1",
                Pass: 1,
                OpenHome: false,
                Outcome: LaunchOutcome.Success,
                CaptchaState: ClientGameJoinState.JoinedGame,
                RebindPerformed: true,
                RebindMs: 145,
                TicketMs: 320,
                SpawnMs: 1620,
                WindowMs: 3180,
                JoinMs: 6250,
                GapMs: first.GapMs,
                Recent10mCount: first.Recent10mCount));

            now = now.AddSeconds(3);
            var second = tracker.RecordStart();
            Assert.Equal(3000, second.GapMs);
            Assert.Equal(2, second.Recent10mCount);

            // Advance beyond the 10-minute rolling window
            now = now.AddMinutes(11);
            var third = tracker.RecordStart();
            Assert.Equal(660_000, third.GapMs);
            Assert.Equal(1, third.Recent10mCount);

            Assert.Single(loggedMessages);
            string line = loggedMessages[0];
            Assert.Contains("[LaunchTiming]", line);
            Assert.Contains("TimingUser1", line);
            Assert.Contains("pass=1", line);
            Assert.Contains("rebind=yes(145ms)", line);
            Assert.Contains("spawnMs=1620", line);
            Assert.Contains("windowMs=3180", line);
            Assert.Contains("joinMs=6250", line);
            Assert.Contains("recent10m=1", line);
        }

        [Fact]
        public async Task EnqueueAsync_WhenWaitForCaptchaDisabled_ClosesChallengedTab_DefersToEnd_AndRetriesOnceAfterCooldown()
        {
            var prevOverride = LaunchCoordinator.TestInspectProcessGameJoinStateOverride;
            var prevProbeDelay = LaunchCoordinator.ChallengeProbeDelayMs;
            var prevCooldown = LaunchCoordinator.TestCaptchaRetryCooldownOverride;
            var prevClose = LaunchCoordinator.TestCloseChallengedProcessOverride;
            var prevSettings = AppSettings.General;

            try
            {
                var ini = new IniFile();
                ini["General"].Set(AppSettingsKeys.General.SmartFastLaunch, "false");
                AppSettings.General = ini["General"];
                using var _settingsScope = AppServices.Override(CreateSettingsWithWaitForCaptcha(waitForCaptcha: false));

                LaunchCoordinator.ChallengeProbeDelayMs = 1;
                LaunchCoordinator.TestCaptchaRetryCooldownOverride = TimeSpan.FromMilliseconds(5);

                var closedAccounts = new List<string>();
                LaunchCoordinator.TestCloseChallengedProcessOverride = acc => closedAccounts.Add(acc.Username);

                int currentPid = Environment.ProcessId;
                var executionOrder = new List<string>();
                var attemptsByUser = new Dictionary<string, int>(StringComparer.Ordinal);

                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = (int pid, string? trackerId, ref string? cachedPath) =>
                {
                    if (trackerId == "trk-1" && attemptsByUser.GetValueOrDefault("User1") == 1)
                        return ClientGameJoinState.Challenged;
                    return ClientGameJoinState.JoinedGame;
                };

                var acc1 = new Account { Username = "User1", BrowserTrackerID = "trk-1", Valid = true };
                var acc2 = new Account { Username = "User2", BrowserTrackerID = "trk-2", Valid = true };
                var acc3 = new Account { Username = "User3", BrowserTrackerID = "trk-3", Valid = true };

                var coordinator = new LaunchCoordinator((req, ct) =>
                {
                    executionOrder.Add(req.Account.Username);
                    attemptsByUser[req.Account.Username] = attemptsByUser.GetValueOrDefault(req.Account.Username) + 1;
                    req.Account.LastLaunchedProcessId = currentPid;
                    return Task.FromResult(LaunchResult.Success());
                });

                var requests = new[]
                {
                    new LaunchRequest(acc1, 100, "", false, false),
                    new LaunchRequest(acc2, 100, "", false, false),
                    new LaunchRequest(acc3, 100, "", false, false)
                };

                var batch = await coordinator.EnqueueAsync(requests, delaySeconds: 0, asyncJoin: false, progress: null);

                Assert.Equal(new[] { "User1", "User2", "User3", "User1" }, executionOrder);
                Assert.Single(closedAccounts);
                Assert.Equal("User1", closedAccounts[0]);

                Assert.Equal(3, batch.Results.Count);
                Assert.All(batch.Results, r => Assert.Equal(LaunchOutcome.Success, r.Status));

                var user1Result = batch.Results.First(r => r.Account!.Username == "User1");
                Assert.True(user1Result.CaptchaDetected);
                Assert.True(user1Result.CaptchaRetried);
                Assert.False(user1Result.CaptchaFailedAfterRetry);
                Assert.True(acc1.Valid);
            }
            finally
            {
                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = prevOverride;
                LaunchCoordinator.ChallengeProbeDelayMs = prevProbeDelay;
                LaunchCoordinator.TestCaptchaRetryCooldownOverride = prevCooldown;
                LaunchCoordinator.TestCloseChallengedProcessOverride = prevClose;
                AppSettings.General = prevSettings;
            }
        }

        [Fact]
        public async Task EnqueueAsync_WhenDeferredCaptchaRetryStillChallenged_DoesNotRetryTwice_AndPreservesValidity()
        {
            var prevOverride = LaunchCoordinator.TestInspectProcessGameJoinStateOverride;
            var prevProbeDelay = LaunchCoordinator.ChallengeProbeDelayMs;
            var prevCooldown = LaunchCoordinator.TestCaptchaRetryCooldownOverride;
            var prevClose = LaunchCoordinator.TestCloseChallengedProcessOverride;
            var prevSettings = AppSettings.General;

            try
            {
                var ini = new IniFile();
                ini["General"].Set(AppSettingsKeys.General.SmartFastLaunch, "false");
                AppSettings.General = ini["General"];
                using var _settingsScope = AppServices.Override(CreateSettingsWithWaitForCaptcha(waitForCaptcha: false));

                LaunchCoordinator.ChallengeProbeDelayMs = 1;
                LaunchCoordinator.TestCaptchaRetryCooldownOverride = TimeSpan.FromMilliseconds(5);

                var closedAccounts = new List<string>();
                LaunchCoordinator.TestCloseChallengedProcessOverride = acc => closedAccounts.Add(acc.Username);

                int currentPid = Environment.ProcessId;
                var executionOrder = new List<string>();

                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = (int pid, string? trackerId, ref string? cachedPath) =>
                    trackerId == "trk-1" ? ClientGameJoinState.Challenged : ClientGameJoinState.JoinedGame;

                var acc1 = new Account { Username = "RetryStillCaptcha", BrowserTrackerID = "trk-1", Valid = true };
                var acc2 = new Account { Username = "CleanUser", BrowserTrackerID = "trk-2", Valid = true };

                var coordinator = new LaunchCoordinator((req, ct) =>
                {
                    executionOrder.Add(req.Account.Username);
                    req.Account.LastLaunchedProcessId = currentPid;
                    return Task.FromResult(LaunchResult.Success());
                });

                var batch = await coordinator.EnqueueAsync(
                    new[]
                    {
                        new LaunchRequest(acc1, 100, "", false, false),
                        new LaunchRequest(acc2, 100, "", false, false)
                    },
                    delaySeconds: 0,
                    asyncJoin: false,
                    progress: null);

                Assert.Equal(new[] { "RetryStillCaptcha", "CleanUser", "RetryStillCaptcha" }, executionOrder);
                Assert.Equal(2, closedAccounts.Count);

                var acc1Result = batch.Results.First(r => r.Account!.Username == "RetryStillCaptcha");
                Assert.Equal(LaunchOutcome.Failed, acc1Result.Status);
                Assert.True(acc1Result.CaptchaDetected);
                Assert.True(acc1Result.CaptchaRetried);
                Assert.True(acc1Result.CaptchaFailedAfterRetry);
                Assert.True(acc1.Valid);
            }
            finally
            {
                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = prevOverride;
                LaunchCoordinator.ChallengeProbeDelayMs = prevProbeDelay;
                LaunchCoordinator.TestCaptchaRetryCooldownOverride = prevCooldown;
                LaunchCoordinator.TestCloseChallengedProcessOverride = prevClose;
                AppSettings.General = prevSettings;
            }
        }

        [Fact]
        public async Task EnqueueAsync_WhenLastAccountInBatchHitsCaptcha_DefersAndRetriesOnce()
        {
            var prevOverride = LaunchCoordinator.TestInspectProcessGameJoinStateOverride;
            var prevProbeDelay = LaunchCoordinator.ChallengeProbeDelayMs;
            var prevCooldown = LaunchCoordinator.TestCaptchaRetryCooldownOverride;
            var prevClose = LaunchCoordinator.TestCloseChallengedProcessOverride;

            try
            {
                using var _settingsScope = AppServices.Override(CreateSettingsWithWaitForCaptcha(waitForCaptcha: false));
                LaunchCoordinator.ChallengeProbeDelayMs = 1;
                LaunchCoordinator.TestCaptchaRetryCooldownOverride = TimeSpan.FromMilliseconds(5);
                LaunchCoordinator.TestCloseChallengedProcessOverride = _ => { };

                int currentPid = Environment.ProcessId;
                var executionOrder = new List<string>();
                int acc2Attempts = 0;

                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = (int pid, string? trackerId, ref string? cachedPath) =>
                    (trackerId == "trk-2" && acc2Attempts == 1) ? ClientGameJoinState.Challenged : ClientGameJoinState.JoinedGame;

                var acc1 = new Account { Username = "FirstClean", BrowserTrackerID = "trk-1", Valid = true };
                var acc2 = new Account { Username = "LastCaptcha", BrowserTrackerID = "trk-2", Valid = true };

                var coordinator = new LaunchCoordinator((req, ct) =>
                {
                    executionOrder.Add(req.Account.Username);
                    if (req.Account.Username == "LastCaptcha") acc2Attempts++;
                    req.Account.LastLaunchedProcessId = currentPid;
                    return Task.FromResult(LaunchResult.Success());
                });

                var batch = await coordinator.EnqueueAsync(
                    new[]
                    {
                        new LaunchRequest(acc1, 100, "", false, false),
                        new LaunchRequest(acc2, 100, "", false, false)
                    },
                    delaySeconds: 0,
                    asyncJoin: false,
                    progress: null);

                Assert.Equal(new[] { "FirstClean", "LastCaptcha", "LastCaptcha" }, executionOrder);
                Assert.Equal(2, batch.Results.Count);
                Assert.All(batch.Results, r => Assert.Equal(LaunchOutcome.Success, r.Status));
                Assert.True(batch.Results[1].CaptchaRetried);
            }
            finally
            {
                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = prevOverride;
                LaunchCoordinator.ChallengeProbeDelayMs = prevProbeDelay;
                LaunchCoordinator.TestCaptchaRetryCooldownOverride = prevCooldown;
                LaunchCoordinator.TestCloseChallengedProcessOverride = prevClose;
            }
        }

        [Fact]
        public async Task EnqueueAsync_WhenWaitForCaptchaEnabled_WaitsInlineWithoutDeferring()
        {
            var prevOverride = LaunchCoordinator.TestInspectProcessGameJoinStateOverride;
            var prevProbeDelay = LaunchCoordinator.ChallengeProbeDelayMs;
            var prevMaxWait = LaunchCoordinator.MaxChallengeVerificationWaitSeconds;
            var prevCooldown = LaunchCoordinator.TestCaptchaRetryCooldownOverride;

            try
            {
                using var _settingsScope = AppServices.Override(CreateSettingsWithWaitForCaptcha(waitForCaptcha: true));
                LaunchCoordinator.ChallengeProbeDelayMs = 1;
                LaunchCoordinator.MaxChallengeVerificationWaitSeconds = 1.0;
                LaunchCoordinator.TestCaptchaRetryCooldownOverride = TimeSpan.FromMilliseconds(5);

                int currentPid = Environment.ProcessId;
                int probes = 0;
                var executionOrder = new List<string>();

                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = (int pid, string? trackerId, ref string? cachedPath) =>
                {
                    if (trackerId == "trk-1")
                    {
                        probes++;
                        return probes < 3 ? ClientGameJoinState.Challenged : ClientGameJoinState.JoinedGame;
                    }
                    return ClientGameJoinState.JoinedGame;
                };

                var acc1 = new Account { Username = "ManualUser1", BrowserTrackerID = "trk-1", Valid = true };
                var acc2 = new Account { Username = "ManualUser2", BrowserTrackerID = "trk-2", Valid = true };

                var coordinator = new LaunchCoordinator((req, ct) =>
                {
                    executionOrder.Add(req.Account.Username);
                    req.Account.LastLaunchedProcessId = currentPid;
                    return Task.FromResult(LaunchResult.Success());
                });

                var batch = await coordinator.EnqueueAsync(
                    new[]
                    {
                        new LaunchRequest(acc1, 100, "", false, false),
                        new LaunchRequest(acc2, 100, "", false, false)
                    },
                    delaySeconds: 0,
                    asyncJoin: false,
                    progress: null);

                Assert.Equal(new[] { "ManualUser1", "ManualUser2" }, executionOrder);
                Assert.True(probes >= 3);
                Assert.False(batch.Results[0].CaptchaRetried);
                Assert.Equal(LaunchOutcome.Success, batch.Results[0].Status);
            }
            finally
            {
                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = prevOverride;
                LaunchCoordinator.ChallengeProbeDelayMs = prevProbeDelay;
                LaunchCoordinator.MaxChallengeVerificationWaitSeconds = prevMaxWait;
                LaunchCoordinator.TestCaptchaRetryCooldownOverride = prevCooldown;
            }
        }

        [Fact]
        public async Task TryAppendRequests_WhileQueueRunning_AppendsNewAccountsAndSkipsDuplicatePending()
        {
            var prevUnlockOverride = LaunchCoordinator.TestWaitForClientUnlockedOverride;
            using var _settingsScope = AppServices.Override(CreateSettingsWithWaitForCaptcha(waitForCaptcha: false));
            LaunchCoordinator.TestWaitForClientUnlockedOverride = (_, _, _) => Task.FromResult(true);
            try
            {
                var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var executionOrder = new List<string>();

                var coordinator = new LaunchCoordinator(async (req, ct) =>
                {
                    executionOrder.Add(req.Account.Username);
                    if (req.Account.Username == "A1")
                    {
                        firstStarted.TrySetResult(true);
                        await releaseFirst.Task.ConfigureAwait(false);
                    }
                    return LaunchResult.Success();
                });

                var a1 = new Account { Username = "A1", Valid = true };
                var a2 = new Account { Username = "A2", Valid = true };
                var a3 = new Account { Username = "A3", Valid = true };
                var a4 = new Account { Username = "A4", Valid = true };

                Assert.False(coordinator.TryAppendRequests(new[] { new LaunchRequest(a3, 1, "", false, false) }, out int preCount));
                Assert.Equal(0, preCount);

                var batchTask = coordinator.EnqueueAsync(
                    new[]
                    {
                        new LaunchRequest(a1, 100, "", false, false),
                        new LaunchRequest(a2, 100, "", false, false)
                    },
                    delaySeconds: 0,
                    asyncJoin: false,
                    progress: null);

                await firstStarted.Task;

                bool accepted = coordinator.TryAppendRequests(
                    new[]
                    {
                        new LaunchRequest(a2, 100, "", false, false),
                        new LaunchRequest(a3, 100, "", false, false),
                        new LaunchRequest(a4, 100, "", false, false)
                    },
                    out int appendedCount);

                Assert.True(accepted);
                Assert.Equal(2, appendedCount);

                releaseFirst.TrySetResult(true);
                var batch = await batchTask;

                Assert.Equal(new[] { "A1", "A2", "A3", "A4" }, executionOrder);
                Assert.Equal(4, batch.Results.Count);
                Assert.All(batch.Results, r => Assert.Equal(LaunchOutcome.Success, r.Status));
            }
            finally
            {
                LaunchCoordinator.TestWaitForClientUnlockedOverride = prevUnlockOverride;
            }
        }
    }
}
