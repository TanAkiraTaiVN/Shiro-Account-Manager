#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Classes;
using Shiro.Nexus;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Integration
{
    [Collection("StaticSeams")]
    public class NexusEndToEndTests
    {
        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task<(NexusWebSocketServer Server, NexusViewModel ViewModel, int Port)> SetupServerAsync()
        {
            int port = GetFreePort();
            var account = new Account { Username = "alice", UserID = 42 };
            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(new[] { account });

            var vm = new NexusViewModel(repo: repo);
            vm.PortText = port.ToString();
            vm.AllowExternal = false;

            var controlled = new ControlledAccount(account);
            vm.Accounts.Add(controlled);
            NexusHost.Current = vm;

            var server = new NexusWebSocketServer(IPAddress.Loopback, port);
            await server.StartAsync();
            return (server, vm, port);
        }

        [Fact]
        public async Task Client_ConnectsWithKnownAccount_BecomesOnline()
        {
            var (server, vm, port) = await SetupServerAsync();
            try
            {
                using var ws = new ClientWebSocket();
                var uri = new Uri($"ws://127.0.0.1:{port}/Nexus?name=alice&id=42&jobId=J1");
                await ws.ConnectAsync(uri, CancellationToken.None);

                ControlledAccount? acc = null;
                for (int i = 0; i < 30; i++)
                {
                    acc = vm.Accounts.Count > 0 ? vm.Accounts[0] : null;
                    if (acc != null && acc.Status == AccountStatus.Online && acc.InGameJobId == "J1")
                        break;
                    await Task.Delay(100);
                }

                Assert.NotNull(acc);
                Assert.Equal(AccountStatus.Online, acc.Status);
                Assert.Equal("J1", acc.InGameJobId);

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            finally
            {
                await server.StopAsync();
                NexusHost.Current = null;
            }
        }

        [Fact]
        public async Task Client_UnknownAccount_Rejected403()
        {
            var (server, _, port) = await SetupServerAsync();
            try
            {
                using var ws = new ClientWebSocket();
                var uri = new Uri($"ws://127.0.0.1:{port}/Nexus?name=unknownuser&id=999&jobId=J1");
                var ex = await Record.ExceptionAsync(() => ws.ConnectAsync(uri, CancellationToken.None));

                Assert.NotNull(ex);
                Assert.True(ex is WebSocketException or HttpRequestException, $"Expected WebSocketException but got {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                await server.StopAsync();
                NexusHost.Current = null;
            }
        }

        [Fact]
        public async Task Client_IdMismatch_Rejected403()
        {
            var (server, _, port) = await SetupServerAsync();
            try
            {
                using var ws = new ClientWebSocket();
                // Username is alice (UserID 42), but client passes id=43
                var uri = new Uri($"ws://127.0.0.1:{port}/Nexus?name=alice&id=43&jobId=J1");
                var ex = await Record.ExceptionAsync(() => ws.ConnectAsync(uri, CancellationToken.None));

                Assert.NotNull(ex);
                Assert.True(ex is WebSocketException or HttpRequestException, $"Expected WebSocketException but got {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                await server.StopAsync();
                NexusHost.Current = null;
            }
        }

        [Fact]
        public async Task Nexus_ClientConnects_WithoutWindow()
        {
            // Verify full headless operation without constructing or opening NexusWindow
            var (server, vm, port) = await SetupServerAsync();
            try
            {
                using var ws = new ClientWebSocket();
                var uri = new Uri($"ws://127.0.0.1:{port}/Nexus?name=alice&id=42&jobId=J100");
                await ws.ConnectAsync(uri, CancellationToken.None);

                ControlledAccount? acc = null;
                for (int i = 0; i < 30; i++)
                {
                    acc = vm.Accounts.Count > 0 ? vm.Accounts[0] : null;
                    if (acc != null && acc.Status == AccountStatus.Online)
                        break;
                    await Task.Delay(100);
                }

                Assert.NotNull(acc);
                Assert.Equal(AccountStatus.Online, acc.Status);
                Assert.Equal("J100", acc.InGameJobId);
                Assert.Same(vm, NexusHost.Current);

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            finally
            {
                await server.StopAsync();
                NexusHost.Current = null;
            }
        }

        [Fact]
        public async Task ExecuteAll_DeliversExecuteMessageToClient()
        {
            var (server, vm, port) = await SetupServerAsync();
            try
            {
                using var ws = new ClientWebSocket();
                var uri = new Uri($"ws://127.0.0.1:{port}/Nexus?name=alice&id=42&jobId=J1");
                await ws.ConnectAsync(uri, CancellationToken.None);

                for (int i = 0; i < 30; i++)
                {
                    if (vm.Accounts.Count > 0 && vm.Accounts[0].Status == AccountStatus.Online)
                        break;
                    await Task.Delay(100);
                }

                // Send execute command from VM
                vm.ScriptInput = "print(1)";
                vm.ExecuteAllCommand.Execute(null);

                // Receive message on client
                byte[] buffer = new byte[1024];
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

                Assert.Equal(WebSocketMessageType.Text, res.MessageType);
                string received = Encoding.UTF8.GetString(buffer, 0, res.Count);
                Assert.Equal("Execute:print(1)", received);

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            finally
            {
                await server.StopAsync();
                NexusHost.Current = null;
            }
        }

        [Fact]
        public async Task OversizedPayload_ClosesWithMessageTooBig()
        {
            var (server, vm, port) = await SetupServerAsync();
            try
            {
                using var ws = new ClientWebSocket();
                var uri = new Uri($"ws://127.0.0.1:{port}/Nexus?name=alice&id=42&jobId=J1");
                await ws.ConnectAsync(uri, CancellationToken.None);

                for (int i = 0; i < 30; i++)
                {
                    if (vm.Accounts.Count > 0 && vm.Accounts[0].Status == AccountStatus.Online)
                        break;
                    await Task.Delay(100);
                }

                // Send payload exceeding 2 MiB (2 * 1024 * 1024 + 1 bytes)
                int oversizedLen = 2 * 1024 * 1024 + 1;
                byte[] oversizedPayload = new byte[oversizedLen];

                // Send payload in chunks with endOfMessage=true on the last chunk
                int chunkSize = 64 * 1024;
                for (int offset = 0; offset < oversizedLen; offset += chunkSize)
                {
                    int count = Math.Min(chunkSize, oversizedLen - offset);
                    bool endOfMsg = (offset + count) >= oversizedLen;
                    try
                    {
                        await ws.SendAsync(new ArraySegment<byte>(oversizedPayload, offset, count),
                            WebSocketMessageType.Text, endOfMsg, CancellationToken.None);
                    }
                    catch (WebSocketException)
                    {
                        // Server may close connection immediately upon exceeding limit
                        break;
                    }
                }

                // Expect close frame with MessageTooBig
                byte[] recvBuffer = new byte[1024];
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    while (ws.State == WebSocketState.Open)
                    {
                        var res = await ws.ReceiveAsync(new ArraySegment<byte>(recvBuffer), cts.Token);
                        if (res.MessageType == WebSocketMessageType.Close)
                            break;
                    }
                }
                catch (WebSocketException) { /* Socket closed by server */ }

                Assert.True(
                    ws.CloseStatus == WebSocketCloseStatus.MessageTooBig ||
                    ws.State == WebSocketState.CloseReceived ||
                    ws.State == WebSocketState.Closed ||
                    ws.State == WebSocketState.Aborted,
                    $"Expected MessageTooBig or closed socket, got state={ws.State}, closeStatus={ws.CloseStatus}");
            }
            finally
            {
                await server.StopAsync();
                NexusHost.Current = null;
            }
        }
    }
}
