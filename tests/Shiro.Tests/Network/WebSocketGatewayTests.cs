using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
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
        public async Task WebSocketEndpoint_CustomValidatorFailures_TriggerBruteForceLockout429()
        {
            WebAuthPolicy.ResetLockoutsForTesting();
            try
            {
                int port = 19896;
                await using var server = new KestrelWebServer(port, false, _ => Task.FromResult("ok"), _ => false);
                await server.StartAsync();

                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                for (int i = 0; i < WebAuthPolicy.MaxFailedAttempts; i++)
                {
                    using var req = CreateWebSocketUpgradeHttpRequest("/ws");
                    using var resp = await http.SendAsync(req);
                    Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
                    Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
                }

                using var lockedReq = CreateWebSocketUpgradeHttpRequest("/ws");
                using var lockedResp = await http.SendAsync(lockedReq);
                Assert.Equal(HttpStatusCode.TooManyRequests, lockedResp.StatusCode);
                Assert.Equal("application/json", lockedResp.Content.Headers.ContentType?.MediaType);
                using var doc = JsonDocument.Parse(await lockedResp.Content.ReadAsStringAsync());
                Assert.False(doc.RootElement.GetProperty("success").GetBoolean());

                await server.StopAsync();
            }
            finally
            {
                WebAuthPolicy.ResetLockoutsForTesting();
            }
        }

        [Fact]
        public async Task WebSocketEndpoints_EventsAndV2Events_ReturnAligned400And401JsonParity()
        {
            WebAuthPolicy.ResetLockoutsForTesting();
            try
            {
                int port = 19897;
                await using var server = new KestrelWebServer(port, false, _ => Task.FromResult("ok"), _ => false);
                await server.StartAsync();

                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

                // 400 parity on /events and /v2/events
                using (var ev400 = await http.GetAsync("/events"))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, ev400.StatusCode);
                    Assert.Equal("application/json", ev400.Content.Headers.ContentType?.MediaType);
                    using var doc = JsonDocument.Parse(await ev400.Content.ReadAsStringAsync());
                    Assert.Contains("/v2/events", doc.RootElement.GetProperty("endpoints").ToString());
                }

                using (var v2400 = await http.GetAsync("/v2/events"))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, v2400.StatusCode);
                    Assert.Equal("application/json", v2400.Content.Headers.ContentType?.MediaType);
                    using var doc = JsonDocument.Parse(await v2400.Content.ReadAsStringAsync());
                    Assert.False(doc.RootElement.GetProperty("Success").GetBoolean());
                    Assert.Contains("/v2/events", doc.RootElement.GetProperty("Message").GetString());
                }

                // 401 parity on /events and /v2/events
                using (var reqEv = CreateWebSocketUpgradeHttpRequest("/events"))
                using (var ev401 = await http.SendAsync(reqEv))
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, ev401.StatusCode);
                    Assert.Equal("application/json", ev401.Content.Headers.ContentType?.MediaType);
                    using var doc = JsonDocument.Parse(await ev401.Content.ReadAsStringAsync());
                    Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
                }

                using (var reqV2 = CreateWebSocketUpgradeHttpRequest("/v2/events"))
                using (var v2401 = await http.SendAsync(reqV2))
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, v2401.StatusCode);
                    Assert.Equal("application/json", v2401.Content.Headers.ContentType?.MediaType);
                    using var doc = JsonDocument.Parse(await v2401.Content.ReadAsStringAsync());
                    Assert.False(doc.RootElement.GetProperty("Success").GetBoolean());
                }

                await server.StopAsync();
            }
            finally
            {
                WebAuthPolicy.ResetLockoutsForTesting();
            }
        }

        [Fact]
        public async Task CloseSafeAsync_WhenSendGateIsHeldBeyondTimeout_AbortsWithoutConcurrentCloseViolation()
        {
            var socket = new StubSlowWebSocket();
            var gate = new SemaphoreSlim(1, 1);
            await gate.WaitAsync();
            try
            {
                await KestrelWebServer.CloseSafeAsync(socket, gate, WebSocketCloseStatus.NormalClosure, "Server stopping", timeoutMs: 40);
                Assert.True(socket.Aborted);
                Assert.Equal(0, socket.CloseCalls);
            }
            finally
            {
                gate.Release();
                WebSocketGateHelper.RetireAndDispose(gate);
            }
        }

        [Fact]
        public void SerializeBroadcastEnvelope_UnknownAndUnserializablePayloads_HandleCleanly()
        {
            byte[] objBytes = KestrelWebServer.SerializeBroadcastEnvelope("custom", new { message = "hi", count = 3 });
            using (var doc = JsonDocument.Parse(objBytes))
            {
                var data = doc.RootElement.GetProperty("data");
                Assert.Equal(JsonValueKind.Object, data.ValueKind);
                Assert.Equal("hi", data.GetProperty("message").GetString());
                Assert.Equal(3, data.GetProperty("count").GetInt32());
            }

            Action unserializable = () => { };
            byte[] fallbackBytes = KestrelWebServer.SerializeBroadcastEnvelope("fallback", unserializable);
            using (var fallbackDoc = JsonDocument.Parse(fallbackBytes))
            {
                Assert.Equal("fallback", fallbackDoc.RootElement.GetProperty("event").GetString());
                Assert.Equal(JsonValueKind.String, fallbackDoc.RootElement.GetProperty("data").ValueKind);
            }
        }

        private static HttpRequestMessage CreateWebSocketUpgradeHttpRequest(string path)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            req.Headers.TryAddWithoutValidation("Upgrade", "websocket");
            req.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
            req.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
            return req;
        }

        private sealed class StubSlowWebSocket : WebSocket
        {
            private WebSocketState _state = WebSocketState.Open;
            public bool Aborted { get; private set; }
            public int CloseCalls { get; private set; }
            public override WebSocketCloseStatus? CloseStatus => null;
            public override string? CloseStatusDescription => null;
            public override WebSocketState State => _state;
            public override string? SubProtocol => null;
            public override void Abort() { Aborted = true; _state = WebSocketState.Aborted; }
            public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { CloseCalls++; _state = WebSocketState.Closed; return Task.CompletedTask; }
            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { CloseCalls++; _state = WebSocketState.CloseSent; return Task.CompletedTask; }
            public override void Dispose() => _state = WebSocketState.Closed;
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        [Fact]
        public async Task WebSocketEndpoint_ExceedingMaxConnections_Returns503()
        {
            WebAuthPolicy.ResetLockoutsForTesting();
            const int maxSockets = 32;
            int port = 19898;
            var clients = new List<ClientWebSocket>(maxSockets);
            await using var server = new KestrelWebServer(port, false, _ => Task.FromResult("ok"), _ => true);
            await server.StartAsync();
            try
            {
                var uri = new Uri($"ws://127.0.0.1:{port}/ws");
                for (int i = 0; i < maxSockets; i++)
                {
                    var ws = new ClientWebSocket();
                    await ws.ConnectAsync(uri, CancellationToken.None);
                    clients.Add(ws);
                }

                Assert.Equal(maxSockets, KestrelWebServer.ActiveWebSocketCount);
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
                using var overflowReq = CreateWebSocketUpgradeHttpRequest("/ws");
                using var overflowResp = await http.SendAsync(overflowReq, HttpCompletionOption.ResponseHeadersRead);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, overflowResp.StatusCode);
            }
            finally
            {
                foreach (var ws in clients)
                {
                    try { ws.Abort(); ws.Dispose(); }
                    catch (ObjectDisposedException) { }
                }
                await server.StopAsync();
                WebAuthPolicy.ResetLockoutsForTesting();
            }
        }

        [Fact]
        public async Task WebSocketEndpoint_OversizedIncomingMessage_ClosesWithMessageTooBig()
        {
            WebAuthPolicy.ResetLockoutsForTesting();
            int port = 19899;
            await using var server = new KestrelWebServer(port, false, _ => Task.FromResult("ok"), _ => true);
            await server.StartAsync();
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), cts.Token);
                byte[] buf = new byte[4096];
                var welcome = await ws.ReceiveAsync(buf, cts.Token);
                Assert.Equal(WebSocketMessageType.Text, welcome.MessageType);

                byte[] oversized = Encoding.UTF8.GetBytes(new string('A', 20_000));
                await ws.SendAsync(oversized, WebSocketMessageType.Text, endOfMessage: true, cts.Token);

                WebSocketReceiveResult? closeFrame = null;
                while (!cts.IsCancellationRequested)
                {
                    var res = await ws.ReceiveAsync(buf, cts.Token);
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        closeFrame = res;
                        break;
                    }
                }

                Assert.NotNull(closeFrame);
                Assert.Equal(WebSocketCloseStatus.MessageTooBig, closeFrame!.CloseStatus);
            }
            finally
            {
                await server.StopAsync();
                WebAuthPolicy.ResetLockoutsForTesting();
            }
        }

        [Fact]
        public async Task KestrelWebServer_TwoInstances_DoNotDuplicateStaticEventBroadcasts()
        {
            WebAuthPolicy.ResetLockoutsForTesting();
            await using var server1 = new KestrelWebServer(19900, false, _ => Task.FromResult("ok"), _ => true);
            await using var server2 = new KestrelWebServer(19901, false, _ => Task.FromResult("ok"), _ => true);
            await server1.StartAsync();
            await server2.StartAsync();

            using var ws = new ClientWebSocket();
            using var overallCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(new Uri("ws://127.0.0.1:19900/ws"), overallCts.Token);
            byte[] buf = new byte[4096];
            var welcome = await ws.ReceiveAsync(buf, overallCts.Token);
            Assert.Equal(WebSocketMessageType.Text, welcome.MessageType);

            var reportMethod = typeof(LaunchCoordinator).GetMethod(
                "Report",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(reportMethod);

            try
            {
                reportMethod!.Invoke(null, new object?[]
                {
                    null,
                    new LaunchProgress { Index = 1, Total = 1, Message = "OnceOnly", Status = LaunchOutcome.Success, Completed = true }
                });

                var firstMsg = await ws.ReceiveAsync(buf, overallCts.Token);
                Assert.Equal(WebSocketMessageType.Text, firstMsg.MessageType);
                Assert.Contains("OnceOnly", Encoding.UTF8.GetString(buf, 0, firstMsg.Count));

                // Keep the second ReceiveAsync alive without canceling it so the socket stays Open.
                var unexpectedDuplicate = ws.ReceiveAsync(buf, overallCts.Token);
                await Task.WhenAny(unexpectedDuplicate, Task.Delay(250));
                Assert.False(unexpectedDuplicate.IsCompleted, "Duplicate static event broadcast detected across two KestrelWebServer instances.");

                await server2.StopAsync();

                reportMethod.Invoke(null, new object?[]
                {
                    null,
                    new LaunchProgress { Index = 2, Total = 2, Message = "AfterStop2", Status = LaunchOutcome.Success, Completed = true }
                });

                var afterStopMsg = await unexpectedDuplicate;
                Assert.Equal(WebSocketMessageType.Text, afterStopMsg.MessageType);
                Assert.Contains("AfterStop2", Encoding.UTF8.GetString(buf, 0, afterStopMsg.Count));
            }
            finally
            {
                await server2.StopAsync();
                await server1.StopAsync();
                WebAuthPolicy.ResetLockoutsForTesting();
            }
        }
    }
}
