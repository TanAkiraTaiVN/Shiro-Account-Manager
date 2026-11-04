#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountLaunchAndLifecycleComprehensiveTests : IDisposable
    {
        public void Dispose()
        {
            AppSettings.General.Set("EnableMultiRbx", "true");
            RobloxSingletonService.Apply(true);
        }
        [Fact]
        public void CreateTicketRequest_BuildsPostRequestWithExpectedHeaders()
        {
            var account = new Account
            {
                Username = "TicketUser",
                UserID = 998877,
                SecurityToken = "SampleRobloSecurityToken"
            };

            using HttpRequestMessage request = AccountLaunchService.CreateTicketRequest(account, "test-csrf-token", "{\"sample\":1}");

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(new Uri("https://auth.roblox.com/v1/authentication-ticket/"), request.RequestUri);
            Assert.True(request.Headers.Contains("X-CSRF-TOKEN"));
            Assert.True(request.Headers.Contains("Origin"));
            Assert.True(request.Headers.Contains("Referer"));
            Assert.True(request.Headers.Contains("rbxauthenticationnegotiation"));
            Assert.NotNull(request.Content);
        }

        [Fact]
        public void CreateTicketRequest_WithoutCsrf_OmitsCsrfHeader()
        {
            var account = new Account { Username = "NoCsrfUser" };
            using HttpRequestMessage request = AccountLaunchService.CreateTicketRequest(account, null, "{}");

            Assert.False(request.Headers.Contains("X-CSRF-TOKEN"));
            Assert.Equal("https://auth.roblox.com/v1/authentication-ticket/", request.RequestUri?.ToString());
        }

        [Fact]
        public async Task ObserveBackgroundTask_HandlesCompletedTaskSafely()
        {
            var exception = await Record.ExceptionAsync(async () =>
            {
                AccountLaunchService.ObserveBackgroundTask(Task.CompletedTask, "test completed");
                await Task.Delay(20);
            });
            Assert.Null(exception);
        }

        [Fact]
        public async Task ObserveBackgroundTask_HandlesCanceledTaskSafely()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var tcs = new TaskCompletionSource();
            tcs.SetCanceled(cts.Token);

            var exception = await Record.ExceptionAsync(async () =>
            {
                AccountLaunchService.ObserveBackgroundTask(tcs.Task, "test canceled");
                await Task.Delay(20);
            });
            Assert.Null(exception);
        }

        [Fact]
        public async Task ObserveBackgroundTask_HandlesFaultedTaskSafely()
        {
            var faulted = Task.FromException(new InvalidOperationException("Simulation error"));

            var exception = await Record.ExceptionAsync(async () =>
            {
                AccountLaunchService.ObserveBackgroundTask(faulted, "test faulted");
                await Task.Delay(20);
            });
            Assert.Null(exception);
        }

        [Fact]
        public async Task AdjustWindowPositionAsync_WithNullAccount_ReturnsImmediately()
        {
            var exception = await Record.ExceptionAsync(() => AccountLaunchService.AdjustWindowPositionAsync(null!));
            Assert.Null(exception);
        }

        [Fact]
        public async Task AdjustWindowPositionAsync_WithCanceledToken_ThrowsOperationCanceledException()
        {
            var account = new Account { Username = "CanceledAccount" };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AccountLaunchService.AdjustWindowPositionAsync(account, 0, cts.Token));
        }

        [Fact]
        public async Task AdjustWindowPositionAsync_WithInvalidOrMissingBounds_ReturnsEarlyWithoutMoving()
        {
            var account = new Account { Username = "WindowBoundsAccount" };
            account.SetField("Window_Position_X", "-15000");
            account.SetField("Window_Position_Y", "-15000");
            account.SetField("Window_Width", "100");
            account.SetField("Window_Height", "100");

            var exception = await Record.ExceptionAsync(() => AccountLaunchService.AdjustWindowPositionAsync(account));
            Assert.Null(exception);
        }

        [Fact]
        public async Task MonitorNativeAuthRateLimitAsync_WithCanceledToken_ThrowsOperationCanceledException()
        {
            var account = new Account { Username = "RateLimitedAccount" };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AccountLaunchService.MonitorNativeAuthRateLimitAsync(DateTime.UtcNow, account, cts.Token));
        }

        [Fact]
        public async Task WaitForExitAsync_WithNullProcess_ReturnsTrue()
        {
            bool exited = await AccountLaunchService.WaitForExitAsync(null!, 500, CancellationToken.None);
            Assert.True(exited);
        }

        [Fact]
        public async Task ResolveNewClientPidAsync_WithZeroMatchingPids_ReturnsZeroAfterTimeout()
        {
            var existingPids = new HashSet<int> { 11111, 22222 };
            int resolved = await AccountLaunchService.ResolveNewClientPidAsync(
                existingPids,
                launcherPid: -1,
                timeout: TimeSpan.FromMilliseconds(50),
                cancellationToken: CancellationToken.None);

            Assert.Equal(0, resolved);
        }

        [Fact]
        public void FindRobloxPlayerExecutable_InvalidatesAndQueriesCache()
        {
            AccountLaunchService.InvalidateExecutableCache();
            string? firstPath = AccountLaunchService.FindRobloxPlayerExecutable();

            // Cached call
            string? secondPath = AccountLaunchService.FindRobloxPlayerExecutable();
            Assert.Equal(firstPath, secondPath);

            AccountLaunchService.InvalidateExecutableCache();
        }

        [Fact]
        public void HasRobloxPlayerProtocolHandler_ExecutesSafely()
        {
            var exception = Record.Exception(() => AccountLaunchService.HasRobloxPlayerProtocolHandler());
            Assert.Null(exception);
        }

        [Fact]
        public void SchedulePostLaunchSingletonSweep_WhenDisabled_DoesNotThrow()
        {
            bool wasMulti = AppSettings.General.Get<bool>("EnableMultiRbx", true);
            try
            {
                AppSettings.General.Set("EnableMultiRbx", "false");
                var exception = Record.Exception(() => AccountLaunchService.SchedulePostLaunchSingletonSweep(10));
                Assert.Null(exception);
            }
            finally
            {
                AppSettings.General.Set("EnableMultiRbx", wasMulti ? "true" : "false");
            }
        }
    }
}
