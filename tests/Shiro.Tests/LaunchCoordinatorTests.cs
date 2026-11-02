using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("CookieGuardSerial")]
    public class LaunchCoordinatorTests
    {
        [Fact]
        public void LaunchRequest_InitializesPropertiesCorrectly()
        {
            var account = new Account();
            account.Username = "TestPlayer";
            account.UserID = 12345678;

            var request = new LaunchRequest(
                account: account,
                placeId: 189707,
                jobId: "job-abc-123",
                followUser: true,
                vipServer: false,
                openHome: false,
                preferChromeHome: false);

            Assert.Same(account, request.Account);
            Assert.Equal(189707, request.PlaceId);
            Assert.Equal("job-abc-123", request.JobId);
            Assert.True(request.FollowUser);
            Assert.False(request.VipServer);
            Assert.False(request.OpenHome);
            Assert.False(request.PreferChromeHome);
        }

        [Fact]
        public void LaunchRequest_HandlesNullJobIdGracefully()
        {
            var account = new Account();
            var request = new LaunchRequest(account, 123, null, false, false);

            Assert.NotNull(request.JobId);
            Assert.Empty(request.JobId);
        }

        [Fact]
        public void LaunchProgress_RecordsStateCorrectly()
        {
            var account = new Account { Username = "TestUser" };
            var progress = new LaunchProgress
            {
                Index = 1,
                Total = 5,
                Account = account,
                Status = LaunchOutcome.Success,
                Message = "Launched successfully",
                Completed = true
            };

            Assert.Equal(1, progress.Index);
            Assert.Equal(5, progress.Total);
            Assert.Same(account, progress.Account);
            Assert.Equal(LaunchOutcome.Success, progress.Status);
            Assert.Equal("Launched successfully", progress.Message);
            Assert.True(progress.Completed);
        }

        [Fact]
        public void GetSmartLaunchMinDelayMs_CalculatesAndClampsDelay()
        {
            // Default clamped between 1s and 60s
            int minDelay = LaunchCoordinator.GetSmartLaunchMinDelayMs(0, includeJitter: false);
            Assert.Equal(1000, minDelay);

            int maxDelay = LaunchCoordinator.GetSmartLaunchMinDelayMs(100, includeJitter: false);
            Assert.Equal(60000, maxDelay);

            int regularDelay = LaunchCoordinator.GetSmartLaunchMinDelayMs(5, includeJitter: false);
            Assert.Equal(5000, regularDelay);

            int withJitter = LaunchCoordinator.GetSmartLaunchMinDelayMs(5, includeJitter: true);
            Assert.InRange(withJitter, 5000, 6500);
        }

        [Fact]
        public void GetSmartLaunchMinDelayMs_RespectsSmartSettingAndIgnoresAccountJoinDelay()
        {
            var prevGeneral = AccountManager.General;
            AccountManager.General = new IniSection("General");
            try
            {
                // AccountJoinDelay = 15s should NOT leak into SmartLaunch delay
                AccountManager.General.Set(AppSettingsKeys.General.AccountJoinDelay, "15");

                // Without explicit SmartLaunchMinDelaySeconds, defaults to DefaultSmartLaunchMinDelaySeconds (2s = 2000ms)
                int defaultDelay = LaunchCoordinator.GetSmartLaunchMinDelayMs(null, includeJitter: false);
                Assert.Equal(2000, defaultDelay);

                // With explicit SmartLaunchMinDelaySeconds = 4s
                AccountManager.General.Set(AppSettingsKeys.General.SmartLaunchMinDelaySeconds, "4");
                int configuredDelay = LaunchCoordinator.GetSmartLaunchMinDelayMs(null, includeJitter: false);
                Assert.Equal(4000, configuredDelay);
            }
            finally
            {
                AccountManager.General = prevGeneral;
            }
        }

        [Theory]
        [InlineData("Success! Process started.", LaunchOutcome.Success)]
        [InlineData(Account.BrowserHomeResult, LaunchOutcome.Success)]
        [InlineData("HTTP 429 Too Many Requests (Rate limit)", LaunchOutcome.RateLimited)]
        [InlineData("Invalid Authentication Ticket: 401 Unauthorized", LaunchOutcome.TicketError)]
        [InlineData("Roblox has probably signed you out", LaunchOutcome.TicketError)]
        [InlineData("Operation timed out waiting for mutex", LaunchOutcome.Timeout)]
        [InlineData("Unknown process crash error", LaunchOutcome.Failed)]
        [InlineData("", LaunchOutcome.Failed)]
        [InlineData(null, LaunchOutcome.Failed)]
        public async System.Threading.Tasks.Task ClassifyResult_CategorizesOutcomesCorrectly(string? result, LaunchOutcome expected)
        {
            var request = new LaunchRequest(new Account { Username = "Player" }, 123, "", false, false);
            var coordinator = new LaunchCoordinator((req, ct) => System.Threading.Tasks.Task.FromResult(result));

            var batchResult = await coordinator.EnqueueAsync(
                new[] { request },
                delaySeconds: 1,
                asyncJoin: false,
                progress: null,
                cancellationToken: System.Threading.CancellationToken.None);

            Assert.Single(batchResult.Results);
            Assert.Equal(expected, batchResult.Results[0].Status);
        }

        [Fact]
        public async System.Threading.Tasks.Task EnqueueAsync_ExecutesSequentiallyAndReportsProgress()
        {
            var executedAccounts = new System.Collections.Generic.List<string>();
            var progressUpdates = new System.Collections.Generic.List<LaunchProgress>();

            var prevGeneral = AccountManager.General;
            AccountManager.General = new IniSection("General");
            AccountManager.General.Set(AppSettingsKeys.General.SmartFastLaunch, "false");

            try
            {
                var coordinator = new LaunchCoordinator((req, ct) =>
                {
                    executedAccounts.Add(req.Account.Username);
                    return System.Threading.Tasks.Task.FromResult("Success");
                });

                var requests = new[]
                {
                    new LaunchRequest(new Account { Username = "Account1" }, 123, "", false, false),
                    new LaunchRequest(new Account { Username = "Account2" }, 123, "", false, false)
                };

                var runTask = coordinator.EnqueueAsync(
                    requests,
                    delaySeconds: 1,
                    asyncJoin: true,
                    progress: p =>
                    {
                        progressUpdates.Add(p);
                        // Once Account1 reports success, signal coordinator to proceed immediately
                        if (p.Account?.Username == "Account1" && p.Status == LaunchOutcome.Success)
                        {
                            coordinator.SignalNext();
                        }
                    });

                var result = await runTask;

                Assert.Equal(2, executedAccounts.Count);
                Assert.Equal("Account1", executedAccounts[0]);
                Assert.Equal("Account2", executedAccounts[1]);
                Assert.Equal(2, result.Results.Count);
                Assert.False(result.Cancelled);
                Assert.NotEmpty(progressUpdates);
            }
            finally
            {
                AccountManager.General = prevGeneral;
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task EnqueueAsync_Cancellation_AbortsRemainingQueue()
        {
            using var cts = new System.Threading.CancellationTokenSource();
            var coordinator = new LaunchCoordinator(async (req, ct) =>
            {
                if (req.Account.Username == "Account1")
                {
                    cts.Cancel();
                }
                await System.Threading.Tasks.Task.Delay(10, ct);
                return "Success";
            });

            var requests = new[]
            {
                new LaunchRequest(new Account { Username = "Account1" }, 123, "", false, false),
                new LaunchRequest(new Account { Username = "Account2" }, 123, "", false, false)
            };

            var result = await coordinator.EnqueueAsync(
                requests,
                delaySeconds: 1,
                asyncJoin: false,
                progress: null,
                cancellationToken: cts.Token);

            Assert.True(result.Cancelled);
        }

        [Fact]
        public async System.Threading.Tasks.Task EnqueueAsync_ConcurrentQueue_ThrowsInvalidOperationException()
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<string>();
            var coordinator = new LaunchCoordinator((req, ct) => tcs.Task);

            var req1 = new[] { new LaunchRequest(new Account { Username = "UserA" }, 1, "", false, false) };
            var req2 = new[] { new LaunchRequest(new Account { Username = "UserB" }, 1, "", false, false) };

            var task1 = coordinator.EnqueueAsync(req1, 1, false, null);

            // Second enqueue while first is still running should throw
            await Assert.ThrowsAsync<System.InvalidOperationException>(() =>
                coordinator.EnqueueAsync(req2, 1, false, null));

            tcs.SetResult("Success");
            await task1;
        }

        [Fact]
        public async System.Threading.Tasks.Task EnqueueAsync_AfterDisposal_ThrowsObjectDisposedException()
        {
            var coordinator = new LaunchCoordinator((req, ct) => System.Threading.Tasks.Task.FromResult("Success"));
            coordinator.Dispose();

            var req = new[] { new LaunchRequest(new Account { Username = "User" }, 1, "", false, false) };
            await Assert.ThrowsAsync<System.ObjectDisposedException>(() =>
                coordinator.EnqueueAsync(req, 1, false, null));
        }

        [Fact]
        public async System.Threading.Tasks.Task EnqueueAsync_WhenAccountIsBanned_PreservesBanStatusInsteadOfClearingToExpired()
        {
            var bannedAcc = new Account
            {
                Username = "BannedUser",
                UserID = 777,
                Valid = false,
                AccountStatusIssue = "Ban 3 ngày"
            };

            var coordinator = new LaunchCoordinator((req, ct) =>
                System.Threading.Tasks.Task.FromResult("ERROR: Không thể lấy Authentication Ticket từ Roblox."));

            var req = new[] { new LaunchRequest(bannedAcc, 0, "", false, false, openHome: true) };
            await coordinator.EnqueueAsync(req, 1, false, null);

            Assert.False(bannedAcc.Valid);
            Assert.True(bannedAcc.IsBannedOrLocked);
            Assert.Equal("Ban 3 ngày", bannedAcc.AccountStatusIssue);
        }
    }
}
