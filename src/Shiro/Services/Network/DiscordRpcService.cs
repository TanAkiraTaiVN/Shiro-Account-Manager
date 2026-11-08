#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
        private static readonly Lazy<DiscordRpcService> _instance = new(() => new DiscordRpcService(AppServices.Logger, AppServices.GeneralSettings));
        public static DiscordRpcService Default => _instance.Value;

        private readonly IAppLogger _logger;
        private readonly ISettingsService _settings;

        public DiscordRpcService() : this(AppServices.Logger, AppServices.GeneralSettings) { }

        public DiscordRpcService(IAppLogger logger, ISettingsService settings)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        // Shiro's registered Discord Client Application ID (or generic fallback)
        public const string DefaultClientId = "1301012345678901234";

        public static string ClientId =>
            AppServices.GeneralSettings.Get<string>(AppSettingsKeys.General.DiscordRpcClientId, DefaultClientId) ?? DefaultClientId;

        private readonly System.Threading.Lock _pipeLock = new();
        private readonly SemaphoreSlim _pipeGate = new(1, 1);
        private NamedPipeClientStream? _pipeStream;
        private CancellationTokenSource? _cts;
        private bool _isConnected;
        private bool _isDisposed;
        private DiscordActivityItem? _lastActivity;
        private byte[]? _lastActivityFrame;
        private readonly long _sessionStartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public bool IsConnected => _isConnected;

        public bool IsEnabled
        {
            get => _settings.Get<bool>(AppSettingsKeys.General.DiscordRpcEnabled, true);
            set
            {
                _settings.Set(AppSettingsKeys.General.DiscordRpcEnabled, value ? "true" : "false");
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
                    if (_isConnected)
                    {
                        byte[]? cachedFrame = Volatile.Read(ref _lastActivityFrame);
                        if (cachedFrame != null)
                        {
                            await WriteFrameBytesAsync(cachedFrame, ct).ConfigureAwait(false);
                        }
                        else if (_lastActivity != null)
                        {
                            await SendActivityAsync(_lastActivity, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[DiscordRpcService] Loop tick exception: {ex.Message}");
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

                    // Send Handshake (Opcode 0) via source-generated DTO
                    byte[] handshakeUtf8 = JsonSerializer.SerializeToUtf8Bytes(
                        new DiscordHandshakePayload(1, ClientId),
                        ShiroAppJson.Default.DiscordHandshakePayload);
                    await WriteFrameBytesAsync(BuildFrame(0, handshakeUtf8), ct).ConfigureAwait(false);

                    // Read Handshake response
                    var (_, _) = await ReadFrameAsync(ct).ConfigureAwait(false);

                    _isConnected = true;
                    _logger.Info($"[DiscordRpcService] Connected to Discord IPC via pipe discord-ipc-{i}.");

                    if (_lastActivity != null)
                    {
                        await SendActivityAsync(_lastActivity, ct).ConfigureAwait(false);
                    }
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[DiscordRpcService] Connect pipe discord-ipc-{i} failed", ex);
                    lock (_pipeLock)
                    {
                        if (ReferenceEquals(_pipeStream, pipe))
                            _pipeStream = null;
                    }
                    try { pipe?.Dispose(); }
                    catch (Exception disposeEx) { _logger.Debug($"[DiscordRpcService.ConnectPipeAsync] Pipe dispose: {disposeEx.Message}"); }
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
                    _logger.Debug($"[DiscordRpcService.Disconnect] {ex.Message}");
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
            bool allowJoin = _settings.Get<bool>(AppSettingsKeys.General.DiscordRpcJoinCurrentServer, true);
            bool showGame = _settings.Get<bool>(AppSettingsKeys.General.DiscordRpcShowGame, true);

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
            byte[] encodedFrame = BuildFrame(1, BuildSetActivityPayload(activity));
            Volatile.Write(ref _lastActivityFrame, encodedFrame);

            if (_isConnected)
            {
                _ = SendFrameSafeAsync(encodedFrame, CancellationToken.None);
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
            try
            {
                active = RobloxProcessService.GetRunningRobloxCount();
            }
            catch (Exception ex)
            {
                _logger.Debug($"[DiscordRpcService.UpdatePresence] {ex.Message}");
            }

            string currentPlace = AppServices.ServerBrowserState.CurrentPlaceId;
            long.TryParse(currentPlace, out long placeId);

            UpdatePresence(null, placeId, AppServices.ServerBrowserState.CurrentJobId, total, active);
        }

        public void ClearPresence()
        {
            _lastActivity = null;
            Volatile.Write(ref _lastActivityFrame, null);
            if (!_isConnected) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    byte[] clearFrame = BuildFrame(1, BuildSetActivityPayload(null));
                    await WriteFrameBytesAsync(clearFrame, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[DiscordRpcService.ClearPresence] {ex.Message}");
                }
            });
        }

        internal static byte[] BuildSetActivityPayload(DiscordActivityItem? activity, string? nonce = null)
        {
            DiscordActivityPayload? activityDto = null;
            if (activity != null)
            {
                List<DiscordButtonPayload>? buttons = null;
                if (activity.Buttons is { Count: > 0 })
                {
                    buttons = new List<DiscordButtonPayload>(activity.Buttons.Count);
                    foreach (var btn in activity.Buttons)
                    {
                        buttons.Add(new DiscordButtonPayload(btn.Label, btn.Url));
                    }
                }

                activityDto = new DiscordActivityPayload
                {
                    details = activity.Details,
                    state = activity.State,
                    timestamps = activity.StartTimestamp.HasValue
                        ? new DiscordTimestampsPayload(activity.StartTimestamp.Value)
                        : null,
                    assets = new DiscordAssetsPayload(
                        activity.LargeImageKey,
                        activity.LargeImageText,
                        activity.SmallImageKey,
                        activity.SmallImageText),
                    buttons = buttons
                };
            }

            var command = new DiscordSetActivityCommand(
                "SET_ACTIVITY",
                new DiscordSetActivityArgs(Environment.ProcessId, activityDto),
                nonce ?? Guid.NewGuid().ToString("N"));

            return JsonSerializer.SerializeToUtf8Bytes(command, ShiroAppJson.Default.DiscordSetActivityCommand);
        }

        internal static byte[] BuildFrame(int opcode, ReadOnlySpan<byte> payloadUtf8)
        {
            byte[] frame = new byte[8 + payloadUtf8.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), opcode);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payloadUtf8.Length);
            payloadUtf8.CopyTo(frame.AsSpan(8));
            return frame;
        }

        private async Task SendActivityAsync(DiscordActivityItem activity, CancellationToken ct)
        {
            byte[] frame = Volatile.Read(ref _lastActivityFrame) ?? BuildFrame(1, BuildSetActivityPayload(activity));
            Volatile.Write(ref _lastActivityFrame, frame);
            await SendFrameSafeAsync(frame, ct).ConfigureAwait(false);
        }

        private async Task SendFrameSafeAsync(byte[] frame, CancellationToken ct)
        {
            try
            {
                await WriteFrameBytesAsync(frame, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Debug($"[DiscordRpcService] SendActivity failed: {ex.Message}");
                Disconnect();
            }
        }

        private async Task WriteFrameBytesAsync(byte[] frameBytes, CancellationToken ct)
        {
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

                await stream.WriteAsync(frameBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _pipeGate.Release();
            }
        }

        private Task<(int Opcode, string Payload)> ReadFrameAsync(CancellationToken ct)
        {
            NamedPipeClientStream? stream;
            lock (_pipeLock)
            {
                stream = _pipeStream;
            }

            if (stream == null || !stream.IsConnected)
                throw new IOException("Discord NamedPipe is not connected.");

            return ReadFrameFromStreamAsync(stream, ct);
        }

        internal static async Task<(int Opcode, string Payload)> ReadFrameFromStreamAsync(Stream stream, CancellationToken ct = default)
        {
            byte[] header = new byte[8];
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

            int opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
            int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

            if (length < 0 || length > 64 * 1024)
                throw new IOException($"Invalid Discord frame length: {length}");

            if (length == 0)
                return (opcode, string.Empty);

            byte[] buffer = new byte[length];
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
            string payload = Encoding.UTF8.GetString(buffer);
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
