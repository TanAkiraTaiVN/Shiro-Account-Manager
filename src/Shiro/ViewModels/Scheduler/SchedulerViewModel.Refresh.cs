#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public sealed partial class SchedulerViewModel
    {
        private void InitializeDayChips()
        {
            (int Offset, string Key)[] specs =
            {
                (0, "Sched_Day_Today"),
                (1, "Sched_Day_Mon"),
                (2, "Sched_Day_Tue"),
                (3, "Sched_Day_Wed"),
                (4, "Sched_Day_Thu"),
                (5, "Sched_Day_Fri"),
                (6, "Sched_Day_Sat"),
                (7, "Sched_Day_Sun")
            };

            foreach (var (offset, key) in specs)
                _dayChips.Add(new SchedulerDayChipViewModel(offset, key, SelectDayCommand) { IsSelected = offset == _selectedDayOffset });
        }

        private ScheduleValidationResult SaveFromEditor(LaunchSchedule schedule)
        {
            ScheduleValidationResult result = _schedulerService.UpsertSchedule(schedule);
            if (result.IsValid)
                RefreshAll();
            return result;
        }

        private void OnSchedulesChanged()
        {
            RefreshSchedulesAndTimeline();
            RefreshHistory();
        }

        private void OnEngineStateChanged()
        {
            OnPropertyChanged(nameof(IsEngineEnabled));
            OnPropertyChanged(nameof(HasActiveBadge));
            RefreshDynamicTiming();
        }

        private void OnAccountsChanged(AccountsChangedEventArgs? e)
        {
            long version = e?.Version ?? 0;
            if (version > 0)
            {
                long observed = Volatile.Read(ref _highestObservedRepoVersion);
                while (version > observed)
                {
                    long prev = Interlocked.CompareExchange(ref _highestObservedRepoVersion, version, observed);
                    if (prev == observed) break;
                    observed = prev;
                }
                if (version < Volatile.Read(ref _highestObservedRepoVersion))
                    return;
            }

            IReadOnlyList<Account> snapshot = e?.Accounts ?? _accountRepository.GetAll();
            lock (_refreshLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;
                if (Editor.IsOpen)
                    Editor.RefreshAccountsCatalog(snapshot);
                RefreshSchedulesAndTimelineLocked(snapshot);
                RefreshHistoryLocked(snapshot);
            }
        }

        private void RefreshAll()
        {
            lock (_refreshLock)
            {
                IReadOnlyList<Account> snapshot = _accountRepository.GetAll();
                RefreshSchedulesAndTimelineLocked(snapshot);
                RefreshHistoryLocked(snapshot);
            }
        }

        private void RefreshSchedulesAndTimeline()
        {
            lock (_refreshLock)
                RefreshSchedulesAndTimelineLocked(_accountRepository.GetAll());
        }

        private void RefreshSchedulesAndTimelineLocked(IReadOnlyList<Account> accounts)
        {
            IReadOnlyList<LaunchSchedule> schedules = _schedulerService.GetSchedules();
            DateTimeOffset localNow = _timeProvider.GetLocalNow();
            TimeZoneInfo tz = _timeProvider.LocalTimeZone;
            DateOnly today = DateOnly.FromDateTime(localNow.DateTime);

            IReadOnlyList<ScheduleConflictInfo> detectedConflicts = LaunchScheduleEvaluatorHelper.DetectConflicts(
                schedules,
                today,
                s => SchedulerBrushHelper.ResolveAccountKeys(s, accounts));

            var conflictScheduleIds = new HashSet<string>(StringComparer.Ordinal);
            _conflicts.Clear();
            foreach (ScheduleConflictInfo c in detectedConflicts)
            {
                _conflicts.Add(c);
                conflictScheduleIds.Add(c.ScheduleAId);
                conflictScheduleIds.Add(c.ScheduleBId);
            }

            OnPropertyChanged(nameof(HasConflicts));
            SetProperty(ref _conflictBannerText, SchedulerBrushHelper.BuildConflictBannerText(detectedConflicts), nameof(ConflictBannerText));

            var existingCardsById = _allScheduleCards.ToDictionary(c => c.Id, StringComparer.Ordinal);
            var nextCards = new List<ScheduleItemViewModel>(schedules.Count);
            foreach (LaunchSchedule sched in schedules)
            {
                bool hasConflict = conflictScheduleIds.Contains(sched.Id);
                if (existingCardsById.TryGetValue(sched.Id, out ScheduleItemViewModel? existing))
                {
                    existing.SyncFromModel(sched, localNow, tz, accounts, hasConflict);
                    nextCards.Add(existing);
                }
                else
                {
                    var card = new ScheduleItemViewModel(
                        sched,
                        _settings,
                        OnToggleScheduleEnabled,
                        RunScheduleNowAsync,
                        CloseScheduleTabsNowAsync,
                        AddNextShiftFromCard,
                        EditScheduleFromCard,
                        DuplicateScheduleFromCard,
                        DeleteScheduleFromCard);
                    card.SyncFromModel(sched, localNow, tz, accounts, hasConflict);
                    nextCards.Add(card);
                }
            }

            _allScheduleCards.Clear();
            _allScheduleCards.AddRange(nextCards);

            UpdateCountsAndHero(schedules, localNow, tz);
            ApplyFilterLocked();
            RebuildTimeline(schedules, conflictScheduleIds, today, accounts);
        }

        private void RefreshDynamicTiming()
        {
            lock (_refreshLock)
            {
                IReadOnlyList<LaunchSchedule> schedules = _schedulerService.GetSchedules();
                IReadOnlyList<Account> accounts = _accountRepository.GetAll();
                DateTimeOffset localNow = _timeProvider.GetLocalNow();
                TimeZoneInfo tz = _timeProvider.LocalTimeZone;

                var conflictIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (ScheduleConflictInfo c in _conflicts)
                {
                    conflictIds.Add(c.ScheduleAId);
                    conflictIds.Add(c.ScheduleBId);
                }

                bool anyActiveChanged = false;
                foreach (ScheduleItemViewModel card in _allScheduleCards)
                {
                    bool wasActive = card.IsActiveNow;
                    card.RefreshDisplayAndTiming(localNow, tz, accounts, conflictIds.Contains(card.Id));
                    if (wasActive != card.IsActiveNow)
                        anyActiveChanged = true;
                }

                UpdateCountsAndHero(schedules, localNow, tz);
                if (anyActiveChanged && _filterMode == SchedulerFilterMode.ActiveNow)
                    ApplyFilterLocked();
            }
        }

        private void UpdateCountsAndHero(IReadOnlyList<LaunchSchedule> schedules, DateTimeOffset localNow, TimeZoneInfo tz)
        {
            int all = _allScheduleCards.Count;
            int enabled = schedules.Count(s => s.IsEnabled);
            int paused = all - enabled;
            int activeNow = _allScheduleCards.Count(c => c.IsActiveNow);

            bool enabledChanged = _enabledCount != enabled;
            SetProperty(ref _allCount, all, nameof(FilterAllText));
            if (SetProperty(ref _enabledCount, enabled, nameof(EnabledCount)) || enabledChanged)
            {
                OnPropertyChanged(nameof(HasActiveBadge));
                OnPropertyChanged(nameof(FilterEnabledText));
            }
            SetProperty(ref _activeNowCount, activeNow, nameof(FilterActiveNowText));
            SetProperty(ref _pausedCount, paused, nameof(FilterPausedText));
            OnPropertyChanged(nameof(FilterAllText));
            OnPropertyChanged(nameof(HasAnySavedSchedules));

            string engineText = IsEngineEnabled
                ? LocalizationService.Format("Sched_Engine_ActiveStatus", enabled)
                : LocalizationService.Get("Sched_Engine_PausedStatus");
            SetProperty(ref _engineStatusText, engineText, nameof(EngineStatusText));

            double fraction = (localNow.Hour * 60.0 + localNow.Minute + localNow.Second / 60.0) / 1440.0;
            SetProperty(ref _nowFraction, Math.Clamp(fraction, 0.0, 1.0), nameof(NowFraction));

            if (!IsEngineEnabled)
            {
                SetProperty(ref _hasNextEvent, false, nameof(HasNextEvent));
                SetProperty(ref _isNextEventClose, false, nameof(IsNextEventClose));
                SetProperty(ref _nextEventSummaryText, LocalizationService.Get("Sched_Next_Paused"), nameof(NextEventSummaryText));
                SetProperty(ref _nextEventCountdownText, "--:--:--", nameof(NextEventCountdownText));
                return;
            }

            ScheduleTransitionInfo? next = LaunchScheduleEvaluatorHelper.GetSoonestTransition(schedules, localNow, tz);
            if (!next.HasValue)
            {
                SetProperty(ref _hasNextEvent, false, nameof(HasNextEvent));
                SetProperty(ref _isNextEventClose, false, nameof(IsNextEventClose));
                SetProperty(ref _nextEventSummaryText, LocalizationService.Get("Sched_Next_None"), nameof(NextEventSummaryText));
                SetProperty(ref _nextEventCountdownText, "--:--:--", nameof(NextEventCountdownText));
                return;
            }

            bool isClose = next.Value.Phase == ScheduleRunPhase.Close;
            string clock = next.Value.OccurringAtLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
            string summary = isClose
                ? LocalizationService.Format("Sched_Next_CloseSummary", next.Value.Schedule.Name, clock)
                : LocalizationService.Format("Sched_Next_OpenSummary", next.Value.Schedule.Name, clock);
            string countdown = ScheduleItemViewModel.FormatCountdown(next.Value.OccurringAtLocal - localNow);

            SetProperty(ref _hasNextEvent, true, nameof(HasNextEvent));
            SetProperty(ref _isNextEventClose, isClose, nameof(IsNextEventClose));
            SetProperty(ref _nextEventSummaryText, summary, nameof(NextEventSummaryText));
            SetProperty(ref _nextEventCountdownText, countdown, nameof(NextEventCountdownText));
        }

        private void ApplyFilter()
        {
            lock (_refreshLock)
                ApplyFilterLocked();
        }

        private void ApplyFilterLocked()
        {
            string query = (_searchText ?? string.Empty).Trim();
            _filteredSchedules.Clear();

            foreach (ScheduleItemViewModel card in _allScheduleCards)
            {
                bool matchesMode = _filterMode switch
                {
                    SchedulerFilterMode.Enabled => card.IsEnabled,
                    SchedulerFilterMode.ActiveNow => card.IsActiveNow,
                    SchedulerFilterMode.Paused => !card.IsEnabled,
                    _ => true
                };
                if (!matchesMode)
                    continue;

                if (!string.IsNullOrEmpty(query) &&
                    !card.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !card.TargetSummaryText.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !card.DestinationSummaryText.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _filteredSchedules.Add(card);
            }

            OnPropertyChanged(nameof(HasNoSchedules));
        }

        private void RefreshTimeline()
        {
            lock (_refreshLock)
            {
                IReadOnlyList<LaunchSchedule> schedules = _schedulerService.GetSchedules();
                IReadOnlyList<Account> accounts = _accountRepository.GetAll();
                DateOnly today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
                var conflictIds = new HashSet<string>(_conflicts.SelectMany(c => new[] { c.ScheduleAId, c.ScheduleBId }), StringComparer.Ordinal);
                RebuildTimeline(schedules, conflictIds, today, accounts);
            }
        }

        private void RebuildTimeline(
            IReadOnlyList<LaunchSchedule> schedules,
            IReadOnlySet<string> conflictScheduleIds,
            DateOnly today,
            IReadOnlyList<Account> accounts)
        {
            DateOnly selectedDate = ResolveDateForDayOffset(today, _selectedDayOffset);
            IReadOnlyList<ScheduleTimelineSegment> segments = LaunchScheduleEvaluatorHelper.ProjectDayTimeline(
                schedules, selectedDate, conflictScheduleIds);
            Dictionary<string, LaunchSchedule> byId = schedules.ToDictionary(s => s.Id, StringComparer.Ordinal);

            _timelineSegments.Clear();
            int maxLane = 0;
            foreach (ScheduleTimelineSegment seg in segments)
            {
                maxLane = Math.Max(maxLane, seg.Lane);
                byId.TryGetValue(seg.ScheduleId, out LaunchSchedule? model);
                _timelineSegments.Add(new SchedulerTimelineBlockViewModel(
                    seg,
                    model,
                    () => _accountRepository.GetAll(),
                    _settings));
            }

            SetProperty(ref _timelineCanvasHeight, Math.Max(1, maxLane + 1) * 34.0 + 6.0, nameof(TimelineCanvasHeight));
        }

        private static DateOnly ResolveDateForDayOffset(DateOnly today, int offset)
        {
            if (offset <= 0)
                return today;

            int targetDow = offset == 7 ? (int)DayOfWeek.Sunday : offset;
            int delta = (targetDow - (int)today.DayOfWeek + 7) % 7;
            return today.AddDays(delta);
        }

        private void RefreshHistory()
        {
            lock (_refreshLock)
                RefreshHistoryLocked(_accountRepository.GetAll());
        }

        private void RefreshHistoryLocked(IReadOnlyList<Account> accounts)
        {
            IReadOnlyList<ScheduleRunEntry> history = _schedulerService.GetHistory();
            List<string> knownUsernames = CollectKnownUsernames(accounts);

            _historyEntries.Clear();
            foreach (ScheduleRunEntry entry in history)
                _historyEntries.Add(new ScheduleHistoryItemViewModel(entry, knownUsernames, _settings));

            OnPropertyChanged(nameof(HasHistoryEntries));
        }

        private List<string> CollectKnownUsernames(IReadOnlyList<Account> accounts)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Account acc in accounts)
            {
                if (!string.IsNullOrWhiteSpace(acc.Username))
                    names.Add(acc.Username);
            }
            foreach (LaunchSchedule sched in _schedulerService.GetSchedules())
            {
                foreach (ScheduleAccountRef target in sched.TargetAccounts)
                {
                    if (!string.IsNullOrWhiteSpace(target.Username))
                        names.Add(target.Username);
                }
            }
            return names.ToList();
        }

        private void OnToggleScheduleEnabled(string scheduleId, bool enabled)
        {
            if (_schedulerService.SetScheduleEnabled(scheduleId, enabled))
                RefreshSchedulesAndTimeline();
        }

        private Task RunScheduleNowAsync(ScheduleItemViewModel item) => _schedulerService.RunScheduleNowAsync(item.Id);
        private Task CloseScheduleTabsNowAsync(ScheduleItemViewModel item) => _schedulerService.CloseScheduleTabsNowAsync(item.Id);
        private void AddNextShiftFromCard(ScheduleItemViewModel item) => Editor.OpenNextShift(item.ModelSnapshot, _accountRepository.GetAll());
        private void EditScheduleFromCard(ScheduleItemViewModel item) => Editor.OpenEdit(item.ModelSnapshot, _accountRepository.GetAll());

        private void DuplicateScheduleFromCard(ScheduleItemViewModel item)
        {
            LaunchSchedule copy = item.ModelSnapshot;
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = LocalizationService.Format("Sched_DuplicateNameFormat", item.Name);
            copy.LastTriggeredStartKey = null;
            copy.LastTriggeredEndKey = null;
            copy.CreatedAtUtc = default;
            copy.UpdatedAtUtc = default;
            _schedulerService.UpsertSchedule(copy);
        }

        private void DeleteScheduleFromCard(ScheduleItemViewModel item)
        {
            if (!_schedulerService.DeleteSchedule(item.Id, out LaunchSchedule? removed) || removed == null)
                return;

            if (Editor.IsOpen && Editor.IsEditingExisting && string.Equals(Editor.EditingScheduleId, item.Id, StringComparison.Ordinal))
                Editor.Close();

            LaunchSchedule removedCopy = removed.Clone();
            int restoredFlag = 0;
            ToastService.ShowWithUndo(
                LocalizationService.Get("Sched_DeletedTitle"),
                LocalizationService.Format("Sched_DeletedMessage", removedCopy.Name),
                LocalizationService.Get("Toast_Undo"),
                () =>
                {
                    if (Interlocked.Exchange(ref restoredFlag, 1) != 0 || Volatile.Read(ref _disposed) != 0)
                        return;
                    if (_schedulerService.RestoreSchedule(removedCopy))
                        RefreshSchedulesAndTimeline();
                },
                durationSeconds: 6.0);
        }

        private void ExecuteSelectDay(object? parameter)
        {
            int offset = parameter switch
            {
                int i => i,
                SchedulerDayChipViewModel chip => chip.Offset,
                string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
                _ => 0
            };
            SelectedDayOffset = offset;
        }

        private void ExecuteSetFilter(object? parameter)
        {
            if (parameter is SchedulerFilterMode mode)
                FilterMode = mode;
            else if (parameter is string s && Enum.TryParse(s, ignoreCase: true, out SchedulerFilterMode parsed))
                FilterMode = parsed;
        }

        private void ExecuteSelectTimelineBlock(object? parameter)
        {
            string? scheduleId = parameter switch
            {
                SchedulerTimelineBlockViewModel block => block.ScheduleId,
                string id => id,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(scheduleId))
                return;

            LaunchSchedule? match = _schedulerService.GetSchedules()
                .FirstOrDefault(s => string.Equals(s.Id, scheduleId, StringComparison.Ordinal));
            if (match != null)
                Editor.OpenEdit(match, _accountRepository.GetAll());
        }
    }
}
