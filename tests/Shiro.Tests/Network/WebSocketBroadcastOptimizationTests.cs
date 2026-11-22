using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class WebSocketBroadcastOptimizationTests
    {
        private sealed class FakeWebSocket : WebSocket
        {
            private readonly int _sendDelayMs;
            private readonly bool _throwOnSend;
            private WebSocketState _state;

            public int SendCallCount { get; private set; }
            public string? LastSentJson { get; private set; }

            public FakeWebSocket(WebSocketState initialState = WebSocketState.Open, int sendDelayMs = 0, bool throwOnSend = false)
            {
                _state = initialState;
                _sendDelayMs = sendDelayMs;
                _throwOnSend = throwOnSend;
            }

            public override WebSocketCloseStatus? CloseStatus => null;
            public override string? CloseStatusDescription => null;
            public override WebSocketState State => _state;
            public override string? SubProtocol => null;

            public override void Abort() => _state = WebSocketState.Aborted;

            public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            {
                _state = WebSocketState.Closed;
                return Task.CompletedTask;
            }

            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            {
                _state = WebSocketState.CloseSent;
                return Task.CompletedTask;
            }

            public override void Dispose() => _state = WebSocketState.Closed;

            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            {
                if (_throwOnSend)
                    throw new WebSocketException("Simulated broken pipe");

                if (_sendDelayMs > 0)
                {
                    await Task.Delay(_sendDelayMs, cancellationToken).ConfigureAwait(false);
                }

                SendCallCount++;
                LastSentJson = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
            }
        }

        [Fact]
        public void SerializeBroadcastEnvelope_ProducesExpectedJsonForAllPayloadTypes()
        {
            byte[] statusBytes = KestrelWebServer.SerializeBroadcastEnvelope("anti_afk", new StatusBroadcastPayload(true, "Active"));
            using var statusDoc = JsonDocument.Parse(statusBytes);
            Assert.Equal("anti_afk", statusDoc.RootElement.GetProperty("event").GetString());
            Assert.True(statusDoc.RootElement.GetProperty("timestamp").GetInt64() > 0);
            Assert.True(statusDoc.RootElement.GetProperty("data").GetProperty("enabled").GetBoolean());
            Assert.Equal("Active", statusDoc.RootElement.GetProperty("data").GetProperty("status").GetString());

            byte[] strBytes = KestrelWebServer.SerializeBroadcastEnvelope("custom_msg", "hello");
            using var strDoc = JsonDocument.Parse(strBytes);
            Assert.Equal("custom_msg", strDoc.RootElement.GetProperty("event").GetString());
            Assert.Equal("hello", strDoc.RootElement.GetProperty("data").GetString());

            byte[] nullBytes = KestrelWebServer.SerializeBroadcastEnvelope("empty_evt", null!);
            using var nullDoc = JsonDocument.Parse(nullBytes);
            Assert.Equal(JsonValueKind.Null, nullDoc.RootElement.GetProperty("data").ValueKind);
        }

        [Fact]
        public async Task BroadcastToSocketsAsync_StalledClientDoesNotHeadOfLineBlockFastClients_AndGetsPruned()
        {
            var sockets = new ConcurrentDictionary<WebSocket, SemaphoreSlim>();
            var fastClient1 = new FakeWebSocket(WebSocketState.Open, sendDelayMs: 0);
            var fastClient2 = new FakeWebSocket(WebSocketState.Open, sendDelayMs: 0);
            var stalledClient = new FakeWebSocket(WebSocketState.Open, sendDelayMs: 2500);
            var closedClient = new FakeWebSocket(WebSocketState.Closed);

            sockets[fastClient1] = new SemaphoreSlim(1, 1);
            sockets[fastClient2] = new SemaphoreSlim(1, 1);
            sockets[stalledClient] = new SemaphoreSlim(1, 1);
            sockets[closedClient] = new SemaphoreSlim(1, 1);

            byte[] payload = KestrelWebServer.SerializeBroadcastEnvelope("pulse", new PulseBroadcastPayload(12345));
            var sw = Stopwatch.StartNew();

            await KestrelWebServer.BroadcastToSocketsAsync(sockets, new ArraySegment<byte>(payload), sendTimeoutMs: 120);
            sw.Stop();

            // Both fast clients received the message
            Assert.Equal(1, fastClient1.SendCallCount);
            Assert.Equal(1, fastClient2.SendCallCount);

            // Stalled and closed clients were pruned automatically, fast clients remain active
            Assert.True(sockets.ContainsKey(fastClient1));
            Assert.True(sockets.ContainsKey(fastClient2));
            Assert.False(sockets.ContainsKey(stalledClient));
            Assert.False(sockets.ContainsKey(closedClient));

            // Total broadcast time finished near the 120ms timeout rather than waiting 2500ms
            Assert.True(sw.ElapsedMilliseconds < 1500, $"Expected broadcast to time out stalled socket quickly, took {sw.ElapsedMilliseconds}ms");
        }
    }
}
