#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.AvaloniaUI.Views
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
                        ? $" (Trùng: {skippedDuplicate}, Lỗi: {invalid})"
                        : string.Empty;
                    TxtStatus.Text = $"✅ Đã thêm {added} proxy{extra}";
                }
            }
            else if (TxtStatus != null)
            {
                TxtStatus.Text = skippedDuplicate > 0
                    ? "⚠️ Proxy đã tồn tại trong danh sách."
                    : "⚠️ Định dạng proxy không hợp lệ (VD: IP:Port hoặc IP:Port:User:Pass).";
            }
        }

        private void OnDeleteProxyClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ProxyItem item)
            {
                _proxies.Remove(item);
                if (TxtStatus != null)
                    TxtStatus.Text = $"Tổng: {_proxies.Count} proxy";
            }
        }

        private async void OnTestAllClicked(object sender, RoutedEventArgs e)
        {
            if (_proxies.Count == 0)
            {
                if (TxtStatus != null)
                    TxtStatus.Text = "⚠️ Chưa có proxy nào để kiểm tra.";
                return;
            }

            _testCts?.Cancel();
            _testCts?.Dispose();
            _testCts = new CancellationTokenSource();

            BtnTestAll.IsEnabled = false;
            if (TxtStatus != null)
                TxtStatus.Text = $"⏳ Đang kiểm tra {_proxies.Count} proxy...";

            try
            {
                await ProxyManagerService.TestAllProxiesAsync(_proxies, ct: _testCts.Token);
                if (TxtStatus != null)
                {
                    int online = _proxies.Count(p => p.IsOnline);
                    int dead = _proxies.Count(p => p.LatencyMs == -2);
                    TxtStatus.Text = $"⚡ Hoạt động: {online}/{_proxies.Count} • Lỗi: {dead}";
                }
            }
            catch (OperationCanceledException) { }
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
                    ? $"🗑️ Đã xóa {removedCount} proxy lỗi."
                    : "Không có proxy lỗi nào cần xóa.";
            }
        }

        private void OnSaveCloseClicked(object sender, RoutedEventArgs e)
        {
            ProxyManagerService.SaveProxies(_proxies);
            Close(true);
        }
    }
}
