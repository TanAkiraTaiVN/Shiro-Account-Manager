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

            AntiLagBoosterCommand = new RelayCommand(() =>
            {
                var (bytesSaved, count, prioCount) = MemoryOptimizer.OptimizeRobloxClients(true);
                double mb = bytesSaved / (1024.0 * 1024.0);
                if (count > 0 || prioCount > 0)
                    StatusMessage = $"⚡ Siêu tối ưu chống Lag thành công: Tiết kiệm {mb:F1} MB RAM, hạ ưu tiên CPU {prioCount} client(s)!";
                else
                    StatusMessage = "⚡ Đã tối ưu hóa bộ nhớ và độ ưu tiên hệ thống.";
            });

            ToggleWindowsCommand = new RelayCommand(() =>
            {
                var (nowHidden, affected) = RobloxWindowService.ToggleVisibility();
                AreWindowsHidden = nowHidden;
                StatusMessage = nowHidden
                    ? $"👻 Đã ẩn {affected} cửa sổ Roblox (Zero GPU Mode)! Tiết kiệm tối đa điện năng và GPU."
                    : $"👁️ Đã hiển thị lại {affected} cửa sổ Roblox.";
            });

            ToggleAntiAfkCommand = new RelayCommand(() =>
            {
                AntiAfkService.Toggle();
                IsAntiAfk = AntiAfkService.IsEnabled;
                StatusMessage = IsAntiAfk
                    ? "🛡️ Anti-AFK đã BẬT: Tự động bảo vệ chống kick 20 phút cho tất cả tài khoản!"
                    : "🛡️ Anti-AFK đã TẮT.";
            });

            TogglePotatoModeCommand = new RelayCommand(() =>
            {
                bool next = !IsPotatoMode;
                IsPotatoMode = next;
                AccountManager.General?.Set("EnablePotatoMode", next ? "true" : "false");
                ClientSettingsPatcher.PatchSettings();
                StatusMessage = next
                    ? "🥔 Đã BẬT FastFlags Potato Mode! Game khởi động tiếp theo sẽ siêu nhẹ."
                    : "🥔 Đã TẮT Potato Mode.";
            });

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
                    ? $"📐 Đã sắp xếp {tiled} cửa sổ Roblox theo lưới {desc} thành công!"
                    : $"⚠️ {desc}";
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
                    StatusMessage = "⚠️ Không có tài khoản hợp lệ nào để cập nhật số dư Robux.";
                    return;
                }
                int total = validAccounts.Count;
                StatusMessage = $"💰 Đang cập nhật số dư Robux cho {total} tài khoản...";
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
                            StatusMessage = $"💰 Đang cập nhật số dư Robux ({current}/{total}): {acc.Username}...";
                        });
                    }
                    finally
                    {
                        concurrencyGate.Release();
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
                await AccountManager.SaveAccountsAsync(true);
                NotifyCounts();
                if (SortMode == AccountSortMode.RobuxDescending)
                {
                    ApplyFilter();
                }
                StatusMessage = $"✅ Đã cập nhật xong số dư Robux cho {updated} tài khoản!";
            });

            CheckRobuxAccountCommand = new AsyncRelayCommand(async p =>
            {
                if (p is AccountViewModel a && a.Model != null)
                {
                    StatusMessage = $"💰 Đang kiểm tra số dư Robux cho {a.Username}...";
                    long bal = await a.Model.FetchRobuxBalanceAsync();
                    a.NotifyRobuxChanged();
                    await AccountManager.SaveAccountsAsync(true);
                    NotifyCounts();
                    if (SortMode == AccountSortMode.RobuxDescending)
                    {
                        ApplyFilter();
                    }
                    StatusMessage = bal >= 0
                        ? $"💰 {a.Username}: Số dư hiện tại là {bal:N0} Robux."
                        : $"⚠️ Chưa lấy được số dư cho {a.Username}.";
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
                StatusMessage = "🔄 Đã cập nhật trạng thái hoạt động của các tài khoản.";
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
                    StatusMessage = "❌ Lỗi khi kiểm tra phiên: " + ex.Message;
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
                            ? $"✅ Không có tài khoản hết hạn bình thường nào cần Re-login (đã bỏ qua {skippedLocked} tài khoản bị khóa/lỗi)."
                            : "✅ Không có tài khoản nào bị hết hạn!";
                        return;
                    }
                    if (skippedLocked > 0)
                    {
                        StatusMessage = $"⚡ Đang Re-login {expired.Count} tài khoản hết hạn (bỏ qua {skippedLocked} tài khoản bị khóa/lỗi)...";
                    }
                    if (expired.Count == 1)
                        AppServices.Dialogs.OpenFastLoginDialog(expired[0]);
                    else
                        AppServices.Dialogs.OpenFastLoginDialogBatch(expired);
                }
                catch (Exception ex)
                {
                    StatusMessage = "❌ Lỗi khi Re-login hàng loạt: " + ex.Message;
                }
            });

            InitializeAccountCommands();

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
                        StatusMessage = $"🔴 Đã đóng {killed} tiến trình Roblox!";
                        RefreshLiveProcessStatus();
                    });
                });
            });
            CleanSystemTrayCommand = new RelayCommand(() =>
            {
                Task.Run(() => Utilities.ThoroughCleanNotificationArea());
                StatusMessage = "🧹 Đã làm sạch toàn bộ khay hệ thống Windows!";
                ToastService.ShowSuccess("Khay hệ thống", "Đã làm sạch khay hệ thống, loại bỏ icon Roblox ma!");
            });
            SelectRecentGameCommand = new RelayCommand(p =>
            {
                if (p is RecentGameItemViewModel item)
                {
                    PlaceId = item.PlaceId.ToString();
                    AccountManager.CurrentPlaceId = PlaceId;
                    AccountManager.General?.Set("SavedPlaceId", PlaceId);
                    AccountManager.QueueDeferredSaveSettings();
                    CurrentPage = NavigationPage.Accounts;
                    StatusMessage = $"🎮 Đã chọn: {item.Name} (Place ID: {item.PlaceId})";
                }
            });
            LaunchRecentGameCommand = new RelayCommand(p =>
            {
                if (p is RecentGameItemViewModel item)
                {
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
                        StatusMessage = $"🎮 Đã chọn {item.Name} (Place ID: {item.PlaceId}). Hãy tick chọn tài khoản rồi nhấn 'Tham gia Server'!";
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
