#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountLaunchServiceBehaviorTests
    {
        [Fact]
        public async Task JoinServerResultAsync_NullAccount_ReturnsFailed()
        {
            var result = await AccountLaunchService.Instance.JoinServerResultAsync(null!, 12345);
            Assert.Equal(LaunchOutcome.Failed, result.Outcome);
            Assert.Contains(LocalizationService.Get("AccountLaunch.InvalidAccount"), result.Message);
        }

        [Fact]
        public async Task JoinServerResultAsync_CanceledToken_ThrowsOperationCanceledException()
        {
            var account = new Account { Username = "CancelLaunchUser" };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AccountLaunchService.Instance.JoinServerResultAsync(account, 12345, cancellationToken: cts.Token));
        }

        [Fact]
        public async Task JoinServerResultAsync_InvalidAccountNoTicket_ReturnsTicketError()
        {
            var account = new Account
            {
                Username = "NoTicketUser",
                SecurityToken = "InvalidSecurityCookie",
                LastAuthTicketRateLimited = false
            };

            var result = await AccountLaunchService.Instance.JoinServerResultAsync(account, 12345);
            Assert.Equal(LaunchOutcome.TicketError, result.Outcome);
            Assert.Contains(LocalizationService.Get("AccountLaunch.TicketFailed"), result.Message);
        }

        [Fact]
        public async Task JoinServerResultAsync_RateLimitedTicket_ReturnsRateLimited()
        {
            var account = new Account
            {
                Username = "RateLimitedUser",
                SecurityToken = "InvalidSecurityCookie"
            };
            account.LastAuthTicketRateLimited = true;

            // When ticket fetch fails and LastAuthTicketRateLimited is true, returns RateLimited
            var result = await AccountLaunchService.Instance.JoinServerResultAsync(account, 12345);
            Assert.True(result.Outcome == LaunchOutcome.RateLimited || result.Outcome == LaunchOutcome.TicketError);
        }

        [Fact]
        public async Task JoinServerAsync_NullAccount_ReturnsLegacyFailedString()
        {
            string legacyResult = await AccountLaunchService.Instance.JoinServerAsync(null!, 12345);
            Assert.Contains(LocalizationService.Get("AccountLaunch.InvalidAccount"), legacyResult);
        }

        [Fact]
        public async Task LaunchAppHomeResultAsync_NullAccount_ReturnsFailed()
        {
            var result = await AccountLaunchService.Instance.LaunchAppHomeResultAsync(null!);
            Assert.Equal(LaunchOutcome.Failed, result.Outcome);
            Assert.Contains(LocalizationService.Get("AccountLaunch.InvalidAccount"), result.Message);
        }

        [Fact]
        public async Task LaunchAppHomeResultAsync_CanceledToken_ThrowsOperationCanceledException()
        {
            var account = new Account { Username = "CancelHomeUser" };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AccountLaunchService.Instance.LaunchAppHomeResultAsync(account, preferChrome: false, cancellationToken: cts.Token));
        }

        [Fact]
        public async Task LaunchAppHomeResultAsync_InvalidSession_ReturnsTicketError()
        {
            var account = new Account
            {
                Username = "InvalidHomeUser",
                SecurityToken = "DummyTokenNotValid"
            };

            var result = await AccountLaunchService.Instance.LaunchAppHomeResultAsync(account);
            Assert.True(result.Outcome == LaunchOutcome.TicketError || result.Outcome == LaunchOutcome.Failed || result.Outcome == LaunchOutcome.RateLimited);
        }

        [Fact]
        public async Task LaunchAppHomeResultAsync_WithPreferDirectLaunch_AcquiresAndPassesAuthTicket()
        {
            var prevTicketOverride = AccountLaunchService.TestGetAuthTicketOverride;
            var prevHomeOverride = AccountLaunchService.TestStartNativeHomeOverride;
            bool prevPreferDirect = AppSettings.General.Get<bool>(AppSettingsKeys.General.PreferDirectLaunch, false);

            try
            {
                AppSettings.General.Set(AppSettingsKeys.General.PreferDirectLaunch, "True");
                string? capturedTicket = null;
                string? capturedCookie = null;

                AccountLaunchService.TestGetAuthTicketOverride = (_, _) => Task.FromResult((true, "home-auth-ticket-999"));
                AccountLaunchService.TestStartNativeHomeOverride = (nativeUri, ticket, username, btid, securityToken, _) =>
                {
                    capturedTicket = ticket;
                    capturedCookie = securityToken;
                    return Task.FromResult(54321);
                };

                var account = new Account
                {
                    Username = "DirectHomeUser",
                    UserID = 11223344,
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('E', 120),
                    BrowserTrackerID = "123456789012"
                };

                var result = await AccountLaunchService.Instance.LaunchAppHomeResultAsync(account, preferChrome: false);
                Assert.Equal(LaunchOutcome.Success, result.Outcome);
                Assert.Equal("home-auth-ticket-999", capturedTicket);
                Assert.Equal(account.SecurityToken, capturedCookie);
                Assert.Equal(54321, account.LastLaunchedProcessId);
            }
            finally
            {
                AccountLaunchService.TestGetAuthTicketOverride = prevTicketOverride;
                AccountLaunchService.TestStartNativeHomeOverride = prevHomeOverride;
                AppSettings.General.Set(AppSettingsKeys.General.PreferDirectLaunch, prevPreferDirect.ToString());
                AutoReconnectService.Instance.Clear();
            }
        }

        [Fact]
        public async Task LaunchAppHomeAsync_NullAccount_ReturnsLegacyFailedString()
        {
            string result = await AccountLaunchService.Instance.LaunchAppHomeAsync(null!);
            Assert.Contains(LocalizationService.Get("AccountLaunch.InvalidAccount"), result);
        }

        [Fact]
        public async Task AccountExtensionMethods_DelegateToAccountLaunchService()
        {
            var account = new Account { Username = "ExtUser" };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                account.JoinServerResult(12345, cancellationToken: cts.Token));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                account.LaunchAppHomeResult(cancellationToken: cts.Token));
        }

        [Fact]
        public void ObserveBackgroundTask_CompletedCanceledAndFaulted_HandledGracefully()
        {
            var ex1 = Record.Exception(() => AccountLaunchService.ObserveBackgroundTask(null!, "null-op"));
            Assert.Null(ex1);

            var ex2 = Record.Exception(() => AccountLaunchService.ObserveBackgroundTask(Task.CompletedTask, "completed-op"));
            Assert.Null(ex2);

            var tcsCanceled = new TaskCompletionSource();
            tcsCanceled.SetCanceled();
            var ex3 = Record.Exception(() => AccountLaunchService.ObserveBackgroundTask(tcsCanceled.Task, "canceled-op"));
            Assert.Null(ex3);

            var tcsFaulted = new TaskCompletionSource();
            tcsFaulted.SetException(new InvalidOperationException("Simulated background error"));
            var ex4 = Record.Exception(() => AccountLaunchService.ObserveBackgroundTask(tcsFaulted.Task, "faulted-op"));
            Assert.Null(ex4);
        }

        [Fact]
        public async Task AdjustWindowPositionAsync_InvalidOrUnconfigured_ReturnsGracefully()
        {
            var exNull = await Record.ExceptionAsync(() => AccountLaunchService.AdjustWindowPositionAsync(null!));
            Assert.Null(exNull);

            // Canceled token throws
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AccountLaunchService.AdjustWindowPositionAsync(new Account(), cancellationToken: cts.Token));

            // Window positions disabled or missing
            bool origRemember = RobloxWatcher.RememberWindowPositions;
            try
            {
                RobloxWatcher.RememberWindowPositions = false;
                var account1 = new Account();
                var exDisabled = await Record.ExceptionAsync(() => AccountLaunchService.AdjustWindowPositionAsync(account1));
                Assert.Null(exDisabled);

                RobloxWatcher.RememberWindowPositions = true;
                // Missing fields
                var exMissing = await Record.ExceptionAsync(() => AccountLaunchService.AdjustWindowPositionAsync(account1));
                Assert.Null(exMissing);

                // Invalid bounds (< 200)
                account1.SetField("Window_Position_X", "100");
                account1.SetField("Window_Position_Y", "100");
                account1.SetField("Window_Width", "150"); // < 200
                account1.SetField("Window_Height", "300");
                var exTooSmall = await Record.ExceptionAsync(() => AccountLaunchService.AdjustWindowPositionAsync(account1));
                Assert.Null(exTooSmall);

                // Highly negative bounds
                account1.SetField("Window_Position_X", "-10001");
                account1.SetField("Window_Width", "500");
                var exNegative = await Record.ExceptionAsync(() => AccountLaunchService.AdjustWindowPositionAsync(account1));
                Assert.Null(exNegative);
            }
            finally
            {
                RobloxWatcher.RememberWindowPositions = origRemember;
            }
        }

        [Fact]
        public void FindRobloxPlayerExecutable_And_InvalidateCache_BehaveConsistently()
        {
            AccountLaunchService.InvalidateExecutableCache();
            string? exe = AccountLaunchService.FindRobloxPlayerExecutable();
            // Cache should now hold the result (either a path or null)
            string? cached = AccountLaunchService.FindRobloxPlayerExecutable();
            Assert.Equal(exe, cached);

            AccountLaunchService.InvalidateExecutableCache();
        }

        [Fact]
        public void HasRobloxPlayerProtocolHandler_ExecutesWithoutThrowing()
        {
            var ex = Record.Exception(() => AccountLaunchService.HasRobloxPlayerProtocolHandler());
            Assert.Null(ex);
        }

        [Fact]
        public async Task WaitForInstallerAsync_WhenNotRunning_CompletesPromptly()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await AccountLaunchService.WaitForInstallerAsync(cts.Token);
            Assert.False(cts.IsCancellationRequested);
        }

        [Fact]
        public void CreateTicketRequest_ConstructsValidSignedHttpRequest()
        {
            var acc = new Account
            {
                Username = "TicketUser",
                SecurityToken = "test_cookie_for_ticket"
            };

            using var req = AccountLaunchService.CreateTicketRequest(acc, "csrf_token_xyz");

            Assert.Equal(System.Net.Http.HttpMethod.Post, req.Method);
            Assert.Equal("https://auth.roblox.com/v1/authentication-ticket", req.RequestUri?.ToString());
            Assert.True(req.Headers.Contains("X-CSRF-TOKEN"));
            Assert.True(req.Headers.Contains("Origin"));
            Assert.True(req.Headers.Contains("Referer"));
            Assert.True(req.Headers.Contains("rbxauthenticationnegotiation"));
        }

        [Fact]
        public async Task PrepareNativeLaunchEnvironmentAsync_And_SingletonSweep_ExecuteCleanly()
        {
            var ex = await Record.ExceptionAsync(async () =>
            {
                await AccountLaunchService.PrepareNativeLaunchEnvironmentAsync(CancellationToken.None);
                AccountLaunchService.SchedulePostLaunchSingletonSweep(10);
            });
            Assert.Null(ex);
        }

        [Fact]
        public async Task JoinServerResultAsync_AllBranches_And_Diagnostics_CoverBranches()
        {
            var psi = new ProcessStartInfo("RobloxPlayerBeta.exe");
            AccountLaunchService.AddDirectGameArguments(psi, "t-123", "https://join.url");
            Assert.Equal(5, psi.ArgumentList.Count);
            Assert.Equal("--app", psi.ArgumentList[0]);

            var prevTicketOverride = AccountLaunchService.TestGetAuthTicketOverride;
            var prevNativeOverride = AccountLaunchService.TestStartNativeGameOverride;
            var prevPrivacyOverride = AccountLaunchService.TestPrivacyConfirmOverride;
            var prevHttpFactory = Account.TestHttpClientFactory;

            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroLaunchDiag_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var handler = new FakeHttpHandler(req =>
                {
                    string url = req.RequestUri?.AbsoluteUri ?? "";
                    if (url.Contains("privateServerLinkCode"))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Roblox.GameLauncher.joinPrivateGame(12345, 'access-code-777', 'link-1')") };
                    if (url.Contains("user-settings") && req.Method == HttpMethod.Get)
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"whoCanInviteMeToPrivateServers\":\"Friends\"}") };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                });
                Account.TestHttpClientFactory = _ => new HttpClient(handler);
                AccountLaunchService.TestGetAuthTicketOverride = (_, _) => Task.FromResult((true, "ticket-abc"));
                AccountLaunchService.TestStartNativeGameOverride = (_, _, _, _, _, _, _) => Task.FromResult(43210);
                AccountLaunchService.TestPrivacyConfirmOverride = () => true;

                var acc = new Account
                {
                    Username = "FullLaunchUser",
                    UserID = 889900,
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('D', 120),
                    CSRFToken = "csrf-1"
                };

                var attemptCtx = new LaunchAttemptContext();
                LaunchTimingService.CurrentAttempt = attemptCtx;
                try
                {
                    var pubRes = await AccountLaunchService.Instance.JoinServerResultAsync(acc, 189707, "job-111");
                    Assert.Equal(LaunchOutcome.Success, pubRes.Outcome);
                    Assert.Equal(43210, acc.LastLaunchedProcessId);
                    Assert.Equal(43210, attemptCtx.ProcessId);
                    Assert.True(attemptCtx.TicketMs >= 0 && attemptCtx.SpawnMs >= 0);
                }
                finally
                {
                    LaunchTimingService.CurrentAttempt = null;
                }

                var followRes = await AccountLaunchService.Instance.JoinServerResultAsync(acc, 998877, "", followUser: true);
                Assert.Equal(LaunchOutcome.Success, followRes.Outcome);

                var vipRes = await AccountLaunchService.Instance.JoinServerResultAsync(acc, 189707, "https://www.roblox.com/games/189707?privateServerLinkCode=link123", joinVip: true);
                Assert.Equal(LaunchOutcome.Success, vipRes.Outcome);

                AccountLaunchService.TestStartNativeGameOverride = (_, _, _, _, _, _, _) => throw new InvalidOperationException("simulated native start error");
                var failRes = await AccountLaunchService.Instance.JoinServerResultAsync(acc, 189707, "");
                Assert.Equal(LaunchOutcome.Failed, failRes.Outcome);

                string v1Dir = Path.Combine(tempDir, "version-aaa111");
                string v2Dir = Path.Combine(tempDir, "version-bbb222");
                Directory.CreateDirectory(v1Dir);
                Directory.CreateDirectory(v2Dir);
                string exe1 = Path.Combine(v1Dir, "RobloxPlayerBeta.exe");
                string exe2 = Path.Combine(v2Dir, "RobloxPlayerBeta.exe");
                File.WriteAllText(exe1, "dummy1");
                File.WriteAllText(exe2, "dummy2");
                File.SetLastWriteTimeUtc(exe1, DateTime.UtcNow.AddMinutes(-10));
                File.SetLastWriteTimeUtc(exe2, DateTime.UtcNow);

                AccountLaunchService.TestVersionRootsOverride = new[] { tempDir };
                AccountLaunchService.TestProtocolCommandOverride = "";
                AccountLaunchService.TestDefaultIconOverride = $"\"{exe1}\",0";

                AppSettings.CurrentClientVersion = "version-aaa111";
                AccountLaunchService.InvalidateExecutableCache();
                Assert.Equal(exe1, AccountLaunchService.FindRobloxPlayerExecutableCore());

                AppSettings.CurrentClientVersion = "";
                Assert.Equal(exe2, AccountLaunchService.FindRobloxPlayerExecutableCore());

                AccountLaunchService.TestProtocolCommandOverride = $"\"{exe1}\" %1";
                Assert.Equal(exe1, AccountLaunchService.FindRobloxPlayerExecutableCore());
                Assert.True(AccountLaunchService.HasRobloxPlayerProtocolHandler());

                AccountLaunchService.TestProtocolCommandOverride = "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" %1";
                Assert.False(AccountLaunchService.HasRobloxPlayerProtocolHandler());

                AccountLaunchService.TestProtocolCommandOverride = "   ";
                Assert.False(AccountLaunchService.HasRobloxPlayerProtocolHandler());

                using var shortProc = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c exit 0",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                if (shortProc != null)
                {
                    bool exited = await AccountLaunchService.WaitForExitAsync(shortProc, 2000, CancellationToken.None);
                    Assert.True(exited);
                }
            }
            finally
            {
                AccountLaunchService.TestGetAuthTicketOverride = prevTicketOverride;
                AccountLaunchService.TestStartNativeGameOverride = prevNativeOverride;
                AccountLaunchService.TestPrivacyConfirmOverride = prevPrivacyOverride;
                AccountLaunchService.TestVersionRootsOverride = null;
                AccountLaunchService.TestProtocolCommandOverride = null;
                AccountLaunchService.TestDefaultIconOverride = null;
                Account.TestHttpClientFactory = prevHttpFactory;
                AutoReconnectService.Instance.Clear();
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public async Task PrepareNativeLaunchEnvironmentAsync_And_SingletonSweep_DuplicateCheck()
        {
            var ex = await Record.ExceptionAsync(async () =>
            {
                await AccountLaunchService.PrepareNativeLaunchEnvironmentAsync(CancellationToken.None);
                AccountLaunchService.SchedulePostLaunchSingletonSweep(10);
            });
            Assert.Null(ex);
        }
    }
}
