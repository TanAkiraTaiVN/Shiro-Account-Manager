#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Shiro.Services
{
    /// <summary>
    /// Next-Generation high-performance Kestrel HTTP &amp; WebSocket Server for Shiro Account Manager.
    /// Supports legacy and /v2 REST routes plus real-time WebSocket event streaming (/ws, /events, /v2/events).
    /// </summary>
    public sealed partial class KestrelWebServer : IAsyncDisposable
    {
        private static readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _activeSockets = new();
        private static readonly AsyncLocal<IAppLogger?> _staticLoggerOverride = new();
        private static IAppLogger _staticLogger => Logger;
        public static IAppLogger Logger
        {
            get => _staticLoggerOverride.Value ?? AppServices.Logger;
            set => _staticLoggerOverride.Value = value ?? throw new ArgumentNullException(nameof(value));
        }

        private WebApplication? _app;
        private readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _instanceSockets = new();
        private readonly int _port;
        private bool _allowExternal;
        private readonly Func<HttpContext, Task<string>> _handler;
        private readonly Func<HttpContext, bool> _authValidator;
        private readonly IAppLogger _logger;
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
            Func<HttpContext, bool>? authValidator = null,
            IAppLogger? logger = null)
        {
            _port = port > 0 ? port : 7963;
            _allowExternal = allowExternal;
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            // Always fail closed unless the caller supplies an explicit validator.
            _authValidator = authValidator ?? (_ => false);
            _logger = logger ?? AppServices.Logger;
        }

        public async Task StartAsync()
        {
            if (IsRunning) return;

            if (_allowExternal && !WebAuthPolicy.IsStrongExternalPassword(AppServices.ServerBrowserState.WebSocketPassword))
            {
                _allowExternal = false;
                _logger.Warn($"[KestrelWebServer] External connections require a WebServer password (>= {WebAuthPolicy.MinExternalPasswordLength} chars); listening on localhost only.");
            }

            _app = LocalKestrelWebServer.Build("Shiro.WebServer", _port, _allowExternal);

            _app.Use(async (context, next) =>
            {
                string origin = context.Request.Headers["Origin"].ToString();
                if (!string.IsNullOrEmpty(origin) &&
                    WebAuthPolicy.IsOriginAllowed(origin, context.Request.Host.Value, _allowExternal))
                {
                    context.Response.Headers.Append("Access-Control-Allow-Origin", origin);
                    context.Response.Headers.Append("Vary", "Origin");
                }
                context.Response.Headers.Append("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
                context.Response.Headers.Append("Access-Control-Allow-Headers", "Content-Type, X-Shiro-Auth, X-RAM-Auth, Authorization");
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

            // Legacy and v2 REST API Catch-All
            _app.Map("/{**catchAll}", async (HttpContext context) =>
            {
                try
                {
                    string result = await _handler(context);
                    if (!context.Response.HasStarted)
                    {
                        if (string.IsNullOrEmpty(context.Response.ContentType))
                        {
                            if (result != null && (result.StartsWith("{") || result.StartsWith("[")))
                                context.Response.ContentType = "application/json; charset=utf-8";
                            else
                                context.Response.ContentType = "text/plain; charset=utf-8";
                        }

                        await context.Response.WriteAsync(result ?? string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[KestrelWebServer] Unhandled request exception on {context.Request.Path}: {LogRedactor.Redact(ex.Message)}");
                    if (!context.Response.HasStarted)
                    {
                        bool hideDetails = _allowExternal ||
                                           context.Connection.RemoteIpAddress == null ||
                                           !IPAddress.IsLoopback(context.Connection.RemoteIpAddress);
                        string clientError = hideDetails
                            ? AccountApiDispatcher.InternalErrorMessage
                            : LogRedactor.Redact(ex.Message);
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        context.Response.ContentType = "application/json; charset=utf-8";
                        await context.Response.WriteAsync(JsonSerializer.Serialize(
                            new ApiErrorResponse(false, clientError),
                            ShiroAppJson.Default.ApiErrorResponse));
                    }
                }
            });

            await _app.StartAsync(_cts.Token);

            // Hook domain event publishers only after Kestrel binds cleanly
            AntiAfkService.StatusChanged += OnAntiAfkStatusChanged;
            RobloxSingletonService.StatusChanged += OnMultiRobloxStatusChanged;
            LaunchCoordinator.LaunchProgressChanged += OnLaunchProgressChanged;

            IsRunning = true;
            _logger.Info($"[KestrelWebServer] ASP.NET Core Kestrel HTTP/WebSocket Gateway started on port {_port} (AllowExternal: {_allowExternal})");
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

            SafeExecuteHelper.Try(() => _cts.Cancel());
            await CloseOwnedSocketsAsync().ConfigureAwait(false);

            try
            {
                if (_app != null)
                {
                    await _app.StopAsync().ConfigureAwait(false);
                    await _app.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Kestrel stop/dispose threw: {ex.Message}");
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
