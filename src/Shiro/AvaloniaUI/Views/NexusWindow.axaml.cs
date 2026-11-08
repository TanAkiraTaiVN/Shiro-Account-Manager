#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Shiro.Classes;
using Shiro.Nexus;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;

namespace Shiro.AvaloniaUI.Views
{
    public partial class NexusWindow : Window
    {
        private static readonly IBrush RunningBadgeBrush = new SolidColorBrush(Color.Parse("#38BDF8"));
        private static readonly IBrush StoppedBadgeBrush = new SolidColorBrush(Color.Parse("#94A3B8"));
        private static readonly IBrush StopButtonBrush = new SolidColorBrush(Color.Parse("#DC2626"));
        private static readonly IBrush StartButtonBrush = new SolidColorBrush(Color.Parse("#0284C7"));

        private readonly NexusViewModel _vm;
        private bool _allowClose;

        public static NexusWindow? Instance { get; internal set; }
        public static NexusWebSocketServer? Server => NexusViewModel.Default.Server;

        public ObservableCollection<ControlledAccount> Accounts => _vm.Accounts;
        public ConcurrentDictionary<WebSocket, ControlledAccount> ContextList => _vm.ContextList;

        public NexusWindow() : this(NexusViewModel.Default)
        {
        }

        public NexusWindow(NexusViewModel viewModel)
        {
            InitializeComponent();
            Instance = this;
            _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            NexusHost.Current ??= _vm;
            DataContext = _vm;

            _vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplyServerVisualState(_vm.IsServerRunning);

            Loaded += NexusWindow_Loaded;
            Closing += (s, e) =>
            {
                if (_allowClose)
                    return;

                _vm.SaveAccounts();
                e.Cancel = true;
                Hide();
            };
        }

        protected override void OnClosed(EventArgs e)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            if (ReferenceEquals(Instance, this))
                Instance = null;
            base.OnClosed(e);
        }

        internal void ForceClose()
        {
            _allowClose = true;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            Close();
        }

        private void NexusWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                _ = _vm.InitializeOnLaunchAsync();
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[NexusWindow.Loaded] {ex.Message}");
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NexusViewModel.IsServerRunning))
            {
                AppDispatcher.Post(() => ApplyServerVisualState(_vm.IsServerRunning));
            }
        }

        private void ApplyServerVisualState(bool running)
        {
            if (TxtServerStatusBadge != null)
                TxtServerStatusBadge.Foreground = running ? RunningBadgeBrush : StoppedBadgeBrush;
            if (BtnToggleServer != null)
                BtnToggleServer.Background = running ? StopButtonBrush : StartButtonBrush;
        }

        public void SaveAccounts() => _vm.SaveAccounts();
        public void LoadAccounts() => _vm.LoadAccounts();
        public Task StartServerAsync() => _vm.StartServerAsync();
        public Task StopServerAsync() => _vm.StopServerAsync();
        public void StartServer() => _ = _vm.StartServerAsync();
        public void StopServer() => _ = _vm.StopServerAsync();
        public void RefreshAccount(ControlledAccount account) => _vm.RefreshAccount(account);
        public void UpdateClientCount() => _vm.UpdateClientCount();
        public void EmitMessage(string message, bool toAll = false) => _vm.EmitMessage(message, toAll);
        public void LogMessage(string message) => _vm.LogMessage(message);
        public void SetStatus(string message) => _vm.LogMessage(message);
    }
}
