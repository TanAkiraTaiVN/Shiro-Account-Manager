#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class AuditRuntimeRegressionTests : IDisposable
    {
        private readonly bool _prevAllowLaunch = AppSettings.WebServer.Get<bool>("AllowLaunchAccount", true);
        private readonly string? _prevWsPassword = AppSettings.WebSocketPassword;

        public void Dispose()
        {
            Account.TestHttpClientFactory = null;
            AccountLaunchService.TestGetAuthTicketOverride = null;
            RobloxProcessService.TestGetRunningRobloxPidsOverride = null;
            AppSettings.WebServer.Set("AllowLaunchAccount", _prevAllowLaunch ? "true" : "false");
            AppSettings.WebSocketPassword = _prevWsPassword ?? string.Empty;
        }

        private sealed class SlowOrThrowingSocket : WebSocket
        {
            private readonly Func<CancellationToken, Task> _onSend;
            private WebSocketState _state = WebSocketState.Open;

            public SlowOrThrowingSocket(Func<CancellationToken, Task> onSend) => _onSend = onSend;
            public override WebSocketCloseStatus? CloseStatus => null;
            public override string? CloseStatusDescription => null;
            public override WebSocketState State => _state;
            public override string? SubProtocol => null;
            public override void Abort() => _state = WebSocketState.Aborted;
            public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            {
                _state = WebSocketState.Closed;
                return Task.CompletedTask;
            }
            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            {
                _state = WebSocketState.CloseSent;
                return Task.CompletedTask;
            }
            public override void Dispose() => _state = WebSocketState.Closed;
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
                => _onSend(cancellationToken);
        }

        [Fact]
        public async Task Bug1_AutoReconnectService_WhenFirstReconnectFailsAndThrows_RetriesUntilSuccess()
        {
            var ini = new IniFile();
            var general = ini["General"];
            general.Set("EnableAutoReconnect", "true");
            using var settingsScope = AppServices.Override<ISettingsService>(general);

            using var svc = new AutoReconnectService(TimeSpan.FromMilliseconds(20))
            {
                MaxRetries = 4,
                TestReconnectDelayMsOverride = 5
            };

            int attempts = 0;
            var succeeded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            svc.TestJoinServerOverride = _ =>
            {
                int n = Interlocked.Increment(ref attempts);
                if (n == 1) return Task.FromResult("Ticket failed");
                if (n == 2) throw new InvalidOperationException("Transient reconnect failure");
                succeeded.TrySetResult(true);
                return Task.FromResult("Success");
            };

            var acc = new Account { Username = "RetryUser", UserID = 10101 };
            svc.RegisterSession(acc, placeId: 1818, processId: 44444);
            Assert.True(svc.TryGetSession("RetryUser", out var session));

            svc.HandleProcessExit(session!);

            bool completed = await Task.WhenAny(succeeded.Task, Task.Delay(3000)) == succeeded.Task;
            Assert.True(completed, $"Expected reconnect retries to continue until attempt 3, but stuck at {attempts} attempt(s).");
            Assert.Equal(3, attempts);
            Assert.Equal(1, svc.TotalReconnections);
        }

        [Fact]
        public async Task Bug1_AutoReconnectService_WhenAllRetriesReturnError_FiresMaxRetriesExceeded()
        {
            var ini = new IniFile();
            var general = ini["General"];
            general.Set("EnableAutoReconnect", "true");
            using var settingsScope = AppServices.Override<ISettingsService>(general);

            using var svc = new AutoReconnectService(TimeSpan.FromMilliseconds(20))
            {
                MaxRetries = 2,
                TestReconnectDelayMsOverride = 5,
                TestJoinServerOverride = _ => Task.FromResult("Failed to fetch ticket")
            };

            var exceeded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            svc.MaxRetriesExceeded += _ => exceeded.TrySetResult(true);

            var acc = new Account { Username = "ExhaustUser", UserID = 10102 };
            svc.RegisterSession(acc, placeId: 1818, processId: 55555);
            Assert.True(svc.TryGetSession("ExhaustUser", out var session));

            svc.HandleProcessExit(session!);

            bool completed = await Task.WhenAny(exceeded.Task, Task.Delay(3000)) == exceeded.Task;
            Assert.True(completed, "Expected MaxRetriesExceeded to fire after all JoinServer attempts failed.");
        }

        [Fact]
        public async Task Bug3_KestrelWebServer_ConcurrentTimeoutWhileSending_DoesNotDisposeActiveSemaphorePrematurely()
        {
            var enteredFirstSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var socket = new SlowOrThrowingSocket(async _ =>
            {
                enteredFirstSend.TrySetResult(true);
                await releaseFirstSend.Task.ConfigureAwait(false);
            });

            var gate = new SemaphoreSlim(1, 1);
            var sockets = new ConcurrentDictionary<WebSocket, SemaphoreSlim>();
            sockets[socket] = gate;
            byte[] payload = KestrelWebServer.SerializeBroadcastEnvelope("ping", "test");

            // First broadcast holds the gate inside SendAsync.
            Task firstBroadcast = KestrelWebServer.BroadcastToSocketsAsync(sockets, new ArraySegment<byte>(payload), sendTimeoutMs: 5000);
            await enteredFirstSend.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // Second broadcast waits on the same gate and times out at 25ms, pruning the socket.
            await KestrelWebServer.BroadcastToSocketsAsync(sockets, new ArraySegment<byte>(payload), sendTimeoutMs: 25);

            // While first send still holds the gate, the gate must NOT be disposed out from under it.
            bool acquired = await gate.WaitAsync(0);
            Assert.False(acquired);

            releaseFirstSend.SetResult(true);
            await firstBroadcast;
        }

        [Fact]
        public void Bug4_RobloxProcess_UsesCachedPidInsteadOfDisposedRbxProcessId()
        {
            string repoRoot = ArchitectureTests.RepoRoot;
            string file = Path.Combine(repoRoot, "src", "Shiro", "Services", "Windows", "RobloxProcess.cs");
            string content = File.ReadAllText(file);

            // Allow RbxProcess.Id only in constructor/CleanupProcessExit if fallback needed, not in callbacks/logging/lookups.
            string[] forbiddenPatterns =
            {
                @"Connected\?\.\s*Invoke\(\s*RbxProcess\.Id",
                @"Disconnected\?\.\s*Invoke\(\s*RbxProcess\.Id",
                @"LogFile of \{RbxProcess\.Id\}",
                @"LogFile for \{RbxProcess\.Id\}",
                @"Roblox process \{RbxProcess\?\.Id\}"
            };
            foreach (string pat in forbiddenPatterns)
            {
                Assert.False(Regex.IsMatch(content, pat), $"RobloxProcess.cs still accesses RbxProcess.Id via pattern: {pat}");
            }
        }

        [Fact]
        public void Bug9_RobloxWindowService_And_MemoryOptimizer_HonorAutoLowerPriorityFalseAndSingleClientGuard()
        {
            var settings = Substitute.For<ISettingsService>();
            // Key does not exist / returns explicit default when called with defaultValue=false or true
            settings.Get<bool>(AppSettingsKeys.General.AutoLowerProcessPriority, Arg.Any<bool>())
                .Returns(ci => ci.ArgAt<bool>(1));
            settings.GetBool(Arg.Any<string>(), Arg.Any<bool>())
                .Returns(ci => ci.ArgAt<bool>(1));
            settings.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming, false).Returns(false);

            using var settingsScope = AppServices.Override<ISettingsService>(settings);
            RobloxProcessService.TestGetRunningRobloxPidsOverride = () => new[] { Environment.ProcessId };

            var (_, _, prioChanged) = MemoryOptimizer.OptimizeRobloxClients(lowerPriority: true, forceWorkingSetTrim: false);
            Assert.Equal(0, prioChanged);

            if (OperatingSystem.IsWindows())
            {
                // Verify WindowService never requested defaultValue=true for AutoLowerProcessPriority
                RobloxWindowService.HideAllWindows();
                RobloxWindowService.ShowAllWindows();
                settings.DidNotReceive().Get<bool>(AppSettingsKeys.General.AutoLowerProcessPriority, true);
            }
        }

        [Fact]
        public async Task Bug12_AccountApiDispatcher_LaunchAndFollow_ReturnFailureWhenJoinServerFails_AndLuaGuardsNil()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            AppSettings.WebSocketPassword = "AuditGatewayPass99";
            AppSettings.WebServer.Set("AllowLaunchAccount", "true");
            var acc = new Account
            {
                Username = "ApiLaunchFailUser",
                UserID = 888777,
                Valid = true,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--tok"
            };
            AccountManager.ResetAccountsForTests(new[] { acc });

            AccountLaunchService.TestGetAuthTicketOverride = (_, _) => Task.FromResult((false, "Authentication ticket failed"));

            string launchResp = await mgr.ProcessApiRequestAsync(new AccountManager.ApiRequestContext
            {
                AbsolutePath = "/LaunchAccount",
                IsV2 = true,
                IsLocal = true,
                Host = "localhost",
                GetQuery = k => k switch
                {
                    "Account" => "ApiLaunchFailUser",
                    "PlaceId" => "1818",
                    "Password" => "AuditGatewayPass99",
                    _ => null
                },
                GetHeader = _ => null,
                SetStatusCode = _ => { }
            });
            Assert.Contains("\"Success\":false", launchResp);
            Assert.DoesNotContain("Launched ApiLaunchFailUser", launchResp);

            var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"requestedUsername\":\"TargetUser\",\"id\":999888,\"name\":\"TargetUser\",\"displayName\":\"TargetUser\"}]}")
            });
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            string followResp = await mgr.ProcessApiRequestAsync(new AccountManager.ApiRequestContext
            {
                AbsolutePath = "/FollowUser",
                IsV2 = true,
                IsLocal = true,
                Host = "localhost",
                GetQuery = k => k switch
                {
                    "Account" => "ApiLaunchFailUser",
                    "Username" => "TargetUser",
                    "Password" => "AuditGatewayPass99",
                    _ => null
                },
                GetHeader = _ => null,
                SetStatusCode = _ => { }
            });
            Assert.Contains("\"Success\":false", followResp);
            Assert.DoesNotContain("Joining TargetUser", followResp);

            string lua = File.ReadAllText(Path.Combine(ArchitectureTests.RepoRoot, "RAMAccount.lua"));
            Assert.Contains("function Account:SetRecommendedServer(PlaceId) return GET('SetRecommendedServer', self.Username, PlaceId and ('PlaceId=' .. HttpService:UrlEncode(tostring(PlaceId)))) end", lua);
        }

        [Fact]
        public async Task Bug13_ReLoginWithCookie_RollsBackAccountStatusIssue_WhenNewCookieIsModeratedAndOldWasValid()
        {
            var mockRepo = Substitute.For<IAccountRepository>();
            using var repoScope = AppServices.Override<IAccountRepository>(mockRepo);
            var fakeDialogs = Substitute.For<IDialogService>();
            fakeDialogs.ShowInputPrompt(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns("_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('M', 80));
            using var dialogScope = AppServices.Override<IDialogService>(fakeDialogs);

            var handler = new FakeHttpHandler(req =>
            {
                string uri = req.RequestUri!.AbsoluteUri;
                if (uri.Contains("users.roblox.com/v1/users/authenticated"))
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                if (uri.Contains("usermoderation.roblox.com/v1/not-approved"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"punishedUserId\":123,\"punishmentType\":\"Banned for 7 Days\",\"messageToUser\":\"Test\"}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            });
            Account.TestHttpClientFactory = _ => new HttpClient(handler);

            string oldToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('O', 80);
            var acc = new Account
            {
                Username = "HealthyUser",
                UserID = 54321,
                SecurityToken = oldToken,
                Valid = true,
                AccountStatusIssue = string.Empty
            };
            var vm = new AccountViewModel(acc);
            var mainVm = new ViewModels.MainViewModel();

            await AccountWorkflowService.Default.ReLoginWithCookieAsync(vm, mainVm, _ => { });

            Assert.True(acc.Valid);
            Assert.Equal(oldToken, acc.SecurityToken);
            Assert.Equal(string.Empty, acc.AccountStatusIssue);
        }

        [Fact]
        public async Task Bug14_AccountLaunchBatchService_PreCancelledToken_ReportsCancelledWithoutThrowing()
        {
            using var coord = new LaunchCoordinator((_, _) => Task.FromResult(LaunchResult.Success()));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            string status = string.Empty;
            var acc = new Account { Username = "PreCancelUser", Valid = true };

            await AccountLaunchBatchService.LaunchAccountsAsync(
                coord,
                new List<Account> { acc },
                placeId: 1818,
                jobId: string.Empty,
                followUser: false,
                vipServer: false,
                accountJoinDelaySeconds: 0,
                asyncJoin: false,
                statusCallback: s => status = s,
                cancellationToken: cts.Token);

            Assert.Equal(LocalizationService.Get("Launch_QueueCancelled"), status);
        }
    }
}
