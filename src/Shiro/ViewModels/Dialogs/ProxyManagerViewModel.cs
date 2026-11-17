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

            AddProxyCommand = new RelayCommand(AddProxy);
            DeleteProxyCommand = new RelayCommand<ProxyItem>(DeleteProxy);
            TestAllCommand = TrackCommand(new AsyncRelayCommand(TestAllAsync, () => !IsBusy));
            RemoveDeadCommand = new RelayCommand(RemoveDead);
            SaveCommand = new RelayCommand(Save);
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

            IsBusy = true;
            CancellationToken token = BeginOperation();
            SetStatus(LocalizationService.Format("ProxyManager.CheckingProxiesProgress", Proxies.Count), StatusKind.Info);

            try
            {
                await _store.TestAllAsync(Proxies, token);
                if (!token.IsCancellationRequested)
                {
                    int online = Proxies.Count(p => p.IsOnline);
                    int dead = Proxies.Count(p => p.LatencyMs == -2);
                    SetStatus(
                        LocalizationService.Format("ProxyManager.CheckSummaryResult", online, Proxies.Count, dead),
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
            _store.Save(Proxies);
            RequestClose(true);
        }
    }
}
