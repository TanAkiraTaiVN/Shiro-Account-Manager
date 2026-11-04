#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountLauncherAndHelperBehaviorTests
    {
        [Theory]
        [InlineData("https://www.roblox.com/games/189707/blox-fruits", 189707L)]
        [InlineData("https://www.roblox.com/games/9876543210?", 9876543210L)]
        [InlineData("12345678", 12345678L)]
        [InlineData("invalid-url-or-name", 0L)]
        [InlineData("", 0L)]
        [InlineData(null, 0L)]
        public void ParsePlaceId_ParsesVariousFormatsCorrectly(string? input, long expected)
        {
            long placeId = AccountLauncherService.ParsePlaceId(input!);
            Assert.Equal(expected, placeId);
        }

        [Fact]
        public async Task AccountLauncherService_LaunchAppHomeAsync_WithNull_ReturnsSafely()
        {
            var ex = await Record.ExceptionAsync(() => AccountLauncherService.LaunchAppHomeAsync(null!));
            Assert.Null(ex);
        }

        [Fact]
        public async Task AccountLauncherService_JoinServerAsync_WithNullAccounts_ReturnsSafely()
        {
            var ex = await Record.ExceptionAsync(() => AccountLauncherService.JoinServerAsync(null, 189707, null));
            Assert.Null(ex);
        }

        [Fact]
        public void ToRobloxTick_UnixEpoch_ReturnsZero()
        {
            double tick = DateTime.UnixEpoch.ToRobloxTick();
            Assert.Equal(0.0, tick);
        }

        [Fact]
        public void ToRobloxTick_PositiveDate_ReturnsExpectedSecondsAndFraction()
        {
            DateTime date = DateTime.UnixEpoch.AddSeconds(100).AddMilliseconds(250);
            double tick = date.ToRobloxTick();
            Assert.Equal(100.25, Math.Round(tick, 2));
        }

        [Fact]
        public void MapValue_CalculatesLinearInterpolation()
        {
            double mapped = RobloxPlaceHelper.MapValue(5.0, 0.0, 10.0, 0.0, 100.0);
            Assert.Equal(50.0, mapped);

            double mapped2 = RobloxPlaceHelper.MapValue(2.0, 0.0, 4.0, 10.0, 20.0);
            Assert.Equal(15.0, mapped2);
        }

        [Fact]
        public async Task GetRandomJobId_WhenCancelled_ThrowsOperationCanceledException()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await RobloxPlaceHelper.GetRandomJobId(189707, cancellationToken: cts.Token);
            });
        }

        [Fact]
        public void PrerequisiteService_NeedsVCRedist_ReturnsBooleanWithoutThrowing()
        {
            bool needs = PrerequisiteService.NeedsVCRedist();
            Assert.Equal(needs, PrerequisiteService.NeedsVCRedist());
        }

        [Fact]
        public void BrowserService_NoPasswordManagerSwitches_ContainsExpectedSwitches()
        {
            Assert.Contains("--password-store=basic", BrowserService.NoPasswordManagerSwitches);
            Assert.Contains("--disable-sync", BrowserService.NoPasswordManagerSwitches);
        }

        [Fact]
        public void BrowserService_SuppressPasswordManager_WritesPreferencesFile()
        {
            string isolated = Path.Combine(Path.GetTempPath(), "Shiro-BrowserPrefs-" + Guid.NewGuid().ToString("N"));
            try
            {
                BrowserService.SuppressPasswordManager(isolated);
                string prefsPath = Path.Combine(isolated, "Default", "Preferences");
                Assert.True(File.Exists(prefsPath));
                string content = File.ReadAllText(prefsPath);
                Assert.Contains("password_manager_enabled", content);
            }
            finally
            {
                if (Directory.Exists(isolated))
                {
                    try { Directory.Delete(isolated, true); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public async Task BrowserService_OpenHomeAsync_WithNullAccount_ReturnsError()
        {
            string result = await BrowserService.Default.OpenHomeAsync(null!, preferChrome: false);
            Assert.StartsWith("ERROR", result);
        }

        [Fact]
        public async Task BrowserService_OpenHomeResultAsync_WithNullAccount_ReturnsFailedResult()
        {
            var result = await BrowserService.Default.OpenHomeResultAsync(null!, preferChrome: false);
            Assert.Equal(LaunchOutcome.Failed, result.Outcome);
            Assert.Contains("ERROR", result.Message);
        }

        [Fact]
        public void AccountLaunchService_InvalidateExecutableCache_CleansCache()
        {
            var ex = Record.Exception(() => AccountLaunchService.InvalidateExecutableCache());
            Assert.Null(ex);
        }

        [Fact]
        public void AccountLaunchService_HasRobloxPlayerProtocolHandler_ReturnsBoolean()
        {
            bool hasHandler = AccountLaunchService.HasRobloxPlayerProtocolHandler();
            Assert.Equal(hasHandler, AccountLaunchService.HasRobloxPlayerProtocolHandler());
        }

        [Fact]
        public async Task AccountLaunchService_WaitForExitAsync_WithNullProcess_ReturnsTrue()
        {
            bool exited = await AccountLaunchService.WaitForExitAsync(null!, 100, CancellationToken.None);
            Assert.True(exited);
        }

        [Fact]
        public async Task AccountLaunchService_ResolveNewClientPidAsync_WhenTimeout_ReturnsZero()
        {
            var existingPids = new HashSet<int>(RobloxProcessService.GetRunningRobloxPids()) { 11111, 22222 };
            int pid = await AccountLaunchService.ResolveNewClientPidAsync(
                existingPids,
                launcherPid: 0,
                timeout: TimeSpan.FromMilliseconds(50),
                cancellationToken: CancellationToken.None);

            Assert.Equal(0, pid);
        }

        [Fact]
        public void AppSettings_EnsureBackendInitialized_WiresHostUiBridgeLaunchHandlers()
        {
            // Reset handlers to simulate uninitialized startup
            HostUiBridgeService.Default.EnsureBackendInitializedHandler = null;
            HostUiBridgeService.Default.LaunchAppHomeHandler = null;
            HostUiBridgeService.Default.JoinServerHandler = null;

            // Trigger backend initialization
            AppSettings.EnsureBackendInitialized();

            // Verify bridge handlers are registered and ready
            Assert.NotNull(HostUiBridgeService.Default.LaunchAppHomeHandler);
            Assert.NotNull(HostUiBridgeService.Default.JoinServerHandler);
            var task = HostUiBridgeService.Default.LaunchAppHomeHandler(null!);
            Assert.True(task.IsCompletedSuccessfully);
        }

        [Fact]
        public async Task HostUiBridgeService_LaunchAppHome_AutoInitializes_WhenHandlerNull()
        {
            var bridge = new HostUiBridgeService();
            bool initialized = false;
            bridge.EnsureBackendInitializedHandler = () =>
            {
                initialized = true;
                bridge.LaunchAppHomeHandler = acc => Task.CompletedTask;
            };

            await bridge.LaunchAppHomeFromHostAsync(new Account { Username = "TestBridgeUser" });

            Assert.True(initialized);
            Assert.NotNull(bridge.LaunchAppHomeHandler);
        }

        [Fact]
        public async Task HostUiBridgeService_JoinServer_AutoInitializes_WhenHandlerNull()
        {
            var bridge = new HostUiBridgeService();
            bool initialized = false;
            bridge.EnsureBackendInitializedHandler = () =>
            {
                initialized = true;
                bridge.JoinServerHandler = (accs, pid, jid) => Task.CompletedTask;
            };

            await bridge.JoinServerFromHostAsync(new[] { new Account { Username = "TestBridgeUser" } }, 12345, "job-id");

            Assert.True(initialized);
            Assert.NotNull(bridge.JoinServerHandler);
        }

        [Fact]
        public void HostUiBridgeService_MultiLaunchHome_AutoInitializes_WhenHandlerNull()
        {
            var bridge = new HostUiBridgeService();
            bool initialized = false;
            bridge.EnsureBackendInitializedHandler = () =>
            {
                initialized = true;
                bridge.MultiLaunchHomeHandler = () => { };
            };

            bridge.MultiLaunchHomeFromHost();

            Assert.True(initialized);
            Assert.NotNull(bridge.MultiLaunchHomeHandler);
        }

        [Fact]
        public void HostUiBridgeService_InitializePostStartup_AutoInitializes_WhenHandlerNull()
        {
            var bridge = new HostUiBridgeService();
            bool initialized = false;
            bridge.EnsureBackendInitializedHandler = () =>
            {
                initialized = true;
                bridge.InitializePostStartupHandler = () => { };
            };

            bridge.InitializePostStartup();

            Assert.True(initialized);
            Assert.NotNull(bridge.InitializePostStartupHandler);
        }

        [Fact]
        public void HostUiBridgeService_GetCurrentSelectedAccounts_AutoInitializes_WhenHandlerNull()
        {
            var bridge = new HostUiBridgeService();
            bool initialized = false;
            bridge.EnsureBackendInitializedHandler = () =>
            {
                initialized = true;
                bridge.GetCurrentSelectedAccountsHandler = () => new List<Account>();
            };

            var list = bridge.GetCurrentSelectedAccountsFromHost();

            Assert.True(initialized);
            Assert.NotNull(list);
        }
    }
}
