#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs
{
    public sealed class ProxyManagerViewModel : DialogViewModelBase
    {
        private static readonly char[] ProxySplitSeparators = { '\r', '\n', ',', ';' };

        private readonly IProxyStore _store;
        private readonly IAppLogger _logger;
        private string _newProxyText = string.Empty;

        public ProxyManagerViewModel(IProxyStore? store = null, IAppLogger? logger = null)
        {
            _store = store ?? ProxyStore.Default;
            _logger = logger ?? AppServices.Logger;

            foreach (var item in _store.Load())
            {
                Proxies.Add(item);
            }

            AddProxyCommand = TrackCommand(new RelayCommand(AddProxy, () => !IsBusy));
            DeleteProxyCommand = TrackCommand(new RelayCommand<ProxyItem>(DeleteProxy, _ => !IsBusy));
            TestAllCommand = TrackCommand(new AsyncRelayCommand(TestAllAsync, () => !IsBusy));
            RemoveDeadCommand = TrackCommand(new RelayCommand(RemoveDead, () => !IsBusy));
            SaveCommand = TrackCommand(new RelayCommand(Save, () => !IsBusy));
        }

        public ObservableCollection<ProxyItem> Proxies { get; } = new();

        public string NewProxyText
        {
            get => _newProxyText;
            set => SetProperty(ref _newProxyText, value);
        }

        public ICommand AddProxyCommand { get; }
        public ICommand DeleteProxyCommand { get; }
        public IAsyncRelayCommand TestAllCommand { get; }
        public ICommand RemoveDeadCommand { get; }
        public ICommand SaveCommand { get; }

        public (int Added, int Duplicates, int Invalid) AddEntries(string? raw)
        {
            if (IsBusy)
                return (0, 0, 0);

            string? rawInput = raw?.Trim();
            if (string.IsNullOrEmpty(rawInput))
                return (0, 0, 0);

            var entries = rawInput.Split(ProxySplitSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (entries.Length == 0)
                return (0, 0, 0);

            var existingSet = new HashSet<string>(Proxies.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var p in Proxies)
            {
                existingSet.Add($"{p.Protocol}://{p.Host}:{p.Port}:{p.Username}");
            }

            int added = 0;
            int skippedDuplicate = 0;
            int invalid = 0;

            foreach (string entry in entries)
            {
                var item = _store.Parse(entry);
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

                Proxies.Add(item);
                added++;
            }

            return (added, skippedDuplicate, invalid);
        }

        private void AddProxy()
        {
            if (IsBusy)
                return;

            var (added, skippedDuplicate, invalid) = AddEntries(NewProxyText);
            if (added == 0 && skippedDuplicate == 0 && invalid == 0)
                return;

            if (added > 0)
            {
                NewProxyText = string.Empty;
                string extra = (skippedDuplicate > 0 || invalid > 0)
                    ? LocalizationService.Format("ProxyManager.DuplicatesAndErrorsSummary", skippedDuplicate, invalid)
                    : string.Empty;
                SetStatus(LocalizationService.Format("ProxyManager.AddedProxiesCount", added, extra), StatusKind.Success);
            }
            else if (skippedDuplicate > 0)
            {
                SetStatus(LocalizationService.Get("ProxyManager.ProxyAlreadyExists"), StatusKind.Warning);
            }
            else
            {
                SetStatus(LocalizationService.Get("ProxyManager.InvalidProxyFormat"), StatusKind.Error);
            }
        }

        private void DeleteProxy(ProxyItem? item)
        {
            if (IsBusy)
                return;

            if (item != null && Proxies.Remove(item))
            {
                SetStatus(LocalizationService.Format("ProxyManager.TotalProxiesCount", Proxies.Count), StatusKind.Neutral);
            }
        }

        public async Task TestAllAsync()
        {
            if (IsBusy)
                return;

            if (Proxies.Count == 0)
            {
                SetStatus(LocalizationService.Get("ProxyManager.NoProxiesToCheck"), StatusKind.Warning);
                return;
            }

            var snapshot = Proxies.ToArray();
            IsBusy = true;
            CancellationToken token = BeginOperation();
            SetStatus(LocalizationService.Format("ProxyManager.CheckingProxiesProgress", snapshot.Length), StatusKind.Info);

            try
            {
                await _store.TestAllAsync(snapshot, token);
                if (!token.IsCancellationRequested)
                {
                    int online = snapshot.Count(p => p.IsOnline);
                    int dead = snapshot.Count(p => p.LatencyMs == -2);
                    SetStatus(
                        LocalizationService.Format("ProxyManager.CheckSummaryResult", online, snapshot.Length, dead),
                        online > 0 ? StatusKind.Success : StatusKind.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                // Testing cancelled
            }
            catch (Exception ex)
            {
                _logger.Warn($"[ProxyManagerViewModel] Error during proxy testing: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void RemoveDead()
        {
            if (IsBusy)
                return;

            int removedCount = 0;
            for (int i = Proxies.Count - 1; i >= 0; i--)
            {
                if (Proxies[i].LatencyMs == -2)
                {
                    Proxies.RemoveAt(i);
                    removedCount++;
                }
            }

            if (removedCount > 0)
                SetStatus(LocalizationService.Format("ProxyManager.DeletedFailedProxies", removedCount), StatusKind.Success);
            else
                SetStatus(LocalizationService.Get("ProxyManager.NoFailedProxiesToDelete"), StatusKind.Neutral);
        }

        private void Save()
        {
            if (IsBusy)
                return;

            _store.Save(Proxies);
            RequestClose(true);
        }
    }
}
