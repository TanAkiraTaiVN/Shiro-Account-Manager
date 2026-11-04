#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class KestrelWebServerTests
    {
        [Fact]
        public void Constructor_DefaultsPortWhenInvalid()
        {
            var server = new KestrelWebServer(0, false, ctx => Task.FromResult("ok"));
            Assert.False(server.IsRunning);
            Assert.False(server.IsListeningExternally);
        }

        [Fact]
        public void Constructor_NullHandler_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new KestrelWebServer(8000, false, null!));
        }

        [Fact]
        public async Task KestrelWebServer_HttpLifecycleAndEndpoints_WorkCorrectly()
        {
            int testPort = 19875;
            await using var server = new KestrelWebServer(testPort, false, async ctx =>
            {
                if (ctx.Request.Path == "/api/json")
                    return "{\"hello\":\"world\"}";
                if (ctx.Request.Path == "/api/array")
                    return "[1,2,3]";
                if (ctx.Request.Path == "/api/error")
                    throw new InvalidOperationException("Endpoint failure simulation");
                return "simple text response";
            });

            Assert.False(server.IsRunning);
            await server.StartAsync();
            Assert.True(server.IsRunning);

            // Double start should be a no-op
            await server.StartAsync();
            Assert.True(server.IsRunning);

            using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{testPort}") };

            // 1. JSON object response
            using (var resp = await httpClient.GetAsync("/api/json"))
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
                string text = await resp.Content.ReadAsStringAsync();
                Assert.Equal("{\"hello\":\"world\"}", text);
            }

            // 2. JSON array response
            using (var resp = await httpClient.GetAsync("/api/array"))
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
                string text = await resp.Content.ReadAsStringAsync();
                Assert.Equal("[1,2,3]", text);
            }

            // 3. Plain text response
            using (var resp = await httpClient.GetAsync("/api/plain"))
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Equal("text/plain", resp.Content.Headers.ContentType?.MediaType);
                string text = await resp.Content.ReadAsStringAsync();
                Assert.Equal("simple text response", text);
            }

            // 4. Exception in handler returns 500 InternalServerError with JSON
            using (var resp = await httpClient.GetAsync("/api/error"))
            {
                Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
                string text = await resp.Content.ReadAsStringAsync();
                Assert.Contains("Endpoint failure simulation", text);
            }

            // 5. OPTIONS preflight request
            using (var optReq = new HttpRequestMessage(HttpMethod.Options, "/api/json"))
            {
                optReq.Headers.TryAddWithoutValidation("Origin", "http://localhost");
                using var optResp = await httpClient.SendAsync(optReq);
                Assert.Equal(HttpStatusCode.OK, optResp.StatusCode);
                Assert.True(optResp.Headers.Contains("Access-Control-Allow-Methods"));
                Assert.True(optResp.Headers.Contains("X-Frame-Options"));
            }

            // 6. WebSocket endpoint without upgrade request returns 400
            using (var wsResp = await httpClient.GetAsync("/ws"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, wsResp.StatusCode);
                string text = await wsResp.Content.ReadAsStringAsync();
                Assert.Contains("WebSocket upgrade request expected", text);
            }

            // Stop server
            await server.StopAsync();
            Assert.False(server.IsRunning);

            // Double stop should be safe
            await server.StopAsync();
            Assert.False(server.IsRunning);
        }

        [Fact]
        public void SerializeBroadcastEnvelope_SerializesVariousPayloads()
        {
            var method = typeof(KestrelWebServer).GetMethod("SerializeBroadcastEnvelope", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            // 1. StatusBroadcastPayload
            var statusPayload = new StatusBroadcastPayload(true, "running");
            byte[]? statusBytes = method!.Invoke(null, new object[] { "status_evt", statusPayload }) as byte[];
            Assert.NotNull(statusBytes);
            string statusJson = System.Text.Encoding.UTF8.GetString(statusBytes!);
            Assert.Contains("\"event\":\"status_evt\"", statusJson);
            Assert.Contains("\"enabled\":true", statusJson);
            Assert.Contains("\"status\":\"running\"", statusJson);

            // 2. PulseBroadcastPayload
            var pulsePayload = new PulseBroadcastPayload(1234567890);
            byte[]? pulseBytes = method.Invoke(null, new object[] { "pulse_evt", pulsePayload }) as byte[];
            Assert.NotNull(pulseBytes);
            string pulseJson = System.Text.Encoding.UTF8.GetString(pulseBytes!);
            Assert.Contains("\"event\":\"pulse_evt\"", pulseJson);
            Assert.Contains("\"timestamp\":1234567890", pulseJson);

            // 3. String payload
            byte[]? strBytes = method.Invoke(null, new object[] { "str_evt", "custom message" }) as byte[];
            Assert.NotNull(strBytes);
            string strJson = System.Text.Encoding.UTF8.GetString(strBytes!);
            Assert.Contains("\"event\":\"str_evt\"", strJson);
            Assert.Contains("\"data\":\"custom message\"", strJson);

            // 4. Null payload
            byte[]? nullBytes = method.Invoke(null, new object[] { "null_evt", new object() }) as byte[];
            Assert.NotNull(nullBytes);
            string nullJson = System.Text.Encoding.UTF8.GetString(nullBytes!);
            Assert.Contains("\"event\":\"null_evt\"", nullJson);
            Assert.Contains("\"data\":null", nullJson);
        }

        [Fact]
        public void DomainEvents_TriggerBroadcastSafely()
        {
            // Verify event handler wiring via reflection
            var onAntiAfk = typeof(KestrelWebServer).GetMethod("OnAntiAfkStatusChanged", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(onAntiAfk);
            var ex1 = Record.Exception(() => onAntiAfk!.Invoke(null, null));
            Assert.Null(ex1);

            var onMultiRoblox = typeof(KestrelWebServer).GetMethod("OnMultiRobloxStatusChanged", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(onMultiRoblox);
            var ex2 = Record.Exception(() => onMultiRoblox!.Invoke(null, null));
            Assert.Null(ex2);

            var onProgress = typeof(KestrelWebServer).GetMethod("OnLaunchProgressChanged", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(onProgress);
            var ex3 = Record.Exception(() => onProgress!.Invoke(null, new object?[] { null }));
            Assert.Null(ex3);

            var progress = new LaunchProgress
            {
                Index = 1,
                Total = 5,
                Status = LaunchOutcome.Success,
                Result = "ok",
                Message = "Launched successfully",
                Completed = true
            };
            var ex4 = Record.Exception(() => onProgress!.Invoke(null, new object[] { progress }));
            Assert.Null(ex4);
        }

        [Fact]
        public async Task KestrelWebServer_WebSocketLifecycleAndBroadcast_WorksCorrectly()
        {
            int testPort = 19888;
            await using var server = new KestrelWebServer(testPort, false, ctx => Task.FromResult("ok"));
            await server.StartAsync();

            try
            {
                using var clientWs = new System.Net.WebSockets.ClientWebSocket();
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));

                await clientWs.ConnectAsync(new Uri($"ws://127.0.0.1:{testPort}/ws"), cts.Token);
                Assert.Equal(System.Net.WebSockets.WebSocketState.Open, clientWs.State);
                Assert.True(KestrelWebServer.ActiveWebSocketCount > 0);

                // 1. Receive welcome frame
                byte[] buffer = new byte[2048];
                var recvResult = await clientWs.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Text, recvResult.MessageType);
                string welcomeJson = System.Text.Encoding.UTF8.GetString(buffer, 0, recvResult.Count);
                Assert.Contains("\"event\":\"connected\"", welcomeJson);

                // 2. Send ping frame and receive pong
                byte[] pingBytes = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
                await clientWs.SendAsync(new ArraySegment<byte>(pingBytes), System.Net.WebSockets.WebSocketMessageType.Text, true, cts.Token);

                var pongResult = await clientWs.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                string pongJson = System.Text.Encoding.UTF8.GetString(buffer, 0, pongResult.Count);
                Assert.Contains("\"event\":\"pong\"", pongJson);

                // 3. Test Broadcast
                KestrelWebServer.Broadcast("test_alert", new StatusBroadcastPayload(true, "monitoring"));
                var broadcastResult = await clientWs.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                string broadcastJson = System.Text.Encoding.UTF8.GetString(buffer, 0, broadcastResult.Count);
                Assert.Contains("\"event\":\"test_alert\"", broadcastJson);

                // 4. Normal closure
                await clientWs.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Closing test", cts.Token);
            }
            finally
            {
                await server.StopAsync();
            }
        }

        [Fact]
        public void KestrelWebServer_Logger_PropertyEnforcesNonNull()
        {
            var origLogger = KestrelWebServer.Logger;
            try
            {
                var mockLogger = NSubstitute.Substitute.For<IAppLogger>();
                KestrelWebServer.Logger = mockLogger;
                Assert.Same(mockLogger, KestrelWebServer.Logger);

                Assert.Throws<ArgumentNullException>(() => KestrelWebServer.Logger = null!);
            }
            finally
            {
                KestrelWebServer.Logger = origLogger;
            }
        }
    }
}
