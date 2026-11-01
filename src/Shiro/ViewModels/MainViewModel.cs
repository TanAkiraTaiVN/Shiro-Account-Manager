#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public enum NavigationPage
    {
        Accounts,
        ServerList,
        RecentGames,
        Settings
    }

    public partial class MainViewModel : ObservableObject
    {
        private readonly BulkObservableCollection<AccountViewModel> _accounts = new BulkObservableCollection<AccountViewModel>();
        private readonly BulkObservableCollection<AccountViewModel> _filteredAccounts = new BulkObservableCollection<AccountViewModel>();
        private readonly BulkObservableCollection<AccountRowViewModel> _filteredAccountRows = new BulkObservableCollection<AccountRowViewModel>();
        private readonly ObservableCollection<string> _availableGroups = new ObservableCollection<string>();
        private readonly Dictionary<Account, AccountViewModel> _vmByModel = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<long, AccountViewModel> _vmByUserId = new();
        private readonly Dictionary<string, AccountViewModel> _vmByUsername = new(StringComparer.OrdinalIgnoreCase);

        private NavigationPage _currentPage = NavigationPage.Accounts;
        private string _statusMessage = string.Empty;
        private string _placeId = string.Empty;
        private string _jobId = string.Empty;
        private bool _isPotatoMode;
        private bool _isAntiAfk;
        private bool _areWindowsHidden;
        private int _lastRobloxProcessCount = -1;

        public NavigationPage CurrentPage
        {
            get => _currentPage;
            set
            {
                if (SetProperty(ref _currentPage, value))
                {
                    OnPropertyChanged(nameof(IsAccountsActive));
                    OnPropertyChanged(nameof(IsServerListActive));
                    OnPropertyChanged(nameof(IsRecentGamesActive));
                    OnPropertyChanged(nameof(IsSettingsActive));
                    OnPageNavigated?.Invoke(value);
                }
            }
        }

        public bool IsAccountsActive => CurrentPage == NavigationPage.Accounts;
        public bool IsServerListActive => CurrentPage == NavigationPage.ServerList;
        public bool IsRecentGamesActive => CurrentPage == NavigationPage.RecentGames;
        public bool IsSettingsActive => CurrentPage == NavigationPage.Settings;

        // Callback when navigation page changes
        public Action<NavigationPage>? OnPageNavigated { get; set; }

        public ObservableCollection<AccountViewModel> Accounts => _accounts;
        public ObservableCollection<AccountViewModel> FilteredAccounts => _filteredAccounts;
        public ObservableCollection<AccountRowViewModel> FilteredAccountRows => _filteredAccountRows;
        public ObservableCollection<string> AvailableGroups => _availableGroups;

        public ServerListViewModel ServerListViewModel { get; private set; }

        public MainViewModel()
        {
            string? savedPid = AccountManager.General?.Get("SavedPlaceId");
            if (!string.IsNullOrWhiteSpace(savedPid))
            {
                _placeId = savedPid.Trim();
                AccountManager.CurrentPlaceId = _placeId;
            }
            else if (!string.IsNullOrWhiteSpace(AccountManager.CurrentPlaceId))
            {
                _placeId = AccountManager.CurrentPlaceId;
            }
            else
            {
                _placeId = string.Empty;
            }
            _statusMessage = Loc.Get("Status_ReadyDefault");
            LocalizationService.LanguageChanged += lang =>
            {
                OnPropertyChanged(nameof(CloseRobloxButtonText));
                OnPropertyChanged(nameof(CurrentSortLabel));
                NotifyLocalizedLabelsChanged();
                if (string.IsNullOrWhiteSpace(_statusMessage) || _statusMessage.Contains("Double-click"))
                {
                    StatusMessage = Loc.Get("Status_ReadyDefault");
                }
                if (Accounts != null)
                {
                    foreach (var acc in Accounts.ToArray())
                        acc.NotifyLanguageChanged();
                }
            };

            ServerListViewModel = new ServerListViewModel(this);
            InitializeCommands();
            RefreshStateFromSettings();
            ScheduleCurrentGameLookup(_placeId);
            RobloxAudioService.StatusChanged += OnAudioStatusChanged;
            AccentThemeService.ThemeChanged += OnAccentThemeChanged;
            LaunchCoordinator.LaunchProgressChanged += OnLaunchProgressChanged;
        }

        private void OnAudioStatusChanged()
        {
            AppDispatcher.Post(NotifyToolStatesChanged);
        }

        private void OnLaunchProgressChanged(LaunchProgress progress)
        {
            if (progress == null) return;
            AppDispatcher.Post(() =>
            {
                try
                {
                    UpdateBatchLaunchBanner(progress);
                    if (!string.IsNullOrWhiteSpace(progress.Message))
                    {
                        StatusMessage = progress.Message;
                    }

                    if (progress.Account != null)
                    {
                        var vm = TryGetAccountViewModel(progress.Account);
                        if (vm != null)
                        {
                            bool isActivelyLaunching = !progress.Completed &&
                                progress.Status != LaunchOutcome.Cancelled &&
                                progress.Status != LaunchOutcome.Failed &&
                                progress.Status != LaunchOutcome.TicketError;

                            vm.IsLaunching = isActivelyLaunching;
                        }
                    }
                    else if (progress.Completed)
                    {
                        foreach (var acc in _accounts)
                        {
                            acc.IsLaunching = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Clearing launching state on queue empty threw: {ex.Message}");
                }
            });
        }

        private void OnAccentThemeChanged(AccentTheme theme)
        {
            try
            {
                foreach (var acc in _accounts)
                {
                    acc.NotifyThemeChanged();
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"NotifyThemeChanged loop threw: {ex.Message}");
            }
        }

        public bool IsShuffleJobId
        {
            get => AccountManager.ShuffleJobID;
            set
            {
                if (AccountManager.ShuffleJobID != value)
                {
                    AccountManager.ShuffleJobID = value;
                    OnPropertyChanged();
                    StatusMessage = value
                        ? "Shuffle Job ID đã BẬT: Tham gia server ngẫu nhiên khi vào game."
                        : "Shuffle Job ID đã TẮT.";
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public string PlaceId
        {
            get => _placeId;
            set
            {
                if (SetProperty(ref _placeId, value))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        AccountManager.CurrentPlaceId = value.Trim();
                        AccountManager.General?.Set("SavedPlaceId", value.Trim());
                        AccountManager.QueueDeferredSaveSettings();
                    }
                    ServerListViewModel?.SyncPlaceId(value);
                    ScheduleCurrentGameLookup(value);
                }
            }
        }

        public string JobId
        {
            get => _jobId;
            set
            {
                if (SetProperty(ref _jobId, value))
                {
                    AccountManager.CurrentJobId = value?.Trim() ?? string.Empty;
                }
            }
        }

        private int _runningRobloxProcessCount;
        public int RunningRobloxProcessCount
        {
            get => _runningRobloxProcessCount;
            set
            {
                if (SetProperty(ref _runningRobloxProcessCount, value))
                {
                    OnPropertyChanged(nameof(CloseRobloxButtonText));
                    OnPropertyChanged(nameof(HasRunningRobloxProcesses));
                }
            }
        }

        public bool HasRunningRobloxProcesses => _runningRobloxProcessCount > 0;

        private bool _isCompactView;
        public bool IsCompactView
        {
            get => _isCompactView;
            set
            {
                if (SetProperty(ref _isCompactView, value))
                {
                    OnPropertyChanged(nameof(IsCardView));
                }
            }
        }

        public bool IsCardView => !IsCompactView;

        public AccountViewModel? TryGetAccountViewModel(Account? account)
        {
            if (account == null) return null;
            if (_vmByModel.TryGetValue(account, out var byModel))
                return byModel;
            if (account.UserID > 0 && _vmByUserId.TryGetValue(account.UserID, out var byId))
                return byId;
            if (!string.IsNullOrEmpty(account.Username) && _vmByUsername.TryGetValue(account.Username, out var byName))
                return byName;
            return null;
        }

        private void RebuildLookupIndex()
        {
            _vmByModel.Clear();
            _vmByUserId.Clear();
            _vmByUsername.Clear();

            for (int i = 0; i < _accounts.Count; i++)
            {
                var vm = _accounts[i];
                if (vm.Model != null)
                    _vmByModel.TryAdd(vm.Model, vm);
                if (vm.UserId > 0)
                    _vmByUserId.TryAdd(vm.UserId, vm);
                if (!string.IsNullOrEmpty(vm.Username))
                    _vmByUsername.TryAdd(vm.Username, vm);
            }
        }

        public void LoadAccounts(IEnumerable<Account> sourceAccounts)
        {
            if (Avalonia.Application.Current != null && Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                AppDispatcher.Post(() => LoadAccounts(sourceAccounts));
                return;
            }

            if (sourceAccounts == null)
            {
                _accounts.Clear();
                RebuildLookupIndex();
                RebuildGroupList();
                ApplyFilter();
                RefreshLiveProcessStatus();
                return;
            }

            var existingByRef = new Dictionary<Account, AccountViewModel>();
            var existingById = new Dictionary<long, AccountViewModel>();
            var existingByName = new Dictionary<string, AccountViewModel>(StringComparer.OrdinalIgnoreCase);

            foreach (var existing in _accounts)
            {
                if (existing.Model != null && !existingByRef.ContainsKey(existing.Model))
                    existingByRef[existing.Model] = existing;
                if (existing.UserId > 0 && !existingById.ContainsKey(existing.UserId))
                    existingById[existing.UserId] = existing;
                if (!string.IsNullOrEmpty(existing.Username) && !existingByName.ContainsKey(existing.Username))
                    existingByName[existing.Username] = existing;
            }

            var claimed = new HashSet<AccountViewModel>();
            var list = new List<AccountViewModel>();

            foreach (var acc in sourceAccounts)
            {
                if (acc == null) continue;

                AccountViewModel? vm = null;
                if (existingByRef.TryGetValue(acc, out var byRef) && claimed.Add(byRef))
                {
                    vm = byRef;
                }
                else if (acc.UserID > 0 && existingById.TryGetValue(acc.UserID, out var byId) && claimed.Add(byId))
                {
                    vm = byId;
                }
                else if (!string.IsNullOrEmpty(acc.Username) && existingByName.TryGetValue(acc.Username, out var byName) && claimed.Add(byName))
                {
                    vm = byName;
                }

                if (vm != null)
                {
                    vm.UpdateModel(acc);
                }
                else
                {
                    vm = new AccountViewModel(acc);
                    vm.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(AccountViewModel.IsSelected))
                            NotifyCounts();
                    };
                }

                list.Add(vm);
            }

            _accounts.ReplaceAll(list);
            RebuildLookupIndex();

            // Warm up avatar cache for any accounts that don't have local disk cache (only when UI app is active)
            if (Avalonia.Application.Current != null)
            {
                var missingIds = list.Where(a => a.UserId > 0 && !Services.AvatarService.HasDiskCache(a.UserId)).Select(a => a.UserId).ToList();
                if (missingIds.Count > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var updatedIds = await Services.AvatarService.BatchEnsureAvatarsAsync(missingIds).ConfigureAwait(false);
                            if (updatedIds != null && updatedIds.Count > 0)
                            {
                                var updatedSet = new HashSet<long>(updatedIds);
                                AppDispatcher.Post(() =>
                                {
                                    foreach (var acc in _accounts)
                                    {
                                        if (updatedSet.Contains(acc.UserId))
                                        {
                                            acc.InvalidateAvatar();
                                        }
                                    }
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"[LoadAccounts] Avatar warmup error: {ex.Message}");
                        }
                    });
                }
            }

            RebuildGroupList(applyFilterOnReset: false);
            ApplyFilter();
            RefreshLiveProcessStatus();
        }
    }
}
