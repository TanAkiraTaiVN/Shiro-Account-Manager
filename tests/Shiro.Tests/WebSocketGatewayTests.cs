using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class WebSocketGatewayTests
    {
        [Fact]
        public void KestrelWebServer_InitialActiveWebSocketCount_IsZero()
        {
            Assert.Equal(0, KestrelWebServer.ActiveWebSocketCount);
        }

        [Fact]
        public async Task KestrelWebServer_BroadcastAsync_NoClients_CompletesSuccessfully()
        {
            var exception = await Record.ExceptionAsync(async () =>
            {
                await KestrelWebServer.BroadcastAsync("test_event", new { message = "hello world" });
            });

            Assert.Null(exception);
        }

        [Fact]
        public void KestrelWebServer_BroadcastSync_NoClients_DoesNotThrow()
        {
            var exception = Record.Exception(() =>
            {
                KestrelWebServer.Broadcast("test_sync_event", new { status = "ok" });
            });

            Assert.Null(exception);
        }

        [Fact]
        public void LaunchCoordinator_LaunchProgressChanged_EventCanBeSubscribedAndFires()
        {
            bool eventFired = false;
            LaunchProgress? receivedProgress = null;

            Action<LaunchProgress> handler = p =>
            {
                eventFired = true;
                receivedProgress = p;
            };

            LaunchCoordinator.LaunchProgressChanged += handler;
            try
            {
                // Trigger event subscription test via mock progress
                var coordinatorType = typeof(LaunchCoordinator);
                var reportMethod = coordinatorType.GetMethod("Report", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                Assert.NotNull(reportMethod);

                var dummyProgress = new LaunchProgress
                {
                    Index = 1,
                    Total = 1,
                    Message = "Test Launch Event",
                    Status = LaunchOutcome.Success,
                    Completed = true
                };

                reportMethod.Invoke(null, new object?[] { null, dummyProgress });

                Assert.True(eventFired);
                Assert.NotNull(receivedProgress);
                Assert.Equal("Test Launch Event", receivedProgress.Message);
                Assert.Equal(LaunchOutcome.Success, receivedProgress.Status);
            }
            finally
            {
                LaunchCoordinator.LaunchProgressChanged -= handler;
            }
        }

        [Fact]
        public void RobloxSingletonService_IsEnabled_ExposesBooleanState()
        {
            Assert.True(RobloxSingletonService.IsEnabled);
        }

        [Fact]
        public void RAMAccountLua_ContainsModernAntiAfkAndWebSocketHelpers()
        {
            string luaPath = Path.Combine(ArchitectureTests.RepoRoot, "src", "Shiro", "Nexus", "RAMAccount.lua");
            Assert.True(File.Exists(luaPath), $"Missing tracked Lua script: {luaPath}");
            string lua = File.ReadAllText(luaPath);
            Assert.Contains("EnableInGameAntiAfk", lua);
            Assert.Contains("DisableInGameAntiAfk", lua);
            Assert.Contains("VirtualUser", lua);
            Assert.Contains("ConnectEvents", lua);
            Assert.Contains("GetWebSocketStats", lua);
        }

        [Fact]
        public void RAMAccountLua_IsCopiedToOutput()
        {
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Nexus", "RAMAccount.lua")));
        }
    }
}
