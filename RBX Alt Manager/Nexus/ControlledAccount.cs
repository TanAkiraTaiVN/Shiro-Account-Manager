#nullable enable
using System;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Shiro.Classes;
using Shiro.Services;
using static Shiro.Program;

namespace Shiro.Nexus
{
    public class ControlledAccount : INotifyPropertyChanged
    {
        private static readonly IBrush OnlineBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
        private static readonly IBrush OfflineBrush = new SolidColorBrush(Color.FromRgb(100, 116, 139));

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            AppDispatcher.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
        }

        [JsonIgnore] public Account? LinkedAccount { get; set; }

        private AccountStatus _status;
        [JsonIgnore]
        public AccountStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusColorBrush));
                }
            }
        }

        [JsonIgnore]
        public string StatusText => Status == AccountStatus.Online ? "Online" : "Offline";

        [JsonIgnore]
        public IBrush StatusColorBrush => Status == AccountStatus.Online ? OnlineBrush : OfflineBrush;

        [JsonIgnore] public DateTime LastPing { get; set; }

        private string _username = string.Empty;
        public string Username
        {
            get => _username;
            set { if (_username != value) { _username = value ?? string.Empty; OnPropertyChanged(); } }
        }

        public string AutoExecute { get; set; } = "";

        public long PlaceId { get; set; }

        private string? _jobId;
        public string? JobId
        {
            get => _jobId;
            set { if (_jobId != value) { _jobId = value; OnPropertyChanged(); } }
        }

        [JsonIgnore] public string? InGameJobId { get; set; }
        public double RelaunchDelay { get; set; } = 30;

        public bool AutoRelaunch { get; set; }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(); } }
        }

        public bool ClientCanReceive { get; set; }

        private string _position = "-";
        [JsonIgnore]
        public string Position
        {
            get => _position;
            set { if (_position != value) { _position = value; OnPropertyChanged(); } }
        }

        private string _fps = "-";
        [JsonIgnore]
        public string Fps
        {
            get => _fps;
            set { if (_fps != value) { _fps = value; OnPropertyChanged(); } }
        }

        private string _ping = "-";
        [JsonIgnore]
        public string Ping
        {
            get => _ping;
            set { if (_ping != value) { _ping = value; OnPropertyChanged(); } }
        }

        private const int MaxCustomControlsPerSession = 50;
        private int _customControlsCount = 0;

        [JsonIgnore] public WebSocket? WebSocket;

        public ControlledAccount()
        {
            Status = AccountStatus.Offline;
            LastPing = DateTime.UtcNow.AddSeconds(-20);
        }

        public ControlledAccount(Account? account) : this()
        {
            LinkedAccount = account;
            Username = LinkedAccount?.Username ?? string.Empty;
        }

        public void Connect(WebSocket? webSocket)
        {
            if (Status == AccountStatus.Online)
            {
                Logger.Warn($"{Username} was already connected, disconnecting...");
                Disconnect();
            }

            Status = AccountStatus.Online;
            LastPing = DateTime.UtcNow;

            this.WebSocket = webSocket;

            if (Shiro.AvaloniaUI.Views.NexusWindow.Instance != null && webSocket != null)
            {
                Shiro.AvaloniaUI.Views.NexusWindow.Instance.ContextList[webSocket] = this;
                Shiro.AvaloniaUI.Views.NexusWindow.Instance.RefreshAccount(this);
            }

            Logger.Info($"{Username} has connected");

            Task.Run(async () =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(AutoExecute))
                    {
                        long startTs = Stopwatch.GetTimestamp();
                        while (!ClientCanReceive && Status == AccountStatus.Online && Stopwatch.GetElapsedTime(startTs).TotalSeconds < 15)
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }

                        if (ClientCanReceive && Status == AccountStatus.Online)
                            SendMessage("execute " + AutoExecute);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[ControlledAccount.AutoExecute] {ex.Message}");
                }
            });
        }

        public void Disconnect()
        {
            Logger.Info($"{Username} has disconnected");

            Status = AccountStatus.Offline;
            ClientCanReceive = false;
            _customControlsCount = 0;

            if (WebSocket != null)
            {
                Shiro.AvaloniaUI.Views.NexusWindow.Instance?.ContextList?.TryRemove(WebSocket, out _);
            }

            try
            {
                if (WebSocket != null && WebSocket.State == WebSocketState.Open)
                    _ = WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"WebSocket close ignored: {ex.Message}");
            }

            WebSocket = null;

            Shiro.AvaloniaUI.Views.NexusWindow.Instance?.RefreshAccount(this);
        }

        public void HandleMessage(string Message)
        {
            if (string.IsNullOrEmpty(Message)) return;

            if (Message.TryParseJson(out Command? command) && command != null)
            {
#if DEBUG
                if (command.Name != "ping")
                    Console.WriteLine($"{command.Name}: {Message}");
#endif

                if (command.Name == "ping")
                {
                    LastPing = DateTime.UtcNow;
                    ClientCanReceive = true;

                    if (command.Payload != null)
                    {
                        if (command.Payload.TryGetValue("fps", out string? fpsVal) || command.Payload.TryGetValue("Fps", out fpsVal))
                            Fps = fpsVal ?? "-";
                        if (command.Payload.TryGetValue("ping", out string? pingVal) || command.Payload.TryGetValue("Ping", out pingVal))
                            Ping = pingVal ?? "-";
                        if (command.Payload.TryGetValue("pos", out string? posVal) || command.Payload.TryGetValue("Position", out posVal))
                            Position = posVal ?? "-";
                    }
                }
                else if (command.Name == "Log" && command.Payload != null && command.Payload.TryGetValue("Content", out string? logContent) && logContent != null)
                {
                    Shiro.AvaloniaUI.Views.NexusWindow.Instance?.LogMessage(logContent);
                }
                else if (command.Name == "GetText" && command.Payload != null && command.Payload.TryGetValue("Name", out string? _))
                {
                    SendMessage($"ElementText:");
                }
                else if (command.Name == "SetRelaunch" && command.Payload != null && command.Payload.TryGetValue("Seconds", out string? secStr) && secStr != null && double.TryParse(secStr, out double Delay))
                {
                    Logger.Info($"Relaunch Delay for {Username} has been set to {Delay} through Nexus");
                    RelaunchDelay = Delay;
                }
                else if (command.Name == "SetAutoRelaunch" && command.Payload != null && command.Payload.TryGetValue("Content", out string? bRelaunchStr) && bRelaunchStr != null && bool.TryParse(bRelaunchStr, out bool bRelaunch))
                    AutoRelaunch = bRelaunch;
                else if (command.Name == "SetPlaceId" && command.Payload != null && command.Payload.TryGetValue("Content", out string? lPlaceIdStr) && lPlaceIdStr != null && long.TryParse(lPlaceIdStr, out long lPlaceId))
                    PlaceId = lPlaceId;
                else if (command.Name == "SetJobId" && command.Payload != null && command.Payload.TryGetValue("Content", out string? jobIdContent) && !string.IsNullOrEmpty(jobIdContent))
                    JobId = jobIdContent;
                else if (command.Name == "Echo" && command.Payload != null && command.Payload.TryGetValue("Content", out string? echoContent) && !string.IsNullOrEmpty(echoContent))
                {
                    Shiro.AvaloniaUI.Views.NexusWindow.Instance?.EmitMessage(echoContent, true);
                }
                else if (Enum.TryParse(command.Name, out CommandCreateElement elementType))
                {
                    string elementName = "";
                    if (elementType != CommandCreateElement.NewLine)
                    {
                        if (command.Payload == null || !command.Payload.TryGetValue("Name", out string? parsedName) || parsedName == null || !command.Payload.ContainsKey("Content"))
                            return;
                        elementName = parsedName;
                    }

                    if (_customControlsCount >= MaxCustomControlsPerSession)
                    {
                        Logger.Warn($"Max custom control limit ({MaxCustomControlsPerSession}) reached for {Username}. Ignoring new control request.");
                        return;
                    }
                    _customControlsCount++;

                    Logger.Info($"[Nexus] Custom element received from {Username}: {elementType} ({elementName})");
                }
            }
        }

        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        public void SendMessage(string Message)
        {
            _ = SendMessageAsync(Message);
        }

        public async Task SendMessageAsync(string Message)
        {
            if (Status == AccountStatus.Offline || WebSocket == null || string.IsNullOrEmpty(Message)) return;

            try
            {
                await _sendLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (WebSocket != null && WebSocket.State == WebSocketState.Open)
                    {
                        int maxByteCount = Encoding.UTF8.GetMaxByteCount(Message.Length);
                        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(maxByteCount);
                        try
                        {
                            int actualBytes = Encoding.UTF8.GetBytes(Message, 0, Message.Length, rentedBuffer, 0);
                            await WebSocket.SendAsync(new ArraySegment<byte>(rentedBuffer, 0, actualBytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(rentedBuffer);
                        }
                    }
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to send message to {Username}: {ex.Message}");
            }
        }
    }
}
