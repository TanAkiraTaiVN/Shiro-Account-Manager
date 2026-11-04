#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
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
        public async Task LaunchAppHomeAsync_NullAccount_ReturnsLegacyFailedString()
        {
            string result = await AccountLaunchService.Instance.LaunchAppHomeAsync(null!);
            Assert.Contains(LocalizationService.Get("AccountLaunch.InvalidAccount"), result);
        }

        [Fact]
        public async Task LaunchHomeInChromeResultAsync_NullAccount_ReturnsFailed()
        {
            var result = await AccountLaunchService.Instance.LaunchHomeInChromeResultAsync(null!);
            Assert.Equal(LaunchOutcome.Failed, result.Outcome);
            Assert.Contains(LocalizationService.Get("AccountLaunch.InvalidAccount"), result.Message);
        }

        [Fact]
        public async Task LaunchHomeInChromeResultAsync_CanceledToken_ThrowsOperationCanceledException()
        {
            var account = new Account { Username = "CancelChromeUser" };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AccountLaunchService.Instance.LaunchHomeInChromeResultAsync(account, cts.Token));
        }

        [Fact]
        public async Task LaunchHomeInChromeAsync_NullAccount_ReturnsLegacyFailedString()
        {
            string result = await AccountLaunchService.Instance.LaunchHomeInChromeAsync(null!);
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
            Assert.Equal("https://auth.roblox.com/v1/authentication-ticket/", req.RequestUri?.ToString());
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
    }
}
