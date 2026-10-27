using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.ViewModels
{
    public partial class MainViewModel
    {
        // Reference to optional host callbacks for modal popups and launches (used by tests or custom overrides)
        public Action<AccountViewModel> OnLaunchRequested { get; set; }
        public Action<AccountViewModel> OnOpenHomeRequested { get; set; }
        public Action<IList<AccountViewModel>> OnMultiLaunchRequested { get; set; }
        public Action<IList<AccountViewModel>> OnJoinServerSelectedRequested { get; set; }
        public Action<AccountViewModel> OnJoinServerSingleRequested { get; set; }
        public Action OnAddAccountRequested { get; set; }
        public Action OnAddAccountChromeRequested { get; set; }
        public Action OnAddAccountFastLoginRequested { get; set; }
        public Action OnAddAccountCookieRequested { get; set; }
        public Action OnBulkImportRequested { get; set; }
        public Action OnSettingsRequested { get; set; }
        public Action OnServerListRequested { get; set; }
        public Action OnRecentGamesRequested { get; set; }
        public Action OnCheckSessionsRequested { get; set; }
        public Action OnReLoginAllExpiredRequested { get; set; }
        public Action OnKillAllRequested { get; set; }
        public Action OnProxyManagerRequested { get; set; }
        public Action OnNexusRequested { get; set; }

        // Card Level Callbacks
        public Action<AccountViewModel> OnFastReLoginRequested { get; set; }
        public Action<AccountViewModel> OnReLoginRequested { get; set; }
        public Action<AccountViewModel> OnReLoginWithCookieRequested { get; set; }
        public Action<AccountViewModel> OnValidateSessionRequested { get; set; }
        public Action<AccountViewModel> OnOpenProfileRequested { get; set; }
        public Action<AccountViewModel> OnSetAliasRequested { get; set; }
        public Action<AccountViewModel> OnSetGroupRequested { get; set; }
        public Action<AccountViewModel> OnSetDescriptionRequested { get; set; }
        public Action<AccountViewModel> OnTogglePinRequested { get; set; }
        public Action<AccountViewModel> OnCopyUsernameRequested { get; set; }
        public Action<AccountViewModel> OnCopyUserPassRequested { get; set; }
        public Action<AccountViewModel> OnCopyUserIdRequested { get; set; }
        public Action<AccountViewModel> OnCopyProfileUrlRequested { get; set; }
        public Action<AccountViewModel> OnCopySecurityTokenRequested { get; set; }
        public Action<AccountViewModel> OnSavePlaceIdToAccountRequested { get; set; }
        public Action<AccountViewModel> OnViewFieldsRequested { get; set; }
        public Action<AccountViewModel> OnOpenUtilsRequested { get; set; }
        public Action OnOpenAboutRequested { get; set; }
        public Action<AccountViewModel> OnRemoveAccountRequested { get; set; }

        // Batch Level Callbacks
        public Action<IList<AccountViewModel>> OnReLoginSelectedRequested { get; set; }
        public Action<IList<AccountViewModel>> OnValidateSessionSelectedRequested { get; set; }
        public Action OnSetGroupSelectedRequested { get; set; }
        public Action OnSavePlaceIdSelectedRequested { get; set; }
        public Action OnCopyUserPassSelectedRequested { get; set; }
        public Action OnRemoveSelectedRequested { get; set; }

        #region Commands
        public ICommand ToggleViewModeCommand { get; private set; }
        public ICommand ToggleCommandPaletteCommand { get; private set; }
        public ICommand OpenCommandPaletteCommand { get; private set; }
        public ICommand CloseCommandPaletteCommand { get; private set; }
        public ICommand ChangeAccentThemeCommand { get; private set; }
        public ICommand LaunchAccountCommand { get; private set; }
        public ICommand OpenHomeCommand { get; private set; }
        public ICommand JoinServerAccountCommand { get; private set; }
        public ICommand LaunchSelectedCommand { get; private set; }
        public ICommand JoinServerSelectedCommand { get; private set; }
        public ICommand AntiLagBoosterCommand { get; private set; }
        public ICommand ToggleWindowsCommand { get; private set; }
        public ICommand ToggleAntiAfkCommand { get; private set; }
        public ICommand TogglePotatoModeCommand { get; private set; }
        public ICommand SelectAllCommand { get; private set; }
        public ICommand ClearSelectionCommand { get; private set; }
        public ICommand ClearSearchCommand { get; private set; }
        public ICommand RefreshPresenceCommand { get; private set; }
        public ICommand CheckSessionsCommand { get; private set; }
        public ICommand ReLoginAllExpiredCommand { get; private set; }
        public ICommand AddAccountCommand { get; private set; }
        public ICommand AddAccountChromeCommand { get; private set; }
        public ICommand AddAccountFastLoginCommand { get; private set; }
        public ICommand AddAccountCookieCommand { get; private set; }
        public ICommand BulkImportCommand { get; private set; }
        public ICommand OpenSettingsCommand { get; private set; }
        public ICommand OpenServerListCommand { get; private set; }
        public ICommand OpenRecentGamesCommand { get; private set; }
        public ICommand NavigateToAccountsCommand { get; private set; }
        public ICommand NavigateToServerListCommand { get; private set; }
        public ICommand NavigateToRecentGamesCommand { get; private set; }
        public ICommand NavigateToSettingsCommand { get; private set; }
        public ICommand ToggleShuffleCommand { get; private set; }
        public ICommand KillAllClientsCommand { get; private set; }
        public ICommand SetFilterTabCommand { get; private set; }
        public ICommand SetSortModeCommand { get; private set; }
        public ICommand SelectRecentGameCommand { get; private set; }
        public ICommand LaunchRecentGameCommand { get; private set; }
        public ICommand RefreshRecentGamesCommand { get; private set; }
        public ICommand TileWindowsCommand { get; private set; }
        public ICommand RefreshAllRobuxCommand { get; private set; }
        public ICommand CheckRobuxAccountCommand { get; private set; }
        public ICommand OpenProxyManagerCommand { get; private set; }
        public ICommand OpenNexusCommand { get; private set; }
        public ICommand CleanSystemTrayCommand { get; private set; }

        // Card Level Commands
        public ICommand FastReLoginAccountCommand { get; private set; }
        public ICommand ReLoginAccountCommand { get; private set; }
        public ICommand ReLoginWithCookieCommand { get; private set; }
        public ICommand ValidateSessionCommand { get; private set; }
        public ICommand OpenProfileCommand { get; private set; }
        public ICommand SetAliasCommand { get; private set; }
        public ICommand SetGroupCommand { get; private set; }
        public ICommand SetDescriptionCommand { get; private set; }
        public ICommand TogglePinCommand { get; private set; }
        public ICommand CopyUsernameCommand { get; private set; }
        public ICommand CopyUserPassCommand { get; private set; }
        public ICommand CopyUserIdCommand { get; private set; }
        public ICommand CopyProfileUrlCommand { get; private set; }
        public ICommand CopySecurityTokenCommand { get; private set; }
        public ICommand SavePlaceIdToAccountCommand { get; private set; }
        public ICommand ViewFieldsCommand { get; private set; }
        public ICommand OpenUtilsCommand { get; private set; }
        public ICommand OpenAboutDialogCommand { get; private set; }
        public ICommand RemoveAccountCommand { get; private set; }

        // Batch Level Commands
        public ICommand SetGroupSelectedCommand { get; private set; }
        public ICommand SavePlaceIdSelectedCommand { get; private set; }
        public ICommand CopyUserPassSelectedCommand { get; private set; }
        public ICommand RemoveSelectedCommand { get; private set; }
        public ICommand ReLoginSelectedCommand { get; private set; }
        public ICommand ValidateSessionSelectedCommand { get; private set; }

        private void PostStatusMessage(string message)
        {
            AppDispatcher.Post(() => StatusMessage = message);
        }

        private void ExecuteLaunchSingleHome(AccountViewModel acc)
        {
            if (acc?.Model != null)
            {
                _ = Task.Run(async () => await AccountLauncherService.LaunchAppHomeAsync(acc.Model).ConfigureAwait(false));
            }
        }

        private void ExecuteJoinServerForAccounts(IList<AccountViewModel> list)
        {
            if (list == null || list.Count == 0)
                return;

            string placeIdStr = PlaceId?.Trim();
            long placeId = AccountLauncherService.ParsePlaceId(placeIdStr);
            if (placeId > 0)
            {
                var models = list.Where(a => a?.Model != null).Select(a => a.Model).ToList();
                if (models.Count > 0)
                {
                    string jobId = JobId?.Trim() ?? string.Empty;
                    _ = Task.Run(async () => await AccountLauncherService.JoinServerAsync(models, placeId, jobId).ConfigureAwait(false));
                }
            }
            else
            {
                StatusMessage = "⚠️ Vui lòng nhập Place ID hợp lệ trước khi vào Server!";
            }
        }

        private void InitializeCommands()
        {
            LaunchAccountCommand = new RelayCommand(param =>
            {
                if (param is AccountViewModel acc)
                {
                    if (OnLaunchRequested != null) OnLaunchRequested.Invoke(acc);
                    else ExecuteLaunchSingleHome(acc);
                }
            });

            OpenHomeCommand = new RelayCommand(param =>
            {
                if (param is AccountViewModel acc)
                {
                    if (OnOpenHomeRequested != null) OnOpenHomeRequested.Invoke(acc);
                    else if (OnLaunchRequested != null) OnLaunchRequested.Invoke(acc);
                    else ExecuteLaunchSingleHome(acc);
                }
            });

            JoinServerAccountCommand = new RelayCommand(param =>
            {
                if (param is AccountViewModel acc)
                {
                    if (OnJoinServerSingleRequested != null) OnJoinServerSingleRequested.Invoke(acc);
                    else ExecuteJoinServerForAccounts(new[] { acc });
                }
            });

            LaunchSelectedCommand = new RelayCommand(() =>
            {
                var selected = _accounts.Where(a => a.IsSelected).ToList();
                if (selected.Count == 0)
                {
                    StatusMessage = "⚠️ Vui lòng tick chọn ít nhất một tài khoản (hoặc bấm 'Chọn hết') trước khi mở Roblox Home (Launch Home)!";
                    return;
                }

                if (OnMultiLaunchRequested != null)
                {
                    OnMultiLaunchRequested.Invoke(selected);
                    return;
                }

                var models = selected.Where(a => a.Model != null).Select(a => a.Model).ToList();
                if (models.Count > 0)
                {
                    AccountManager.SelectedAccounts = models;
                    AccountLauncherService.MultiLaunchHome();
                }
            });

            JoinServerSelectedCommand = new RelayCommand(() =>
            {
                var selected = _accounts.Where(a => a.IsSelected).ToList();
                if (selected.Count == 0)
                {
                    StatusMessage = "⚠️ Vui lòng tick chọn ít nhất một tài khoản trước khi Vào Server!";
                    return;
                }

                if (OnJoinServerSelectedRequested != null) OnJoinServerSelectedRequested.Invoke(selected);
                else ExecuteJoinServerForAccounts(selected);
            });

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

            AddAccountCommand = new RelayCommand(() =>
            {
                if (OnAddAccountFastLoginRequested != null) OnAddAccountFastLoginRequested.Invoke();
                else AppServices.Dialogs.OpenFastLoginDialog(null);
            });
            AddAccountFastLoginCommand = AddAccountCommand;
            AddAccountChromeCommand = new RelayCommand(() => OnAddAccountChromeRequested?.Invoke());
            AddAccountCookieCommand = new RelayCommand(() => OnAddAccountCookieRequested?.Invoke());
            BulkImportCommand = new RelayCommand(() =>
            {
                if (OnBulkImportRequested != null) OnBulkImportRequested.Invoke();
                else AppServices.Dialogs.OpenBulkImportDialog();
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

            // Card Commands
            FastReLoginAccountCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnFastReLoginRequested != null) OnFastReLoginRequested.Invoke(a);
                    else if (a.Model != null) AppServices.Dialogs.OpenFastLoginDialog(a.Model);
                }
            });
            ReLoginAccountCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnReLoginRequested != null) OnReLoginRequested.Invoke(a);
                    else if (OnFastReLoginRequested != null) OnFastReLoginRequested.Invoke(a);
                    else if (a.Model != null) AppServices.Dialogs.OpenFastLoginDialog(a.Model);
                }
            });
            ReLoginWithCookieCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnReLoginWithCookieRequested != null) OnReLoginWithCookieRequested.Invoke(a);
                    else _ = Task.Run(async () => await AccountWorkflowService.Default.ReLoginWithCookieAsync(a, this, PostStatusMessage).ConfigureAwait(false));
                }
            });
            ValidateSessionCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnValidateSessionRequested != null)
                    {
                        OnValidateSessionRequested.Invoke(a);
                        return;
                    }
                    if (a.Model == null) return;
                    _ = Task.Run(async () =>
                    {
                        bool ok = await a.Model.ValidateSessionAsync().ConfigureAwait(false);
                        AccountManager.SaveAccounts(true);
                        UpdateAccountValidity(a.Model, ok);
                        PostStatusMessage(ok ? $"✅ {a.Username}: Phiên đăng nhập hợp lệ!" : $"⚠️ {a.Username}: Phiên đã hết hạn!");
                    });
                }
            });
            OpenProfileCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnOpenProfileRequested != null) OnOpenProfileRequested.Invoke(a);
                    else if (a.Model != null && a.UserId > 0) Utilities.OpenUrl($"https://www.roblox.com/users/{a.UserId}/profile");
                }
            });
            SetAliasCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnSetAliasRequested != null) OnSetAliasRequested.Invoke(a);
                    else AccountWorkflowService.Default.SetAlias(a, msg => StatusMessage = msg);
                }
            });
            SetGroupCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnSetGroupRequested != null) OnSetGroupRequested.Invoke(a);
                    else AccountWorkflowService.Default.SetGroup(a, this, msg => StatusMessage = msg);
                }
            });
            SetDescriptionCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnSetDescriptionRequested != null) OnSetDescriptionRequested.Invoke(a);
                    else AccountWorkflowService.Default.SetDescription(a, msg => StatusMessage = msg);
                }
            });
            TogglePinCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnTogglePinRequested != null) OnTogglePinRequested.Invoke(a);
                    else AccountWorkflowService.Default.TogglePin(a, this, msg => StatusMessage = msg);
                }
            });
            CopyUsernameCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnCopyUsernameRequested != null) OnCopyUsernameRequested.Invoke(a);
                    else AccountWorkflowService.Default.CopyUsername(a, msg => StatusMessage = msg);
                }
            });
            CopyUserPassCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnCopyUserPassRequested != null) OnCopyUserPassRequested.Invoke(a);
                    else AccountWorkflowService.Default.CopyUserPass(a, msg => StatusMessage = msg);
                }
            });
            CopyUserIdCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnCopyUserIdRequested != null) OnCopyUserIdRequested.Invoke(a);
                    else AccountWorkflowService.Default.CopyUserId(a, msg => StatusMessage = msg);
                }
            });
            CopyProfileUrlCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnCopyProfileUrlRequested != null) OnCopyProfileUrlRequested.Invoke(a);
                    else AccountWorkflowService.Default.CopyProfileUrl(a, msg => StatusMessage = msg);
                }
            });
            CopySecurityTokenCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnCopySecurityTokenRequested != null) OnCopySecurityTokenRequested.Invoke(a);
                    else AccountWorkflowService.Default.CopySecurityToken(a, msg => StatusMessage = msg);
                }
            });
            SavePlaceIdToAccountCommand = new RelayCommand(p => { if (p is AccountViewModel a) OnSavePlaceIdToAccountRequested?.Invoke(a); });
            ViewFieldsCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnViewFieldsRequested != null) OnViewFieldsRequested.Invoke(a);
                    else if (a.Model != null) AppServices.Dialogs.OpenAccountFieldsDialog(a.Model);
                }
            });
            OpenUtilsCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnOpenUtilsRequested != null) OnOpenUtilsRequested.Invoke(a);
                    else if (a.Model != null) AppServices.Dialogs.OpenAccountUtilsDialog(a.Model);
                }
            });
            OpenAboutDialogCommand = new RelayCommand(() =>
            {
                if (OnOpenAboutRequested != null) OnOpenAboutRequested.Invoke();
                else AppServices.Dialogs.OpenAboutDialog();
            });
            RemoveAccountCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnRemoveAccountRequested != null) OnRemoveAccountRequested.Invoke(a);
                    else AccountWorkflowService.Default.RemoveAccount(a, this, msg => StatusMessage = msg);
                }
            });

            // Batch Commands
            SetGroupSelectedCommand = new RelayCommand(() =>
            {
                if (OnSetGroupSelectedRequested != null) OnSetGroupSelectedRequested.Invoke();
                else AccountWorkflowService.Default.SetGroupSelected(this, PostStatusMessage);
            });
            SavePlaceIdSelectedCommand = new RelayCommand(() =>
            {
                if (OnSavePlaceIdSelectedRequested != null) OnSavePlaceIdSelectedRequested.Invoke();
                else AccountWorkflowService.Default.SavePlaceIdSelected(this, PostStatusMessage);
            });
            CopyUserPassSelectedCommand = new RelayCommand(() =>
            {
                if (OnCopyUserPassSelectedRequested != null) OnCopyUserPassSelectedRequested.Invoke();
                else AccountWorkflowService.Default.CopyUserPassSelected(this, PostStatusMessage);
            });
            RemoveSelectedCommand = new RelayCommand(() =>
            {
                if (OnRemoveSelectedRequested != null) OnRemoveSelectedRequested.Invoke();
                else AccountWorkflowService.Default.RemoveSelectedAccounts(this, PostStatusMessage);
            });
            ReLoginSelectedCommand = new RelayCommand(() =>
            {
                var selected = _accounts.Where(a => a.IsSelected).ToList();
                if (selected.Count == 0)
                {
                    StatusMessage = "⚠️ Vui lòng tick chọn ít nhất một tài khoản trước khi Re-login!";
                    return;
                }
                if (OnReLoginSelectedRequested != null)
                {
                    OnReLoginSelectedRequested.Invoke(selected);
                    return;
                }
                var models = selected.Where(a => a.Model != null).Select(a => a.Model).ToList();
                if (models.Count == 1)
                    AppServices.Dialogs.OpenFastLoginDialog(models[0]);
                else if (models.Count > 1)
                    AppServices.Dialogs.OpenFastLoginDialogBatch(models);
            });
            ValidateSessionSelectedCommand = new RelayCommand(() =>
            {
                var selected = _accounts.Where(a => a.IsSelected).ToList();
                if (selected.Count == 0)
                {
                    StatusMessage = "⚠️ Vui lòng tick chọn ít nhất một tài khoản trước khi kiểm tra phiên!";
                    return;
                }
                if (OnValidateSessionSelectedRequested != null) OnValidateSessionSelectedRequested.Invoke(selected);
                else AccountWorkflowService.Default.ValidateSessionSelected(selected, this, PostStatusMessage);
            });

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
