#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shiro.Services;

namespace Shiro.Nexus
{
    /// <summary>
    /// Cloud-Native ASP.NET Core Kestrel WebSocket Server for Shiro Nexus Controller.
    /// Completely replaces legacy System.Net.HttpListener with high-performance ASP.NET Core Kestrel engine.
    /// </summary>
    public sealed class NexusWebSocketServer : IDisposable, IAsyncDisposable
    {
        private WebApplication? _app;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<WebSocket, byte> _activeSockets = new();
        private readonly int _port;
        private bool _allowExternal;
        private bool _isDisposed;

        public bool IsListening { get; private set; }
        internal bool IsListeningExternally => _allowExternal;

        public NexusWebSocketServer(IPAddress? address, int port)
        {
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");

            _port = port;
            _allowExternal = address != null && !address.Equals(IPAddress.Loopback);
        }

        public async Task StartAsync()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(NexusWebSocketServer));
            if (IsListening) return;

            if (_allowExternal && !WebAuthPolicy.IsPasswordConfigured(AccountManager.WSPassword))
            {
                _allowExternal = false;
                Program.Logger?.Warn("[NexusWebSocketServer] External connections require a WebServer password (>= 6 chars); listening on localhost only.");
            }
            else if (_allowExternal && !WebAuthPolicy.IsStrongExternalPassword(AccountManager.WSPassword))
            {
                Program.Logger?.Warn($"[NexusWebSocketServer] External access is enabled with a short password (< {WebAuthPolicy.RecommendedExternalPasswordLength} chars). A password of at least {WebAuthPolicy.RecommendedExternalPasswordLength} characters is strongly recommended.");
            }

            // Build and start entirely on ThreadPool to avoid capturing UI SynchronizationContext
            await Task.Run(async () =>
            {
                _app = LocalKestrelWebServer.Build("Shiro.NexusServer", _port, _allowExternal);
                _app.UseShiroWebSockets();

                _app.Map("/Nexus", async (HttpContext context) =>
                {
                    bool allowExt = _allowExternal && (AccountManager.AccountControl?.Get<bool>("AllowExternalConnections") ?? _allowExternal);
                    bool everyReq = AccountManager.WebServer != null && AccountManager.WebServer.Get<bool>("EveryRequestRequiresPassword");

                    var remoteIp = context.Connection.RemoteIpAddress;
                    bool isLoopback = remoteIp != null && IPAddress.IsLoopback(remoteIp);

                    string? headerPass = AccountManager.ExtractHeaderPassword(k => context.Request.Headers.TryGetValue(k, out var hVal) ? hVal.ToString() : null);
                    string? queryPass = AccountManager.ExtractQueryPassword(k => context.Request.Query.TryGetValue(k, out var qVal) ? qVal.ToString() : null);

                    var authSettings = new AuthSettings(allowExt, everyReq, AccountManager.WSPassword);
                    var authRequest = new AuthRequest(
                        AuthScope.NexusControl,
                        isLoopback,
                        context.Request.Host.Host,
                        context.Request.Headers["Origin"].ToString(),
                        headerPass,
                        queryPass);

                    var decision = WebAuthPolicy.Evaluate(authRequest, authSettings);
                    if (decision != AuthDecision.Allow)
                    {
                        context.Response.StatusCode = WebAuthPolicy.ToStatusCode(decision);
                        return;
                    }

                    if (!context.WebSockets.IsWebSocketRequest)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        return;
                    }

                    var query = context.Request.Query;
                    string? name = query["name"];
                    string? id = query["id"];
                    string jobId = string.IsNullOrEmpty(query["jobId"]) ? "UNKNOWN" : query["jobId"].ToString();

                    var modernNexus = NexusHost.Current;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id) || modernNexus == null)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        return;
                    }

                    ControlledAccount? account = null;
                    lock (_activeSockets)
                    {
                        account = modernNexus.Accounts?.FirstOrDefault(x => string.Equals(x.Username, name, StringComparison.OrdinalIgnoreCase));
                    }
                    if (account == null)
                    {
                        var snapshot = AccountManager.GetAccountsSnapshot();
                        var matched = snapshot.FirstOrDefault(x => string.Equals(x.Username, name, StringComparison.OrdinalIgnoreCase));
                        if (matched != null)
                        {
                            account = new ControlledAccount(matched);
                            AppDispatcher.Post(() =>
                            {
                                lock (_activeSockets)
                                {
                                    if (modernNexus.Accounts != null && !modernNexus.Accounts.Any(a => string.Equals(a.Username, name, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        modernNexus.Accounts.Add(account);
                                    }
                                }
                            });
                        }
                    }

                    if (account == null)
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }

                    if (account.LinkedAccount != null && account.LinkedAccount.UserID > 0 &&
                        long.TryParse(id, out long parsedUserId) && parsedUserId > 0 &&
                        account.LinkedAccount.UserID != parsedUserId)
                    {
                        Program.Logger?.Warn($"[Nexus] Account ID mismatch for '{name}': account UserID is {account.LinkedAccount.UserID}, client supplied {parsedUserId}.");
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }

                    using WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    _activeSockets.TryAdd(webSocket, 0);
                    account.InGameJobId = jobId;
                    account.Connect(webSocket);

                    const int MaxPayloadBytes = 2 * 1024 * 1024;
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
                    try
                    {
                        while (webSocket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                        {
                            var (msgType, messageText, payloadExceeded) = await ReceiveFullTextMessageAsync(
                                webSocket,
                                buffer,
                                MaxPayloadBytes,
                                _cts.Token).ConfigureAwait(false);

                            if (payloadExceeded)
                            {
                                Program.Logger?.Warn($"[Nexus] Oversized WebSocket payload exceeded {MaxPayloadBytes} bytes from {account.Username}. Terminating socket.");
                                try
                                {
                                    await webSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Payload exceeded limit", CancellationToken.None).ConfigureAwait(false);
                                }
                                catch (Exception ex) { Program.Logger?.Debug($"[Nexus] CloseAsync after oversized payload: {ex.Message}"); }
                                break;
                            }

                            if (msgType == WebSocketMessageType.Close)
                            {
                                try
                                {
                                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    Program.Logger?.Debug($"WebSocket normal closure error: {ex.Message}");
                                }
                                break;
                            }

                            if (msgType == WebSocketMessageType.Text && !string.IsNullOrEmpty(messageText))
                            {
                                account.HandleMessage(messageText);
                            }
                        }
                    }
                    catch (WebSocketException ex)
                    {
                        Program.Logger?.Debug($"[Nexus] Client WebSocket disconnected: {ex.Message}");
                    }
                    catch (OperationCanceledException) { /* canceled */ }
                    catch (Exception ex)
                    {
                        Program.Logger?.Error($"[Nexus] Client error for {account?.Username ?? "unknown"}: {ex}");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        _activeSockets.TryRemove(webSocket, out _);
                        account.Disconnect(webSocket);
                    }
                });

                await _app.StartAsync(_cts.Token).ConfigureAwait(false);
                IsListening = true;
                Program.Logger?.Info($"[Nexus] Native Kestrel WebSocket Server started on port {_port} (AllowExternal: {_allowExternal})");
            }).ConfigureAwait(false);
        }

        internal static async Task<(WebSocketMessageType MessageType, string? Text, bool PayloadExceeded)> ReceiveFullTextMessageAsync(
            WebSocket webSocket,
            byte[] buffer,
            int maxPayloadBytes,
            CancellationToken ct)
        {
            ValueWebSocketReceiveResult first = await webSocket.ReceiveAsync(new Memory<byte>(buffer), ct).ConfigureAwait(false);
            if (first.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, null, false);

            if (first.Count > maxPayloadBytes)
                return (first.MessageType, null, true);

            // Fast path: 99%+ of Nexus control frames fit in a single WebSocket frame (zero MemoryStream allocation)
            if (first.EndOfMessage)
            {
                string? singleFrameText = (first.MessageType == WebSocketMessageType.Text && first.Count > 0)
                    ? Encoding.UTF8.GetString(buffer.AsSpan(0, first.Count))
                    : null;
                return (first.MessageType, singleFrameText, false);
            }

            using var ms = new MemoryStream(Math.Max(8192, first.Count * 2));
            ms.Write(buffer, 0, first.Count);

            ValueWebSocketReceiveResult next;
            do
            {
                next = await webSocket.ReceiveAsync(new Memory<byte>(buffer), ct).ConfigureAwait(false);
                if (next.MessageType == WebSocketMessageType.Close)
                    return (WebSocketMessageType.Close, null, false);

                if (ms.Length + next.Count > maxPayloadBytes)
                    return (first.MessageType, null, true);

                ms.Write(buffer, 0, next.Count);
            }
            while (!next.EndOfMessage);

            string? text = (first.MessageType == WebSocketMessageType.Text && ms.Length > 0)
                ? Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length)
                : null;
            return (first.MessageType, text, false);
        }

        public async Task StopAsync()
        {
            if (!IsListening || _app == null) return;

            try
            {
                _cts.Cancel();

                foreach (var ws in _activeSockets.Keys)
                {
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"WebSocket server stopping close error: {ex.Message}");
                    }
                }
                _activeSockets.Clear();

                await _app.StopAsync().ConfigureAwait(false);
                await _app.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[Nexus] Error stopping server: {ex.Message}");
            }
            finally
            {
                IsListening = false;
                _app = null;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _cts.Cancel();
                foreach (var ws in _activeSockets.Keys)
                {
                    try { ws.Dispose(); }
                    catch (Exception ex) { Program.Logger?.Debug($"Error disposing socket on Nexus dispose: {ex.Message}"); }
                }
                _activeSockets.Clear();
                if (_app != null)
                {
                    _ = _app.DisposeAsync();
                    _app = null;
                }
                IsListening = false;
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Error stopping Nexus on dispose: {ex.Message}");
            }

            _cts.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            await StopAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
