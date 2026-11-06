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
using Shiro.Classes;
using Shiro.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountLaunchServiceBranchTests : IDisposable
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

        [Fact]
        public async Task JoinServerResultAsync_FailedAuthTicket_ReturnsRateLimitedOrTicketError()
        {
            AccountLaunchService.TestGetAuthTicketOverride = (acc, token) => Task.FromResult((false, string.Empty));

            var accountRateLimited = new Account { Username = "RateLimitedUser", LastAuthTicketRateLimited = true };
            var res1 = await AccountLaunchService.Instance.JoinServerResultAsync(accountRateLimited, 12345);
            Assert.Equal(LaunchOutcome.RateLimited, res1.Outcome);

            var accountTicketError = new Account { Username = "TicketErrorUser", LastAuthTicketRateLimited = false };
            var res2 = await AccountLaunchService.Instance.JoinServerResultAsync(accountTicketError, 12345);
            Assert.Equal(LaunchOutcome.TicketError, res2.Outcome);
        }

        [Fact]
        public void FindRobloxPlayerExecutableCore_CurrentClientVersion_FindsExe()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroCurrentVersion_" + Guid.NewGuid().ToString("N"));
            string vFolder = Path.Combine(tempDir, "version-exact");
            Directory.CreateDirectory(vFolder);
            try
            {
                string fakeExe = Path.Combine(vFolder, "RobloxPlayerBeta.exe");
                File.WriteAllText(fakeExe, "MZ_EXACT");

                string? origVersion = AppSettings.CurrentClientVersion;
                try
                {
                    AppSettings.CurrentClientVersion = "version-exact";
                    AccountLaunchService.TestProtocolCommandOverride = "invalid";
                    AccountLaunchService.TestDefaultIconOverride = "invalid";
                    AccountLaunchService.TestVersionRootsOverride = new[] { tempDir };

                    string? found = AccountLaunchService.FindRobloxPlayerExecutableCore();
                    Assert.Equal(fakeExe, found);
                }
                finally
                {
                    AppSettings.CurrentClientVersion = origVersion ?? string.Empty;
                }
            }
            finally
            {
                AccountLaunchService.TestProtocolCommandOverride = null;
                AccountLaunchService.TestDefaultIconOverride = null;
                AccountLaunchService.TestVersionRootsOverride = null;
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void FindRobloxPlayerExecutableCore_HandlesCandidateVariations()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroCandTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string fakeExe = Path.Combine(tempDir, "RobloxPlayerBeta.exe");
                File.WriteAllText(fakeExe, "MZ");

                // 1. Icon path without comma
                AccountLaunchService.TestProtocolCommandOverride = "invalid";
                AccountLaunchService.TestDefaultIconOverride = fakeExe;
                AccountLaunchService.TestVersionRootsOverride = new[] { tempDir };
                string? found1 = AccountLaunchService.FindRobloxPlayerExecutableCore();
                Assert.Equal(fakeExe, found1);

                // 2. Icon path that doesn't exist
                AccountLaunchService.TestDefaultIconOverride = "C:\\NonExistent\\RobloxPlayerBeta.exe,0";
                string? found2 = AccountLaunchService.FindRobloxPlayerExecutableCore();
                Assert.Null(found2);

                // 3. Duplicate paths in candidates
                string subDir = Path.Combine(tempDir, "version-123");
                Directory.CreateDirectory(subDir);
                string fakeSubExe = Path.Combine(subDir, "RobloxPlayerBeta.exe");
                File.WriteAllText(fakeSubExe, "MZ");
                AccountLaunchService.TestDefaultIconOverride = fakeSubExe;
                string? found3 = AccountLaunchService.FindRobloxPlayerExecutableCore();
                Assert.Equal(fakeSubExe, found3);
            }
            finally
            {
                AccountLaunchService.TestProtocolCommandOverride = null;
                AccountLaunchService.TestDefaultIconOverride = null;
                AccountLaunchService.TestVersionRootsOverride = null;
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task WaitForExitAsync_UserCancellationRequested_ThrowsOperationCanceledException()
        {
            using var proc = Process.GetCurrentProcess();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AccountLaunchService.WaitForExitAsync(proc, 1000, cts.Token));
        }

        [Fact]
        public async Task ResolveNewClientPidAsync_CanceledToken_ThrowsOperationCanceledException()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AccountLaunchService.ResolveNewClientPidAsync(new HashSet<int>(), 0, TimeSpan.FromSeconds(1), cts.Token));
        }

        [Fact]
        public async Task ResolveNewClientPidAsync_CoversPidOverrideAndLauncherMatchBranches()
        {
            RobloxProcessService.TestGetRunningRobloxPidsOverride = () => new[] { 1001, 1002 };
            int matchedLauncher = await AccountLaunchService.ResolveNewClientPidAsync(
                new HashSet<int> { 1001, 1002 },
                launcherPid: 1002,
                timeout: TimeSpan.FromMilliseconds(200),
                cancellationToken: CancellationToken.None);
            Assert.Equal(1002, matchedLauncher);

            RobloxProcessService.TestGetRunningRobloxPidsOverride = () => new[] { 1001, 2002 };
            int newClient = await AccountLaunchService.ResolveNewClientPidAsync(
                new HashSet<int> { 1001 },
                launcherPid: 9999,
                timeout: TimeSpan.FromMilliseconds(200),
                cancellationToken: CancellationToken.None);
            Assert.Equal(2002, newClient);

            RobloxProcessService.TestGetRunningRobloxPidsOverride = () => new[] { 3003 };
            int fromNullExisting = await AccountLaunchService.ResolveNewClientPidAsync(
                null!,
                launcherPid: 0,
                timeout: TimeSpan.FromMilliseconds(200),
                cancellationToken: CancellationToken.None);
            Assert.Equal(3003, fromNullExisting);
        }

        [Fact]
        public void FindRobloxPlayerExecutableCore_CoversEmptyRootsMissingTargetAndNullOverrides()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroDiagEdge_" + Guid.NewGuid().ToString("N"));
            string emptyVersionDir = Path.Combine(tempDir, "version-empty");
            Directory.CreateDirectory(emptyVersionDir);
            string? origVersion = AppSettings.CurrentClientVersion;

            try
            {
                AppSettings.CurrentClientVersion = "version-missing";
                AccountLaunchService.TestVersionRootsOverride = new[] { string.Empty, Path.Combine(tempDir, "missing-root"), tempDir };
                AccountLaunchService.TestProtocolCommandOverride = "\"C:\\NonExistentRobloxDir\\RobloxPlayerBeta.exe\" %1";
                AccountLaunchService.TestDefaultIconOverride = "invalid";

                string? missing = AccountLaunchService.FindRobloxPlayerExecutableCore();
                Assert.Null(missing);

                AppSettings.CurrentClientVersion = string.Empty;
                AccountLaunchService.TestVersionRootsOverride = null;
                AccountLaunchService.TestProtocolCommandOverride = null;
                AccountLaunchService.TestDefaultIconOverride = null;

                _ = AccountLaunchService.HasRobloxPlayerProtocolHandler();
                string? hostResolved = AccountLaunchService.FindRobloxPlayerExecutableCore();
                Assert.True(hostResolved == null || hostResolved.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                AppSettings.CurrentClientVersion = origVersion ?? string.Empty;
                ResetOverrides();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task WaitForInstallerAsync_WithShortLivedInstallerStub_WaitsAndRefreshes()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroInstallerStub_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string pingSource = Path.Combine(Environment.SystemDirectory, "ping.exe");
            string stubInstaller = Path.Combine(tempDir, "RobloxPlayerInstaller.exe");

            try
            {
                File.Copy(pingSource, stubInstaller, overwrite: true);
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = stubInstaller,
                    Arguments = "-n 2 127.0.0.1",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                Assert.NotNull(proc);

                RobloxProcessService.InvalidateSnapshotCache();
                await AccountLaunchService.WaitForInstallerAsync(CancellationToken.None);
                Assert.True(proc!.HasExited);
            }
            finally
            {
                RobloxProcessService.InvalidateSnapshotCache();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public async Task JoinServerResultAsync_CoversShuffleJobIdAndVipPrivacyBranches()
        {
            bool origShuffle = AppSettings.ShuffleJobId;
            var prevApi = AppServices.RobloxApi;
            var prevHttpFactory = Account.TestHttpClientFactory;

            try
            {
                string? capturedJoinUrl = null;
                AccountLaunchService.TestGetAuthTicketOverride = (_, _) => Task.FromResult((true, "ticket-shuffle"));
                AccountLaunchService.TestStartNativeGameOverride = (_, _, joinUrl, _, _, _, _) =>
                {
                    capturedJoinUrl = joinUrl;
                    return Task.FromResult(6789);
                };

                var gamesHandler = new FakeHttpHandler(_ =>
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":[{\"id\":\"shuffled-job-42\",\"playing\":2,\"maxPlayers\":10}]}")
                    });
                var apiSub = Substitute.For<IRobloxApiClient>();
                apiSub.Games.Returns(new RestClient(new HttpClient(gamesHandler), "https://games.roblox.com/"));
                apiSub.Main.Returns(prevApi.Main);
                apiSub.Api.Returns(prevApi.Api);
                apiSub.Auth.Returns(prevApi.Auth);
                using var _ = AppServices.Override<IRobloxApiClient>(apiSub);

                AppSettings.ShuffleJobId = true;
                var shuffleAcc = new Account { Username = "ShuffleUser", UserID = 334455 };
                var shuffleRes = await AccountLaunchService.Instance.JoinServerResultAsync(shuffleAcc, 1818, jobId: string.Empty);
                Assert.Equal(LaunchOutcome.Success, shuffleRes.Outcome);
                Assert.Contains("gameId=shuffled-job-42", capturedJoinUrl ?? string.Empty);

                AppSettings.ShuffleJobId = false;

                string settingsBody = "{\"whoCanInviteMeToPrivateServers\":\"AllUsers\"}";
                var vipHandler = new FakeHttpHandler(req =>
                {
                    string url = req.RequestUri?.AbsoluteUri ?? string.Empty;
                    if (url.Contains("privateServerLinkCode"))
                        return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("denied") };
                    if (url.Contains("v1/account/pin"))
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
                    if (url.Contains("user-settings") && req.Method == HttpMethod.Get)
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(settingsBody) };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                });
                Account.TestHttpClientFactory = _ => new HttpClient(vipHandler);

                var vipAcc = new Account
                {
                    Username = "VipBranchUser",
                    UserID = 556677,
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('F', 120),
                    CSRFToken = "csrf-vip",
                    PinUnlocked = DateTime.MinValue
                };

                var resAllUsers = await AccountLaunchService.Instance.JoinServerResultAsync(
                    vipAcc, 1818, "https://www.roblox.com/games/1818?privateServerLinkCode=abc123", joinVip: true);
                Assert.Equal(LaunchOutcome.Success, resAllUsers.Outcome);
                Assert.Contains("linkCode=abc123", capturedJoinUrl ?? string.Empty);
                Assert.DoesNotContain("accessCode=", capturedJoinUrl ?? string.Empty);

                settingsBody = "{malformed-json";
                var resBadJson = await AccountLaunchService.Instance.JoinServerResultAsync(vipAcc, 1818, "code1", joinVip: true);
                Assert.Equal(LaunchOutcome.Success, resBadJson.Outcome);

                settingsBody = "{\"whoCanInviteMeToPrivateServers\":\"Friends\"}";
                AccountLaunchService.TestPrivacyConfirmOverride = () => false;
                var resDeclined = await AccountLaunchService.Instance.JoinServerResultAsync(vipAcc, 1818, "code1", joinVip: true);
                Assert.Equal(LaunchOutcome.Success, resDeclined.Outcome);

                AccountLaunchService.TestPrivacyConfirmOverride = () => true;
                var resPinFail = await AccountLaunchService.Instance.JoinServerResultAsync(vipAcc, 1818, "code1", joinVip: true);
                Assert.Equal(LaunchOutcome.Failed, resPinFail.Outcome);
            }
            finally
            {
                AppSettings.ShuffleJobId = origShuffle;
                Account.TestHttpClientFactory = prevHttpFactory;
                AutoReconnectService.Instance.Clear();
                ResetOverrides();
            }
        }
    }
}
