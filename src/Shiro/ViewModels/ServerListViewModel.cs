#nullable enable
using System;
using System.ComponentModel;
using System.Threading;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class ServerListViewModel : ObservableObject, IDisposable
    {
        private readonly MainViewModel? _mainViewModel;
        private readonly IAppLogger _logger;
        private readonly ISettingsService _settings;
        private readonly ServerWatcherConfigService _watcherConfigService;

        private string _placeIdInput = string.Empty;
        private string _loadingStatus = LocalizationService.Get("Status_Ready");
        private string _lastDefaultReadyStatus = LocalizationService.Get("Status_Ready");
        private int _selectedSubTabIndex;
        private int _disposed;
        private Action<AppLanguage>? _languageChangedHandler;
        private Action<ServerWatcherConfig>? _watcherConfigChangedHandler;
        private PropertyChangedEventHandler? _browserPropHandler;
        private PropertyChangedEventHandler? _explorerPropHandler;
        private PropertyChangedEventHandler? _libraryPropHandler;

        public ServerListViewModel(MainViewModel? mainViewModel = null, IAppLogger? logger = null, ISettingsService? settings = null)
        {
            _mainViewModel = mainViewModel;
            _logger = logger ?? AppServices.Logger;
            _settings = settings ?? AppServices.GeneralSettings;
            _watcherConfigService = AppServices.ServerWatcherConfig;
            _placeIdInput = mainViewModel?.PlaceId ?? string.Empty;

            BrowserTab = new ServerBrowserTabViewModel(
                _mainViewModel,
                _logger,
                getPlaceIdInput: () => PlaceIdInput,
                syncPlaceIdInput: SyncPlaceId,
                setLoadingStatus: s => LoadingStatus = s,
                notifyCancelState: () => OnPropertyChanged(nameof(CanCancelOperation)));

            ExplorerTab = new GameExplorerTabViewModel(
                _mainViewModel,
                _logger,
                setPlaceIdInput: pid => PlaceIdInput = pid,
                setLoadingStatus: s => LoadingStatus = s,
                setSelectedSubTabIndex: idx => SelectedSubTabIndex = idx,
                refreshServersAsync: () => BrowserTab.RefreshServersAsync());

            LibraryTab = new GameLibraryTabViewModel(this, _mainViewModel, _logger);

            CancelOperationCommand = new RelayCommand(CancelOperation);

            AttachChildPropertyForwarding();
            LoadWatcherSettings();

            _languageChangedHandler = WeakEventSubscription.CreateAction<ServerListViewModel, AppLanguage>(
                this,
                static (vm, lang) => vm.OnLanguageChanged(lang),
                static h => LocalizationService.LanguageChanged -= h);
            LocalizationService.LanguageChanged += _languageChangedHandler;
        }

        public ServerBrowserTabViewModel BrowserTab { get; }
        public GameExplorerTabViewModel ExplorerTab { get; }
        public GameLibraryTabViewModel LibraryTab { get; }
        public ICommand CancelOperationCommand { get; }

        public string PlaceIdInput
        {
            get => _placeIdInput;
            set
            {
                if (SetProperty(ref _placeIdInput, value) &&
                    _mainViewModel != null &&
                    !string.IsNullOrWhiteSpace(value) &&
                    _mainViewModel.PlaceId != value)
                {
                    _mainViewModel.PlaceId = value;
                }
            }
        }

        public string LoadingStatus
        {
            get => _loadingStatus;
            set => SetProperty(ref _loadingStatus, value);
        }

        public int SelectedSubTabIndex
        {
            get => _selectedSubTabIndex;
            set
            {
                if (SetProperty(ref _selectedSubTabIndex, value))
                {
                    if (value == 0) BrowserTab.EnsureServersLoadedForCurrentPlaceId();
                    else if (value == 1 && ExplorerTab.SearchGames.Count == 0 && !ExplorerTab.IsSearchingGames)
                        _ = ExplorerTab.SearchGamesAsync();
                    else if (value == 2)
                    {
                        _mainViewModel?.ReloadRecentGames();
                        LibraryTab.NotifyRecentGamesChanged();
                    }
                }
            }
        }

        public bool CanCancelOperation => BrowserTab.IsLoadingServers || BrowserTab.IsLoadingRegions || BrowserTab.IsSearchingPlayer;
        public string CurrentGameName => _mainViewModel?.CurrentGameName ?? string.Empty;
        public Bitmap? CurrentGameThumbnail => _mainViewModel?.CurrentGameThumbnail;
        public bool HasCurrentGameInfo => _mainViewModel?.HasCurrentGameInfo == true;

        public void SyncPlaceId(string pid)
        {
            if (!string.IsNullOrWhiteSpace(pid) && _placeIdInput != pid)
            {
                _placeIdInput = pid;
                OnPropertyChanged(nameof(PlaceIdInput));
            }
        }

        public void NotifyCurrentGameChanged()
        {
            OnPropertyChanged(nameof(CurrentGameName));
            OnPropertyChanged(nameof(CurrentGameThumbnail));
            OnPropertyChanged(nameof(HasCurrentGameInfo));
        }

        public void CancelOperation()
        {
            BrowserTab.CancelOperations();
            ExplorerTab.CancelSearch();
            LoadingStatus = LocalizationService.Get("ServerList.OperationCancelled");
            OnPropertyChanged(nameof(CanCancelOperation));
        }

        private void AttachChildPropertyForwarding()
        {
            _browserPropHandler = (_, e) => { if (Volatile.Read(ref _disposed) == 0 && !string.IsNullOrEmpty(e.PropertyName)) OnPropertyChanged(e.PropertyName); };
            _explorerPropHandler = (_, e) => { if (Volatile.Read(ref _disposed) == 0 && !string.IsNullOrEmpty(e.PropertyName)) OnPropertyChanged(e.PropertyName); };
            _libraryPropHandler = (_, e) => { if (Volatile.Read(ref _disposed) == 0 && !string.IsNullOrEmpty(e.PropertyName)) OnPropertyChanged(e.PropertyName); };

            BrowserTab.PropertyChanged += _browserPropHandler;
            ExplorerTab.PropertyChanged += _explorerPropHandler;
            LibraryTab.PropertyChanged += _libraryPropHandler;
        }

        private void DetachChildPropertyForwarding()
        {
            if (_browserPropHandler != null) BrowserTab.PropertyChanged -= _browserPropHandler;
            if (_explorerPropHandler != null) ExplorerTab.PropertyChanged -= _explorerPropHandler;
            if (_libraryPropHandler != null) LibraryTab.PropertyChanged -= _libraryPropHandler;
            _browserPropHandler = null;
            _explorerPropHandler = null;
            _libraryPropHandler = null;
        }

        private void OnLanguageChanged(AppLanguage lang)
        {
            AppDispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                string localizedReady = LocalizationService.Get("Status_Ready");
                if (string.IsNullOrWhiteSpace(_loadingStatus)
                    || string.Equals(_loadingStatus, _lastDefaultReadyStatus, StringComparison.Ordinal)
                    || string.Equals(_loadingStatus, localizedReady, StringComparison.Ordinal))
                {
                    _lastDefaultReadyStatus = localizedReady;
                    LoadingStatus = localizedReady;
                }
                BrowserTab.NotifyLanguageChanged();
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (_languageChangedHandler != null)
                LocalizationService.LanguageChanged -= _languageChangedHandler;
            if (_watcherConfigChangedHandler != null)
                _watcherConfigService.ConfigChanged -= _watcherConfigChangedHandler;
            DetachChildPropertyForwarding();
            BrowserTab.Dispose();
            ExplorerTab.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
