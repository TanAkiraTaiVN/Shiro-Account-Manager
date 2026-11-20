#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class MainViewModel
    {
        #region Commands
        public ICommand AntiLagBoosterCommand { get; private set; } = null!;
        public ICommand ToggleWindowsCommand { get; private set; } = null!;
        public ICommand ToggleAntiAfkCommand { get; private set; } = null!;
        public ICommand TogglePotatoModeCommand { get; private set; } = null!;
        public ICommand ToggleStreamerModeCommand { get; private set; } = null!;
        public ICommand SelectAllCommand { get; private set; } = null!;
        public ICommand ClearSelectionCommand { get; private set; } = null!;
        public ICommand ClearSearchCommand { get; private set; } = null!;
        public ICommand RefreshPresenceCommand { get; private set; } = null!;
        public ICommand CheckSessionsCommand { get; private set; } = null!;
        public ICommand ReLoginAllExpiredCommand { get; private set; } = null!;
        public ICommand NavigateToAccountsCommand { get; private set; } = null!;
        public ICommand NavigateToServerListCommand { get; private set; } = null!;
        public ICommand NavigateToRecentGamesCommand { get; private set; } = null!;
        public ICommand NavigateToSettingsCommand { get; private set; } = null!;
        public ICommand KillAllClientsCommand { get; private set; } = null!;
        public ICommand SetSortModeCommand { get; private set; } = null!;
        public ICommand SelectRecentGameCommand { get; private set; } = null!;
        public ICommand RefreshAllRobuxCommand { get; private set; } = null!;
        public ICommand CheckRobuxAccountCommand { get; private set; } = null!;
        public ICommand OpenProxyManagerCommand { get; private set; } = null!;
        public ICommand CleanSystemTrayCommand { get; private set; } = null!;
        public IAsyncRelayCommand AuditAllAccountsCommand { get; private set; } = null!;

        private void PostStatusMessage(string message)
        {
            AppDispatcher.Post(() => { if (System.Threading.Volatile.Read(ref _disposed) == 0) StatusMessage = message; });
        }

        private void OnStreamerModeStateChanged(bool active)
        {
            AppDispatcher.Post(() =>
            {
                if (System.Threading.Volatile.Read(ref _disposed) != 0) return;
                NotifyToolStatesChanged();
                RefreshAllAccountDisplays();
            });
        }

        private void InitializeCommands()
        {
            InitializeLaunchCommands();
            InitializeAccountCommands();
            InitializeUiUpgradeCommands();

            AntiLagBoosterCommand = new RelayCommand(() =>
            {
                var (bytesSaved, count, prioCount) = MemoryOptimizer.OptimizeRobloxClients(true);
                double mb = bytesSaved / (1024.0 * 1024.0);
                if (count > 0 || prioCount > 0)
                    StatusMessage = Shiro.Services.LocalizationService.Format("Main.SuperOptimizationSuccess", mb, prioCount);
                else
                    StatusMessage = Shiro.Services.LocalizationService.Get("Main.MemoryOptimizedStatus");
            });

            ToggleWindowsCommand = new RelayCommand(() =>
            {
                bool wasHidden = RobloxWindowService.AreWindowsHidden || _areWindowsHidden;
                if (wasHidden)
                {
                    var (shown, _) = RobloxWindowService.ShowAllWindows();
                    _areWindowsHidden = false;
                    OnPropertyChanged(nameof(AreWindowsHidden));
                    NotifyToolStatesChanged();
                    StatusMessage = LocalizationService.Format("Main.RestoredRobloxWindows", shown);
                }
                else
                {
                    var (hidden, _) = RobloxWindowService.HideAllWindows();
                    bool active = hidden > 0 && RobloxWindowService.AreWindowsHidden;
                    _areWindowsHidden = active;
                    OnPropertyChanged(nameof(AreWindowsHidden));
                    NotifyToolStatesChanged();

                    if (active)
                    {
                        string msg = LocalizationService.Format("Main.ZeroGpuActivated", hidden);
                        StatusMessage = msg;
                        ToastService.ShowSuccess("Zero-GPU Mode", msg);
                    }
                    else
                    {
                        string warn = LocalizationService.Get("Main.ZeroGpuNoWindowsOpen");
                        StatusMessage = "" + warn;
                        ToastService.ShowInfo("Zero-GPU Mode", warn);
                    }
                }
            });

            ToggleAntiAfkCommand = new RelayCommand(() =>
            {
                bool next = !AntiAfkService.IsEnabled;
                if (IsAntiAfk != next)
                    IsAntiAfk = next;
                else
                    AntiAfkService.SetConfig(next);
            });

            TogglePotatoModeCommand = new RelayCommand(() =>
            {
                IsPotatoMode = !IsPotatoMode;
            });

            ToggleStreamerModeCommand = new RelayCommand(() =>
            {
                IsStreamerModeEnabled = !IsStreamerModeEnabled;
            });

            OpenProxyManagerCommand = new RelayCommand(() => AppServices.Dialogs.OpenProxyManagerDialog());

            AuditAllAccountsCommand = new AsyncRelayCommand(async () =>
            {
                StatusMessage = LocalizationService.Get("Main.AuditAllSessionsDescription");
                var audited = await AuditAccountSessionsAsync();
                StatusMessage = LocalizationService.Format("Main.AuditAllSessionsCompleted", audited.Count);
            });

            RefreshAllRobuxCommand = new AsyncRelayCommand(async () =>
            {
                var validAccounts = _accounts.Where(a => a.Valid && a.UserId > 0 && a.Model != null).ToList();
                if (validAccounts.Count == 0)
                {
                    StatusMessage = Shiro.Services.LocalizationService.Get("Main.NoValidAccountsForRobux");
                    return;
                }
                int total = validAccounts.Count;
                StatusMessage = Shiro.Services.LocalizationService.Format("Main.RefreshingRobuxMultiple", total);
                int updated = 0;

                using var concurrencyGate = new SemaphoreSlim(4, 4);
                var tasks = validAccounts.Select(async acc =>
                {
                    await concurrencyGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await acc.Model.FetchRobuxBalanceAsync().ConfigureAwait(false);

                        int current = Interlocked.Increment(ref updated);
                        AppDispatcher.Post(() =>
                        {
                            acc.NotifyRobuxChanged();
                            StatusMessage = Shiro.Services.LocalizationService.Format("Main.RefreshingRobuxProgress", current, total, acc.Username);
                        });
                    }
                    finally
                    {
                        concurrencyGate.Release();
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
                await AppServices.AccountRepository.SaveAsync(true);
                NotifyCounts();
                if (SortMode == AccountSortMode.RobuxDescending)
                {
                    ApplyFilter();
                }
                StatusMessage = Shiro.Services.LocalizationService.Format("Main.RefreshingRobuxCompleted", updated);
            });

            CheckRobuxAccountCommand = new AsyncRelayCommand(async p =>
            {
                if (p is AccountViewModel a && a.Model != null)
                {
                    StatusMessage = Shiro.Services.LocalizationService.Format("Main.CheckingRobuxSingle", a.Username);
                    long bal = await a.Model.FetchRobuxBalanceAsync();
                    a.NotifyRobuxChanged();
                    await AppServices.AccountRepository.SaveAsync(true);
                    NotifyCounts();
                    if (SortMode == AccountSortMode.RobuxDescending)
                    {
                        ApplyFilter();
                    }
                    StatusMessage = bal >= 0
                        ? Shiro.Services.LocalizationService.Format("Main.RobuxBalanceResult", a.Username, bal)
                        : Shiro.Services.LocalizationService.Format("Main.CannotFetchRobux", a.Username);
                }
            });

            SelectAllCommand = new RelayCommand(() =>
            {
                bool allSelected = _filteredAccounts.Count > 0 && _filteredAccounts.All(a => a.IsSelected);
                _suppressCountNotifications = true;
                try
                {
                    foreach (var acc in _filteredAccounts)
                        acc.IsSelected = !allSelected;
                }
                finally
                {
                    _suppressCountNotifications = false;
                }
                NotifyCounts();
                OnPropertyChanged(nameof(IsAllSelected));
            });

            ClearSelectionCommand = new RelayCommand(() =>
            {
                _suppressCountNotifications = true;
                try
                {
                    foreach (var acc in _accounts)
                        acc.IsSelected = false;
                }
                finally
                {
                    _suppressCountNotifications = false;
                }
                NotifyCounts();
                OnPropertyChanged(nameof(IsAllSelected));
            });

            ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
            RefreshPresenceCommand = new RelayCommand(() =>
            {
                RefreshLiveProcessStatus();
                StatusMessage = Shiro.Services.LocalizationService.Get("Main.PresenceStatusUpdated");
            });

            CheckSessionsCommand = new RelayCommand(() =>
            {
                try
                {
                    _ = Task.Run(async () =>
                    {
                        await AppServices.AccountWorkflowService.CheckAllSessionsAsync(this, PostStatusMessage).ConfigureAwait(false);
                    });
                }
                catch (Exception ex)
                {
                    StatusMessage = Shiro.Services.LocalizationService.Get("Main.SessionCheckErrorPrefix") + ex.Message;
                }
            });

            ReLoginAllExpiredCommand = new RelayCommand(() =>
            {
                try
                {
                    int skippedLocked = 0;
                    var expired = new List<Account>();
                    for (int i = 0; i < _accounts.Count; i++)
                    {
                        var a = _accounts[i];
                        if (!a.IsExpired || a.Model == null)
                            continue;
                        if (a.Model.IsBannedOrLocked)
                        {
                            skippedLocked++;
                            continue;
                        }
                        expired.Add(a.Model);
                    }
                    if (expired.Count == 0)
                    {
                        StatusMessage = skippedLocked > 0
                            ? Shiro.Services.LocalizationService.Format("Main.NoNormalExpiredAccounts", skippedLocked)
                            : Shiro.Services.LocalizationService.Get("Main.NoExpiredAccountsFound");
                        return;
                    }
                    if (skippedLocked > 0)
                    {
                        StatusMessage = Shiro.Services.LocalizationService.Format("Main.ReloggingInExpiredAccounts", expired.Count, skippedLocked);
                    }
                    if (expired.Count == 1)
                        AppServices.Dialogs.OpenFastLoginDialog(expired[0]);
                    else
                        AppServices.Dialogs.OpenFastLoginDialogBatch(expired);
                }
                catch (Exception ex)
                {
                    StatusMessage = Shiro.Services.LocalizationService.Get("Main.BatchReloginErrorPrefix") + ex.Message;
                }
            });

            NavigateToAccountsCommand = new RelayCommand(() => CurrentPage = NavigationPage.Accounts);
            NavigateToServerListCommand = new RelayCommand(() => CurrentPage = NavigationPage.ServerList);
            NavigateToRecentGamesCommand = new RelayCommand(() => { CurrentPage = NavigationPage.RecentGames; ReloadRecentGames(); });
            NavigateToSettingsCommand = new RelayCommand(() => CurrentPage = NavigationPage.Settings);
            KillAllClientsCommand = new RelayCommand(() =>
            {
                _ = Task.Run(() =>
                {
                    int killed = RobloxProcessService.KillAllRobloxProcesses();
                    AppDispatcher.Post(() =>
                    {
                        StatusMessage = Shiro.Services.LocalizationService.Format("Main.KilledRobloxCount", killed);
                        RefreshLiveProcessStatus();
                    });
                });
            });
            CleanSystemTrayCommand = new RelayCommand(() =>
            {
                Task.Run(() => Utilities.ThoroughCleanNotificationArea());
                StatusMessage = Shiro.Services.LocalizationService.Get("Main.TrayCleanedSuccess");
                ToastService.ShowSuccess(Shiro.Services.LocalizationService.Get("Main.SystemTrayTitle"), Shiro.Services.LocalizationService.Get("Main.TrayCleanedGhostIcons"));
            });
            SelectRecentGameCommand = new RelayCommand(p =>
            {
                if (p is RecentGameItemViewModel item)
                {
                    SetKnownGameTitle(item.PlaceId, item.Name);
                    PlaceId = item.PlaceId.ToString();
                    AppServices.ServerBrowserState.CurrentPlaceId = PlaceId;
                    _settings.Set("SavedPlaceId", PlaceId);
                    AppServices.HostUiBridge.QueueDeferredSaveSettings();
                    CurrentPage = NavigationPage.Accounts;
                    StatusMessage = Shiro.Services.LocalizationService.Format("Main.GameSelected", item.Name, item.PlaceId);
                }
            });

            SetSortModeCommand = new RelayCommand(param =>
            {
                if (param is AccountSortMode mode)
                    SortMode = mode;
                else if (param is string modeStr && Enum.TryParse<AccountSortMode>(modeStr, out var m))
                    SortMode = m;
            });

            // Populate recent games immediately on boot
            ReloadRecentGames();
        }
        #endregion
    }
}
