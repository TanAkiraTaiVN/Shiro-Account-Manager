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
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Shiro.Classes;
using Shiro.Nexus;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public partial class NexusWindow : Window
    {
        private static readonly string ACFile = StoragePaths.GetUserFile("AccountControlData.json");
        private static readonly System.Threading.Lock SaveLock = new();
        private static readonly IBrush RunningBadgeBrush = new SolidColorBrush(Color.Parse("#38BDF8"));
        private static readonly IBrush StoppedBadgeBrush = new SolidColorBrush(Color.Parse("#94A3B8"));
        private static readonly IBrush StopButtonBrush = new SolidColorBrush(Color.Parse("#DC2626"));
        private static readonly IBrush StartButtonBrush = new SolidColorBrush(Color.Parse("#0284C7"));

        public static NexusWindow? Instance { get; private set; }
        public static NexusWebSocketServer? Server { get; private set; }

        public ObservableCollection<ControlledAccount> Accounts { get; } = new();
        public ConcurrentDictionary<WebSocket, ControlledAccount> ContextList { get; } = new();

        public NexusWindow()
        {
            InitializeComponent();
            Instance = this;
            DgAccounts.ItemsSource = Accounts;

            Loaded += NexusWindow_Loaded;
            Closing += (s, e) =>
            {
                SaveAccounts();
                e.Cancel = true;
                Hide();
            };
        }

        private async void NexusWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            LoadSettings();
            LoadAccounts();

            if (AccountManager.AccountControl?.Get<bool>("StartOnLaunch") == true)
            {
                await StartServerAsync();
            }
        }

        #region Settings & Persistence

        private void LoadSettings()
        {
            try
            {
                TxtNexusPort.Text = AccountManager.AccountControl?.Get("NexusPort") ?? "5242";
                ChkAllowExternal.IsChecked = AccountManager.AccountControl?.Get<bool>("AllowExternalConnections") ?? false;
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"LoadSettings threw: {ex.Message}");
            }
        }

        public void SaveAccounts()
        {
            lock (SaveLock)
            {
                try
                {
                    StoragePaths.AtomicWriteText(ACFile, Utilities.FastJson.Serialize(Accounts.ToList(), indented: true));
                }
                catch (Exception ex)
                {
                    Program.Logger?.Error($"[NexusWindow] SaveAccounts failed: {ex.Message}");
                }
            }
        }

        public void LoadAccounts()
        {
            try
            {
                Accounts.Clear();
                var savedConfigs = new Dictionary<string, ControlledAccount>(StringComparer.OrdinalIgnoreCase);

                if (File.Exists(ACFile))
                {
                    string json = File.ReadAllText(ACFile);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        if (Utilities.FastJson.TryDeserialize<List<ControlledAccount>>(json, out var list) && list != null)
                        {
                            foreach (var ca in list)
                            {
                                if (!string.IsNullOrEmpty(ca?.Username))
                                    savedConfigs[ca.Username] = ca;
                            }
                        }
                    }
                }

                var snapshot = AccountManager.GetAccountsSnapshot();
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
                Program.Logger?.Error($"[NexusWindow] LoadAccounts error: {ex.Message}");
            }
            UpdateClientCount();
        }

        #endregion

        #region Server Controls

        private async void BtnToggleServer_Click(object sender, RoutedEventArgs e)
        {
            if (BtnToggleServer.IsEnabled == false) return;
            BtnToggleServer.IsEnabled = false;

            try
            {
                if (Server != null && Server.IsListening)
                    await StopServerAsync();
                else
                    await StartServerAsync();
            }
            finally
            {
                BtnToggleServer.IsEnabled = true;
            }
        }

        public async Task StartServerAsync()
        {
            if (Server != null && Server.IsListening) return;

            if (!int.TryParse(TxtNexusPort.Text?.Trim(), out int port) || port < 1 || port > 65535)
            {
                SetStatus("⚠️ Cổng kết nối không hợp lệ (1-65535).");
                return;
            }

            SetStatus("⏳ Đang khởi động WebSocket Server...");
            try
            {
                bool allowExternal = ChkAllowExternal.IsChecked == true;
                Server = new NexusWebSocketServer(allowExternal ? IPAddress.Any : IPAddress.Loopback, port);
                await Server.StartAsync();

                try
                {
                    AccountManager.AccountControl?.Set("NexusPort", port.ToString());
                    AccountManager.AccountControl?.Set("AllowExternalConnections", allowExternal ? "true" : "false");
                    AccountManager.SaveSettings();
                }
                catch (Exception ex)
                {
                    Program.Logger?.Warn($"Save Nexus settings threw: {ex.Message}");
                }

                TxtServerStatusBadge.Text = $"Server: Đang chạy (Port {port})";
                TxtServerStatusBadge.Foreground = RunningBadgeBrush;
                BtnToggleServer.Content = "Dừng Server";
                BtnToggleServer.Background = StopButtonBrush;
                SetStatus($"✅ WebSocket Server đã khởi động trên cổng {port}.");
            }
            catch (Exception ex)
            {
                Server = null;
                TxtServerStatusBadge.Text = "Server: Đang dừng";
                TxtServerStatusBadge.Foreground = StoppedBadgeBrush;
                BtnToggleServer.Content = "Bật Server";
                BtnToggleServer.Background = StartButtonBrush;
                SetStatus($"❌ Lỗi khởi động WebSocket Server: {ex.Message}");
            }
        }

        public async Task StopServerAsync()
        {
            SetStatus("⏳ Đang dừng WebSocket Server...");
            try
            {
                if (Server != null)
                {
                    await Server.StopAsync();
                    Server = null;
                }
                TxtServerStatusBadge.Text = "Server: Đang dừng";
                TxtServerStatusBadge.Foreground = StoppedBadgeBrush;
                BtnToggleServer.Content = "Bật Server";
                BtnToggleServer.Background = StartButtonBrush;
                SetStatus("⏹️ WebSocket Server đã dừng.");
            }
            catch (Exception ex)
            {
                SetStatus($"⚠️ Lỗi khi dừng WebSocket Server: {ex.Message}");
            }
        }

        public void StartServer() => _ = StartServerAsync();
        public void StopServer() => _ = StopServerAsync();

        #endregion

        #region Account & Messages

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
                    Program.Logger?.Debug($"RefreshAccount UI update threw: {ex.Message}");
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
                TxtClientCount.Text = $"Đã kết nối: {online} client";
            });
        }

        public void EmitMessage(string message, bool toAll = false)
        {
            for (int i = 0; i < Accounts.Count; i++)
            {
                var account = Accounts[i];
                if (account != null && (toAll || account.IsChecked))
                {
                    account.SendMessage(message);
                }
            }
        }

        private void BtnExecuteSelected_Click(object? sender, RoutedEventArgs e)
        {
            string? script = TxtScriptInput.Text;
            if (string.IsNullOrWhiteSpace(script))
            {
                SetStatus("⚠️ Vui lòng nhập mã script cần thực thi.");
                return;
            }
            EmitMessage($"Execute:{script}", toAll: false);
            SetStatus("▶️ Đã gửi mã script tới các tài khoản đã chọn.");
        }

        private void BtnExecuteAll_Click(object? sender, RoutedEventArgs e)
        {
            string? script = TxtScriptInput.Text;
            if (string.IsNullOrWhiteSpace(script))
            {
                SetStatus("⚠️ Vui lòng nhập mã script cần thực thi.");
                return;
            }
            EmitMessage($"Execute:{script}", toAll: true);
            SetStatus("▶️ Đã gửi mã script tới TẤT CẢ tài khoản.");
        }

        public void LogMessage(string message)
        {
            SetStatus(message);
        }

        public void SetStatus(string message)
        {
            AppDispatcher.Post(() =>
            {
                TxtStatus.Text = message;
            });
        }

        #endregion
    }
}
