#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public partial class AccountLaunchServiceBranchTests : IDisposable
    {
        public AccountLaunchServiceBranchTests()
        {
            ResetOverrides();
        }

        public void Dispose()
        {
            ResetOverrides();
        }

        private static void ResetOverrides()
        {
            AccountLaunchService.TestStartNativeGameOverride = null;
            AccountLaunchService.TestGetAuthTicketOverride = null;
            AccountLaunchService.TestPrivacyConfirmOverride = null;
            AccountLaunchService.TestVersionRootsOverride = null;
            AccountLaunchService.TestProtocolCommandOverride = null;
            AccountLaunchService.TestDefaultIconOverride = null;
            RobloxProcessService.TestGetRunningRobloxPidsOverride = null;
            AccountLaunchService.InvalidateExecutableCache();
        }

        [Fact]
        public async Task PrepareNativeLaunchEnvironmentAsync_CoversSettingsBranches()
        {
            bool origZombies = AppSettings.General.Get<bool>("AutoCleanZombiesOnLaunch", true);
            bool origMulti = AppSettings.General.Get<bool>("EnableMultiRbx", true);
            try
            {
                AppSettings.General.Set("AutoCleanZombiesOnLaunch", "true");
                AppSettings.General.Set("EnableMultiRbx", "true");
                await AccountLaunchService.PrepareNativeLaunchEnvironmentAsync(CancellationToken.None);
                Assert.True(AppSettings.General.Get<bool>("AutoCleanZombiesOnLaunch"));

                AppSettings.General.Set("AutoCleanZombiesOnLaunch", "false");
                AppSettings.General.Set("EnableMultiRbx", "false");
                await AccountLaunchService.PrepareNativeLaunchEnvironmentAsync(CancellationToken.None);
                Assert.False(AppSettings.General.Get<bool>("AutoCleanZombiesOnLaunch"));
            }
            finally
            {
                AppSettings.General.Set("AutoCleanZombiesOnLaunch", origZombies.ToString().ToLowerInvariant());
                AppSettings.General.Set("EnableMultiRbx", origMulti.ToString().ToLowerInvariant());
            }
        }

        [Fact]
        public void SchedulePostLaunchSingletonSweep_MultiRbxDisabled_ReturnsImmediately()
        {
            bool origMulti = AppSettings.General.Get<bool>("EnableMultiRbx", true);
            try
            {
                AppSettings.General.Set("EnableMultiRbx", "false");
                AccountLaunchService.SchedulePostLaunchSingletonSweep(10);
                Assert.False(AppSettings.General.Get<bool>("EnableMultiRbx"));

                AppSettings.General.Set("EnableMultiRbx", "true");
                AccountLaunchService.SchedulePostLaunchSingletonSweep(10);
                Assert.True(AppSettings.General.Get<bool>("EnableMultiRbx"));
            }
            finally
            {
                AppSettings.General.Set("EnableMultiRbx", origMulti.ToString().ToLowerInvariant());
            }
        }

        [Fact]
        public void CreateTicketRequest_CoversCsrfAndBodyBranches()
        {
            var account = new Account { Username = "TicketBranchUser", UserID = 12345 };

            using var req1 = AccountLaunchService.CreateTicketRequest(account, csrfToken: null, body: "{}");
            Assert.Equal(HttpMethod.Post, req1.Method);
            Assert.False(req1.Headers.Contains("X-CSRF-TOKEN"));

            using var req2 = AccountLaunchService.CreateTicketRequest(account, csrfToken: "test-csrf-token", body: "{\"test\":1}");
            Assert.True(req2.Headers.Contains("X-CSRF-TOKEN"));
        }

        [Fact]
        public void AddDirectGameArguments_PopulatesArgumentList()
        {
            var psi = new ProcessStartInfo();
            AccountLaunchService.AddDirectGameArguments(psi, "mockTicket", "https://roblox.com/join");
            Assert.Contains("--app", psi.ArgumentList);
            Assert.Contains("-t", psi.ArgumentList);
            Assert.Contains("mockTicket", psi.ArgumentList);
            Assert.Contains("-j", psi.ArgumentList);
            Assert.Contains("https://roblox.com/join", psi.ArgumentList);
        }

        [Fact]
        public async Task JoinServerResultAsync_FollowUser_UsesFollowUserJoinUrl()
        {
            string? capturedJoinUrl = null;
            AccountLaunchService.TestGetAuthTicketOverride = (acc, token) => Task.FromResult((true, "mockTicketFollow"));
            AccountLaunchService.TestStartNativeGameOverride = (uri, ticket, joinUrl, user, tracker, sec, ct) =>
            {
                capturedJoinUrl = joinUrl;
                return Task.FromResult(4321);
            };

            var account = new Account { Username = "FollowTestUser", UserID = 998877 };
            var result = await AccountLaunchService.Instance.JoinServerResultAsync(account, 998877, followUser: true);

            Assert.Equal(LaunchOutcome.Success, result.Outcome);
            Assert.NotNull(capturedJoinUrl);
            Assert.Contains("RequestFollowUser", capturedJoinUrl);
            Assert.Contains("userId=998877", capturedJoinUrl);
        }

        [Fact]
        public async Task JoinServerResultAsync_JoinVip_UsesPrivateGameJoinUrl()
        {
            string? capturedJoinUrl = null;
            AccountLaunchService.TestGetAuthTicketOverride = (acc, token) => Task.FromResult((true, "mockTicketVip"));
            AccountLaunchService.TestStartNativeGameOverride = (uri, ticket, joinUrl, user, tracker, sec, ct) =>
            {
                capturedJoinUrl = joinUrl;
                return Task.FromResult(5432);
            };

            var account = new Account { Username = "VipTestUser", UserID = 112233 };
            var result = await AccountLaunchService.Instance.JoinServerResultAsync(
                account,
                placeId: 1818,
                jobId: "sampleJobCode",
                joinVip: true);

            Assert.Equal(LaunchOutcome.Success, result.Outcome);
            Assert.NotNull(capturedJoinUrl);
            Assert.Contains("RequestPrivateGame", capturedJoinUrl);
            Assert.Contains("placeId=1818", capturedJoinUrl);
        }

        [Fact]
        public async Task JoinServerResultAsync_LaunchException_ReturnsFailOutcome()
        {
            AccountLaunchService.TestGetAuthTicketOverride = (acc, token) => Task.FromResult((true, "mockTicketCrash"));
            AccountLaunchService.TestStartNativeGameOverride = (uri, ticket, joinUrl, user, tracker, sec, ct) =>
                throw new InvalidOperationException("Simulated process startup crash");

            var account = new Account { Username = "CrashUser", UserID = 445566 };
            var result = await AccountLaunchService.Instance.JoinServerResultAsync(account, 12345);

            Assert.Equal(LaunchOutcome.Failed, result.Outcome);
            Assert.Contains("Simulated process startup crash", result.Message);
        }

        [Fact]
        public async Task JoinServerAsync_CallsJoinServerResultAsyncAndReturnsLegacyString()
        {
            AccountLaunchService.TestGetAuthTicketOverride = (acc, token) => Task.FromResult((true, "mockTicketLegacy"));
            AccountLaunchService.TestStartNativeGameOverride = (uri, ticket, joinUrl, user, tracker, sec, ct) => Task.FromResult(9876);

            var account = new Account { Username = "LegacyLaunchUser", UserID = 778899 };
            string legacyResult = await AccountLaunchService.Instance.JoinServerAsync(account, 12345);

            Assert.Equal("Success", legacyResult);
        }

        [Fact]
        public void FindRobloxPlayerExecutable_CacheHitAndInvalidate()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroFindExe_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string fakeExe = Path.Combine(tempDir, "RobloxPlayerBeta.exe");
                File.WriteAllText(fakeExe, "MZ");

                AccountLaunchService.TestVersionRootsOverride = new[] { tempDir };
                AccountLaunchService.TestDefaultIconOverride = "invalid";
                AccountLaunchService.TestProtocolCommandOverride = $"\"{fakeExe}\" %1";
                string? first = AccountLaunchService.FindRobloxPlayerExecutable();
                Assert.Equal(fakeExe, first);

                // Second call should hit memory cache without re-reading
                AccountLaunchService.TestProtocolCommandOverride = "invalid";
                string? cached = AccountLaunchService.FindRobloxPlayerExecutable();
                Assert.Equal(fakeExe, cached);

                // After invalidating, cache miss occurs; since TestProtocolCommandOverride and TestDefaultIconOverride are invalid and no version-* subdirs exist
                AccountLaunchService.InvalidateExecutableCache();
                string? invalidated = AccountLaunchService.FindRobloxPlayerExecutable();
                Assert.Null(invalidated);
            }
            finally
            {
                AccountLaunchService.TestDefaultIconOverride = null;
                AccountLaunchService.TestVersionRootsOverride = null;
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void FindRobloxPlayerExecutableCore_VersionAndDateComparison_PicksHighest()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroFindVersions_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string v1Dir = Path.Combine(tempDir, "version-aaa");
                string v2Dir = Path.Combine(tempDir, "version-bbb");
                Directory.CreateDirectory(v1Dir);
                Directory.CreateDirectory(v2Dir);

                string exe1 = Path.Combine(v1Dir, "RobloxPlayerBeta.exe");
                string exe2 = Path.Combine(v2Dir, "RobloxPlayerBeta.exe");
                File.WriteAllText(exe1, "MZ1");
                File.WriteAllText(exe2, "MZ2");
                File.SetLastWriteTimeUtc(exe1, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(exe2, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

                AccountLaunchService.TestVersionRootsOverride = new[] { tempDir };
                AccountLaunchService.TestProtocolCommandOverride = "invalid";
                AccountLaunchService.TestDefaultIconOverride = $"\"{exe1}\",0";

                string? best = AccountLaunchService.FindRobloxPlayerExecutableCore();
                Assert.Equal(exe2, best);
            }
            finally
            {
                AccountLaunchService.TestVersionRootsOverride = null;
                AccountLaunchService.TestDefaultIconOverride = null;
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Theory]
        [InlineData("\"C:\\Browser\\chrome.exe\" --url %1", false)]
        [InlineData("\"C:\\Browser\\msedge.exe\" --url %1", false)]
        [InlineData("\"C:\\Browser\\firefox.exe\" --url %1", false)]
        [InlineData("\"C:\\Program Files\\Roblox\\RobloxPlayerBeta.exe\" %1", true)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData("some-other-app %1", false)]
        public void HasRobloxPlayerProtocolHandler_EvaluatesCommandCorrectly(string command, bool expected)
        {
            AccountLaunchService.TestProtocolCommandOverride = command;
            bool result = AccountLaunchService.HasRobloxPlayerProtocolHandler();
            Assert.Equal(expected, result);
        }

        [Fact]
        public async Task WaitForExitAsync_HandlesNullAndCurrentProcess()
        {
            bool nullResult = await AccountLaunchService.WaitForExitAsync(null!, 100, CancellationToken.None);
            Assert.True(nullResult);

            using var currentProc = Process.GetCurrentProcess();
            bool timeoutResult = await AccountLaunchService.WaitForExitAsync(currentProc, 5, CancellationToken.None);
            Assert.False(timeoutResult);
        }

        [Fact]
        public async Task ResolveNewClientPidAsync_ReturnsLauncherPidOrZeroOnTimeout()
        {
            using var cts = new CancellationTokenSource();
            var existing = new HashSet<int>(RobloxProcessService.GetRunningRobloxPids());
            int timedOutPid = await AccountLaunchService.ResolveNewClientPidAsync(
                existing,
                launcherPid: -1,
                timeout: TimeSpan.FromMilliseconds(50),
                cancellationToken: cts.Token);
            Assert.Equal(0, timedOutPid);
        }

        [Fact]
        public async Task StartNativeGameAsync_NoProtocolAndNoExecutable_ThrowsFileNotFoundException()
        {
            string emptyDir = Path.Combine(Path.GetTempPath(), "EmptyRobloxDir_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(emptyDir);
            bool origPreferDirect = AppSettings.General.Get<bool>(AppSettingsKeys.General.PreferDirectLaunch, false);
            try
            {
                AppSettings.General.Set(AppSettingsKeys.General.PreferDirectLaunch, "true");
                AccountLaunchService.TestProtocolCommandOverride = "invalid";
                AccountLaunchService.TestDefaultIconOverride = "invalid";
                AccountLaunchService.TestVersionRootsOverride = new[] { emptyDir };
                AccountLaunchService.InvalidateExecutableCache();

                await Assert.ThrowsAsync<FileNotFoundException>(() =>
                    AccountLaunchService.StartNativeGameAsync(
                        "roblox-player:test",
                        "ticket123",
                        "https://roblox.com/join",
                        "UserNoRoblox",
                        cancellationToken: CancellationToken.None));
            }
            finally
            {
                AppSettings.General.Set(AppSettingsKeys.General.PreferDirectLaunch, origPreferDirect.ToString().ToLowerInvariant());
                AccountLaunchService.TestProtocolCommandOverride = null;
                AccountLaunchService.TestDefaultIconOverride = null;
                AccountLaunchService.TestVersionRootsOverride = null;
                AccountLaunchService.InvalidateExecutableCache();
                if (Directory.Exists(emptyDir)) Directory.Delete(emptyDir, true);
            }
        }
    }
}
