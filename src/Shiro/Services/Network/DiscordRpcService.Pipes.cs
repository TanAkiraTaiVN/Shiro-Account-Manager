#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class DiscordRpcService
    {
        private const int BaseReconnectDelayMs = 15000;
        private const int MaxReconnectDelayMs = 60000;
        internal const int DefaultHandshakeTimeoutMs = 3000;
        internal const int OpcodeHandshake = 0;
        internal const int OpcodeFrame = 1;
        internal const int OpcodeClose = 2;
        private const string DefaultPipeNamePrefix = "discord-ipc-";
        private bool _loggedMissingClientId;

        internal static Func<IReadOnlyList<int>>? TestAvailablePipesOverride { get; set; }
        internal DiscordActivityItem? LastActivityForTest => _lastActivity;

        internal static IReadOnlyList<int> FindAvailableDiscordIpcIndices()
        {
            if (TestAvailablePipesOverride != null)
            {
                return TestAvailablePipesOverride() ?? Array.Empty<int>();
            }

            if (!OperatingSystem.IsWindows())
            {
                return new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
            }

            var indices = new List<int>(2);
            try
            {
                foreach (string fullPath in Directory.EnumerateFiles(@"\\.\pipe\", "discord-ipc-*"))
                {
                    ReadOnlySpan<char> name = Path.GetFileName(fullPath.AsSpan());
                    const string prefix = "discord-ipc-";
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(name.Slice(prefix.Length), out int idx) &&
                        idx is >= 0 and <= 9 &&
                        !indices.Contains(idx))
                    {
                        indices.Add(idx);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Fall back to direct index probing when pipe enumeration is restricted.
                return new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
            }

            indices.Sort();
            return indices;
        }

        internal static int ComputeNextReconnectDelayMs(int currentDelayMs)
            => Math.Min(MaxReconnectDelayMs, Math.Max(BaseReconnectDelayMs, currentDelayMs * 2));

        internal Task<bool> TryConnectForTestAsync(CancellationToken ct, string? pipeNamePrefix = null)
            => TryConnectAsync(ct, pipeNamePrefix);

        private static string BuildPipeName(string? pipeNamePrefix, int index)
            => $"{(string.IsNullOrWhiteSpace(pipeNamePrefix) ? DefaultPipeNamePrefix : pipeNamePrefix)}{index}";

        private bool TryGetValidClientId(out string clientId)
        {
            clientId = GetConfiguredClientId();
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                _loggedMissingClientId = false;
                return true;
            }

            if (!_loggedMissingClientId)
            {
                _loggedMissingClientId = true;
                _logger.Debug("[DiscordRpcService] Discord Client ID is not configured; skipping IPC handshake.");
            }
            return false;
        }

        internal static string? ResolveActiveGameTitle(long placeId)
        {
            if (placeId <= 0)
                return null;

            if (GameInfoService.TryGetCachedName(placeId, out string cachedTitle) &&
                !string.IsNullOrWhiteSpace(cachedTitle))
            {
                return cachedTitle;
            }

            var accounts = AppServices.AccountRepository.GetAll();
            for (int i = 0; i < accounts.Count; i++)
            {
                UserPresence? presence = accounts[i]?.Presence;
                if (presence == null || presence.userPresenceType != UserPresenceType.InGame)
                    continue;

                if (presence.placeId != placeId && presence.rootPlaceId != placeId)
                    continue;

                string sanitized = GameInfoService.SanitizeDisplayTitle(presence.gameName);
                if (!string.IsNullOrWhiteSpace(sanitized) &&
                    !GameInfoService.IsGenericPlaceholderName(placeId, sanitized))
                {
                    return sanitized;
                }
            }

            return null;
        }

        internal static byte[] BuildFrame(int opcode, ReadOnlySpan<byte> payloadUtf8)
        {
            byte[] frame = new byte[8 + payloadUtf8.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), opcode);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payloadUtf8.Length);
            payloadUtf8.CopyTo(frame.AsSpan(8));
            return frame;
        }

        private async Task WriteFrameBytesAsync(byte[] frameBytes, CancellationToken ct)
        {
            if (!WebSocketGateHelper.TryEnterUse(_pipeGate))
                throw new ObjectDisposedException(nameof(DiscordRpcService));

            try
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
            finally
            {
                WebSocketGateHelper.ExitUse(_pipeGate);
            }
        }

        private Task<(int Opcode, string Payload)> ReadHandshakeFrameAsync(CancellationToken ct)
        {
            NamedPipeClientStream? stream;
            lock (_pipeLock)
            {
                stream = _pipeStream;
            }

            if (stream == null || !stream.IsConnected)
                throw new IOException("Discord NamedPipe is not connected.");

            return ReadHandshakeResponseFromStreamAsync(stream, ct);
        }

        internal static async Task<(int Opcode, string Payload)> ReadHandshakeResponseFromStreamAsync(
            Stream stream,
            CancellationToken ct = default,
            int timeoutMs = DefaultHandshakeTimeoutMs)
        {
            using var handshakeTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeTimeoutCts.CancelAfter(timeoutMs);

            var (opcode, payload) = await ReadFrameFromStreamAsync(stream, handshakeTimeoutCts.Token).ConfigureAwait(false);
            if (opcode == OpcodeClose)
                throw new IOException($"Discord IPC rejected handshake with CLOSE frame: {payload}");

            return (opcode, payload);
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

        private async Task DrainPipeLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    var (opcode, _) = await ReadFrameFromStreamAsync(pipe, ct).ConfigureAwait(false);
                    if (opcode == OpcodeClose)
                    {
                        _logger.Debug("[DiscordRpcService.DrainPipeLoop] Received CLOSE frame from Discord IPC.");
                        Disconnect();
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { /* stopped */ }
            catch (Exception ex)
            {
                _logger.Debug($"[DiscordRpcService.DrainPipeLoop] {ex.Message}");
                Disconnect();
            }
        }
    }
}
