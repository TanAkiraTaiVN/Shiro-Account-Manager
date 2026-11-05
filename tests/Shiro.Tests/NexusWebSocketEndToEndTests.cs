using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Nexus;
using Xunit;

namespace Shiro.Tests
{
    public class NexusWebSocketEndToEndTests
    {
        private sealed class ScriptedWebSocket : WebSocket
        {
            private readonly Queue<(byte[] Data, WebSocketMessageType Type, bool EndOfMessage)> _inboundFrames = new();
            private readonly int _sendDelayMs;
            private WebSocketState _state = WebSocketState.Open;

            public ScriptedWebSocket(int sendDelayMs = 0)
            {
                _sendDelayMs = sendDelayMs;
            }

            public void EnqueueFrame(string text, bool endOfMessage = true)
            {
                _inboundFrames.Enqueue((Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage));
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
            {
                if (_inboundFrames.Count == 0)
                {
                    _state = WebSocketState.CloseReceived;
                    return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
                }

                var frame = _inboundFrames.Dequeue();
                Buffer.BlockCopy(frame.Data, 0, buffer.Array!, buffer.Offset, frame.Data.Length);
                return Task.FromResult(new WebSocketReceiveResult(frame.Data.Length, frame.Type, frame.EndOfMessage));
            }

            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            {
                if (_sendDelayMs > 0)
                {
                    await Task.Delay(_sendDelayMs, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        [Fact]
        public async Task ReceiveFullTextMessageAsync_SingleFrame_DecodesDirectlyWithoutMemoryStream()
        {
            using var ws = new ScriptedWebSocket();
            ws.EnqueueFrame("{\"Name\":\"ping\",\"Payload\":{\"fps\":\"60\"}}", endOfMessage: true);

            byte[] buffer = new byte[8192];
            var (msgType, text, exceeded) = await NexusWebSocketServer.ReceiveFullTextMessageAsync(
                ws,
                buffer,
                maxPayloadBytes: 2 * 1024 * 1024,
                CancellationToken.None);

            Assert.Equal(WebSocketMessageType.Text, msgType);
            Assert.False(exceeded);
            Assert.Equal("{\"Name\":\"ping\",\"Payload\":{\"fps\":\"60\"}}", text);
        }

        [Fact]
        public async Task ReceiveFullTextMessageAsync_MultiFrame_ReassemblesCorrectly()
        {
            using var ws = new ScriptedWebSocket();
            ws.EnqueueFrame("{\"Name\":\"Echo\",", endOfMessage: false);
            ws.EnqueueFrame("\"Payload\":{\"Content\":\"Hello World\"}}", endOfMessage: true);

            byte[] buffer = new byte[8192];
            var (msgType, text, exceeded) = await NexusWebSocketServer.ReceiveFullTextMessageAsync(
                ws,
                buffer,
                maxPayloadBytes: 2 * 1024 * 1024,
                CancellationToken.None);

            Assert.Equal(WebSocketMessageType.Text, msgType);
            Assert.False(exceeded);
            Assert.Equal("{\"Name\":\"Echo\",\"Payload\":{\"Content\":\"Hello World\"}}", text);
        }

        [Fact]
        public async Task ControlledAccount_SendMessageAsync_TimesOutCleanlyWhenSocketStalls()
        {
            var account = new Account { Username = "NexusFarmer" };
            var controlled = new ControlledAccount(account);
            using var stalledWs = new ScriptedWebSocket(sendDelayMs: 5000);
            controlled.Connect(stalledWs);

            var sw = Stopwatch.StartNew();
            await controlled.SendMessageAsync("execute print('hello')", sendTimeoutMs: 120);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 1500, $"Expected SendMessageAsync to abort within timeout, took {sw.ElapsedMilliseconds}ms");
        }
    }
}

