#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Http;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P1LaunchRegressionTests : IDisposable
    {
        private readonly Action<Action>? _origUiRefresh = AppDispatcher.CustomDispatcher;
        private readonly bool _origMultiRbx = AppSettings.General.Get<bool>("EnableMultiRbx", true);

        public P1LaunchRegressionTests()
        {
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
        }

        public void Dispose()
        {
            RobloxRateLimitCoordinator.Instance.ResetBackoff();
            AppDispatcher.CustomDispatcher = _origUiRefresh;
            AccountLaunchService.TestProtocolCommandOverride = null;
            AccountLaunchService.TestVersionRootsOverride = null;
            AccountLaunchService.TestGetAuthTicketOverride = null;
            AccountLaunchService.TestStartNativeGameOverride = null;
            AccountLaunchService.InvalidateExecutableCache();
            AutoReconnectService.Instance.Clear();
            AutoReconnectService.Instance.TestJoinServerOverride = null;
            AutoReconnectService.Instance.TestReconnectDelayMsOverride = null;
            AppSettings.General.Set("EnableMultiRbx", _origMultiRbx ? "true" : "false");
            AppSettings.General.Set("ShuffleJobId", "false");
            ToastService.ClearAll();
        }

        private sealed class FakeCoordinator(Func<IReadOnlyList<LaunchRequest>, LaunchBatchResult> onEnqueue) : ILaunchCoordinator
        {
            public bool IsRunning => false;
            public Task<LaunchBatchResult> EnqueueAsync(IReadOnlyList<LaunchRequest> requests, int delaySeconds, bool asyncJoin, Action<LaunchProgress>? progress, CancellationToken cancellationToken = default)
                => Task.FromResult(onEnqueue(requests));
            public bool TryAppendRequests(IReadOnlyList<LaunchRequest> requests, out int appendedCount) { appendedCount = 0; return false; }
            public void SignalNext() { }
            public void Cancel() { }
            public Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
            public void Dispose() { }
        }

        [Fact]
        public async Task HighD1_BatchSummary_ReportsRateLimitedAndCancelledAsNonSuccess()
        {
            AppDispatcher.CustomDispatcher = a => a();
            ToastService.ClearAll();
            var acc = new Account { Username = "RlUser", Valid = true };
            var coord = new FakeCoordinator(_ => new LaunchBatchResult(
                new List<LaunchProgress> { new() { Index = 1, Total = 1, Account = acc, Status = LaunchOutcome.RateLimited, Completed = true } },
                cancelled: false));

            string? status = null;
            await AccountLaunchBatchService.LaunchAccountsAsync(coord, new List<Account> { acc }, 1818, "", false, false, 1, false, s => status = s, CancellationToken.None);

            Assert.NotNull(status);
            Assert.DoesNotContain("All 1/1", status!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("RlUser", status!, StringComparison.Ordinal);

            ToastService.ClearAll();
            await AccountLaunchBatchService.LaunchHomeAccountsAsync(coord, new[] { acc }, false, 1000, null, s => status = s, CancellationToken.None);
            Assert.NotNull(status);
            Assert.DoesNotContain("Successfully launched all", status!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task HighD2_AsyncJoinTimeout_DoesNotOverwriteSuccessStatus_AndHomeSignalsOnError()
        {
            var bridge = Substitute.For<IHostUiBridgeService>();
            using var bridgeScope = AppServices.Override(bridge);
            AccountLaunchService.TestGetAuthTicketOverride = (_, _) => Task.FromResult((true, "tkt-123"));
            AccountLaunchService.TestStartNativeGameOverride = (_, _, _, _, _, _, _) => throw new InvalidOperationException("boom");
            AppSettings.General.Set("EnableMultiRbx", "false");

            var acc = new Account { Username = "HomeErr", UserID = 10, Valid = true };
            AccountLaunchService.TestProtocolCommandOverride = null;

            LaunchQueuePacingHelper.TestAsyncJoinTimeoutOverride = TimeSpan.FromMilliseconds(20);
            try
            {
                var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var req = new LaunchRequest(acc, 1818, "", false, false);
                var prog = new LaunchProgress { Index = 1, Total = 2, Account = acc, Status = LaunchOutcome.Success, Result = "OK", Completed = true };
                await LaunchQueuePacingHelper.WaitSpacingBetweenLaunchesAsync(
                    req, 1, 2, "NextUser", 123, prog, 1, asyncJoin: true, smartLaunch: false, hasNextLaunch: true, signal, null, CancellationToken.None);
                Assert.Equal(LaunchOutcome.Success, prog.Status);
            }
            finally
            {
                LaunchQueuePacingHelper.TestAsyncJoinTimeoutOverride = null;
            }
        }

        [Fact]
        public async Task HighD3_ProtocolHijackByBrowser_DoesNotExecuteWhenPreferDirectFalse()
        {
            AppSettings.General.Set(AppSettingsKeys.General.PreferDirectLaunch, "false");
            AccountLaunchService.TestProtocolCommandOverride = "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" \"%1\"";
            string emptyDir = Path.Combine(Path.GetTempPath(), "ShiroNoRoblox_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(emptyDir);
            AccountLaunchService.TestVersionRootsOverride = new[] { emptyDir };
            AccountLaunchService.TestDefaultIconOverride = "nonexistent";
            AccountLaunchService.InvalidateExecutableCache();

            try
            {
                Assert.False(AccountLaunchService.HasRobloxPlayerProtocolHandler());
                await Assert.ThrowsAsync<FileNotFoundException>(() =>
                    AccountLaunchService.StartNativeGameAsync("roblox-player:1+gameinfo:secret-tkt", "secret-tkt", "https://assetgame.roblox.com/join", "U1"));

                var psi = new System.Diagnostics.ProcessStartInfo("RobloxPlayerBeta.exe");
                AccountLaunchService.AddDirectGameArguments(psi, "tkt-1", "https://join", "999888777");
                Assert.Contains("-b", psi.ArgumentList);
                Assert.Contains("999888777", psi.ArgumentList);
            }
            finally
            {
                if (Directory.Exists(emptyDir)) Directory.Delete(emptyDir, true);
            }
        }

        [Fact]
        public async Task HighD4_TryAppendRequests_ExcludesAccountsAlreadyDeferredForCaptchaPass2()
        {
            var ini = new IniFile();
            var gen = ini["General"];
            gen.Set(AppSettingsKeys.General.SmartFastLaunch, "true");
            gen.Set(AppSettingsKeys.General.SmartLaunchMinDelaySeconds, "0");
            gen.Set(AppSettingsKeys.General.WaitForCaptchaInQueue, "false");
            using var settingsScope = AppServices.Override(gen);
            var origProbeDelay = LaunchCoordinator.ChallengeProbeDelayMs;
            LaunchCoordinator.ChallengeProbeDelayMs = 1;
            var accA = new Account { Username = "CaptchaPendingA", Valid = true };
            var accB = new Account { Username = "FreshUserB", Valid = true };
            var pass1Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            LaunchCoordinator.TestCaptchaRetryCooldownOverride = TimeSpan.FromMilliseconds(150);
            LaunchCoordinator.TestWaitForClientUnlockedOverride = (_, _, _) => Task.FromResult(true);

            LaunchCoordinator? coord = null;
            var origInspect = LaunchCoordinator.TestInspectProcessGameJoinStateOverride;
            var origClose = LaunchCoordinator.TestCloseChallengedProcessOverride;
            int livePid = Environment.ProcessId;

            coord = new LaunchCoordinator(async (req, ct) =>
            {
                if (req.Account.Username == "CaptchaPendingA")
                {
                    req.Account.LastLaunchedProcessId = livePid;
                    return LaunchResult.Success();
                }
                await releaseSecond.Task.WaitAsync(ct);
                return LaunchResult.Success();
            });

            LaunchCoordinator.TestInspectProcessGameJoinStateOverride = (int pid, string? _, ref string? _) =>
            {
                pass1Done.TrySetResult();
                return pid == livePid ? ClientGameJoinState.Challenged : ClientGameJoinState.JoinedGame;
            };
            LaunchCoordinator.TestCloseChallengedProcessOverride = _ => { };

            try
            {
                var holder = new Account { Username = "Holder", Valid = true };
                var enqueueTask = coord.EnqueueAsync(
                    new[] { new LaunchRequest(accA, 1818, "", false, false), new LaunchRequest(holder, 1818, "", false, false) },
                    delaySeconds: 0, asyncJoin: false, progress: null);

                await pass1Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
                bool ok = coord.TryAppendRequests(
                    new[] { new LaunchRequest(accA, 1818, "", false, false), new LaunchRequest(accB, 1818, "", false, false) },
                    out int appended);

                releaseSecond.TrySetResult();
                await enqueueTask;
                Assert.True(ok);
                Assert.Equal(1, appended);
            }
            finally
            {
                LaunchCoordinator.ChallengeProbeDelayMs = origProbeDelay;
                LaunchCoordinator.TestInspectProcessGameJoinStateOverride = origInspect;
                LaunchCoordinator.TestCloseChallengedProcessOverride = origClose;
                LaunchCoordinator.TestCaptchaRetryCooldownOverride = null;
                LaunchCoordinator.TestWaitForClientUnlockedOverride = null;
                coord.Dispose();
            }
        }

        [Fact]
        public async Task HighD5_MonitorNativeAuthRateLimit_RethrowsCancellation()
        {
            var acc = new Account { Username = "CancelMon" };
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AccountLaunchService.MonitorNativeAuthRateLimitAsync(DateTime.UtcNow, acc, cts.Token));
        }

        [Fact]
        public async Task HighD6_BuildLaunchRequests_DoesNotLeakOldJobId_AndShuffleSkipsFollowUser()
        {
            var acc = new Account { Username = "SavedUser", Valid = true };
            acc.SetField("SavedPlaceId", "100");
            acc.SetField("SavedJobId", "old-server-guid");

            var diffPlaceReqs = AccountLaunchBatchService.BuildLaunchRequests(new[] { acc }, placeId: 200, jobId: "", followUser: false, vipServer: false);
            Assert.Single(diffPlaceReqs);
            Assert.Equal(200, diffPlaceReqs[0].PlaceId);
            Assert.Equal(string.Empty, diffPlaceReqs[0].JobId);

            int gamesApiHits = 0;
            var handler = new DelegateHandler(req =>
            {
                if (req.RequestUri!.ToString().Contains("servers/public", StringComparison.OrdinalIgnoreCase))
                    Interlocked.Increment(ref gamesApiHits);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json") };
            });
            using var apiScope = AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));
            AppSettings.General.Set("ShuffleJobId", "true");
            AccountLaunchService.TestGetAuthTicketOverride = (_, _) => Task.FromResult((false, string.Empty));

            await AccountLaunchService.Instance.JoinServerResultAsync(acc, placeId: 987654321, jobId: "", followUser: true, joinVip: false);
            Assert.Equal(0, gamesApiHits);
        }

        [Fact]
        public async Task HighD7_HighD8_HighD9_AutoReconnect_UidSuppressionAndDisableCancelsBackoff()
        {
            var svc = AutoReconnectService.Instance;
            svc.Clear();
            svc.IsEnabled = true;
            svc.InitialBackoff = TimeSpan.FromSeconds(1);

            var noNameAcc = new Account { Username = string.Empty, UserID = 889900 };
            svc.RegisterSession(noNameAcc, 1818, processId: 0);
            svc.SuppressReconnectForAccount(noNameAcc);
            Assert.True(svc.TryGetAccountSession(noNameAcc, out var found));
            Assert.True(found!.IsSuppressed);

            int reconnectCalls = 0;
            svc.TestReconnectDelayMsOverride = 200;
            svc.TestJoinServerOverride = _ => { Interlocked.Increment(ref reconnectCalls); return Task.FromResult("Success"); };
            var recAcc = new Account { Username = "RecDisable", UserID = 889901 };
            svc.RegisterSession(recAcc, 1818, processId: 0);
            Assert.True(svc.TryGetAccountSession(recAcc, out var recSess));
            svc.HandleProcessExit(recSess!);
            svc.IsEnabled = false;
            await Task.Delay(350);
            Assert.Equal(0, reconnectCalls);
        }

        [Fact]
        public void HighD12_ClientSettingsPatcher_StripsQuotesAndCommaFromRegistryIconPath()
        {
            string cleaned = ClientSettingsPatcher.NormalizeRegistryExecutablePath("\"C:\\Roblox\\Versions\\version-abc\\RobloxPlayerBeta.exe\",0");
            Assert.Equal("C:\\Roblox\\Versions\\version-abc\\RobloxPlayerBeta.exe", cleaned);
        }

        [Fact]
        public async Task HighD13_PrerequisiteService_AcceptsExitCode3010()
        {
            string dummyExe = Path.GetTempFileName();
            try
            {
                bool ok = await PrerequisiteService.InstallVCRedistCoreAsync(
                    _ => Task.FromResult(Encoding.UTF8.GetBytes("MZ")),
                    (_, _) => Task.FromResult(3010),
                    verifyAuthenticodeOverride: _ => true);
                Assert.True(ok);
            }
            finally
            {
                if (File.Exists(dummyExe)) File.Delete(dummyExe);
            }
        }

        [Fact]
        public async Task HighD14_Batch_PreservesFirstSuccessfulChunkWhenSecondChunkFails()
        {
            var handler = new DelegateHandler(async req =>
            {
                string body = await req.Content!.ReadAsStringAsync();
                if (body.Contains("999000", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);

                var sb = new StringBuilder("{\"data\":[");
                for (int i = 1; i <= 100; i++)
                {
                    if (i > 1) sb.Append(',');
                    sb.Append($"{{\"targetId\":{i},\"errorCode\":0,\"imageUrl\":\"https://img/{i}.png\"}}");
                }
                sb.Append("]}");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json") };
            });

            var mockApi = Substitute.For<IRobloxApiClient>();
            mockApi.Thumbnails.Returns(new RestClient(new HttpClient(handler), "https://thumbnails.roblox.com/"));
            using var scope = AppServices.Override(mockApi);
            AppSettings.General.Set("DisableImages", "false");

            var tasks = Enumerable.Range(1, 100).Select(id => Batch.GetImage(id, "Avatar")).ToList();
            tasks.Add(Batch.GetImage(999000, "Avatar"));
            await Task.WhenAll(tasks);

            Assert.Equal("https://img/1.png", await tasks[0]);
            Assert.Equal("https://img/100.png", await tasks[99]);
        }

        [Fact]
        public void AutoReconnectMonitor_WhenPidRecycled_MarksSessionExitedAndTriggersReconnect()
        {
            var ini = new IniFile();
            var general = ini["General"];
            general.Set(AppSettingsKeys.General.AutoReconnect, "true");
            using var settingsScope = AppServices.Override<ISettingsService>(general);

            var prevPidsOverride = RobloxProcessService.TestGetRunningRobloxPidsOverride;
            using var svc = new AutoReconnectService(TimeSpan.FromHours(1))
            {
                MaxRetries = 5,
                LaunchGracePeriod = TimeSpan.Zero,
                TestReconnectDelayMsOverride = 60_000,
                TestJoinServerOverride = _ => Task.FromResult("Failed")
            };

            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/c \"more >nul\"",
                RedirectStandardInput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            Assert.NotNull(proc);

            try
            {
                RobloxProcessService.TestGetRunningRobloxPidsOverride = () => new[] { proc!.Id };
                var acc = new Account { Username = "RecycledPidUser", UserID = 90011 };
                svc.RegisterSession(acc, 1818, processId: proc!.Id, nowUtcOverride: DateTime.UtcNow.AddMinutes(-2));

                Assert.True(svc.TryGetSession("RecycledPidUser", out var session));
                Assert.True(session!.ProcessCreationTicks > 0);

                // Simulate OS PID recycling to a newer process with different creation ticks
                session.ProcessCreationTicks -= 10_000_000L;

                int attempts = 0;
                svc.ReconnectAttempted += (_, _) => Interlocked.Increment(ref attempts);
                svc.CheckSessionsCallback(null);

                Assert.Equal(1, attempts);
                Assert.Equal(1, session.RetryCount);
                Assert.False(session.IsActive);
            }
            finally
            {
                RobloxProcessService.TestGetRunningRobloxPidsOverride = prevPidsOverride;
                try { if (!proc!.HasExited) proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            }
        }

        private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _asyncFn = req => Task.FromResult(fn(req));
            public DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> asyncFn) : this(_ => new HttpResponseMessage(HttpStatusCode.OK)) => _asyncFn = asyncFn;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _asyncFn(request);
        }
    }
}
