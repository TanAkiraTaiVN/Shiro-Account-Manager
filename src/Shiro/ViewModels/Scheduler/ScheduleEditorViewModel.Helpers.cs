#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public sealed partial class ScheduleEditorViewModel
    {
        public void OpenNew(IReadOnlyList<Account> accounts)
        {
            OpenDraftScope(isEditingExisting: false, scheduleId: Guid.NewGuid().ToString("N"), createdAtUtc: default, isEnabled: true, () =>
            {
                LoadFieldsFromSchedule(new LaunchSchedule(), accounts);
                Name = string.Empty;
                var (dashPlaceId, dashJobId) = _getDashboardDestination();
                if (string.IsNullOrWhiteSpace(PlaceIdText) && !string.IsNullOrWhiteSpace(dashPlaceId))
                    PlaceIdText = dashPlaceId;
                if (string.IsNullOrWhiteSpace(JobIdText) && !string.IsNullOrWhiteSpace(dashJobId))
                    JobIdText = dashJobId;
            });
        }

        public void OpenEdit(LaunchSchedule schedule, IReadOnlyList<Account> accounts)
        {
            OpenDraftScope(isEditingExisting: true, scheduleId: schedule.Id, createdAtUtc: schedule.CreatedAtUtc, isEnabled: schedule.IsEnabled, () =>
                LoadFieldsFromSchedule(schedule, accounts));
        }

        public void OpenNextShift(LaunchSchedule previous, IReadOnlyList<Account> accounts)
        {
            OpenDraftScope(isEditingExisting: false, scheduleId: Guid.NewGuid().ToString("N"), createdAtUtc: default, isEnabled: true, () =>
            {
                LaunchSchedule draft = previous.Clone();
                draft.Name = string.Empty;
                draft.TargetAccounts = new List<ScheduleAccountRef>();

                if (previous.HasEndTime &&
                    LaunchScheduleEvaluatorHelper.TryParseTime(previous.StartTime, out TimeOnly prevStart) &&
                    LaunchScheduleEvaluatorHelper.TryParseTime(previous.EndTime, out TimeOnly prevEnd))
                {
                    TimeSpan duration = prevEnd.ToTimeSpan() - prevStart.ToTimeSpan();
                    if (duration <= TimeSpan.Zero)
                        duration += TimeSpan.FromHours(24);

                    draft.StartTime = prevEnd.ToString("HH:mm", CultureInfo.InvariantCulture);
                    draft.HasEndTime = true;
                    draft.EndTime = prevEnd.Add(duration).ToString("HH:mm", CultureInfo.InvariantCulture);

                    if (previous.Recurrence == ScheduleRecurrence.OneTime &&
                        prevEnd < prevStart &&
                        LaunchScheduleEvaluatorHelper.TryParseDate(previous.OneTimeDate, out DateOnly prevDate))
                    {
                        draft.OneTimeDate = prevDate.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    }
                }

                LoadFieldsFromSchedule(draft, accounts);
            });
        }

        public void OpenNewForAccounts(IReadOnlyList<Account> accounts, string? placeId, string? jobId, IReadOnlyList<Account> allAccounts)
        {
            OpenDraftScope(isEditingExisting: false, scheduleId: Guid.NewGuid().ToString("N"), createdAtUtc: default, isEnabled: true, () =>
            {
                var draft = new LaunchSchedule
                {
                    TargetMode = ScheduleTargetMode.SpecificAccounts,
                    LaunchMode = ScheduleLaunchMode.JoinServer,
                    JobId = jobId?.Trim() ?? string.Empty
                };

                if (accounts != null)
                {
                    foreach (Account acc in accounts)
                    {
                        if (acc != null)
                            draft.TargetAccounts.Add(new ScheduleAccountRef { UserId = acc.UserID, Username = acc.Username ?? string.Empty });
                    }
                }

                LoadFieldsFromSchedule(draft, allAccounts);
                Name = string.Empty;

                long parsedPlaceId = ParseStrictPlaceId(placeId);
                PlaceIdText = parsedPlaceId > 0
                    ? parsedPlaceId.ToString(CultureInfo.InvariantCulture)
                    : (placeId?.Trim() ?? string.Empty);
                JobIdText = jobId?.Trim() ?? string.Empty;
            });
        }

        public void Close() => IsOpen = false;

        public void RefreshAccountsCatalog(IReadOnlyList<Account> accounts)
        {
            if (!IsOpen)
                return;

            lock (_editorLock)
            {
                var checkedIds = new HashSet<long>(_allChecklistItems.Where(i => i.IsChecked && i.UserId > 0).Select(i => i.UserId));
                var checkedNames = new HashSet<string>(_allChecklistItems.Where(i => i.IsChecked && !string.IsNullOrWhiteSpace(i.Username)).Select(i => i.Username), StringComparer.OrdinalIgnoreCase);

                RebuildAccountChecklistAndGroups(accounts, checkedIds, checkedNames);
            }
            OnPropertyChanged(nameof(SelectedAccountsSummaryText));
            RefreshLiveDiagnostics();
        }

        public void NotifyStreamerModeChanged()
        {
            List<ScheduleAccountChecklistItemViewModel> snapshot;
            lock (_editorLock)
                snapshot = _allChecklistItems.ToList();

            OnPropertyChanged(nameof(JobIdMaskChar));
            foreach (ScheduleAccountChecklistItemViewModel item in snapshot)
                item.NotifyStreamerModeChanged();
        }

        public void NotifyLanguageChanged()
        {
            List<ScheduleAccountChecklistItemViewModel> snapshot;
            lock (_editorLock)
                snapshot = _allChecklistItems.ToList();

            OnPropertyChanged(nameof(EditorTitleText));
            OnPropertyChanged(nameof(SelectedAccountsSummaryText));
            foreach (ScheduleAccountChecklistItemViewModel item in snapshot)
                item.NotifyLanguageChanged();
            foreach (ScheduleColorSwatchViewModel swatch in _availableColorSwatches)
                swatch.NotifyLanguageChanged();
            foreach (ScheduleArrangeOptionViewModel option in _arrangeModeOptions)
                option.NotifyLanguageChanged();
            RefreshLiveDiagnostics();
        }

        private void OpenDraftScope(bool isEditingExisting, string scheduleId, DateTimeOffset createdAtUtc, bool isEnabled, Action populate)
        {
            lock (_editorLock)
            {
                _suppressDiagnostics = true;
                try
                {
                    IsEditingExisting = isEditingExisting;
                    _editingScheduleId = scheduleId;
                    _editingCreatedAtUtc = createdAtUtc;
                    _editingIsEnabled = isEnabled;
                    populate();
                }
                finally
                {
                    _suppressDiagnostics = false;
                }
            }

            RefreshLiveDiagnostics();
            IsOpen = true;
        }

        private void InitializeOptionCollections()
        {
            string[] colorKeys =
            {
                "Sched_Color_Sky",
                "Sched_Color_Emerald",
                "Sched_Color_Violet",
                "Sched_Color_Amber",
                "Sched_Color_Rose",
                "Sched_Color_Teal"
            };
            for (int i = 0; i < SchedulerBrushHelper.DefaultColorSwatches.Length; i++)
                _availableColorSwatches.Add(new ScheduleColorSwatchViewModel(SchedulerBrushHelper.DefaultColorSwatches[i], colorKeys[i]));

            foreach (ScheduleArrangeMode mode in Enum.GetValues<ScheduleArrangeMode>())
                _arrangeModeOptions.Add(new ScheduleArrangeOptionViewModel(mode));

            _selectedArrangeModeOption = _arrangeModeOptions.FirstOrDefault(o => o.Mode == _postLaunchArrange);
            UpdateSelectedColorSwatch();
        }

        private void LoadFieldsFromSchedule(LaunchSchedule schedule, IReadOnlyList<Account> accounts)
        {
            DateOnly today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
            Name = schedule.Name ?? string.Empty;
            ColorHex = schedule.ColorHex;
            Recurrence = schedule.Recurrence;
            OneTimeDateText = !string.IsNullOrWhiteSpace(schedule.OneTimeDate)
                ? schedule.OneTimeDate!
                : today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            ApplyDaysOfWeek(schedule.DaysOfWeek);

            StartTimeText = schedule.StartTime ?? "08:00";
            HasEndTime = schedule.HasEndTime;
            EndTimeText = schedule.EndTime ?? "12:00";
            TargetMode = schedule.TargetMode;
            LaunchMode = schedule.LaunchMode;
            PlaceIdText = schedule.PlaceId > 0 ? schedule.PlaceId.ToString(CultureInfo.InvariantCulture) : string.Empty;
            JobIdText = schedule.JobId ?? string.Empty;
            CloseOtherTabsBeforeLaunch = schedule.CloseOtherTabsBeforeLaunch;
            HandoffDelaySeconds = schedule.HandoffDelaySeconds;
            PostLaunchArrange = schedule.PostLaunchArrange;
            CatchUpIfActiveOnStartup = schedule.CatchUpIfActiveOnStartup;
            SendWebhookNotification = schedule.SendWebhookNotification;
            AccountSearchText = string.Empty;

            var checkedIds = new HashSet<long>(schedule.TargetAccounts.Where(a => a.UserId > 0).Select(a => a.UserId));
            var checkedNames = new HashSet<string>(schedule.TargetAccounts.Where(a => !string.IsNullOrWhiteSpace(a.Username)).Select(a => a.Username), StringComparer.OrdinalIgnoreCase);

            RebuildAccountChecklistAndGroups(accounts, checkedIds, checkedNames);
            if (!string.IsNullOrWhiteSpace(schedule.TargetGroup))
            {
                if (!_availableGroups.Contains(schedule.TargetGroup))
                    _availableGroups.Add(schedule.TargetGroup);
                SelectedGroup = schedule.TargetGroup;
            }
            else
            {
                SelectedGroup = _availableGroups.FirstOrDefault() ?? "Default";
            }

            RebuildQuickPickGames();
            OnPropertyChanged(nameof(HasWebhookConfigured));
            OnPropertyChanged(nameof(JobIdMaskChar));
            OnPropertyChanged(nameof(SelectedAccountsSummaryText));
        }

        private void RebuildAccountChecklistAndGroups(
            IReadOnlyList<Account> accounts,
            HashSet<long> checkedIds,
            HashSet<string> checkedNames)
        {
            _allChecklistItems.Clear();
            var groupSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Account acc in accounts)
            {
                if (acc == null)
                    continue;

                string group = string.IsNullOrWhiteSpace(acc.Group) ? "Default" : acc.Group.Trim();
                groupSet.Add(group);

                var item = new ScheduleAccountChecklistItemViewModel(acc, _settings, OnChecklistItemToggled);
                bool shouldCheck = (acc.UserID > 0 && checkedIds.Contains(acc.UserID)) ||
                                   (!string.IsNullOrWhiteSpace(acc.Username) && checkedNames.Contains(acc.Username));
                item.SetCheckedSilent(shouldCheck);
                _allChecklistItems.Add(item);
            }

            if (groupSet.Count == 0)
                groupSet.Add("Default");

            _availableGroups.Clear();
            foreach (string g in groupSet.OrderBy(g => g, NaturalStringComparer.Instance))
                _availableGroups.Add(g);

            if (string.IsNullOrWhiteSpace(_selectedGroup) || !_availableGroups.Contains(_selectedGroup))
                _selectedGroup = _availableGroups.FirstOrDefault() ?? "Default";
            OnPropertyChanged(nameof(SelectedGroup));

            ApplyChecklistFilter();
        }

        private void ApplyChecklistFilter()
        {
            string query = (_accountSearchText ?? string.Empty).Trim();
            _filteredAccountChecklist.Clear();

            foreach (ScheduleAccountChecklistItemViewModel item in _allChecklistItems)
            {
                if (string.IsNullOrEmpty(query) ||
                    item.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.DisplayAlias.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.Group.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.UserId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.Ordinal))
                {
                    _filteredAccountChecklist.Add(item);
                }
            }
        }

        private void RebuildQuickPickGames()
        {
            _recentAndFavoriteGames.Clear();
            var seenPlaceIds = new HashSet<long>();

            IReadOnlyList<FavoriteGameItemViewModel>? favorites = _getFavorites();
            if (favorites != null)
            {
                foreach (FavoriteGameItemViewModel fav in favorites)
                {
                    if (fav.PlaceId > 0 && seenPlaceIds.Add(fav.PlaceId))
                    {
                        _recentAndFavoriteGames.Add(new ScheduleGameQuickPickViewModel
                        {
                            PlaceId = fav.PlaceId,
                            Name = string.IsNullOrWhiteSpace(fav.Name) ? $"Place {fav.PlaceId}" : fav.Name,
                            JobId = fav.PrivateServer ?? string.Empty,
                            IsFavorite = true
                        });
                    }
                }
            }

            IReadOnlyList<RecentGameItemViewModel>? recents = _getRecentGames();
            if (recents != null)
            {
                foreach (RecentGameItemViewModel rec in recents)
                {
                    if (rec.PlaceId > 0 && seenPlaceIds.Add(rec.PlaceId))
                    {
                        _recentAndFavoriteGames.Add(new ScheduleGameQuickPickViewModel
                        {
                            PlaceId = rec.PlaceId,
                            Name = string.IsNullOrWhiteSpace(rec.Name) ? $"Place {rec.PlaceId}" : rec.Name,
                            JobId = string.Empty,
                            IsFavorite = false
                        });
                    }
                }
            }

            OnPropertyChanged(nameof(HasRecentAndFavoriteGames));
        }

        private void OnChecklistItemToggled(ScheduleAccountChecklistItemViewModel _)
        {
            if (_suppressDiagnostics)
                return;

            OnPropertyChanged(nameof(SelectedAccountsSummaryText));
            RefreshLiveDiagnostics();
        }
    }
}
