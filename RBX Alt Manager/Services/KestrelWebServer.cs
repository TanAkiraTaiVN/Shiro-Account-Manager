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

namespace RBX_Alt_Manager.Services
{
    /// <summary>
    /// Next-Generation high-performance Kestrel HTTP & WebSocket Server for Shiro Account Manager.
    /// Supports legacy REST routes and real-time dual-stack WebSocket event streaming (/ws, /events).
    /// </summary>
    public sealed class KestrelWebServer : IAsyncDisposable
    {
        private static readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _activeSockets = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private WebApplication _app;
        private readonly int _port;
        private readonly bool _allowExternal;
        private readonly Func<HttpContext, Task<string>> _handler;
        private readonly Func<HttpContext, bool> _authValidator;
        private readonly CancellationTokenSource _cts = new();

        public bool IsRunning { get; private set; }

        /// <summary>
        /// Total number of active real-time WebSocket clients currently connected.
        /// </summary>
        public static int ActiveWebSocketCount => _activeSockets.Count;

        public KestrelWebServer(
            int port,
            bool allowExternal,
            Func<HttpContext, Task<string>> handler,
            Func<HttpContext, bool> authValidator = null)
        {
            _port = port > 0 ? port : 7963;
            _allowExternal = allowExternal;
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _authValidator = authValidator;
        }

        public async Task StartAsync()
        {
            if (IsRunning) return;

            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ApplicationName = "Shiro.WebServer",
                EnvironmentName = "Production"
            });

            builder.WebHost.UseKestrel(options =>
            {
                if (_allowExternal)
                    options.ListenAnyIP(_port);
                else
                    options.ListenLocalhost(_port);

                options.AddServerHeader = false;
            });

            builder.Services.AddRouting();

            _app = builder.Build();

            _app.Use(async (context, next) =>
            {
                if (_allowExternal)
                {
                    context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
                }
                else
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

            _app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30)
            });

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
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions));
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

            WebSocket socket = null;
            SemaphoreSlim gate = null;
            try
            {
                socket = await context.WebSockets.AcceptWebSocketAsync();
                gate = new SemaphoreSlim(1, 1);
                _activeSockets.TryAdd(socket, gate);

                // Initial welcome handshake frame
                var welcome = new
                {
                    @event = "connected",
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    data = new
                    {
                        server = "Shiro Account Manager Gateway",
                        version = "4.1.0",
                        activeClients = _activeSockets.Count
                    }
                };

                byte[] welcomeBytes = JsonSerializer.SerializeToUtf8Bytes(welcome, JsonOptions);
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
                                var pong = new
                                {
                                    @event = "pong",
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                };
                                byte[] pongBytes = JsonSerializer.SerializeToUtf8Bytes(pong, JsonOptions);
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
                        try { removedGate.Dispose(); } catch { }
                    }
                    try { socket.Dispose(); } catch { }
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
                    try { gate.Release(); } catch { }
                }
            }
        }

        /// <summary>
        /// Asynchronously broadcasts an event envelope to all connected WebSocket clients.
        /// </summary>
        public static async Task BroadcastAsync(string eventName, object payload)
        {
            if (_activeSockets.IsEmpty) return;

            try
            {
                var envelope = new
                {
                    @event = eventName,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    data = payload
                };

                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
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
                        catch
                        {
                            if (_activeSockets.TryRemove(socket, out var removedGate))
                            {
                                try { removedGate.Dispose(); } catch { }
                            }
                        }
                    }
                    else
                    {
                        if (_activeSockets.TryRemove(socket, out var removedGate))
                        {
                            try { removedGate.Dispose(); } catch { }
                        }
                    }
                }
            }
            catch { }
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
            Broadcast("anti_afk", new
            {
                enabled = AntiAfkService.IsEnabled,
                status = AntiAfkService.StatusText
            });
        }

        private static void OnMultiRobloxStatusChanged()
        {
            Broadcast("multi_roblox", new
            {
                enabled = RobloxSingletonService.IsEnabled,
                status = RobloxSingletonService.StatusText
            });
        }

        private static void OnLaunchProgressChanged(LaunchProgress progress)
        {
            if (progress == null) return;
            Broadcast("launch_progress", new
            {
                index = progress.Index,
                total = progress.Total,
                username = progress.Account?.Username,
                userId = progress.Account?.UserID,
                status = progress.Status.ToString(),
                result = progress.Result,
                message = progress.Message,
                completed = progress.Completed
            });
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
                catch { }
                try { kvp.Value.Dispose(); } catch { }
            }
            _activeSockets.Clear();

            try
            {
                _cts.Cancel();
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
            catch { }
            finally
            {
                IsRunning = false;
                _app = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _cts.Dispose();
        }
    }
}
