#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Nexus;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs;

public sealed class NexusViewModel : DialogViewModelBase, INexusHost
{
    private static readonly string DefaultAcFile = StoragePaths.GetUserFile("AccountControlData.json");
    private static readonly System.Threading.Lock SaveLock = new();
    private static readonly Lazy<NexusViewModel> _defaultInstance = new(() => new NexusViewModel());

    public static NexusViewModel Default => _defaultInstance.Value;

    private readonly IAccountRepository _repo;
    private readonly ISettingsService? _accountControl;
    private readonly IAppLogger _logger;
    private readonly Func<IPAddress, int, NexusWebSocketServer> _serverFactory;
    private readonly Func<NexusWebSocketServer, Task> _serverStarter;
    private readonly Action<ControlledAccount, string> _sendTo;

    private string _portText = "5242";
    private bool _allowExternal;
    private bool _isServerRunning;
    private string _serverStatusText = LocalizationService.Get("Nexus.ServerStoppedStatus");
    private string _toggleLabel = LocalizationService.Get("Nexus.StartServerAction");
    private string _clientCountText = LocalizationService.Format("Nexus.ConnectedClientsCount", 0);
    private string _scriptInput = string.Empty;

    public NexusViewModel(
        IAccountRepository? repo = null,
        ISettingsService? accountControl = null,
        Func<IPAddress, int, NexusWebSocketServer>? serverFactory = null,
        Func<NexusWebSocketServer, Task>? serverStarter = null,
        Action<ControlledAccount, string>? sendTo = null,
        IAppLogger? logger = null)
    {
        _repo = repo ?? AppServices.AccountRepository;
        _accountControl = accountControl;
        _logger = logger ?? AppServices.Logger;
        _serverFactory = serverFactory ?? ((ip, p) => new NexusWebSocketServer(ip, p));
        _serverStarter = serverStarter ?? (s => s.StartAsync());
        _sendTo = sendTo ?? ((ca, msg) => ca.SendMessage(msg));

        SetStatus(LocalizationService.Get("Nexus.ReadyStatus"), StatusKind.Neutral);

        ToggleServerCommand = TrackCommand(new AsyncRelayCommand(ToggleServerAsync, () => !IsBusy));
        ExecuteSelectedCommand = new RelayCommand(() => ExecuteScript(toAll: false));
        ExecuteAllCommand = new RelayCommand(() => ExecuteScript(toAll: true));
    }

    public NexusWebSocketServer? Server { get; private set; }

    public ObservableCollection<ControlledAccount> Accounts { get; } = new();
    public ConcurrentDictionary<WebSocket, ControlledAccount> ContextList { get; } = new();

    public string PortText
    {
        get => _portText;
        set => SetProperty(ref _portText, value ?? string.Empty);
    }

    public bool AllowExternal
    {
        get => _allowExternal;
        set => SetProperty(ref _allowExternal, value);
    }

    public bool IsServerRunning
    {
        get => _isServerRunning;
        private set => SetProperty(ref _isServerRunning, value);
    }

    public string ServerStatusText
    {
        get => _serverStatusText;
        private set => SetProperty(ref _serverStatusText, value);
    }

    public string ToggleLabel
    {
        get => _toggleLabel;
        private set => SetProperty(ref _toggleLabel, value);
    }

    public string ClientCountText
    {
        get => _clientCountText;
        private set => SetProperty(ref _clientCountText, value);
    }

    public string ScriptInput
    {
        get => _scriptInput;
        set => SetProperty(ref _scriptInput, value ?? string.Empty);
    }

    public IAsyncRelayCommand ToggleServerCommand { get; }
    public ICommand ExecuteSelectedCommand { get; }
    public ICommand ExecuteAllCommand { get; }

    public static bool TryParsePort(string? text, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (!int.TryParse(text.Trim(), out int parsed) || parsed < 1 || parsed > 65535)
            return false;

        port = parsed;
        return true;
    }

    public void LoadSettings()
    {
        try
        {
            var sec = ResolveAccountControl();
            PortText = sec?.Get("NexusPort") ?? "5242";
            AllowExternal = sec?.Get<bool>("AllowExternalConnections") ?? false;
        }
        catch (Exception ex)
        {
            _logger.Debug($"[NexusViewModel.LoadSettings] {ex.Message}");
        }
    }

    public void SaveAccounts(string? path = null)
    {
        string targetPath = string.IsNullOrWhiteSpace(path) ? DefaultAcFile : path;
        lock (SaveLock)
        {
            try
            {
                StoragePaths.AtomicWriteText(targetPath, FastJson.Serialize(Accounts.ToList(), indented: true));
            }
            catch (Exception ex)
            {
                _logger.Error($"[NexusViewModel] SaveAccounts failed: {ex.Message}");
            }
        }
    }

    public void LoadAccounts(string? path = null)
    {
        string targetPath = string.IsNullOrWhiteSpace(path) ? DefaultAcFile : path;
        try
        {
            Accounts.Clear();
            var savedConfigs = new Dictionary<string, ControlledAccount>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(targetPath))
            {
                string json = File.ReadAllText(targetPath);
                if (!string.IsNullOrWhiteSpace(json) &&
                    FastJson.TryDeserialize<List<ControlledAccount>>(json, out var list) &&
                    list != null)
                {
                    foreach (var ca in list)
                    {
                        if (!string.IsNullOrEmpty(ca?.Username))
                            savedConfigs[ca.Username] = ca;
                    }
                }
            }

            var snapshot = _repo.GetAll();
            foreach (var acc in snapshot)
            {
                if (string.IsNullOrEmpty(acc?.Username)) continue;

                if (savedConfigs.TryGetValue(acc.Username, out var ca))
                {
                    ca.LinkedAccount = acc;
                    Accounts.Add(ca);
                }
                else
                {
                    Accounts.Add(new ControlledAccount(acc));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[NexusViewModel] LoadAccounts error: {ex.Message}");
        }
        UpdateClientCount();
    }

    public async Task InitializeOnLaunchAsync()
    {
        LoadSettings();
        LoadAccounts();

        var sec = ResolveAccountControl();
        if (sec?.Get<bool>("StartOnLaunch") == true)
        {
            await StartServerAsync().ConfigureAwait(true);
        }
    }

    private async Task ToggleServerAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (IsServerRunning || (Server != null && Server.IsListening))
                await StopServerAsync().ConfigureAwait(true);
            else
                await StartServerAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error($"[NexusViewModel.ToggleServer] {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task StartServerAsync()
    {
        if (Server != null && Server.IsListening)
            return;

        if (!TryParsePort(PortText, out int port))
        {
            SetStatus(LocalizationService.Get("Nexus.InvalidPort"), StatusKind.Warning);
            return;
        }

        SetStatus(LocalizationService.Get("Nexus.StartingServer"), StatusKind.Info);
        try
        {
            bool allowExternal = AllowExternal;
            Server = _serverFactory(allowExternal ? IPAddress.Any : IPAddress.Loopback, port);
            await _serverStarter(Server).ConfigureAwait(true);

            try
            {
                var sec = ResolveAccountControl();
                sec?.Set("NexusPort", port.ToString());
                sec?.Set("AllowExternalConnections", allowExternal ? "true" : "false");
                AppServices.SaveSettings();
            }
            catch (Exception ex)
            {
                _logger.Warn($"Save Nexus settings threw: {ex.Message}");
            }

            IsServerRunning = true;
            ServerStatusText = LocalizationService.Format("Nexus.ServerRunning", port);
            ToggleLabel = LocalizationService.Get("Nexus.StopServerAction");
            SetStatus(LocalizationService.Format("Nexus.ServerStartedOnPort", port), StatusKind.Success);
        }
        catch (Exception ex)
        {
            Server = null;
            IsServerRunning = false;
            ServerStatusText = LocalizationService.Get("Nexus.ServerStoppedStatus");
            ToggleLabel = LocalizationService.Get("Nexus.StartServerAction");
            SetStatus(LocalizationService.Format("Nexus.ServerStartError", ex.Message), StatusKind.Error);
        }
    }

    public async Task StopServerAsync()
    {
        SetStatus(LocalizationService.Get("Nexus.StoppingServer"), StatusKind.Info);
        try
        {
            if (Server != null)
            {
                await Server.StopAsync().ConfigureAwait(true);
                Server = null;
            }
            IsServerRunning = false;
            ServerStatusText = LocalizationService.Get("Nexus.ServerStoppedStatus");
            ToggleLabel = LocalizationService.Get("Nexus.StartServerAction");
            SetStatus(LocalizationService.Get("Nexus.ServerStoppedNotification"), StatusKind.Neutral);
        }
        catch (Exception ex)
        {
            SetStatus(LocalizationService.Format("Nexus.ServerStopError", ex.Message), StatusKind.Error);
        }
    }

    public void RefreshAccount(ControlledAccount account)
    {
        AppDispatcher.Post(() =>
        {
            try
            {
                if (account == null) return;
                if (!Accounts.Contains(account))
                {
                    Accounts.Add(account);
                }
                UpdateClientCount();
            }
            catch (Exception ex)
            {
                _logger.Debug($"RefreshAccount UI update threw: {ex.Message}");
            }
        });
    }

    public void UpdateClientCount()
    {
        AppDispatcher.Post(() =>
        {
            int online = 0;
            foreach (var acc in Accounts)
            {
                if (acc?.Status == AccountStatus.Online)
                    online++;
            }
            ClientCountText = LocalizationService.Format("Nexus.ConnectedClientsCount", online);
        });
    }

    public void EmitMessage(string message, bool toAll)
    {
        for (int i = 0; i < Accounts.Count; i++)
        {
            var account = Accounts[i];
            if (account != null && (toAll || account.IsChecked))
            {
                _sendTo(account, message);
            }
        }
    }

    public void LogMessage(string message)
    {
        AppDispatcher.Post(() => SetStatus(message, StatusKind.Info));
    }

    private void ExecuteScript(bool toAll)
    {
        if (string.IsNullOrWhiteSpace(ScriptInput))
        {
            SetStatus(LocalizationService.Get("Nexus.EnterScriptPrompt"), StatusKind.Warning);
            return;
        }

        EmitMessage($"execute {ScriptInput}", toAll);
        SetStatus(
            LocalizationService.Get(toAll ? "Nexus.ScriptSentAll" : "Nexus.ScriptSentSelected"),
            StatusKind.Success);
    }

    private ISettingsService? ResolveAccountControl()
    {
        return _accountControl ?? AppServices.Settings.GetSection(AppSettingsKeys.Sections.AccountControl);
    }
}
