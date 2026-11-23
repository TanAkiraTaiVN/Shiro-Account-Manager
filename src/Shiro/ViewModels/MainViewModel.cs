#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public enum NavigationPage
    {
        Accounts,
        ServerList,
        RecentGames,
        Settings,
        About
    }

    public partial class MainViewModel : ObservableObject, IDisposable, IAccountWorkflowHost
    {
        private readonly IAppLogger _logger;
        private readonly ISettingsService _settings;
        private readonly IAccountRepository _accountRepository;
        private NavigationPage _currentPage = NavigationPage.Accounts;
        private bool _isNotificationCenterOpen;
        private int _unreadNotificationCount;
        private int _disposed;
        private Action<AppLanguage>? _languageChangedHandler;
        private Action<AccentTheme>? _themeChangedHandler;
        private PropertyChangedEventHandler? _accountsPropHandler;
        private PropertyChangedEventHandler? _launchBarPropHandler;
        private PropertyChangedEventHandler? _toolsPropHandler;
        private PropertyChangedEventHandler? _palettePropHandler;

        public MainViewModel() : this(null, null, null) { }

        public MainViewModel(IAppLogger? logger = null, ISettingsService? settings = null, IAccountRepository? accountRepository = null)
        {
            _logger = logger ?? AppServices.Logger;
            _settings = settings ?? AppServices.GeneralSettings!;
            _accountRepository = accountRepository ?? AppServices.AccountRepository;

            ToolsStatus = new ToolsStatusViewModel(
                _logger,
                _settings,
                getSelectedCount: () => AccountsVm?.SelectedCount ?? 0,
                getAccountModelsSnapshot: () => AccountsVm?.GetAccountModelsSnapshot(),
                applyLiveProcessStatuses: map => AccountsVm?.ApplyLiveProcessStatuses(map),
                refreshDisplays: RefreshAllAccountDisplays);

            AccountsVm = new AccountCollectionViewModel(
                _logger,
                _settings,
                _accountRepository,
                setStatus: msg => ToolsStatus.StatusMessage = msg,
                getPlaceId: () => LaunchBar?.PlaceId ?? string.Empty,
                getJobId: () => LaunchBar?.JobId ?? string.Empty,
                onSelectionChanged: () =>
                {
                    LaunchBar?.NotifySelectionChanged();
                    ToolsStatus.NotifySelectionChanged();
                },
                onAccountsLoaded: () => ToolsStatus.RefreshLiveProcessStatus());

            LaunchBar = new LaunchBarViewModel(
                _logger,
                _settings,
                getAccounts: () => AccountsVm.Accounts,
                getSelectedCount: () => AccountsVm.SelectedCount,
                setStatus: msg => ToolsStatus.StatusMessage = msg,
                refreshLiveProcessStatus: () => ToolsStatus.RefreshLiveProcessStatus(),
                navigateToPage: page => CurrentPage = page,
                syncPlaceIdToServerList: pid => ServerListViewModel?.SyncPlaceId(pid),
                getFavorites: () => ServerListViewModel?.LibraryTab.Favorites,
                notifyCurrentGameChanged: () => ServerListViewModel?.NotifyCurrentGameChanged(),
                notifyRecentGamesChanged: () => ServerListViewModel?.NotifyRecentGamesChanged(),
                clearLaunchingFlags: () => AccountsVm.ClearLaunchingFlags(),
                tryGetAccountVm: acc => AccountsVm.TryGetAccountViewModel(acc));

            CommandPalette = new CommandPaletteViewModel(this);
            ServerListViewModel = new ServerListViewModel(this, _logger, _settings);

            NavigateToAccountsCommand = new RelayCommand(() => CurrentPage = NavigationPage.Accounts);
            NavigateToServerListCommand = new RelayCommand(() => CurrentPage = NavigationPage.ServerList);
            NavigateToRecentGamesCommand = new RelayCommand(() => { CurrentPage = NavigationPage.RecentGames; LaunchBar.ReloadRecentGames(); });
            NavigateToSettingsCommand = new RelayCommand(() => CurrentPage = NavigationPage.Settings);

            AttachChildPropertyForwarding();
            ToolsStatus.RefreshStateFromSettings();
            LaunchBar.ScheduleCurrentGameLookup(LaunchBar.PlaceId);
            LaunchBar.ReloadRecentGames();
            SubscribeWeakEvents();
        }

        public AccountCollectionViewModel AccountsVm { get; }
        public LaunchBarViewModel LaunchBar { get; }
        public ToolsStatusViewModel ToolsStatus { get; }
        public CommandPaletteViewModel CommandPalette { get; }
        public ServerListViewModel ServerListViewModel { get; private set; }

        public ICommand NavigateToAccountsCommand { get; }
        public ICommand NavigateToServerListCommand { get; }
        public ICommand NavigateToRecentGamesCommand { get; }
        public ICommand NavigateToSettingsCommand { get; }

        public Action<NavigationPage>? OnPageNavigated { get; set; }

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
                    OnPropertyChanged(nameof(IsAboutActive));
                    OnPageNavigated?.Invoke(value);
                }
            }
        }

        public bool IsAccountsActive => CurrentPage == NavigationPage.Accounts;
        public bool IsServerListActive => CurrentPage == NavigationPage.ServerList;
        public bool IsRecentGamesActive => CurrentPage == NavigationPage.RecentGames;
        public bool IsSettingsActive => CurrentPage == NavigationPage.Settings;
        public bool IsAboutActive => CurrentPage == NavigationPage.About;

        public bool IsNotificationCenterOpen
        {
            get => _isNotificationCenterOpen;
            set => SetProperty(ref _isNotificationCenterOpen, value);
        }

        public int UnreadNotificationCount
        {
            get => _unreadNotificationCount;
            set
            {
                int normalized = Math.Max(0, value);
                if (SetProperty(ref _unreadNotificationCount, normalized))
                {
                    OnPropertyChanged(nameof(HasUnreadNotifications));
                }
            }
        }

        public bool HasUnreadNotifications => _unreadNotificationCount > 0;

        public void RefreshAllAccountDisplays()
        {
            AccountsVm.NotifyDisplayAndRobuxChanged();
            ServerListViewModel?.BrowserTab.NotifyStreamerModeChanged();
        }

        private void AttachChildPropertyForwarding()
        {
            _accountsPropHandler = (_, e) => { if (Volatile.Read(ref _disposed) == 0 && !string.IsNullOrEmpty(e.PropertyName)) OnPropertyChanged(e.PropertyName); };
            _launchBarPropHandler = (_, e) => { if (Volatile.Read(ref _disposed) == 0 && !string.IsNullOrEmpty(e.PropertyName)) OnPropertyChanged(e.PropertyName); };
            _toolsPropHandler = (_, e) => { if (Volatile.Read(ref _disposed) == 0 && !string.IsNullOrEmpty(e.PropertyName)) OnPropertyChanged(e.PropertyName); };
            _palettePropHandler = (_, e) => { if (Volatile.Read(ref _disposed) == 0 && !string.IsNullOrEmpty(e.PropertyName)) OnPropertyChanged(e.PropertyName); };

            AccountsVm.PropertyChanged += _accountsPropHandler;
            LaunchBar.PropertyChanged += _launchBarPropHandler;
            ToolsStatus.PropertyChanged += _toolsPropHandler;
            CommandPalette.PropertyChanged += _palettePropHandler;
        }

        private void DetachChildPropertyForwarding()
        {
            if (_accountsPropHandler != null) AccountsVm.PropertyChanged -= _accountsPropHandler;
            if (_launchBarPropHandler != null) LaunchBar.PropertyChanged -= _launchBarPropHandler;
            if (_toolsPropHandler != null) ToolsStatus.PropertyChanged -= _toolsPropHandler;
            if (_palettePropHandler != null) CommandPalette.PropertyChanged -= _palettePropHandler;
            _accountsPropHandler = null;
            _launchBarPropHandler = null;
            _toolsPropHandler = null;
            _palettePropHandler = null;
        }

        private void SubscribeWeakEvents()
        {
            _languageChangedHandler = WeakEventSubscription.CreateAction<MainViewModel, AppLanguage>(
                this,
                static (vm, _) => vm.OnLanguageChanged(),
                static h => LocalizationService.LanguageChanged -= h);
            LocalizationService.LanguageChanged += _languageChangedHandler;

            _themeChangedHandler = WeakEventSubscription.CreateAction<MainViewModel, AccentTheme>(
                this,
                static (vm, _) => vm.OnAccentThemeChanged(),
                static h => AccentThemeService.ThemeChanged -= h);
            AccentThemeService.ThemeChanged += _themeChangedHandler;
        }

        private void OnLanguageChanged()
        {
            AppDispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                AccountsVm.NotifyLanguageChanged();
                LaunchBar.NotifyLanguageChanged();
                ToolsStatus.NotifyLanguageChanged();
            });
        }

        private void OnAccentThemeChanged()
        {
            AppDispatcher.Post(() =>
            {
                try
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    AccountsVm.NotifyThemeChanged();
                }
                catch (Exception ex) { _logger.Debug($"NotifyThemeChanged loop threw: {ex.Message}"); }
            });
        }

        private static IReadOnlyList<CommandPaletteCatalog.BuiltinCommandDef> GetBuiltinCommands() =>
            CommandPaletteCatalog.GetBuiltinCommands();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (_languageChangedHandler != null) LocalizationService.LanguageChanged -= _languageChangedHandler;
            if (_themeChangedHandler != null) AccentThemeService.ThemeChanged -= _themeChangedHandler;
            DetachChildPropertyForwarding();
            OnPageNavigated = null;
            AccountsVm.Dispose();
            LaunchBar.Dispose();
            ToolsStatus.Dispose();
            ServerListViewModel?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
