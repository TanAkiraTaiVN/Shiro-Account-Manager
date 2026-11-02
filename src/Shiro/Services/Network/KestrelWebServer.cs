#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Shiro.Services
{
    /// <summary>
    /// Next-Generation high-performance Kestrel HTTP & WebSocket Server for Shiro Account Manager.
    /// Supports legacy REST routes and real-time dual-stack WebSocket event streaming (/ws, /events).
    /// </summary>
    public sealed class KestrelWebServer : IAsyncDisposable
    {
        private static readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _activeSockets = new();

        private WebApplication? _app;
        private readonly int _port;
        private bool _allowExternal;
        private readonly Func<HttpContext, Task<string>> _handler;
        private readonly Func<HttpContext, bool>? _authValidator;
        private readonly CancellationTokenSource _cts = new();

        public bool IsRunning { get; private set; }
        internal bool IsListeningExternally => _allowExternal;

        /// <summary>
        /// Total number of active real-time WebSocket clients currently connected.
        /// </summary>
        public static int ActiveWebSocketCount => _activeSockets.Count;

        public KestrelWebServer(
            int port,
            bool allowExternal,
            Func<HttpContext, Task<string>> handler,
            Func<HttpContext, bool>? authValidator = null)
        {
            _port = port > 0 ? port : 7963;
            _allowExternal = allowExternal;
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _authValidator = authValidator;
        }

        public async Task StartAsync()
        {
            if (IsRunning) return;

            if (_allowExternal && !WebAuthPolicy.IsPasswordConfigured(AccountManager.WSPassword))
            {
                _allowExternal = false;
                Program.Logger?.Warn("[KestrelWebServer] External connections require a WebServer password (>= 6 chars); listening on localhost only.");
            }

            _app = LocalKestrelWebServer.Build("Shiro.WebServer", _port, _allowExternal);

            _app.Use(async (context, next) =>
            {
                if (!_allowExternal)
                {
                    // Only reflect loopback origins; other browser origins get no CORS grant.
                    string origin = context.Request.Headers["Origin"].ToString();
                    if (!string.IsNullOrEmpty(origin) && AccountManager.IsTrustedLocalOrigin(origin))
                    {
                        context.Response.Headers.Append("Access-Control-Allow-Origin", origin);
                        context.Response.Headers.Append("Vary", "Origin");
                    }
                }
                context.Response.Headers.Append("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
                context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
                context.Response.Headers.Append("X-Frame-Options", "DENY");

                if (HttpMethods.IsOptions(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    return;
                }

                await next();
            });

            _app.UseShiroWebSockets();

            // WebSocket Real-time Event Endpoints
            _app.Map("/ws", HandleWebSocketEndpointAsync);
            _app.Map("/events", HandleWebSocketEndpointAsync);
            _app.Map("/v2/events", HandleWebSocketEndpointAsync);

            // Legacy REST API Catch-All
            _app.Map("/{**catchAll}", async (HttpContext context) =>
            {
                try
                {
                    string result = await _handler(context);
                    if (!context.Response.HasStarted)
                    {
                        if (result != null && (result.StartsWith("{") || result.StartsWith("[")))
                            context.Response.ContentType = "application/json; charset=utf-8";
                        else
                            context.Response.ContentType = "text/plain; charset=utf-8";

                        await context.Response.WriteAsync(result ?? string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new ApiErrorResponse(false, ex.Message), ShiroAppJson.Default.ApiErrorResponse));
                }
            });

            // Hook domain event publishers to broadcast automatically
            AntiAfkService.StatusChanged += OnAntiAfkStatusChanged;
            RobloxSingletonService.StatusChanged += OnMultiRobloxStatusChanged;
            LaunchCoordinator.LaunchProgressChanged += OnLaunchProgressChanged;

            await _app.StartAsync(_cts.Token);
            IsRunning = true;
            Program.Logger?.Info($"[KestrelWebServer] ASP.NET Core Kestrel HTTP/WebSocket Gateway started on port {_port} (AllowExternal: {_allowExternal})");
        }

        private async Task HandleWebSocketEndpointAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("{\"error\":\"WebSocket upgrade request expected.\",\"endpoints\":[\"/ws\",\"/events\"]}");
                return;
            }

            if (_authValidator != null && !_authValidator(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            WebSocket? socket = null;
            SemaphoreSlim? gate = null;
            try
            {
                socket = await context.WebSockets.AcceptWebSocketAsync();
                gate = new SemaphoreSlim(1, 1);
                _activeSockets.TryAdd(socket, gate);

                // Initial welcome handshake frame
                var welcome = new GatewayWelcomeMessage(
                    "connected",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    new GatewayWelcomeData(
                        "Shiro Account Manager Gateway",
                        typeof(KestrelWebServer).Assembly.GetName().Version?.ToString(3) ?? "4.2.0",
                        _activeSockets.Count));

                byte[] welcomeBytes = JsonSerializer.SerializeToUtf8Bytes(welcome, ShiroAppJson.Default.GatewayWelcomeMessage);
                await SendSafeAsync(socket, gate, new ArraySegment<byte>(welcomeBytes), CancellationToken.None).ConfigureAwait(false);

                // Inbound message listening loop (pings, heartbeats, RPC)
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(2048);
                try
                {
                    while (socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                    {
                        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, 2048), _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            break;
                        }

                        if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                        {
                            ReadOnlySpan<byte> msgSpan = buffer.AsSpan(0, result.Count);
                            if (IsPingPayload(msgSpan))
                            {
                                var pong = new GatewayPongMessage(
                                    "pong",
                                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                                byte[] pongBytes = JsonSerializer.SerializeToUtf8Bytes(pong, ShiroAppJson.Default.GatewayPongMessage);
                                await SendSafeAsync(socket, gate, new ArraySegment<byte>(pongBytes), CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[KestrelWebServer] WebSocket client error: {ex.Message}");
            }
            finally
            {
                if (socket != null)
                {
                    if (_activeSockets.TryRemove(socket, out var removedGate))
                    {
                        try { removedGate.Dispose(); } catch (Exception) { }
                    }
                    try { socket.Dispose(); } catch (Exception) { }
                }
            }
        }

        private static bool IsPingPayload(ReadOnlySpan<byte> utf8Span)
        {
            while (!utf8Span.IsEmpty && utf8Span[0] <= (byte)' ')
                utf8Span = utf8Span.Slice(1);
            while (!utf8Span.IsEmpty && utf8Span[utf8Span.Length - 1] <= (byte)' ')
                utf8Span = utf8Span.Slice(0, utf8Span.Length - 1);

            if (utf8Span.Length == 4 &&
                (utf8Span[0] | 0x20) == (byte)'p' &&
                (utf8Span[1] | 0x20) == (byte)'i' &&
                (utf8Span[2] | 0x20) == (byte)'n' &&
                (utf8Span[3] | 0x20) == (byte)'g')
            {
                return true;
            }

            return utf8Span.IndexOf("\"ping\""u8) >= 0;
        }

        private static async Task SendSafeAsync(WebSocket socket, SemaphoreSlim gate, ArraySegment<byte> segment, CancellationToken ct = default)
        {
            if (socket.State != WebSocketState.Open || gate == null) return;
            bool acquired = false;
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                acquired = true;
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(segment, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                if (acquired)
                {
                    try { gate.Release(); }
                    catch (ObjectDisposedException) { }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// Asynchronously broadcasts an event envelope to all connected WebSocket clients.
        /// </summary>
        private static byte[] SerializeBroadcastEnvelope(string eventName, object payload)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("event"u8, eventName);
                writer.WriteNumber("timestamp"u8, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                writer.WritePropertyName("data"u8);
                if (payload is StatusBroadcastPayload status)
                {
                    JsonSerializer.Serialize(writer, status, ShiroAppJson.Default.StatusBroadcastPayload);
                }
                else if (payload is LaunchProgressBroadcastPayload progress)
                {
                    JsonSerializer.Serialize(writer, progress, ShiroAppJson.Default.LaunchProgressBroadcastPayload);
                }
                else if (payload is PulseBroadcastPayload pulse)
                {
                    JsonSerializer.Serialize(writer, pulse, ShiroAppJson.Default.PulseBroadcastPayload);
                }
                else if (payload is string str)
                {
                    writer.WriteStringValue(str);
                }
                else
                {
                    writer.WriteNullValue();
                }
                writer.WriteEndObject();
            }
            return stream.ToArray();
        }

        public static async Task BroadcastAsync(string eventName, object payload)
        {
            if (_activeSockets.IsEmpty) return;

            try
            {
                byte[] bytes = SerializeBroadcastEnvelope(eventName, payload);
                var segment = new ArraySegment<byte>(bytes);

                foreach (var kvp in _activeSockets)
                {
                    var socket = kvp.Key;
                    var gate = kvp.Value;
                    if (socket.State == WebSocketState.Open)
                    {
                        try
                        {
                            await SendSafeAsync(socket, gate, segment).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"WebSocket broadcast to client failed: {ex.Message}");
                            if (_activeSockets.TryRemove(socket, out var removedGate))
                            {
                                try { removedGate.Dispose(); } catch (Exception) { }
                            }
                        }
                    }
                    else
                    {
                        if (_activeSockets.TryRemove(socket, out var removedGate))
                        {
                            try { removedGate.Dispose(); } catch (Exception) { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"BroadcastAsync failed: {ex.Message}");
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

        private static void OnAntiAfkStatusChanged()
        {
            Broadcast("anti_afk", new StatusBroadcastPayload(AntiAfkService.IsEnabled, AntiAfkService.StatusText));
        }

        private static void OnMultiRobloxStatusChanged()
        {
            Broadcast("multi_roblox", new StatusBroadcastPayload(RobloxSingletonService.IsEnabled, RobloxSingletonService.StatusText));
        }

        private static void OnLaunchProgressChanged(LaunchProgress progress)
        {
            if (progress == null) return;
            Broadcast("launch_progress", new LaunchProgressBroadcastPayload(
                progress.Index,
                progress.Total,
                progress.Account?.Username,
                progress.Account?.UserID,
                progress.Status.ToString(),
                progress.Result,
                progress.Message,
                progress.Completed));
        }

        public async Task StopAsync()
        {
            if (!IsRunning || _app == null) return;

            AntiAfkService.StatusChanged -= OnAntiAfkStatusChanged;
            RobloxSingletonService.StatusChanged -= OnMultiRobloxStatusChanged;
            LaunchCoordinator.LaunchProgressChanged -= OnLaunchProgressChanged;

            // Gracefully close any active WebSockets
            foreach (var kvp in _activeSockets)
            {
                try
                {
                    if (kvp.Key.State == WebSocketState.Open)
                        await kvp.Key.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Closing active socket failed: {ex.Message}");
                }
                try { kvp.Value.Dispose(); } catch (Exception) { }
            }
            _activeSockets.Clear();

            try
            {
                _cts.Cancel();
                if (_app != null)
                {
                    await _app.StopAsync().ConfigureAwait(false);
                    await _app.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"Kestrel stop/dispose threw: {ex.Message}");
            }
            finally
            {
                IsRunning = false;
                _app = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
