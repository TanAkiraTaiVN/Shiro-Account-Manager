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
    public partial class AccountLaunchServiceBranchTests
    {
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
                    if (url.Contains("user-settings") && req.Method == HttpMethod.Get)
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(settingsBody) };
                    if (url.Contains("user-settings") && req.Method == HttpMethod.Post)
                        return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                });
                Account.TestHttpClientFactory = _ => new HttpClient(vipHandler);

                var vipAcc = new Account
                {
                    Username = "VipBranchUser",
                    UserID = 556677,
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('F', 120),
                    CSRFToken = "csrf-vip"
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
                var resPrivacyFail = await AccountLaunchService.Instance.JoinServerResultAsync(vipAcc, 1818, "code1", joinVip: true);
                Assert.Equal(LaunchOutcome.Failed, resPrivacyFail.Outcome);
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
