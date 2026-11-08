#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels
{
    /// <summary>
    /// Modern UI/UX state &amp; behavior extensions for <see cref="MainViewModel"/>:
    /// - Live Tool ON/OFF states &amp; footer chips
    /// - Active Game Info Chip next to Place ID
    /// - Shift+Click contiguous range selection &amp; Quick-Select helpers
    /// - Interactive 4 Summary Metric Cards &amp; Sort dropdown label
    /// - Live Batch Launch Progress Banner state
    /// - Responsive 2/3/4-column virtualized card grid &amp; Ctrl+K keyboard navigation
    /// </summary>
    public partial class MainViewModel
    {
        private AccountViewModel? _lastClickedAccount;
        private int _cardColumns = 3;
        private string _currentGameName = string.Empty;
        private Bitmap? _currentGameThumbnail;
        private CancellationTokenSource? _gameLookupCts;

        private bool _isBatchLaunching;
        private string _batchLaunchTitleText = string.Empty;
        private string _batchLaunchDetailText = string.Empty;
        private double _batchLaunchProgressPercent;

        #region Item 2: Live Tool ON/OFF Indicators & Footer Chips
        public bool IsSmartMuteActive => RobloxAudioService.IsSmartMuteEnabled;

        public int ActiveToolsCount
        {
            get
            {
                int count = 0;
                if (IsAntiAfk) count++;
                if (IsPotatoMode) count++;
                if (AreWindowsHidden) count++;
                if (IsSmartMuteActive) count++;
                if (IsStreamerModeActive) count++;
                return count;
            }
        }

        public bool HasActiveTools => ActiveToolsCount > 0;

        public void NotifyToolStatesChanged()
        {
            OnPropertyChanged(nameof(IsAntiAfk));
            OnPropertyChanged(nameof(IsPotatoMode));
            OnPropertyChanged(nameof(AreWindowsHidden));
            OnPropertyChanged(nameof(IsSmartMuteActive));
            OnPropertyChanged(nameof(IsStreamerModeEnabled));
            OnPropertyChanged(nameof(IsStreamerModeActive));
            OnPropertyChanged(nameof(ActiveToolsCount));
            OnPropertyChanged(nameof(HasActiveTools));
        }
        #endregion

        #region Item 3: Active Game Info Chip Next to Place ID
        public string CurrentGameName
        {
            get => _currentGameName;
            private set
            {
                if (SetProperty(ref _currentGameName, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasCurrentGameInfo));
                    ServerListViewModel?.NotifyCurrentGameChanged();
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
                    ServerListViewModel?.NotifyCurrentGameChanged();
                }
            }
        }

        public bool HasCurrentGameInfo => !string.IsNullOrWhiteSpace(_currentGameName) || _currentGameThumbnail != null;

        public void SetKnownGameTitle(long placeId, string? title)
        {
            if (placeId <= 0 || string.IsNullOrWhiteSpace(title) || GameInfoService.IsGenericPlaceholderName(placeId, title))
                return;

            GameInfoService.CachePlaceName(placeId, title);
            if (AccountLauncherService.ParsePlaceId(PlaceId) == placeId)
            {
                CurrentGameName = title.Trim();
            }
        }

        public void ScheduleCurrentGameLookup(string? rawPlaceId)
        {
            var prev = Interlocked.Exchange(ref _gameLookupCts, null);
            if (prev != null)
            {
                SafeExecuteHelper.Try(() => prev.Cancel(), "MainViewModel.ScheduleCurrentGameLookup.Cancel");
                SafeExecuteHelper.Try(() => prev.Dispose(), "MainViewModel.ScheduleCurrentGameLookup.Dispose");
            }

            long placeId = AccountLauncherService.ParsePlaceId(rawPlaceId ?? string.Empty);
            if (placeId <= 0)
            {
                CurrentGameName = string.Empty;
                CurrentGameThumbnail = null;
                return;
            }

            string instantName = string.Empty;
            if (!GameInfoService.TryGetCachedName(placeId, out instantName))
            {
                RecentGameItemViewModel? recent = null; try { var rl = RecentGamesList; if (rl != null) { for (int i = 0; i < rl.Count; i++) { var r = i < rl.Count ? rl[i] : null; if (r != null && r.PlaceId == placeId && !GameInfoService.IsGenericPlaceholderName(placeId, r.Name)) { recent = r; break; } } } } catch (Exception ex) { AppServices.Logger.Debug("Lookup error: " + ex.Message); }
                if (recent != null)
                {
                    instantName = recent.Name;
                    GameInfoService.CachePlaceName(placeId, instantName);
                }
                else
                {
                    FavoriteGameItemViewModel? fav = null; try { var fl = ServerListViewModel?.Favorites; if (fl != null) { for (int i = 0; i < fl.Count; i++) { var f = i < fl.Count ? fl[i] : null; if (f != null && f.PlaceId == placeId && !GameInfoService.IsGenericPlaceholderName(placeId, f.Name)) { fav = f; break; } } } } catch (Exception ex) { AppServices.Logger.Debug("Lookup error: " + ex.Message); }
                    if (fav != null)
                    {
                        instantName = fav.Name;
                        GameInfoService.CachePlaceName(placeId, instantName);
                    }
                }
            }

            Bitmap? instantThumb = null;
            if (Avalonia.Application.Current != null)
            {
                GameThumbnailService.TryGetCachedBitmap(placeId, out instantThumb);
            }

            CurrentGameName = instantName;
            CurrentGameThumbnail = instantThumb;

            if (Avalonia.Application.Current == null || (!string.IsNullOrWhiteSpace(instantName) && instantThumb != null))
                return;

            var cts = new CancellationTokenSource();
            var ct = cts.Token;
            _gameLookupCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(220, ct).ConfigureAwait(false);
                    var (resolvedName, resolvedThumb) = await GameInfoService.ResolveGameInfoAsync(placeId, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                        return;

                    AppDispatcher.Post(() =>
                    {
                        if (System.Threading.Volatile.Read(ref _disposed) != 0 || ct.IsCancellationRequested || AccountLauncherService.ParsePlaceId(PlaceId) != placeId)
                            return;

                        if (!string.IsNullOrWhiteSpace(resolvedName))
                            CurrentGameName = resolvedName!;
                        if (resolvedThumb != null)
                            CurrentGameThumbnail = resolvedThumb;
                    });
                }
                catch (OperationCanceledException) { /* lookup canceled */ }
                catch (Exception ex)
                {
                    _logger.Debug($"[ScheduleCurrentGameLookup] {ex.Message}");
                }
            }, ct);
        }
        #endregion

        #region Item 4: Shift+Click Range Selection & Quick-Select Menu
        public void HandleAccountClickSelection(AccountViewModel? target, bool isShiftPressed, bool? explicitTargetState = null)
        {
            if (target == null)
                return;

            if (isShiftPressed && _lastClickedAccount != null && !ReferenceEquals(_lastClickedAccount, target))
            {
                int idxA = _filteredAccounts.IndexOf(_lastClickedAccount);
                int idxB = _filteredAccounts.IndexOf(target);
                if (idxA >= 0 && idxB >= 0)
                {
                    int start = Math.Min(idxA, idxB);
                    int end = Math.Max(idxA, idxB);
                    bool desiredState = explicitTargetState ?? _lastClickedAccount.IsSelected;

                    _suppressCountNotifications = true;
                    try
                    {
                        for (int i = start; i <= end; i++)
                        {
                            _filteredAccounts[i].IsSelected = desiredState;
                        }
                    }
                    finally
                    {
                        _suppressCountNotifications = false;
                    }

                    _lastClickedAccount = target;
                    NotifyCounts();
                    OnPropertyChanged(nameof(IsAllSelected));
                    return;
                }
            }

            bool nextState = explicitTargetState ?? !target.IsSelected;
            target.IsSelected = nextState;
            _lastClickedAccount = target;
        }

        public int SelectFirstReadyAccounts(int count)
        {
            if (count <= 0) return 0;

            int picked = 0;
            _suppressCountNotifications = true;
            try
            {
                foreach (var acc in _accounts)
                    acc.IsSelected = false;

                for (int i = 0; i < _filteredAccounts.Count && picked < count; i++)
                {
                    var candidate = _filteredAccounts[i];
                    if (candidate.Valid && !candidate.IsBannedOrLocked && !candidate.IsInGame && !candidate.IsLocalClientRunning)
                    {
                        candidate.IsSelected = true;
                        _lastClickedAccount = candidate;
                        picked++;
                    }
                }
            }
            finally
            {
                _suppressCountNotifications = false;
            }

            NotifyCounts();
            OnPropertyChanged(nameof(IsAllSelected));
            return picked;
        }

        public int InvertFilteredSelection()
        {
            int nowSelected = 0;
            _suppressCountNotifications = true;
            try
            {
                for (int i = 0; i < _filteredAccounts.Count; i++)
                {
                    bool next = !_filteredAccounts[i].IsSelected;
                    _filteredAccounts[i].IsSelected = next;
                    if (next) nowSelected++;
                }
            }
            finally
            {
                _suppressCountNotifications = false;
            }

            NotifyCounts();
            OnPropertyChanged(nameof(IsAllSelected));
            return nowSelected;
        }

        public int SelectRangeExpression(string? expression)
        {
            if (string.IsNullOrWhiteSpace(expression) || _filteredAccounts.Count == 0)
                return 0;

            var matchedIndices = new HashSet<int>();
            var tokens = expression.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawToken in tokens)
            {
                string token = rawToken.Trim();
                int dashIdx = token.IndexOf('-');
                if (dashIdx > 0 && dashIdx < token.Length - 1)
                {
                    if (int.TryParse(token.AsSpan(0, dashIdx), out int from) &&
                        int.TryParse(token.AsSpan(dashIdx + 1), out int to))
                    {
                        int lo = Math.Max(1, Math.Min(from, to));
                        int hi = Math.Min(_filteredAccounts.Count, Math.Max(from, to));
                        for (int idx = lo; idx <= hi; idx++)
                            matchedIndices.Add(idx - 1);
                    }
                }
                else if (int.TryParse(token, out int single) && single >= 1 && single <= _filteredAccounts.Count)
                {
                    matchedIndices.Add(single - 1);
                }
            }

            if (matchedIndices.Count == 0)
                return 0;

            _suppressCountNotifications = true;
            try
            {
                foreach (var acc in _accounts)
                    acc.IsSelected = false;

                foreach (int zeroBased in matchedIndices.OrderBy(x => x))
                {
                    _filteredAccounts[zeroBased].IsSelected = true;
                    _lastClickedAccount = _filteredAccounts[zeroBased];
                }
            }
            finally
            {
                _suppressCountNotifications = false;
            }

            NotifyCounts();
            OnPropertyChanged(nameof(IsAllSelected));
            return matchedIndices.Count;
        }
        #endregion

        #region Item 5: Sort Mode Display
        public string CurrentSortLabel => SortMode switch
        {
            AccountSortMode.NaturalDescending => Shiro.Services.LocalizationService.Get("Main.SortNameDesc"),
            AccountSortMode.Original => Shiro.Services.LocalizationService.Get("Main.SortOriginal"),
            AccountSortMode.Status => Shiro.Services.LocalizationService.Get("Main.SortPlayingFirst"),
            AccountSortMode.RobuxDescending => Shiro.Services.LocalizationService.Get("Main.SortRobuxDesc"),
            _ => Shiro.Services.LocalizationService.Get("Main.SortNameAsc")
        };
        #endregion

        #region Item 6: Live Batch Launch Progress Banner
        public bool IsBatchLaunching
        {
            get => _isBatchLaunching;
            private set => SetProperty(ref _isBatchLaunching, value);
        }

        public string BatchLaunchTitleText
        {
            get => _batchLaunchTitleText;
            private set => SetProperty(ref _batchLaunchTitleText, value ?? string.Empty);
        }

        public string BatchLaunchDetailText
        {
            get => _batchLaunchDetailText;
            private set => SetProperty(ref _batchLaunchDetailText, value ?? string.Empty);
        }

        public double BatchLaunchProgressPercent
        {
            get => _batchLaunchProgressPercent;
            private set => SetProperty(ref _batchLaunchProgressPercent, value);
        }

        internal void UpdateBatchLaunchBanner(LaunchProgress progress)
        {
            if (progress == null)
                return;

            if (progress.Account == null && progress.Completed)
            {
                IsBatchLaunching = false;
                BatchLaunchProgressPercent = 0;
                return;
            }

            if (progress.Total > 1 || (!progress.Completed && progress.Status == LaunchOutcome.RateLimited))
            {
                int total = Math.Max(1, progress.Total);
                int index = Math.Clamp(progress.Index, 1, total);
                double units = Math.Max(0.15, index - (progress.Completed ? 0.0 : 0.45));

                IsBatchLaunching = true;
                BatchLaunchProgressPercent = Math.Clamp((units / total) * 100.0, 5.0, 100.0);
                string accName = progress.Account?.Username ?? string.Empty;
                BatchLaunchTitleText = Shiro.Services.LocalizationService.Format("Main.LaunchQueueProgress", index, total, accName);
                BatchLaunchDetailText = !string.IsNullOrWhiteSpace(progress.Message)
                    ? progress.Message!
                    : Shiro.Services.LocalizationService.Get("Main.PreparingClientLaunch");
            }
        }
        #endregion

        #region Item 9: Responsive 2/3/4-Column Grid & Ctrl+K Keyboard Navigation
        public int CardColumns
        {
            get => _cardColumns;
            set
            {
                int clamped = Math.Clamp(value, 2, 4);
                if (SetProperty(ref _cardColumns, clamped))
                {
                    RebuildAccountRows();
                }
            }
        }

        public static int CalculateResponsiveCardColumns(double availableWidth)
        {
            if (double.IsNaN(availableWidth) || availableWidth <= 0)
                return 3;
            if (availableWidth < 820)
                return 2;
            if (availableWidth >= 1240)
                return 4;
            return 3;
        }
        #endregion
    }
}
