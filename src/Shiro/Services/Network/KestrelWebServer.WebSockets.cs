#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Shiro.Services
{
    public sealed partial class KestrelWebServer
    {
        private const int DefaultBroadcastSendTimeoutMs = 2000;
        private const int DefaultCloseTimeoutMs = 1500;
        private static readonly string[] SupportedWebSocketEndpoints = new[] { "/ws", "/events", "/v2/events" };

        private static async Task WriteWebSocketErrorAsync(
            HttpContext context,
            int statusCode,
            string message,
            bool isV2,
            bool includeEndpoints = false)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";

            string json;
            if (isV2)
            {
                json = AccountApiDispatcher.WebServerResponse(message, false);
            }
            else if (includeEndpoints)
            {
                json = JsonSerializer.Serialize(
                    new WebSocketUpgradeExpectedResponse(false, message, SupportedWebSocketEndpoints),
                    ShiroAppJson.Default.WebSocketUpgradeExpectedResponse);
            }
            else
            {
                json = JsonSerializer.Serialize(
                    new ApiErrorResponse(false, message),
                    ShiroAppJson.Default.ApiErrorResponse);
            }

            await context.Response.WriteAsync(json).ConfigureAwait(false);
        }

        private async Task HandleWebSocketEndpointAsync(HttpContext context)
        {
            bool isV2 = context.Request.Path.StartsWithSegments("/v2", StringComparison.OrdinalIgnoreCase);

            if (!context.WebSockets.IsWebSocketRequest)
            {
                string msg = isV2
                    ? "WebSocket upgrade request expected. Endpoints: /ws, /events, /v2/events"
                    : "WebSocket upgrade request expected.";
                await WriteWebSocketErrorAsync(context, StatusCodes.Status400BadRequest, msg, isV2, includeEndpoints: true)
                    .ConfigureAwait(false);
                return;
            }

            string? clientKey = context.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrEmpty(clientKey) && WebAuthPolicy.IsClientLockedOut(clientKey))
            {
                await WriteWebSocketErrorAsync(
                    context,
                    StatusCodes.Status429TooManyRequests,
                    "Too many failed authentication attempts. Please try again later.",
                    isV2).ConfigureAwait(false);
                return;
            }

            bool authorized = _authValidator(context);
            if (!context.Items.ContainsKey(AccountApiDispatcher.WsAuthRecordedItemKey))
            {
                WebAuthPolicy.RecordAuthResult(
                    clientKey,
                    authorized ? AuthDecision.Allow : AuthDecision.InvalidPassword);
            }

            if (!authorized)
            {
                await WriteWebSocketErrorAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "Unauthorized",
                    isV2).ConfigureAwait(false);
                return;
            }

            WebSocket? socket = null;
            SemaphoreSlim? gate = null;
            try
            {
                socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                gate = new SemaphoreSlim(1, 1);
                _instanceSockets.TryAdd(socket, gate);
                _activeSockets.TryAdd(socket, gate);

                var welcome = new GatewayWelcomeMessage(
                    "connected",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    new GatewayWelcomeData(
                        "Shiro Account Manager Gateway",
                        typeof(KestrelWebServer).Assembly.GetName().Version?.ToString(3) ?? "5.0.0",
                        _activeSockets.Count));

                byte[] welcomeBytes = JsonSerializer.SerializeToUtf8Bytes(welcome, ShiroAppJson.Default.GatewayWelcomeMessage);
                await SendSafeAsync(socket, gate, new ArraySegment<byte>(welcomeBytes), CancellationToken.None).ConfigureAwait(false);

                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(2048);
                using var frameStream = new MemoryStream();
                try
                {
                    while (socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                    {
                        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, 2048), _cts.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await CloseSafeAsync(socket, gate, WebSocketCloseStatus.NormalClosure, "Closing").ConfigureAwait(false);
                            break;
                        }
                        if (result.MessageType != WebSocketMessageType.Text) continue;

                        if (frameStream.Length + result.Count <= 16384 && result.Count > 0)
                            frameStream.Write(buffer, 0, result.Count);
                        if (!result.EndOfMessage) continue;

                        if (frameStream.Length > 0 && IsPingPayload(frameStream.GetBuffer().AsSpan(0, (int)frameStream.Length)))
                        {
                            byte[] pongBytes = JsonSerializer.SerializeToUtf8Bytes(
                                new GatewayPongMessage("pong", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                                ShiroAppJson.Default.GatewayPongMessage);
                            await SendSafeAsync(socket, gate, new ArraySegment<byte>(pongBytes), CancellationToken.None).ConfigureAwait(false);
                        }
                        frameStream.SetLength(0);
                    }
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
            }
            catch (OperationCanceledException) { /* canceled */ }
            catch (WebSocketException ex) { _logger.Debug($"[KestrelWebServer] WebSocket closed: {ex.Message}"); }
            catch (Exception ex) { _logger.Warn($"[KestrelWebServer] WebSocket client error: {ex.Message}"); }
            finally
            {
                if (socket != null)
                {
                    _instanceSockets.TryRemove(socket, out _);
                    if (_activeSockets.TryRemove(socket, out var removedGate))
                        WebSocketGateHelper.RetireAndDispose(removedGate);
                    SafeExecuteHelper.Try(socket.Dispose);
                }
            }
        }

        private async Task CloseOwnedSocketsAsync()
        {
            foreach (var kvp in _instanceSockets)
            {
                _instanceSockets.TryRemove(kvp.Key, out _);
                _activeSockets.TryRemove(kvp.Key, out _);
                await CloseSafeAsync(kvp.Key, kvp.Value, WebSocketCloseStatus.NormalClosure, "Server stopping", DefaultCloseTimeoutMs)
                    .ConfigureAwait(false);
                WebSocketGateHelper.RetireAndDispose(kvp.Value);
                SafeExecuteHelper.Try(kvp.Key.Dispose);
            }
        }

        internal static async Task CloseSafeAsync(
            WebSocket socket,
            SemaphoreSlim? gate,
            WebSocketCloseStatus closeStatus,
            string description,
            int timeoutMs = DefaultCloseTimeoutMs)
        {
            if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
                return;

            using var closeCts = new CancellationTokenSource(Math.Max(1, timeoutMs));
            if (gate == null || !WebSocketGateHelper.TryEnterUse(gate))
            {
                await CloseSocketFrameAsync(socket, closeStatus, description, closeCts.Token).ConfigureAwait(false);
                return;
            }

            bool acquired = false;
            try
            {
                acquired = await gate.WaitAsync(Math.Max(1, timeoutMs), closeCts.Token).ConfigureAwait(false);
                if (!acquired)
                {
                    SafeExecuteHelper.Try(socket.Abort);
                    return;
                }

                await CloseSocketFrameAsync(socket, closeStatus, description, closeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SafeExecuteHelper.Try(socket.Abort);
            }
            catch (ObjectDisposedException)
            {
                SafeExecuteHelper.Try(socket.Abort);
            }
            catch (Exception ex)
            {
                _staticLogger.Debug($"Closing active socket failed: {ex.Message}");
                SafeExecuteHelper.Try(socket.Abort);
            }
            finally
            {
                if (acquired)
                {
                    SafeExecuteHelper.Try(() => gate.Release(), "KestrelWebServer.CloseSafeAsync.ReleaseGate");
                }
                WebSocketGateHelper.ExitUse(gate);
            }
        }

        private static async Task CloseSocketFrameAsync(
            WebSocket socket,
            WebSocketCloseStatus closeStatus,
            string description,
            CancellationToken ct)
        {
            try
            {
                if (socket.State == WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(closeStatus, description, ct).ConfigureAwait(false);
                else if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(closeStatus, description, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or WebSocketException or InvalidOperationException)
            {
                SafeExecuteHelper.Try(socket.Abort);
            }
        }

        private static bool IsPingPayload(ReadOnlySpan<byte> utf8Span)
        {
            while (!utf8Span.IsEmpty && utf8Span[0] <= (byte)' ') utf8Span = utf8Span.Slice(1);
            while (!utf8Span.IsEmpty && utf8Span[^1] <= (byte)' ') utf8Span = utf8Span.Slice(0, utf8Span.Length - 1);

            if (utf8Span.Length == 4 &&
                (utf8Span[0] | 0x20) == (byte)'p' && (utf8Span[1] | 0x20) == (byte)'i' &&
                (utf8Span[2] | 0x20) == (byte)'n' && (utf8Span[3] | 0x20) == (byte)'g')
                return true;

            return utf8Span.SequenceEqual("\"ping\""u8) ||
                   utf8Span.IndexOf("\"type\":\"ping\""u8) >= 0 ||
                   utf8Span.IndexOf("\"type\": \"ping\""u8) >= 0;
        }

        private static async Task SendSafeAsync(WebSocket socket, SemaphoreSlim gate, ArraySegment<byte> segment, CancellationToken ct = default)
        {
            if (socket.State != WebSocketState.Open || gate == null) return;
            if (!WebSocketGateHelper.TryEnterUse(gate)) return;
            bool acquired = false;
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                acquired = true;
                if (socket.State == WebSocketState.Open && !WebSocketGateHelper.IsRetired(gate))
                {
                    await socket.SendAsync(segment, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException)
            {
                // Gate was retired concurrently during socket shutdown
            }
            finally
            {
                if (acquired)
                {
                    SafeExecuteHelper.Try(() => gate.Release(), "KestrelWebServer.SendSafeAsync.ReleaseGate");
                }
                WebSocketGateHelper.ExitUse(gate);
            }
        }

        /// <summary>
        /// Serializes a broadcast envelope directly to UTF-8 bytes, using source-generated metadata for known types
        /// and safe JSON fallback for arbitrary payload objects.
        /// </summary>
        internal static byte[] SerializeBroadcastEnvelope(string eventName, object? payload)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            switch (payload)
            {
                case null:
                    return JsonSerializer.SerializeToUtf8Bytes(
                        new BroadcastEnvelope<string?>(eventName, timestamp, null),
                        ShiroAppJson.Default.BroadcastEnvelopeString);
                case StatusBroadcastPayload status:
                    return JsonSerializer.SerializeToUtf8Bytes(
                        new BroadcastEnvelope<StatusBroadcastPayload>(eventName, timestamp, status),
                        ShiroAppJson.Default.BroadcastEnvelopeStatusBroadcastPayload);
                case LaunchProgressBroadcastPayload progress:
                    return JsonSerializer.SerializeToUtf8Bytes(
                        new BroadcastEnvelope<LaunchProgressBroadcastPayload>(eventName, timestamp, progress),
                        ShiroAppJson.Default.BroadcastEnvelopeLaunchProgressBroadcastPayload);
                case PulseBroadcastPayload pulse:
                    return JsonSerializer.SerializeToUtf8Bytes(
                        new BroadcastEnvelope<PulseBroadcastPayload>(eventName, timestamp, pulse),
                        ShiroAppJson.Default.BroadcastEnvelopePulseBroadcastPayload);
                case string str:
                    return JsonSerializer.SerializeToUtf8Bytes(
                        new BroadcastEnvelope<string?>(eventName, timestamp, str),
                        ShiroAppJson.Default.BroadcastEnvelopeString);
                case JsonElement elem:
                    return JsonSerializer.SerializeToUtf8Bytes(
                        new BroadcastEnvelope<JsonElement>(eventName, timestamp, elem),
                        ShiroAppJson.Default.BroadcastEnvelopeJsonElement);
                default:
                    try
                    {
                        JsonElement serialized = ShiroAppJson.Default.GetTypeInfo(payload.GetType()) is JsonTypeInfo typeInfo
                            ? JsonSerializer.SerializeToElement(payload, typeInfo)
                            : JsonSerializer.SerializeToElement(payload, payload.GetType(), FastJson.DefaultOptions);
                        return JsonSerializer.SerializeToUtf8Bytes(
                            new BroadcastEnvelope<JsonElement>(eventName, timestamp, serialized),
                            ShiroAppJson.Default.BroadcastEnvelopeJsonElement);
                    }
                    catch (Exception ex)
                    {
                        _staticLogger.Debug($"SerializeBroadcastEnvelope fallback for {payload.GetType().Name}: {ex.Message}");
                        return JsonSerializer.SerializeToUtf8Bytes(
                            new BroadcastEnvelope<string?>(eventName, timestamp, payload.ToString()),
                            ShiroAppJson.Default.BroadcastEnvelopeString);
                    }
            }
        }

        internal static async Task BroadcastToSocketsAsync(
            ConcurrentDictionary<WebSocket, SemaphoreSlim> sockets,
            ArraySegment<byte> segment,
            int sendTimeoutMs = DefaultBroadcastSendTimeoutMs)
        {
            if (sockets.IsEmpty) return;

            var tasks = new List<Task>(sockets.Count);
            foreach (var kvp in sockets)
            {
                var socket = kvp.Key;
                var gate = kvp.Value;
                if (socket.State == WebSocketState.Open)
                {
                    tasks.Add(SendWithTimeoutAndPruneAsync(sockets, socket, gate, segment, sendTimeoutMs));
                }
                else if (sockets.TryRemove(socket, out var removedGate))
                {
                    WebSocketGateHelper.RetireAndDispose(removedGate);
                }
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        private static async Task SendWithTimeoutAndPruneAsync(
            ConcurrentDictionary<WebSocket, SemaphoreSlim> sockets,
            WebSocket socket,
            SemaphoreSlim gate,
            ArraySegment<byte> segment,
            int sendTimeoutMs)
        {
            using var cts = new CancellationTokenSource(sendTimeoutMs);
            try
            {
                await SendSafeAsync(socket, gate, segment, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _staticLogger.Debug($"WebSocket broadcast to client failed or timed out: {ex.Message}");
                if (sockets.TryRemove(socket, out var removedGate))
                {
                    WebSocketGateHelper.RetireAndDispose(removedGate);
                }
                SafeExecuteHelper.Try(socket.Abort);
            }
        }

        public static async Task BroadcastAsync(string eventName, object payload)
        {
            if (_activeSockets.IsEmpty) return;

            try
            {
                byte[] bytes = SerializeBroadcastEnvelope(eventName, payload);
                await BroadcastToSocketsAsync(_activeSockets, new ArraySegment<byte>(bytes)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _staticLogger.Debug($"BroadcastAsync failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Fires a non-blocking background broadcast to all connected WebSocket clients.
        /// </summary>
        public static void Broadcast(string eventName, object payload)
        {
            if (_activeSockets.IsEmpty) return;
            _ = BroadcastAsync(eventName, payload);
        }
    }
}
