#nullable enable
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
    public sealed partial class DiscordRpcService : IDisposable
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

        public string GetConfiguredClientId() =>
            _settings.Get<string>(AppSettingsKeys.General.DiscordRpcClientId, DefaultClientId) ?? DefaultClientId;

        public static string ClientId =>
            AppServices.GeneralSettings.Get<string>(AppSettingsKeys.General.DiscordRpcClientId, DefaultClientId) ?? DefaultClientId;

        private readonly System.Threading.Lock _pipeLock = new();
        private readonly SemaphoreSlim _pipeGate = new(1, 1);
        private NamedPipeClientStream? _pipeStream;
        private CancellationTokenSource? _cts;
        private Task? _drainTask;
        private bool _isConnected;
        private bool _isDisposed;
        private bool _loggedNoPipeFound;
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
            lock (_pipeLock)
            {
                if (_isDisposed || !IsEnabled || _isConnected || _cts != null) return;
                var loopCts = new CancellationTokenSource();
                _cts = loopCts;
                _ = Task.Run(() => WorkerLoopAsync(loopCts));
            }
        }

        public void Stop()
        {
            CancellationTokenSource? ctsToStop;
            lock (_pipeLock)
            {
                ctsToStop = _cts;
                _cts = null;
            }
            SafeExecuteHelper.Try(() => ctsToStop?.Cancel());
            Disconnect();
        }

        public void ApplyConfigurationChanged(bool enabled, bool reconnect = false)
        {
            if (_isDisposed) return;

            _settings.Set(AppSettingsKeys.General.DiscordRpcEnabled, enabled ? "true" : "false");
            if (!enabled)
            {
                ClearPresence();
                Stop();
                return;
            }

            if (reconnect)
            {
                ClearPresence();
                Stop();
            }

            Start();
            UpdateCurrentPresence();
        }

        private async Task WorkerLoopAsync(CancellationTokenSource loopCts)
        {
            CancellationToken ct = loopCts.Token;
            int reconnectDelayMs = BaseReconnectDelayMs;
            try
            {
                while (!ct.IsCancellationRequested && !_isDisposed)
                {
                    if (!_isConnected)
                    {
                        bool connected = await TryConnectAsync(ct).ConfigureAwait(false);
                        reconnectDelayMs = connected
                            ? BaseReconnectDelayMs
                            : ComputeNextReconnectDelayMs(reconnectDelayMs);
                    }
                    else
                    {
                        reconnectDelayMs = BaseReconnectDelayMs;
                    }

                    try
                    {
                        await Task.Delay(_isConnected ? BaseReconnectDelayMs : reconnectDelayMs, ct).ConfigureAwait(false);
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
            finally
            {
                lock (_pipeLock)
                {
                    if (ReferenceEquals(_cts, loopCts))
                        _cts = null;
                }
                Disconnect();
                Task? drainToAwait;
                lock (_pipeLock)
                {
                    drainToAwait = _drainTask;
                    _drainTask = null;
                }
                if (drainToAwait != null)
                    await drainToAwait.ConfigureAwait(false);
                SafeExecuteHelper.Try(loopCts.Dispose);
            }
        }

        private async Task<bool> TryConnectAsync(CancellationToken ct)
        {
            IReadOnlyList<int> pipeIndices = FindAvailableDiscordIpcIndices();
            if (pipeIndices.Count == 0)
            {
                if (!_loggedNoPipeFound)
                {
                    _loggedNoPipeFound = true;
                    _logger.Debug("[DiscordRpcService] No active Discord IPC pipe found; waiting before retry.");
                }
                return false;
            }

            _loggedNoPipeFound = false;
            for (int k = 0; k < pipeIndices.Count; k++)
            {
                int i = pipeIndices[k];
                if (ct.IsCancellationRequested) return false;
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
                        new DiscordHandshakePayload(1, GetConfiguredClientId()),
                        ShiroAppJson.Default.DiscordHandshakePayload);
                    await WriteFrameBytesAsync(BuildFrame(0, handshakeUtf8), ct).ConfigureAwait(false);

                    // Read Handshake response
                    var (_, _) = await ReadFrameAsync(ct).ConfigureAwait(false);

                    _isConnected = true;
                    _logger.Info($"[DiscordRpcService] Connected to Discord IPC via pipe discord-ipc-{i}.");
                    Task drainTask = DrainPipeLoopAsync(pipe, ct);
                    lock (_pipeLock)
                    {
                        _drainTask = drainTask;
                    }

                    if (_lastActivity != null)
                    {
                        await SendActivityAsync(_lastActivity, ct).ConfigureAwait(false);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[DiscordRpcService] Connect pipe discord-ipc-{i} failed: {ex.Message}");
                    lock (_pipeLock)
                    {
                        if (ReferenceEquals(_pipeStream, pipe))
                            _pipeStream = null;
                    }
                    try { pipe?.Dispose(); }
                    catch (Exception disposeEx) { _logger.Debug($"[DiscordRpcService.ConnectPipeAsync] Pipe dispose: {disposeEx.Message}"); }
                }
            }

            return false;
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
                        Label = LocalizationService.Get("DiscordRpc.JoinServer"),
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

            string currentPlace = (AppServices.ServerBrowserState as ServerBrowserStateService)?.IsPlaceIdExplicitlyCleared == true
                ? string.Empty
                : AppServices.ServerBrowserState.CurrentPlaceId;
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

        public void Dispose()
        {
            lock (_pipeLock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
            }
            Stop();
            WebSocketGateHelper.RetireAndDispose(_pipeGate);
        }
    }
}
