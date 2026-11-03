#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;

namespace Shiro.Services
{
    public sealed class DiscordActivityItem
    {
        public string? Details { get; set; }
        public string? State { get; set; }
        public long? StartTimestamp { get; set; }
        public string? LargeImageKey { get; set; } = "shiro_logo";
        public string? LargeImageText { get; set; } = "Shiro Account Manager";
        public string? SmallImageKey { get; set; }
        public string? SmallImageText { get; set; }
        public List<DiscordButtonItem>? Buttons { get; set; }
    }

    public sealed class DiscordButtonItem
    {
        public string Label { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    /// <summary>
    /// Native Discord Rich Presence service communicating directly via Windows Named Pipes (\\.\pipe\discord-ipc-0..9).
    /// Features one-click "Join Current Server" button integration, streamer mode awareness, and zero external dependencies.
    /// </summary>
    public sealed class DiscordRpcService : IDisposable
    {
        private static readonly Lazy<DiscordRpcService> _instance = new(() => new DiscordRpcService());
        public static DiscordRpcService Default => _instance.Value;

        // Shiro's registered Discord Client Application ID (or generic fallback)
        public const string DefaultClientId = "1301012345678901234";

        public static string ClientId =>
            AppSettings.General.Get<string>(AppSettingsKeys.General.DiscordRpcClientId, DefaultClientId) ?? DefaultClientId;

        private readonly System.Threading.Lock _pipeLock = new();
        private readonly SemaphoreSlim _pipeGate = new(1, 1);
        private NamedPipeClientStream? _pipeStream;
        private CancellationTokenSource? _cts;
        private bool _isConnected;
        private bool _isDisposed;
        private DiscordActivityItem? _lastActivity;
        private readonly long _sessionStartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public bool IsConnected => _isConnected;

        public bool IsEnabled
        {
            get => AppSettings.General.Get<bool>(AppSettingsKeys.General.DiscordRpcEnabled, true);
            set
            {
                AppSettings.General.Set(AppSettingsKeys.General.DiscordRpcEnabled, value ? "true" : "false");
                if (value)
                {
                    Start();
                    UpdateCurrentPresence();
                }
                else
                {
                    ClearPresence();
                    Stop();
                }
            }
        }

        public void Start()
        {
            if (!IsEnabled || _isConnected || _cts != null) return;

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => WorkerLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            Disconnect();
        }

        private async Task WorkerLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !_isDisposed)
            {
                if (!_isConnected)
                {
                    await TryConnectAsync(ct).ConfigureAwait(false);
                }

                try
                {
                    await Task.Delay(15000, ct).ConfigureAwait(false);
                    if (_isConnected && _lastActivity != null)
                    {
                        await SendActivityAsync(_lastActivity, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[DiscordRpcService] Loop tick exception: {ex.Message}");
                    Disconnect();
                }
            }
        }

        private async Task TryConnectAsync(CancellationToken ct)
        {
            for (int i = 0; i < 10; i++)
            {
                if (ct.IsCancellationRequested) return;
                NamedPipeClientStream? pipe = null;
                try
                {
                    pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(1000, ct).ConfigureAwait(false);

                    lock (_pipeLock)
                    {
                        _pipeStream = pipe;
                    }

                    // Send Handshake (Opcode 0)
                    string handshakeJson = JsonSerializer.Serialize(new { v = 1, client_id = ClientId });
                    await WriteFrameAsync(0, handshakeJson, ct).ConfigureAwait(false);

                    // Read Handshake response
                    var (_, _) = await ReadFrameAsync(ct).ConfigureAwait(false);

                    _isConnected = true;
                    Program.Logger?.Info($"[DiscordRpcService] Connected to Discord IPC via pipe discord-ipc-{i}.");

                    if (_lastActivity != null)
                    {
                        await SendActivityAsync(_lastActivity, ct).ConfigureAwait(false);
                    }
                    return;
                }
                catch
                {
                    lock (_pipeLock)
                    {
                        if (ReferenceEquals(_pipeStream, pipe))
                            _pipeStream = null;
                    }
                    try { pipe?.Dispose(); }
                    catch (Exception ex) { Program.Logger?.Debug($"[DiscordRpcService.ConnectPipeAsync] Pipe dispose: {ex.Message}"); }
                    // Continue probing next pipe index
                }
            }
        }

        private void Disconnect()
        {
            lock (_pipeLock)
            {
                _isConnected = false;
                try
                {
                    _pipeStream?.Dispose();
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[DiscordRpcService.Disconnect] {ex.Message}");
                }
                _pipeStream = null;
            }
        }

        /// <summary>
        /// Updates Discord Rich Presence based on currently running Roblox accounts and active game server.
        /// </summary>
        public void UpdatePresence(string? gameName, long placeId, string? jobId, int totalAccounts, int activeInGameAccounts)
        {
            if (!IsEnabled) return;

            bool isStreamerMode = StreamerModeService.IsActive;
            bool allowJoin = AppSettings.General.Get<bool>(AppSettingsKeys.General.DiscordRpcJoinCurrentServer, true);
            bool showGame = AppSettings.General.Get<bool>(AppSettingsKeys.General.DiscordRpcShowGame, true);

            string details;
            if (!string.IsNullOrWhiteSpace(gameName) && showGame)
            {
                details = Shiro.Services.LocalizationService.Format("DiscordRpc.PlayingGame", gameName);
            }
            else
            {
                details = Shiro.Services.LocalizationService.Format("DiscordRpc.ManagingAccounts", totalAccounts);
            }

            string state = activeInGameAccounts > 0
                ? Shiro.Services.LocalizationService.Format("DiscordRpc.ClientsOnline", activeInGameAccounts)
                : Shiro.Services.LocalizationService.Get("DiscordRpc.ReadyToLaunch");

            if (isStreamerMode)
            {
                state = Shiro.Services.LocalizationService.Get("DiscordRpc.StreamerMode");
            }

            var activity = new DiscordActivityItem
            {
                Details = details,
                State = state,
                StartTimestamp = _sessionStartTime,
                LargeImageKey = "shiro_logo",
                LargeImageText = "Shiro Account Manager"
            };

            // Add "Join Game" Button if in game, placeId is valid, and streamer mode allows it
            if (placeId > 0 && allowJoin && !isStreamerMode)
            {
                string joinUrl = !string.IsNullOrWhiteSpace(jobId)
                    ? $"https://www.roblox.com/games/start?placeId={placeId}&gameInstanceId={jobId}"
                    : $"https://www.roblox.com/games/{placeId}";

                activity.Buttons = new List<DiscordButtonItem>
                {
                    new DiscordButtonItem
                    {
                        Label = "Tham gia Server",
                        Url = joinUrl
                    }
                };
            }

            _lastActivity = activity;
            if (_isConnected)
            {
                _ = SendActivityAsync(activity, CancellationToken.None);
            }
            else
            {
                Start();
            }
        }

        public void UpdateCurrentPresence()
        {
            int total = AppServices.AccountRepository.Count;
            int active = 0;
            Process[] procs = Array.Empty<Process>();
            try
            {
                procs = Process.GetProcessesByName("RobloxPlayerBeta");
                active = procs.Length;
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[DiscordRpcService.UpdatePresence] {ex.Message}");
            }
            finally
            {
                foreach (var p in procs)
                {
                    try { p.Dispose(); }
                    catch (Exception ex) { Program.Logger?.Debug($"[DiscordRpcService.UpdatePresence] Process dispose: {ex.Message}"); }
                }
            }

            string currentPlace = AppSettings.CurrentPlaceId;
            long placeId = 0;
            long.TryParse(currentPlace, out placeId);

            UpdatePresence(null, placeId, AppSettings.CurrentJobId, total, active);
        }

        public void ClearPresence()
        {
            _lastActivity = null;
            if (!_isConnected) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var payload = new
                    {
                        cmd = "SET_ACTIVITY",
                        args = new { pid = Process.GetCurrentProcess().Id, activity = (object?)null },
                        nonce = Guid.NewGuid().ToString()
                    };
                    string json = JsonSerializer.Serialize(payload);
                    await WriteFrameAsync(1, json, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[DiscordRpcService.ClearPresence] {ex.Message}");
                }
            });
        }

        private async Task SendActivityAsync(DiscordActivityItem activity, CancellationToken ct)
        {
            try
            {
                var discordActivityObj = new Dictionary<string, object?>
                {
                    ["details"] = activity.Details,
                    ["state"] = activity.State,
                    ["timestamps"] = activity.StartTimestamp.HasValue ? new { start = activity.StartTimestamp.Value } : null,
                    ["assets"] = new
                    {
                        large_image = activity.LargeImageKey,
                        large_text = activity.LargeImageText,
                        small_image = activity.SmallImageKey,
                        small_text = activity.SmallImageText
                    }
                };

                if (activity.Buttons != null && activity.Buttons.Count > 0)
                {
                    var buttonsList = new List<object>();
                    foreach (var btn in activity.Buttons)
                    {
                        buttonsList.Add(new { label = btn.Label, url = btn.Url });
                    }
                    discordActivityObj["buttons"] = buttonsList;
                }

                var payload = new
                {
                    cmd = "SET_ACTIVITY",
                    args = new
                    {
                        pid = Process.GetCurrentProcess().Id,
                        activity = discordActivityObj
                    },
                    nonce = Guid.NewGuid().ToString()
                };

                string json = JsonSerializer.Serialize(payload);
                await WriteFrameAsync(1, json, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[DiscordRpcService] SendActivity failed: {ex.Message}");
                Disconnect();
            }
        }

        private async Task WriteFrameAsync(int opcode, string json, CancellationToken ct)
        {
            byte[] payloadBytes = Encoding.UTF8.GetBytes(json);
            byte[] header = new byte[8];
            BitConverter.GetBytes(opcode).CopyTo(header, 0);
            BitConverter.GetBytes(payloadBytes.Length).CopyTo(header, 4);

            await _pipeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                NamedPipeClientStream? stream;
                lock (_pipeLock)
                {
                    stream = _pipeStream;
                }

                if (stream == null || !stream.IsConnected)
                    throw new IOException("Discord NamedPipe is not connected.");

                await stream.WriteAsync(header, ct).ConfigureAwait(false);
                await stream.WriteAsync(payloadBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _pipeGate.Release();
            }
        }

        private async Task<(int Opcode, string Payload)> ReadFrameAsync(CancellationToken ct)
        {
            byte[] header = new byte[8];
            NamedPipeClientStream? stream;
            lock (_pipeLock)
            {
                stream = _pipeStream;
            }

            if (stream == null || !stream.IsConnected)
                throw new IOException("Discord NamedPipe is not connected.");

            int bytesRead = await stream.ReadAsync(header, ct).ConfigureAwait(false);
            if (bytesRead < 8)
                throw new IOException("Incomplete frame header read from Discord pipe.");

            int opcode = BitConverter.ToInt32(header, 0);
            int length = BitConverter.ToInt32(header, 4);

            if (length < 0 || length > 64 * 1024)
                throw new IOException($"Invalid Discord frame length: {length}");

            byte[] buffer = new byte[length];
            int totalRead = 0;
            while (totalRead < length)
            {
                int r = await stream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), ct).ConfigureAwait(false);
                if (r == 0) break;
                totalRead += r;
            }

            string payload = Encoding.UTF8.GetString(buffer, 0, totalRead);
            return (opcode, payload);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Stop();
            _pipeGate.Dispose();
        }
    }
}
