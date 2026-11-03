#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class MainViewModel
    {
        // Reference to optional host callbacks for modal popups and launches (used by tests or custom overrides)
        public Action? OnSettingsRequested { get; set; }
        public Action? OnServerListRequested { get; set; }
        public Action? OnRecentGamesRequested { get; set; }
        public Action? OnCheckSessionsRequested { get; set; }
        public Action? OnReLoginAllExpiredRequested { get; set; }
        public Action? OnKillAllRequested { get; set; }
        public Action? OnProxyManagerRequested { get; set; }
        public Action? OnNexusRequested { get; set; }

        #region Commands
        public ICommand ToggleViewModeCommand { get; private set; } = null!;
        public ICommand ToggleCommandPaletteCommand { get; private set; } = null!;
        public ICommand OpenCommandPaletteCommand { get; private set; } = null!;
        public ICommand CloseCommandPaletteCommand { get; private set; } = null!;
        public ICommand ChangeAccentThemeCommand { get; private set; } = null!;
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
        public ICommand OpenSettingsCommand { get; private set; } = null!;
        public ICommand OpenServerListCommand { get; private set; } = null!;
        public ICommand OpenRecentGamesCommand { get; private set; } = null!;
        public ICommand NavigateToAccountsCommand { get; private set; } = null!;
        public ICommand NavigateToServerListCommand { get; private set; } = null!;
        public ICommand NavigateToRecentGamesCommand { get; private set; } = null!;
        public ICommand NavigateToSettingsCommand { get; private set; } = null!;
        public ICommand ToggleShuffleCommand { get; private set; } = null!;
        public ICommand KillAllClientsCommand { get; private set; } = null!;
        public ICommand SetFilterTabCommand { get; private set; } = null!;
        public ICommand SetSortModeCommand { get; private set; } = null!;
        public ICommand SelectRecentGameCommand { get; private set; } = null!;
        public ICommand LaunchRecentGameCommand { get; private set; } = null!;
        public ICommand RefreshRecentGamesCommand { get; private set; } = null!;
        public ICommand TileWindowsCommand { get; private set; } = null!;
        public ICommand RefreshAllRobuxCommand { get; private set; } = null!;
        public ICommand CheckRobuxAccountCommand { get; private set; } = null!;
        public ICommand OpenProxyManagerCommand { get; private set; } = null!;
        public ICommand OpenNexusCommand { get; private set; } = null!;
        public ICommand CleanSystemTrayCommand { get; private set; } = null!;

        private void PostStatusMessage(string message)
        {
            AppDispatcher.Post(() => StatusMessage = message);
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
                    StatusMessage = Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_442", mb, prioCount);
                else
                    StatusMessage = Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_443");
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
                    StatusMessage = LocalizationService.IsEnglish
                        ? $"Restored {shown} Roblox window(s)."
                        : Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_444", shown);
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
                        string msg = LocalizationService.IsEnglish
                            ? $"Hidden {hidden} Roblox window(s) (Zero-GPU Mode)!"
                            : Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_445", hidden);
                        StatusMessage = msg;
                        ToastService.ShowSuccess("Zero-GPU Mode", msg);
                    }
                    else
                    {
                        string warn = LocalizationService.IsEnglish
                            ? "No running Roblox windows found to hide."
                            : Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_446");
                        StatusMessage = "" + warn;
                        ToastService.ShowInfo("Zero-GPU Mode", warn);
                    }
                }
            });

            ToggleAntiAfkCommand = new RelayCommand(() =>
            {
                AntiAfkService.Toggle();
                IsAntiAfk = AntiAfkService.IsEnabled;
                StatusMessage = IsAntiAfk
                    ? Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_447")
                    : Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_448");
            });

            TogglePotatoModeCommand = new RelayCommand(() =>
            {
                bool next = !IsPotatoMode;
                IsPotatoMode = next;
                AppSettings.General?.Set("EnablePotatoMode", next ? "true" : "false");
                ClientSettingsPatcher.PatchSettings();
                StatusMessage = next
                    ? Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_449")
                    : Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_450");
            });

            ToggleStreamerModeCommand = new RelayCommand(() =>
            {
                IsStreamerModeEnabled = !IsStreamerModeEnabled;
            });

            StreamerModeService.StateChanged += active =>
            {
                AppDispatcher.Post(() =>
                {
                    NotifyToolStatesChanged();
                    RefreshAllAccountDisplays();
                });
            };

            TileWindowsCommand = new RelayCommand(param =>
            {
                int cols = 0, rows = 0;
                if (param is string s && s.Contains('x'))
                {
                    var parts = s.Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int c) && int.TryParse(parts[1], out int r))
                    {
                        cols = c;
                        rows = r;
                    }
                }
                var (tiled, desc) = RobloxWindowService.TileWindows(cols, rows);
                StatusMessage = tiled > 0
                    ? Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_451", tiled, desc)
                    : $"{desc}";
            });

            OpenProxyManagerCommand = new RelayCommand(() =>
            {
                if (OnProxyManagerRequested != null) OnProxyManagerRequested.Invoke();
                else AppServices.Dialogs.OpenProxyManagerDialog();
            });

            OpenNexusCommand = new RelayCommand(() =>
            {
                if (OnNexusRequested != null) OnNexusRequested.Invoke();
                else AppServices.Dialogs.OpenNexusWindow();
            });

            RefreshAllRobuxCommand = new AsyncRelayCommand(async () =>
            {
                var validAccounts = _accounts.Where(a => a.Valid && a.UserId > 0 && a.Model != null).ToList();
                if (validAccounts.Count == 0)
                {
                    StatusMessage = Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_452");
                    return;
                }
                int total = validAccounts.Count;
                StatusMessage = Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_453", total);
                int updated = 0;

                using var concurrencyGate = new SemaphoreSlim(4, 4);
                var tasks = validAccounts.Select(async acc =>
                {
                    await concurrencyGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await RobloxRateLimitCoordinator.Instance.ExecuteWithPacingAsync(
                            RateLimitCategory.General,
                            () => acc.Model.FetchRobuxBalanceAsync(),
                            minIntervalMs: 180).ConfigureAwait(false);

                        int current = Interlocked.Increment(ref updated);
                        AppDispatcher.Post(() =>
                        {
                            acc.NotifyRobuxChanged();
                            StatusMessage = Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_454", current, total, acc.Username);
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
                StatusMessage = Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_455", updated);
            });

            CheckRobuxAccountCommand = new AsyncRelayCommand(async p =>
            {
                if (p is AccountViewModel a && a.Model != null)
                {
                    StatusMessage = Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_456", a.Username);
                    long bal = await a.Model.FetchRobuxBalanceAsync();
                    a.NotifyRobuxChanged();
                    await AppServices.AccountRepository.SaveAsync(true);
                    NotifyCounts();
                    if (SortMode == AccountSortMode.RobuxDescending)
                    {
                        ApplyFilter();
                    }
                    StatusMessage = bal >= 0
                        ? Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_457", a.Username, bal)
                        : Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_458", a.Username);
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
                StatusMessage = Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_459");
            });

            CheckSessionsCommand = new RelayCommand(() =>
            {
                try
                {
                    if (OnCheckSessionsRequested != null)
                    {
                        OnCheckSessionsRequested.Invoke();
                        return;
                    }
                    _ = Task.Run(async () =>
                    {
                        await AccountWorkflowService.Default.CheckAllSessionsAsync(this, PostStatusMessage).ConfigureAwait(false);
                    });
                }
                catch (Exception ex)
                {
                    StatusMessage = Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_460") + ex.Message;
                }
            });

            ReLoginAllExpiredCommand = new RelayCommand(() =>
            {
                try
                {
                    if (OnReLoginAllExpiredRequested != null)
                    {
                        OnReLoginAllExpiredRequested.Invoke();
                        return;
                    }
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
                            ? Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_461", skippedLocked)
                            : Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_462");
                        return;
                    }
                    if (skippedLocked > 0)
                    {
                        StatusMessage = Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_463", expired.Count, skippedLocked);
                    }
                    if (expired.Count == 1)
                        AppServices.Dialogs.OpenFastLoginDialog(expired[0]);
                    else
                        AppServices.Dialogs.OpenFastLoginDialogBatch(expired);
                }
                catch (Exception ex)
                {
                    StatusMessage = Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_464") + ex.Message;
                }
            });

            NavigateToAccountsCommand = new RelayCommand(() => CurrentPage = NavigationPage.Accounts);
            NavigateToServerListCommand = new RelayCommand(() => CurrentPage = NavigationPage.ServerList);
            NavigateToRecentGamesCommand = new RelayCommand(() => { CurrentPage = NavigationPage.RecentGames; ReloadRecentGames(); });
            NavigateToSettingsCommand = new RelayCommand(() => CurrentPage = NavigationPage.Settings);
            OpenSettingsCommand = new RelayCommand(() => CurrentPage = NavigationPage.Settings);
            OpenServerListCommand = new RelayCommand(() => CurrentPage = NavigationPage.ServerList);
            OpenRecentGamesCommand = new RelayCommand(() => { CurrentPage = NavigationPage.RecentGames; ReloadRecentGames(); });
            ToggleShuffleCommand = new RelayCommand(() => IsShuffleJobId = !IsShuffleJobId);
            KillAllClientsCommand = new RelayCommand(() =>
            {
                if (OnKillAllRequested != null)
                {
                    OnKillAllRequested.Invoke();
                    return;
                }
                _ = Task.Run(() =>
                {
                    int killed = RobloxProcessService.KillAllRobloxProcesses();
                    AppDispatcher.Post(() =>
                    {
                        StatusMessage = Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_465", killed);
                        RefreshLiveProcessStatus();
                    });
                });
            });
            CleanSystemTrayCommand = new RelayCommand(() =>
            {
                Task.Run(() => Utilities.ThoroughCleanNotificationArea());
                StatusMessage = Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_466");
                ToastService.ShowSuccess(Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_468"), Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_467"));
            });
            SelectRecentGameCommand = new RelayCommand(p =>
            {
                if (p is RecentGameItemViewModel item)
                {
                    SetKnownGameTitle(item.PlaceId, item.Name);
                    PlaceId = item.PlaceId.ToString();
                    AppSettings.CurrentPlaceId = PlaceId;
                    AppSettings.General?.Set("SavedPlaceId", PlaceId);
                    AppSettings.QueueDeferredSave();
                    CurrentPage = NavigationPage.Accounts;
                    StatusMessage = Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_469", item.Name, item.PlaceId);
                }
            });
            LaunchRecentGameCommand = new RelayCommand(p =>
            {
                if (p is RecentGameItemViewModel item)
                {
                    SetKnownGameTitle(item.PlaceId, item.Name);
                    PlaceId = item.PlaceId.ToString();
                    CurrentPage = NavigationPage.Accounts;
                    var selected = Accounts.Where(a => a.IsSelected && a.Model != null).ToList();
                    if (selected.Count > 0)
                    {
                        if (selected.Count == 1)
                        {
                            if (OnJoinServerSingleRequested != null) OnJoinServerSingleRequested.Invoke(selected[0]);
                            else ExecuteJoinServerForAccounts(new[] { selected[0] });
                        }
                        else
                        {
                            if (OnJoinServerSelectedRequested != null) OnJoinServerSelectedRequested.Invoke(selected);
                            else ExecuteJoinServerForAccounts(selected);
                        }
                    }
                    else
                    {
                        string warnMsg = LocalizationService.IsEnglish
                            ? $"Selected {item.Name} (Place ID: {item.PlaceId}). Please select at least one account first!"
                            : Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_470", item.Name, item.PlaceId);
                        StatusMessage = warnMsg;
                        ToastService.ShowInfo(item.Name, warnMsg);
                    }
                }
            });
            RefreshRecentGamesCommand = new RelayCommand(() => ReloadRecentGames());

            SetFilterTabCommand = new RelayCommand(param =>
            {
                if (param is AccountFilterTab tabEnum)
                    ActiveTab = tabEnum;
                else if (param is string tabStr && Enum.TryParse<AccountFilterTab>(tabStr, out var tab))
                    ActiveTab = tab;
            });

            SetSortModeCommand = new RelayCommand(param =>
            {
                if (param is AccountSortMode mode)
                    SortMode = mode;
                else if (param is string modeStr && Enum.TryParse<AccountSortMode>(modeStr, out var m))
                    SortMode = m;
            });

            ToggleViewModeCommand = new RelayCommand(() => IsCompactView = !IsCompactView);
            ToggleCommandPaletteCommand = new RelayCommand(() => IsCommandPaletteOpen = !IsCommandPaletteOpen);
            OpenCommandPaletteCommand = new RelayCommand(() => IsCommandPaletteOpen = true);
            CloseCommandPaletteCommand = new RelayCommand(() => IsCommandPaletteOpen = false);
            ChangeAccentThemeCommand = new RelayCommand(param =>
            {
                if (param is AccentTheme theme)
                    AccentThemeService.ApplyTheme(theme);
                else if (param is string s && Enum.TryParse<AccentTheme>(s, true, out var t))
                    AccentThemeService.ApplyTheme(t);
            });

            // Populate recent games immediately on boot
            ReloadRecentGames();
        }
        #endregion
    }
}
