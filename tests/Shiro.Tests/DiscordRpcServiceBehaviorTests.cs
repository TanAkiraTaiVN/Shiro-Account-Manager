#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class DiscordRpcServiceBehaviorTests
    {
        private const string ReadyHandshakeJson = "{\"cmd\":\"DISPATCH\",\"evt\":\"READY\"}";
        private const string CloseHandshakeJson = "{\"code\":4000,\"message\":\"Invalid Client ID\"}";

        [Fact]
        public void DefaultClientId_IsEmptyByDefault()
        {
            Assert.Equal("", DiscordRpcService.DefaultClientId);
        }

        [Fact]
        public void DiscordActivityItem_PropertiesAndButtons_HoldValues()
        {
            var activity = new DiscordActivityItem
            {
                Details = "Playing Brookhaven",
                State = "In Game",
                StartTimestamp = 1700000000,
                LargeImageKey = "shiro_custom",
                LargeImageText = "Shiro AM",
                SmallImageKey = "game_icon",
                SmallImageText = "Brookhaven",
                Buttons = new List<DiscordButtonItem>
                {
                    new DiscordButtonItem { Label = "Join Server", Url = "https://roblox.com" }
                }
            };

            Assert.Equal("Playing Brookhaven", activity.Details);
            Assert.Equal("In Game", activity.State);
            Assert.Equal(1700000000, activity.StartTimestamp);
            Assert.Equal("shiro_custom", activity.LargeImageKey);
            Assert.Equal("Shiro AM", activity.LargeImageText);
            Assert.Equal("game_icon", activity.SmallImageKey);
            Assert.Equal("Brookhaven", activity.SmallImageText);
            Assert.Single(activity.Buttons);
            Assert.Equal("Join Server", activity.Buttons[0].Label);
            Assert.Equal("https://roblox.com", activity.Buttons[0].Url);
        }

        [Fact]
        public void IsEnabled_ReadsAndWritesFromSettings()
        {
            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();

            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcEnabled, true).Returns(true);

            using var service = new DiscordRpcService(logger, settings);

            Assert.True(service.IsEnabled);
            Assert.False(service.IsConnected);

            service.IsEnabled = false;
            settings.Received().Set(AppSettingsKeys.General.DiscordRpcEnabled, "false");
        }

        [Fact]
        public void UpdatePresence_And_ClearPresence_ManageCachedActivity()
        {
            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcEnabled, true).Returns(false);

            using var service = new DiscordRpcService(logger, settings);

            // When disconnected and disabled, UpdatePresence builds activity without throwing
            var ex1 = Record.Exception(() => service.UpdatePresence("Blox Fruits", 2753915549, "job-123", 5, 2));
            Assert.Null(ex1);

            // ClearPresence removes cached activity
            var ex2 = Record.Exception(() => service.ClearPresence());
            Assert.Null(ex2);

            // Calling Stop when not running is safe
            var ex3 = Record.Exception(() => service.Stop());
            Assert.Null(ex3);
        }

        [Fact]
        public void UpdatePresence_WhenEnabled_FormatsPayloadVariants()
        {
            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcEnabled, true).Returns(true);
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcJoinCurrentServer, true).Returns(true);
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcShowGame, true).Returns(true);

            using var service = new DiscordRpcService(logger, settings);

            // 1. In game with placeId and jobId
            var ex1 = Record.Exception(() => service.UpdatePresence("Blox Fruits", 2753915549, "job-guid-123", 5, 2));
            Assert.Null(ex1);

            // 2. In game with placeId but empty jobId
            var ex2 = Record.Exception(() => service.UpdatePresence("Blox Fruits", 2753915549, null, 5, 2));
            Assert.Null(ex2);

            // 3. In game with placeId <= 0
            var ex3 = Record.Exception(() => service.UpdatePresence("Blox Fruits", 0, null, 5, 2));
            Assert.Null(ex3);

            // 4. In game with 0 active in game accounts (ready to launch)
            var ex4 = Record.Exception(() => service.UpdatePresence(null, 0, null, 10, 0));
            Assert.Null(ex4);

            // 5. With join disabled
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcJoinCurrentServer, true).Returns(false);
            var ex5 = Record.Exception(() => service.UpdatePresence("Blox Fruits", 2753915549, "job-1", 3, 1));
            Assert.Null(ex5);

            // 6. With show game disabled
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcShowGame, true).Returns(false);
            var ex6 = Record.Exception(() => service.UpdatePresence("Blox Fruits", 2753915549, "job-1", 3, 1));
            Assert.Null(ex6);
        }

        [Fact]
        public void Service_DisposesCleanly()
        {
            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            var service = new DiscordRpcService(logger, settings);

            var ex = Record.Exception(() =>
            {
                service.Dispose();
                // Double dispose should be safe
                service.Dispose();
            });

            Assert.Null(ex);
        }

        [Fact]
        public async Task TryConnectForTestAsync_WhenClientIdEmpty_SkipsConnectionEvenIfPipeExists()
        {
            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            settings.Get<string>(AppSettingsKeys.General.DiscordRpcClientId, Arg.Any<string>()).Returns(string.Empty);

            string pipePrefix = $"shiro-test-empty-{Guid.NewGuid():N}-";
            using var server = new NamedPipeServerStream(pipePrefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Task waitConn = server.WaitForConnectionAsync();

            var prevOverride = DiscordRpcService.TestAvailablePipesOverride;
            DiscordRpcService.TestAvailablePipesOverride = () => new[] { 0 };
            try
            {
                using var rpc = new DiscordRpcService(logger, settings);
                bool connected = await rpc.TryConnectForTestAsync(CancellationToken.None, pipePrefix);

                Assert.False(connected);
                Assert.False(rpc.IsConnected);
                Assert.False(waitConn.IsCompleted);
                logger.Received(1).Debug(Arg.Is<string>(s => s.Contains("Client ID", StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                DiscordRpcService.TestAvailablePipesOverride = prevOverride;
            }
        }

        [Fact]
        public async Task TryConnectForTestAsync_WhenOpcodeIsClose_RejectsHandshake()
        {
            byte[] closeFrame = DiscordRpcService.BuildFrame(2, Encoding.UTF8.GetBytes(CloseHandshakeJson));
            await using (var ms = new MemoryStream(closeFrame))
            {
                await Assert.ThrowsAsync<IOException>(() =>
                    DiscordRpcService.ReadHandshakeResponseFromStreamAsync(ms, CancellationToken.None));
            }

            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            settings.Get<string>(AppSettingsKeys.General.DiscordRpcClientId, Arg.Any<string>()).Returns("123456789012345678");

            string pipePrefix = $"shiro-test-close-{Guid.NewGuid():N}-";
            using var server = new NamedPipeServerStream(pipePrefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Task serverTask = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                await DiscordRpcService.ReadFrameFromStreamAsync(server);
                await server.WriteAsync(closeFrame);
                await server.FlushAsync();
            });

            var prevOverride = DiscordRpcService.TestAvailablePipesOverride;
            DiscordRpcService.TestAvailablePipesOverride = () => new[] { 0 };
            try
            {
                using var rpc = new DiscordRpcService(logger, settings);
                bool connected = await rpc.TryConnectForTestAsync(CancellationToken.None, pipePrefix);
                await serverTask.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.False(connected);
                Assert.False(rpc.IsConnected);
            }
            finally
            {
                DiscordRpcService.TestAvailablePipesOverride = prevOverride;
            }
        }

        [Fact]
        public async Task TryConnectForTestAsync_WhenHandshakeTimesOut_AbortsCleanlyWithin3000ms()
        {
            Assert.Equal(3000, DiscordRpcService.DefaultHandshakeTimeoutMs);

            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            settings.Get<string>(AppSettingsKeys.General.DiscordRpcClientId, Arg.Any<string>()).Returns("123456789012345678");

            string pipePrefix = $"shiro-test-timeout-{Guid.NewGuid():N}-";
            using var releaseServer = new ManualResetEventSlim(false);
            using var server = new NamedPipeServerStream(pipePrefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Task serverTask = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                await DiscordRpcService.ReadFrameFromStreamAsync(server);
                releaseServer.Wait(TimeSpan.FromSeconds(6));
            });

            var prevOverride = DiscordRpcService.TestAvailablePipesOverride;
            DiscordRpcService.TestAvailablePipesOverride = () => new[] { 0 };
            try
            {
                using var rpc = new DiscordRpcService(logger, settings);
                var sw = Stopwatch.StartNew();
                bool connected = await rpc.TryConnectForTestAsync(CancellationToken.None, pipePrefix)
                    .WaitAsync(TimeSpan.FromMilliseconds(4500));
                sw.Stop();

                Assert.False(connected);
                Assert.False(rpc.IsConnected);
                Assert.InRange(sw.ElapsedMilliseconds, 2500, 4400);
            }
            finally
            {
                releaseServer.Set();
                DiscordRpcService.TestAvailablePipesOverride = prevOverride;
                await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        [Fact]
        public async Task TryConnectForTestAsync_WhenValidHandshakeResponse_ConnectsAndStopsCleanly()
        {
            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            settings.Get<string>(AppSettingsKeys.General.DiscordRpcClientId, Arg.Any<string>()).Returns("123456789012345678");

            byte[] readyFrame = DiscordRpcService.BuildFrame(1, Encoding.UTF8.GetBytes(ReadyHandshakeJson));
            string pipePrefix = $"shiro-test-ready-{Guid.NewGuid():N}-";
            int op = -1;
            string hsPayload = string.Empty;
            using var server = new NamedPipeServerStream(pipePrefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Task serverTask = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                (op, hsPayload) = await DiscordRpcService.ReadFrameFromStreamAsync(server);
                await server.WriteAsync(readyFrame);
                await server.FlushAsync();
            });

            var prevOverride = DiscordRpcService.TestAvailablePipesOverride;
            DiscordRpcService.TestAvailablePipesOverride = () => new[] { 0 };
            try
            {
                using var rpc = new DiscordRpcService(logger, settings);
                bool connected = await rpc.TryConnectForTestAsync(CancellationToken.None, pipePrefix);
                await serverTask.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.True(connected);
                Assert.True(rpc.IsConnected);
                Assert.Equal(DiscordRpcService.OpcodeHandshake, op);
                using var hsDoc = JsonDocument.Parse(hsPayload);
                Assert.Equal(1, hsDoc.RootElement.GetProperty("v").GetInt32());
                Assert.Equal("123456789012345678", hsDoc.RootElement.GetProperty("client_id").GetString());

                rpc.Stop();
                Assert.False(rpc.IsConnected);
            }
            finally
            {
                DiscordRpcService.TestAvailablePipesOverride = prevOverride;
            }
        }

        [Fact]
        public void UpdateCurrentPresence_WhenPlaceIdPositive_UsesCachedOrActiveGameTitle()
        {
            var logger = Substitute.For<IAppLogger>();
            var settings = Substitute.For<ISettingsService>();
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcEnabled, true).Returns(true);
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcShowGame, true).Returns(true);
            settings.Get<bool>(AppSettingsKeys.General.DiscordRpcJoinCurrentServer, true).Returns(true);

            var prevOverride = DiscordRpcService.TestAvailablePipesOverride;
            var prevAccounts = AppServices.AccountRepository.GetAll();
            DiscordRpcService.TestAvailablePipesOverride = () => Array.Empty<int>();
            try
            {
                var browserState = new ServerBrowserStateService(settings);
                using var browserScope = AppServices.Override<IServerBrowserStateService>(browserState);
                using var rpc = new DiscordRpcService(logger, settings);

                const long cachedPlaceId = 9927539155L;
                GameInfoService.CachePlaceName(cachedPlaceId, "Blox Fruits Cached");
                browserState.CurrentPlaceId = cachedPlaceId.ToString();
                browserState.CurrentJobId = "job-rpc-cached";

                rpc.UpdateCurrentPresence();
                Assert.NotNull(rpc.LastActivityForTest);
                Assert.Equal(
                    LocalizationService.Format("DiscordRpc.PlayingGame", "Blox Fruits Cached"),
                    rpc.LastActivityForTest!.Details);

                const long activePresencePlaceId = 9988776655L;
                var activeAccount = new Account
                {
                    Username = "RpcPresenceAcc",
                    UserID = 554433,
                    Presence = new UserPresence
                    {
                        userPresenceType = UserPresenceType.InGame,
                        placeId = activePresencePlaceId,
                        gameName = "Active Presence Arena"
                    }
                };
                AccountManager.ResetAccountsForTests(new[] { activeAccount });
                browserState.CurrentPlaceId = activePresencePlaceId.ToString();

                rpc.UpdateCurrentPresence();
                Assert.NotNull(rpc.LastActivityForTest);
                Assert.Equal(
                    LocalizationService.Format("DiscordRpc.PlayingGame", "Active Presence Arena"),
                    rpc.LastActivityForTest!.Details);

                rpc.Stop();
            }
            finally
            {
                AccountManager.ResetAccountsForTests(prevAccounts);
                DiscordRpcService.TestAvailablePipesOverride = prevOverride;
            }
        }
    }
}
