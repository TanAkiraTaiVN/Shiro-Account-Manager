#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Shiro.Services;

namespace Shiro.ViewModels
{
    /// <summary>
    /// Manages dashboard Place ID / Job ID inputs, active game chip lookup,
    /// recent games list, launch commands, and batch launch progress banner.
    /// </summary>
    public sealed partial class LaunchBarViewModel : ObservableObject, IDisposable
    {
        private readonly IAppLogger _logger;
        private readonly ISettingsService? _settings;
        private readonly Func<IReadOnlyList<AccountViewModel>> _getAccounts;
        private readonly Func<int> _getSelectedCount;
        private readonly Action<string> _setStatus;
        private readonly Action _refreshLiveProcessStatus;
        private readonly Action<NavigationPage> _navigateToPage;
        private readonly Action<string>? _syncPlaceIdToServerList;
        private readonly Func<IReadOnlyList<FavoriteGameItemViewModel>?>? _getFavorites;
        private readonly Action? _notifyCurrentGameChanged;
        private readonly Action? _notifyRecentGamesChanged;
        private readonly Action _clearLaunchingFlags;
        private readonly Func<Account?, AccountViewModel?>? _tryGetAccountVm;

        private string _placeId = string.Empty;
        private string _jobId = string.Empty;
        private string _currentGameName = string.Empty;
        private Bitmap? _currentGameThumbnail;
        private Color? _adaptiveGameColor;
        private IBrush _heroAuraBrush = Brushes.Transparent;
        private Bitmap? _lastExtractedThumbnail;
        private ObservableCollection<RecentGameItemViewModel> _recentGamesList = new();
        private CancellationTokenSource? _gameLookupCts;
        private CancellationTokenSource? _adaptiveTintCts;
        private long _gameLookupGeneration;
        private long _adaptiveColorGeneration;
        private int _ownsAdaptiveAccent;
        private bool _hasSeenNonEmptyPlaceId;
        private Task? _pendingAdaptiveColorTask;
        private bool _isBatchLaunching;
        private string _batchLaunchTitleText = string.Empty;
        private string _batchLaunchDetailText = string.Empty;
        private double _batchLaunchProgressPercent;
        private Action<LaunchProgress>? _launchProgressChangedHandler;
        private Action? _visualPreferencesChangedHandler;
        private int _disposed;

        public LaunchBarViewModel(
            IAppLogger? logger = null,
            ISettingsService? settings = null,
            Func<IReadOnlyList<AccountViewModel>>? getAccounts = null,
            Func<int>? getSelectedCount = null,
            Action<string>? setStatus = null,
            Action? refreshLiveProcessStatus = null,
            Action<NavigationPage>? navigateToPage = null,
            Action<string>? syncPlaceIdToServerList = null,
            Func<IReadOnlyList<FavoriteGameItemViewModel>?>? getFavorites = null,
            Action? notifyCurrentGameChanged = null,
            Action? notifyRecentGamesChanged = null,
            Action? clearLaunchingFlags = null,
            Func<Account?, AccountViewModel?>? tryGetAccountVm = null)
        {
            _logger = logger ?? AppServices.Logger;
            _settings = settings ?? AppServices.GeneralSettings;
            _getAccounts = getAccounts ?? Array.Empty<AccountViewModel>;
            _getSelectedCount = getSelectedCount ?? (() => _getAccounts().Count(a => a.IsSelected));
            _setStatus = setStatus ?? (_ => { });
            _refreshLiveProcessStatus = refreshLiveProcessStatus ?? (() => { });
            _navigateToPage = navigateToPage ?? (_ => { });
            _syncPlaceIdToServerList = syncPlaceIdToServerList;
            _getFavorites = getFavorites;
            _notifyCurrentGameChanged = notifyCurrentGameChanged;
            _notifyRecentGamesChanged = notifyRecentGamesChanged;
            _clearLaunchingFlags = clearLaunchingFlags ?? (() => { });
            _tryGetAccountVm = tryGetAccountVm;

            string? savedPid = _settings?.Get("SavedPlaceId");
            if (!string.IsNullOrWhiteSpace(savedPid))
            {
                _placeId = savedPid.Trim();
                AppServices.ServerBrowserState.CurrentPlaceId = _placeId;
            }
            else if (!string.IsNullOrWhiteSpace(AppServices.ServerBrowserState.CurrentPlaceId))
            {
                _placeId = AppServices.ServerBrowserState.CurrentPlaceId;
            }

            InitializeCommands();
            _launchProgressChangedHandler = WeakEventSubscription.CreateAction<LaunchBarViewModel, LaunchProgress>(
                this,
                static (vm, p) => vm.OnLaunchProgressChanged(p),
                static h => LaunchCoordinator.LaunchProgressChanged -= h);
            LaunchCoordinator.LaunchProgressChanged += _launchProgressChangedHandler;
            SubscribeAdaptiveTintEvents();
        }

        public Action<AccountViewModel>? OnLaunchRequested { get; set; }
        public Action<AccountViewModel>? OnOpenHomeRequested { get; set; }
        public Action<IList<AccountViewModel>>? OnMultiLaunchRequested { get; set; }
        public Action<IList<AccountViewModel>>? OnJoinServerSelectedRequested { get; set; }
        public Action<AccountViewModel>? OnFastReLoginRequested { get; set; }

        public string PlaceId
        {
            get => _placeId;
            set
            {
                if (SetProperty(ref _placeId, value))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _hasSeenNonEmptyPlaceId = true;
                        AppServices.ServerBrowserState.CurrentPlaceId = value.Trim();
                        _settings?.Set("SavedPlaceId", value.Trim());
                        AppServices.HostUiBridge.QueueDeferredSaveSettings();
                    }
                    OnPropertyChanged(nameof(CurrentPlaceIdLabel));
                    _syncPlaceIdToServerList?.Invoke(value);
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
                    AppServices.ServerBrowserState.CurrentJobId = value?.Trim() ?? string.Empty;
            }
        }

        public bool IsShuffleJobId
        {
            get => AppServices.ServerBrowserState.ShuffleJobId;
            set
            {
                if (AppServices.ServerBrowserState.ShuffleJobId != value)
                {
                    AppServices.ServerBrowserState.ShuffleJobId = value;
                    OnPropertyChanged();
                    _setStatus(value
                        ? LocalizationService.Get("Main.ShuffleJobIdEnabled")
                        : LocalizationService.Get("Main.ShuffleJobIdDisabled"));
                }
            }
        }

        public string CurrentGameName
        {
            get => _currentGameName;
            private set
            {
                if (SetProperty(ref _currentGameName, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasCurrentGameInfo));
                    _notifyCurrentGameChanged?.Invoke();
                }
            }
        }

        public Bitmap? CurrentGameThumbnail
        {
            get => _currentGameThumbnail;
            private set
            {
                if (SetProperty(ref _currentGameThumbnail, value))
                {
                    OnPropertyChanged(nameof(HasCurrentGameInfo));
                    _notifyCurrentGameChanged?.Invoke();
                }
            }
        }

        public bool HasCurrentGameInfo => !string.IsNullOrWhiteSpace(_currentGameName) || _currentGameThumbnail != null;

        public string CurrentPlaceIdLabel =>
            AccountLauncherService.ParsePlaceId(_placeId) is > 0 and var parsed
                ? $"ID {parsed}"
                : (_placeId?.Trim() ?? string.Empty);

        public Color? AdaptiveGameColor
        {
            get => _adaptiveGameColor;
            private set
            {
                if (SetProperty(ref _adaptiveGameColor, value))
                {
                    OnPropertyChanged(nameof(HasAdaptiveGameColor));
                    OnPropertyChanged(nameof(HeroAuraBrush));
                    _notifyCurrentGameChanged?.Invoke();
                }
            }
        }

        public bool HasAdaptiveGameColor => _adaptiveGameColor.HasValue;
        public IBrush HeroAuraBrush => _heroAuraBrush;
        internal Task? PendingAdaptiveColorTask => Volatile.Read(ref _pendingAdaptiveColorTask);

        public ObservableCollection<RecentGameItemViewModel> RecentGamesList
        {
            get => _recentGamesList;
            set
            {
                if (SetProperty(ref _recentGamesList, value))
                {
                    OnPropertyChanged(nameof(HasRecentGames));
                    _notifyRecentGamesChanged?.Invoke();
                }
            }
        }

        public bool HasRecentGames => RecentGamesList != null && RecentGamesList.Count > 0;
        public bool IsBatchLaunching { get => _isBatchLaunching; private set => SetProperty(ref _isBatchLaunching, value); }
        public string BatchLaunchTitleText { get => _batchLaunchTitleText; private set => SetProperty(ref _batchLaunchTitleText, value ?? string.Empty); }
        public string BatchLaunchDetailText { get => _batchLaunchDetailText; private set => SetProperty(ref _batchLaunchDetailText, value ?? string.Empty); }
        public double BatchLaunchProgressPercent { get => _batchLaunchProgressPercent; private set => SetProperty(ref _batchLaunchProgressPercent, value); }
        public string LaunchHomeButtonText => LocalizationService.Format("Main.OpenRobloxHomeAction", _getSelectedCount());
        public string JoinServerButtonText => LocalizationService.Format("Main.JoinServerAction", _getSelectedCount());

        internal void NotifySelectionChanged()
        {
            OnPropertyChanged(nameof(LaunchHomeButtonText));
            OnPropertyChanged(nameof(JoinServerButtonText));
        }

        internal void NotifyLanguageChanged() => NotifySelectionChanged();

        public void SetKnownGameTitle(long placeId, string? title)
        {
            if (placeId <= 0 || string.IsNullOrWhiteSpace(title) || GameInfoService.IsGenericPlaceholderName(placeId, title))
                return;

            GameInfoService.CachePlaceName(placeId, title);
            if (AccountLauncherService.ParsePlaceId(PlaceId) == placeId)
                CurrentGameName = title.Trim();
        }

        public void NormalizePlaceIdInput(string? rawInput = null)
        {
            string candidate = rawInput ?? PlaceId;
            if (string.IsNullOrWhiteSpace(candidate)) return;

            long parsed = AccountLauncherService.ParsePlaceId(candidate);
            if (parsed > 0)
            {
                string normalized = parsed.ToString();
                PlaceId = normalized;
                AppServices.ServerBrowserState.CurrentPlaceId = normalized;
                _settings?.Set("SavedPlaceId", normalized);
                AppServices.HostUiBridge.QueueDeferredSaveSettings();
            }
        }

        public void ScheduleCurrentGameLookup(string? rawPlaceId)
        {
            long gen = Interlocked.Increment(ref _gameLookupGeneration);
            long tintGen = Interlocked.Increment(ref _adaptiveColorGeneration);
            ServerListOperationHelper.ReplaceCts(ref _gameLookupCts, createNew: false, "LaunchBar.ScheduleCurrentGameLookup");
            ServerListOperationHelper.ReplaceCts(ref _adaptiveTintCts, createNew: false, "LaunchBar.ScheduleCurrentGameLookup.Tint");
            if (Volatile.Read(ref _disposed) != 0) return;

            long placeId = AccountLauncherService.ParsePlaceId(rawPlaceId ?? string.Empty);
            if (placeId <= 0)
            {
                CurrentGameName = string.Empty;
                CurrentGameThumbnail = null;
                ClearAdaptiveGameColor(forceResetThemeService: _hasSeenNonEmptyPlaceId);
                return;
            }

            _hasSeenNonEmptyPlaceId = true;
            if (!GameInfoService.TryGetCachedName(placeId, out string instantName))
                instantName = ServerListOperationHelper.TryFindLocalGameName(placeId, RecentGamesList, _getFavorites?.Invoke());

            Bitmap? instantThumb = null;
            if (Avalonia.Application.Current != null)
                GameThumbnailService.TryGetCachedBitmap(placeId, out instantThumb);

            CurrentGameName = instantName;
            CurrentGameThumbnail = instantThumb;

            bool tintEnabled = VisualPreferencesService.Instance.EnableAdaptiveGameTint;
            bool needExtract = instantThumb != null && tintEnabled &&
                (!ReferenceEquals(_lastExtractedThumbnail, instantThumb) || !_adaptiveGameColor.HasValue);
            if (instantThumb == null || !tintEnabled)
                ClearAdaptiveGameColor(forceResetThemeService: !tintEnabled || _ownsAdaptiveAccent != 0);

            bool needLookup = Avalonia.Application.Current != null && (string.IsNullOrWhiteSpace(instantName) || instantThumb == null);
            if (!needLookup && !needExtract)
                return;

            var cts = ServerListOperationHelper.ReplaceCts(ref _gameLookupCts, createNew: true, "LaunchBar.ScheduleCurrentGameLookup")!;
            var ct = cts.Token;

            Task lookupTask = Task.Run(async () =>
            {
                try
                {
                    if (needExtract && instantThumb != null)
                        ExtractAndPostAdaptiveColor(instantThumb, placeId, gen, tintGen, ct);

                    if (!needLookup)
                        return;

                    await Task.Delay(220, ct).ConfigureAwait(false);
                    var (resolvedName, resolvedThumb) = await GameInfoService.ResolveGameInfoAsync(placeId, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested || Volatile.Read(ref _gameLookupGeneration) != gen) return;

                    Color? resolvedColor = resolvedThumb != null && VisualPreferencesService.Instance.EnableAdaptiveGameTint
                        ? AdaptivePaletteExtractor.ExtractAccentFromBitmap(resolvedThumb)
                        : null;
                    if (ct.IsCancellationRequested || Volatile.Read(ref _gameLookupGeneration) != gen) return;

                    AppDispatcher.Post(() =>
                    {
                        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _gameLookupGeneration) != gen || ct.IsCancellationRequested || AccountLauncherService.ParsePlaceId(PlaceId) != placeId)
                            return;
                        if (!string.IsNullOrWhiteSpace(resolvedName)) CurrentGameName = resolvedName!;
                        if (resolvedThumb != null)
                        {
                            CurrentGameThumbnail = resolvedThumb;
                            ApplyOrClearAdaptiveColorOnUi(resolvedThumb, resolvedColor, tintGen);
                        }
                    });
                }
                catch (OperationCanceledException) { /* lookup canceled */ }
                catch (Exception ex) { _logger.Debug($"[ScheduleCurrentGameLookup] {ex.Message}"); }
                finally { ServerListOperationHelper.ReleaseFinishedCts(ref _gameLookupCts, cts, "LaunchBar.ScheduleCurrentGameLookup"); }
            }, ct);

            if (needExtract)
                Volatile.Write(ref _pendingAdaptiveColorTask, lookupTask);
        }

        public void ReloadRecentGames()
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            try
            {
                var list = new List<RecentGameItemViewModel>();
                foreach (var g in GameInfoService.GetRecentGamesSnapshot())
                {
                    if (g?.Details?.placeId != null)
                    {
                        string bestName = g.Details.BestDisplayName;
                        list.Add(new RecentGameItemViewModel
                        {
                            PlaceId = g.Details.placeId,
                            Name = string.IsNullOrWhiteSpace(bestName) ? $"Place {g.Details.placeId}" : bestName,
                            ImageUrl = g.ImageUrl ?? string.Empty
                        });
                    }
                }

                list.Reverse();
                var weakSelf = new WeakReference<LaunchBarViewModel>(this);
                AppDispatcher.Post(() =>
                {
                    if (!weakSelf.TryGetTarget(out var vm) || Volatile.Read(ref vm._disposed) != 0) return;
                    vm.ApplyReloadedRecentGames(list);
                });
            }
            catch (Exception ex)
            {
                _logger.Debug($"Failed to reload recent games: {ex.Message}");
            }
        }

        private void ApplyReloadedRecentGames(List<RecentGameItemViewModel> list)
        {
            if (RecentGamesList.Count == list.Count)
            {
                bool same = true;
                for (int i = 0; i < list.Count; i++)
                {
                    if (RecentGamesList[i].PlaceId != list[i].PlaceId) { same = false; break; }
                }
                if (same) return;
            }

            RecentGamesList.Clear();
            foreach (var item in list)
            {
                if (!GameInfoService.IsGenericPlaceholderName(item.PlaceId, item.Name))
                    GameInfoService.CachePlaceName(item.PlaceId, item.Name);
                else if (GameInfoService.TryGetCachedName(item.PlaceId, out string cachedName))
                    item.Name = cachedName;
                else if (Avalonia.Application.Current != null)
                {
                    var targetItem = item;
                    _ = Task.Run(async () =>
                    {
                        var resolved = await GameInfoService.ResolvePlaceNameAsync(targetItem.PlaceId).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(resolved) && Volatile.Read(ref _disposed) == 0)
                        {
                            AppDispatcher.Post(() =>
                            {
                                if (Volatile.Read(ref _disposed) != 0) return;
                                targetItem.Name = resolved!;
                                SetKnownGameTitle(targetItem.PlaceId, resolved);
                            });
                        }
                    });
                }
                RecentGamesList.Add(item);
            }

            OnPropertyChanged(nameof(HasRecentGames));
            _notifyRecentGamesChanged?.Invoke();
            if (string.IsNullOrWhiteSpace(CurrentGameName) && !string.IsNullOrWhiteSpace(_placeId))
                ScheduleCurrentGameLookup(_placeId);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            if (_launchProgressChangedHandler != null)
                LaunchCoordinator.LaunchProgressChanged -= _launchProgressChangedHandler;
            UnsubscribeAdaptiveTintEvents();

            Interlocked.Increment(ref _gameLookupGeneration);
            Interlocked.Increment(ref _adaptiveColorGeneration);
            ServerListOperationHelper.ReplaceCts(ref _gameLookupCts, createNew: false, "LaunchBarViewModel.Dispose.GameCts");
            ServerListOperationHelper.ReplaceCts(ref _adaptiveTintCts, createNew: false, "LaunchBarViewModel.Dispose.TintCts");
            ClearAdaptiveGameColor(forceResetThemeService: false);
            GC.SuppressFinalize(this);
        }
    }
}
