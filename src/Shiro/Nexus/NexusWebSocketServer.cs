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
        private readonly bool _allowExternal;
        private bool _isDisposed;

        public bool IsListening { get; private set; }

        public NexusWebSocketServer(IPAddress? address, int port)
        {
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");

            _port = port;
            _allowExternal = address != null && !address.Equals(IPAddress.Loopback);
        }

        public void Start() => Task.Run(async () => await StartAsync().ConfigureAwait(false)).GetAwaiter().GetResult();

        public async Task StartAsync()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(NexusWebSocketServer));
            if (IsListening) return;

            // Build and start entirely on ThreadPool to avoid capturing UI SynchronizationContext
            await Task.Run(async () =>
            {
                _app = LocalKestrelWebServer.Build("Shiro.NexusServer", _port, _allowExternal);
                _app.UseShiroWebSockets();

                _app.Map("/Nexus", async (HttpContext context) =>
                {
                    if (!_allowExternal)
                    {
                        var remoteIp = context.Connection.RemoteIpAddress;
                        if (remoteIp != null && !IPAddress.IsLoopback(remoteIp))
                        {
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return;
                        }

                        if (!AccountManager.IsLoopbackHostName(context.Request.Host.Host))
                        {
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return;
                        }

                        if (!AccountManager.IsTrustedLocalOrigin(context.Request.Headers["Origin"].ToString()))
                        {
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return;
                        }
                    }

                    string wsPass = AccountManager.WSPassword;
                    bool requiresPassword = (AccountManager.WebServer != null && AccountManager.WebServer.Get<bool>("EveryRequestRequiresPassword")) ||
                                            (!string.IsNullOrEmpty(wsPass) && wsPass.Length >= 6);
                    if (requiresPassword)
                    {
                        if (string.IsNullOrEmpty(wsPass) || wsPass.Length < 6)
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return;
                        }

                        string providedAuth = AccountManager.ExtractRequestPassword(
                            k => context.Request.Headers.TryGetValue(k, out var hVal) ? hVal.ToString() : null,
                            k => context.Request.Query.TryGetValue(k, out var qVal) ? qVal.ToString() : null);

                        if (string.IsNullOrEmpty(providedAuth) || !AccountManager.FixedTimeEqualsPassword(providedAuth, wsPass))
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return;
                        }
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

                    var modernNexus = Shiro.AvaloniaUI.Views.NexusWindow.Instance;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id) || modernNexus == null)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        return;
                    }

                    ControlledAccount? account = modernNexus.Accounts?.FirstOrDefault(x => string.Equals(x.Username, name, StringComparison.OrdinalIgnoreCase));
                    if (account == null)
                    {
                        var snapshot = AccountManager.GetAccountsSnapshot();
                        var matched = snapshot.FirstOrDefault(x => string.Equals(x.Username, name, StringComparison.OrdinalIgnoreCase));
                        if (matched != null)
                        {
                            account = new ControlledAccount(matched);
                            AppDispatcher.Post(() =>
                            {
                                if (modernNexus.Accounts != null && !modernNexus.Accounts.Any(a => string.Equals(a.Username, name, StringComparison.OrdinalIgnoreCase)))
                                {
                                    modernNexus.Accounts.Add(account);
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
                        using var ms = new MemoryStream();
                        while (webSocket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                        {
                            ms.SetLength(0);
                            ValueWebSocketReceiveResult result;
                            bool payloadExceeded = false;
                            do
                            {
                                result = await webSocket.ReceiveAsync(new Memory<byte>(buffer), _cts.Token);
                                if (result.MessageType == WebSocketMessageType.Close)
                                    break;

                                if (ms.Length + result.Count > MaxPayloadBytes)
                                {
                                    payloadExceeded = true;
                                    break;
                                }

                                ms.Write(buffer, 0, result.Count);
                            }
                            while (!result.EndOfMessage);

                            if (payloadExceeded)
                            {
                                Program.Logger?.Warn($"[Nexus] Oversized WebSocket payload exceeded {MaxPayloadBytes} bytes from {account.Username}. Terminating socket.");
                                try
                                {
                                    await webSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Payload exceeded limit", CancellationToken.None).ConfigureAwait(false);
                                }
                                catch (Exception) { }
                                break;
                            }

                            if (result.MessageType == WebSocketMessageType.Close)
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

                            if (result.MessageType == WebSocketMessageType.Text && ms.Length > 0)
                            {
                                string message = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                                account.HandleMessage(message);
                            }
                        }
                    }
                    catch (WebSocketException ex)
                    {
                        Program.Logger?.Debug($"[Nexus] Client WebSocket disconnected: {ex.Message}");
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Program.Logger?.Error($"[Nexus] Client error for {account?.Username ?? "unknown"}: {ex}");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        _activeSockets.TryRemove(webSocket, out _);
                        account.Disconnect();
                    }
                });

                await _app.StartAsync(_cts.Token).ConfigureAwait(false);
                IsListening = true;
                Program.Logger?.Info($"[Nexus] Native Kestrel WebSocket Server started on port {_port} (AllowExternal: {_allowExternal})");
            }).ConfigureAwait(false);
        }

        public void Stop() => Task.Run(async () => await StopAsync().ConfigureAwait(false)).GetAwaiter().GetResult();

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

            try { Stop(); }
            catch (Exception ex) { Program.Logger?.Debug($"Error stopping Nexus on dispose: {ex.Message}"); }

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
