#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public partial class ProxyManagerDialog : Window
    {
        private readonly ObservableCollection<ProxyItem> _proxies = new();
        private CancellationTokenSource? _testCts;

        public ProxyManagerDialog()
        {
            InitializeComponent();
            LoadProxies();
        }

        private void LoadProxies()
        {
            _proxies.Clear();
            var list = ProxyManagerService.LoadProxies();
            foreach (var item in list)
                _proxies.Add(item);

            LstProxies.ItemsSource = _proxies;
        }

        private static readonly char[] ProxySplitSeparators = ['\r', '\n', ',', ';'];

        private void OnAddProxyClicked(object sender, RoutedEventArgs e)
        {
            string? rawInput = TxtNewProxy.Text?.Trim();
            if (string.IsNullOrEmpty(rawInput)) return;

            var entries = rawInput.Split(ProxySplitSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (entries.Length == 0) return;

            var existingSet = new HashSet<string>(_proxies.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var p in _proxies)
            {
                existingSet.Add($"{p.Protocol}://{p.Host}:{p.Port}:{p.Username}");
            }

            int added = 0;
            int skippedDuplicate = 0;
            int invalid = 0;

            foreach (string entry in entries)
            {
                var item = ProxyManagerService.ParseProxy(entry);
                if (item == null)
                {
                    invalid++;
                    continue;
                }

                string key = $"{item.Protocol}://{item.Host}:{item.Port}:{item.Username}";
                if (!existingSet.Add(key))
                {
                    skippedDuplicate++;
                    continue;
                }

                _proxies.Add(item);
                added++;
            }

            if (added > 0)
            {
                TxtNewProxy.Text = string.Empty;
                if (TxtStatus != null)
                {
                    string extra = (skippedDuplicate > 0 || invalid > 0)
                        ? Shiro.Services.LocalizationService.Format("ProxyManager.DuplicatesAndErrorsSummary", skippedDuplicate, invalid)
                        : string.Empty;
                    TxtStatus.Text = Shiro.Services.LocalizationService.Format("ProxyManager.AddedProxiesCount", added, extra);
                }
            }
            else if (TxtStatus != null)
            {
                TxtStatus.Text = skippedDuplicate > 0
                    ? Shiro.Services.LocalizationService.Get("ProxyManager.ProxyAlreadyExists")
                    : Shiro.Services.LocalizationService.Get("ProxyManager.InvalidProxyFormat");
            }
        }

        private void OnDeleteProxyClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ProxyItem item)
            {
                _proxies.Remove(item);
                if (TxtStatus != null)
                    TxtStatus.Text = Shiro.Services.LocalizationService.Format("ProxyManager.TotalProxiesCount", _proxies.Count);
            }
        }

        private async void OnTestAllClicked(object sender, RoutedEventArgs e)
        {
            if (_proxies.Count == 0)
            {
                if (TxtStatus != null)
                    TxtStatus.Text = Shiro.Services.LocalizationService.Get("ProxyManager.NoProxiesToCheck");
                return;
            }

            _testCts?.Cancel();
            _testCts?.Dispose();
            _testCts = new CancellationTokenSource();

            BtnTestAll.IsEnabled = false;
            if (TxtStatus != null)
                TxtStatus.Text = Shiro.Services.LocalizationService.Format("ProxyManager.CheckingProxiesProgress", _proxies.Count);

            try
            {
                await ProxyManagerService.TestAllProxiesAsync(_proxies, ct: _testCts.Token);
                if (TxtStatus != null)
                {
                    int online = _proxies.Count(p => p.IsOnline);
                    int dead = _proxies.Count(p => p.LatencyMs == -2);
                    TxtStatus.Text = Shiro.Services.LocalizationService.Format("ProxyManager.CheckSummaryResult", online, _proxies.Count, dead);
                }
            }
            catch (OperationCanceledException) { /* canceled */ }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"[ProxyManagerDialog] Error during proxy testing: {ex.Message}");
            }
            finally
            {
                BtnTestAll.IsEnabled = true;
            }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            _testCts?.Cancel();
            _testCts?.Dispose();
            _testCts = null;
            base.OnClosing(e);
        }

        private void OnRemoveDeadClicked(object sender, RoutedEventArgs e)
        {
            int removedCount = 0;
            for (int i = _proxies.Count - 1; i >= 0; i--)
            {
                if (_proxies[i].LatencyMs == -2)
                {
                    _proxies.RemoveAt(i);
                    removedCount++;
                }
            }

            if (TxtStatus != null)
            {
                TxtStatus.Text = removedCount > 0
                    ? Shiro.Services.LocalizationService.Format("ProxyManager.DeletedFailedProxies", removedCount)
                    : Shiro.Services.LocalizationService.Get("ProxyManager.NoFailedProxiesToDelete");
            }
        }

        private void OnSaveCloseClicked(object sender, RoutedEventArgs e)
        {
            ProxyManagerService.SaveProxies(_proxies);
            Close(true);
        }
    }
}
