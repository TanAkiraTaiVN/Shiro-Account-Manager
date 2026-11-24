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
        private string ComputeTargetSummaryText()
        {
            List<ScheduleAccountChecklistItemViewModel> snapshot;
            lock (_editorLock)
                snapshot = _allChecklistItems.ToList();

            if (_targetMode == ScheduleTargetMode.Group)
            {
                List<ScheduleAccountChecklistItemViewModel> members = snapshot
                    .Where(a => string.Equals(a.Group, _selectedGroup, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return LocalizationService.Format("Sched_Editor_GroupSummary", members.Count, members.Count(m => m.IsReady));
            }

            List<ScheduleAccountChecklistItemViewModel> selected = snapshot.Where(a => a.IsChecked).ToList();
            return LocalizationService.Format("Sched_Editor_SelectedSummary", selected.Count, selected.Count(s => s.IsReady));
        }

        private void RefreshLiveDiagnostics()
        {
            if (_suppressDiagnostics)
                return;

            LaunchSchedule draft = BuildDraftSchedule();
            ScheduleValidationResult validation = LaunchScheduleEvaluatorHelper.Validate(draft);
            ValidationError = validation.IsValid || string.IsNullOrEmpty(validation.ErrorKey)
                ? null
                : LocalizationService.Get(validation.ErrorKey);

            LiveConflictWarning = ComputeLiveConflictWarning(draft);
        }

        private string? ComputeLiveConflictWarning(LaunchSchedule draft)
        {
            if (!LaunchScheduleEvaluatorHelper.TryParseTime(draft.StartTime, out TimeOnly start))
                return null;
            if (draft.HasEndTime && (!LaunchScheduleEvaluatorHelper.TryParseTime(draft.EndTime, out TimeOnly end) || end == start))
                return null;

            bool hasTargets = draft.TargetMode == ScheduleTargetMode.Group
                ? !string.IsNullOrWhiteSpace(draft.TargetGroup)
                : draft.TargetAccounts.Count > 0;
            if (!hasTargets)
                return null;

            LaunchSchedule previewDraft = draft.Clone();
            previewDraft.IsEnabled = true;
            if (string.IsNullOrWhiteSpace(previewDraft.Name))
                previewDraft.Name = LocalizationService.Get("Sched_Editor_NewTitle");

            var candidateList = new List<LaunchSchedule>(_getExistingSchedules().Where(s => !string.Equals(s.Id, previewDraft.Id, StringComparison.Ordinal)))
            {
                previewDraft
            };

            IReadOnlyList<Account> allAccounts = _accountRepository.GetAll();
            DateOnly today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
            IReadOnlyList<ScheduleConflictInfo> conflicts = LaunchScheduleEvaluatorHelper.DetectConflicts(
                candidateList,
                today,
                s => SchedulerBrushHelper.ResolveAccountKeys(s, allAccounts));

            List<ScheduleConflictInfo> relevant = conflicts
                .Where(c => string.Equals(c.ScheduleAId, previewDraft.Id, StringComparison.Ordinal) ||
                            string.Equals(c.ScheduleBId, previewDraft.Id, StringComparison.Ordinal))
                .ToList();

            return relevant.Count == 0 ? null : SchedulerBrushHelper.BuildConflictBannerText(relevant);
        }

        private LaunchSchedule BuildDraftSchedule()
        {
            List<ScheduleAccountRef> targets;
            lock (_editorLock)
            {
                targets = _allChecklistItems
                    .Where(a => a.IsChecked)
                    .Select(a => new ScheduleAccountRef { UserId = a.UserId, Username = a.Username })
                    .ToList();
            }

            return new LaunchSchedule
            {
                Id = string.IsNullOrWhiteSpace(_editingScheduleId) ? Guid.NewGuid().ToString("N") : _editingScheduleId,
                Name = (_name ?? string.Empty).Trim(),
                IsEnabled = _isEditingExisting ? _editingIsEnabled : true,
                ColorHex = ColorHex,
                Recurrence = _recurrence,
                DaysOfWeek = ReadDaysOfWeek(),
                OneTimeDate = _recurrence == ScheduleRecurrence.OneTime
                    ? (_oneTimeDateText ?? string.Empty).Trim()
                    : null,
                StartTime = LaunchScheduleEvaluatorHelper.NormalizeTimeText(_startTimeText),
                HasEndTime = _hasEndTime,
                EndTime = _hasEndTime ? LaunchScheduleEvaluatorHelper.NormalizeTimeText(_endTimeText) : null,
                TargetMode = _targetMode,
                TargetGroup = (_selectedGroup ?? string.Empty).Trim(),
                TargetAccounts = targets,
                LaunchMode = _launchMode,
                PlaceId = _launchMode == ScheduleLaunchMode.JoinServer ? ParseStrictPlaceId(_placeIdText) : 0,
                JobId = (_jobIdText ?? string.Empty).Trim(),
                CloseOtherTabsBeforeLaunch = _closeOtherTabsBeforeLaunch,
                HandoffDelaySeconds = Math.Clamp(_handoffDelaySeconds, 0, 15),
                PostLaunchArrange = _postLaunchArrange,
                CatchUpIfActiveOnStartup = _catchUpIfActiveOnStartup,
                SendWebhookNotification = _sendWebhookNotification,
                CreatedAtUtc = _editingCreatedAtUtc
            };
        }

        private static long ParseStrictPlaceId(string? rawInput)
        {
            string trimmed = (rawInput ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(trimmed))
                return 0;

            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long direct))
                return direct > 0 ? direct : 0;

            return trimmed.Contains("roblox.com", StringComparison.OrdinalIgnoreCase)
                ? AccountLauncherService.ParsePlaceId(trimmed)
                : 0;
        }

        private ScheduleDaysOfWeek ReadDaysOfWeek()
        {
            ScheduleDaysOfWeek days = ScheduleDaysOfWeek.None;
            if (_dayMon) days |= ScheduleDaysOfWeek.Monday;
            if (_dayTue) days |= ScheduleDaysOfWeek.Tuesday;
            if (_dayWed) days |= ScheduleDaysOfWeek.Wednesday;
            if (_dayThu) days |= ScheduleDaysOfWeek.Thursday;
            if (_dayFri) days |= ScheduleDaysOfWeek.Friday;
            if (_daySat) days |= ScheduleDaysOfWeek.Saturday;
            if (_daySun) days |= ScheduleDaysOfWeek.Sunday;
            return days;
        }

        private void ApplyDaysOfWeek(ScheduleDaysOfWeek days)
        {
            _dayMon = days.HasFlag(ScheduleDaysOfWeek.Monday);
            _dayTue = days.HasFlag(ScheduleDaysOfWeek.Tuesday);
            _dayWed = days.HasFlag(ScheduleDaysOfWeek.Wednesday);
            _dayThu = days.HasFlag(ScheduleDaysOfWeek.Thursday);
            _dayFri = days.HasFlag(ScheduleDaysOfWeek.Friday);
            _daySat = days.HasFlag(ScheduleDaysOfWeek.Saturday);
            _daySun = days.HasFlag(ScheduleDaysOfWeek.Sunday);
            OnPropertyChanged(nameof(DayMon));
            OnPropertyChanged(nameof(DayTue));
            OnPropertyChanged(nameof(DayWed));
            OnPropertyChanged(nameof(DayThu));
            OnPropertyChanged(nameof(DayFri));
            OnPropertyChanged(nameof(DaySat));
            OnPropertyChanged(nameof(DaySun));
        }

        private void Save()
        {
            LaunchSchedule draft = BuildDraftSchedule();
            ScheduleValidationResult result = _onSave(draft);
            if (!result.IsValid)
            {
                ValidationError = !string.IsNullOrEmpty(result.ErrorKey)
                    ? LocalizationService.Get(result.ErrorKey)
                    : LocalizationService.Get("Sched_Err_NameRequired");
                return;
            }

            ValidationError = null;
            IsOpen = false;
        }

        private void ApplyDurationHours(object? parameter)
        {
            int hours = parameter switch
            {
                int i => i,
                string s when int.TryParse(s.Trim().TrimStart('+').TrimEnd('h', 'H'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
                _ => 0
            };

            if (hours <= 0 || hours >= 24)
                return;

            if (!LaunchScheduleEvaluatorHelper.TryParseTime(_startTimeText, out TimeOnly start))
                start = new TimeOnly(8, 0);

            HasEndTime = true;
            EndTimeText = start.AddHours(hours).ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        private void SelectReadyAccounts()
        {
            TargetMode = ScheduleTargetMode.SpecificAccounts;
            SetBulkCheckedState(static item => item.IsReady);
        }

        private void UseDashboardSelection()
        {
            IReadOnlyList<Account> selected = _getSelectedDashboardAccounts();
            var selectedIds = new HashSet<long>(selected.Where(a => a.UserID > 0).Select(a => a.UserID));
            var selectedNames = new HashSet<string>(selected.Where(a => !string.IsNullOrWhiteSpace(a.Username)).Select(a => a.Username), StringComparer.OrdinalIgnoreCase);

            TargetMode = ScheduleTargetMode.SpecificAccounts;
            SetBulkCheckedState(item =>
                (item.UserId > 0 && selectedIds.Contains(item.UserId)) ||
                (!string.IsNullOrWhiteSpace(item.Username) && selectedNames.Contains(item.Username)));
        }

        private void ClearAccounts() => SetBulkCheckedState(static _ => false);

        private void SetBulkCheckedState(Func<ScheduleAccountChecklistItemViewModel, bool> predicate)
        {
            _suppressDiagnostics = true;
            try
            {
                foreach (ScheduleAccountChecklistItemViewModel item in _allChecklistItems)
                    item.SetCheckedSilent(predicate(item));
            }
            finally
            {
                _suppressDiagnostics = false;
            }

            OnPropertyChanged(nameof(SelectedAccountsSummaryText));
            RefreshLiveDiagnostics();
        }

        private void SelectRecentOrFavoriteGame(object? parameter)
        {
            long placeId = 0;
            string? jobId = string.Empty;

            switch (parameter)
            {
                case ScheduleGameQuickPickViewModel pick:
                    placeId = pick.PlaceId;
                    jobId = pick.JobId;
                    break;
                case FavoriteGameItemViewModel fav:
                    placeId = fav.PlaceId;
                    jobId = fav.PrivateServer ?? string.Empty;
                    break;
                case RecentGameItemViewModel rec:
                    placeId = rec.PlaceId;
                    break;
                case long id:
                    placeId = id;
                    break;
            }

            if (placeId <= 0)
                return;

            LaunchMode = ScheduleLaunchMode.JoinServer;
            PlaceIdText = placeId.ToString(CultureInfo.InvariantCulture);
            JobIdText = jobId ?? string.Empty;
        }

        private void SetColor(object? parameter)
        {
            string? hex = parameter switch
            {
                ScheduleColorSwatchViewModel swatch => swatch.ColorHex,
                string s => s,
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(hex))
                ColorHex = hex;
        }

        private void SetRecurrence(object? parameter)
        {
            if (parameter is ScheduleRecurrence r)
                Recurrence = r;
            else if (parameter is string s && Enum.TryParse(s, ignoreCase: true, out ScheduleRecurrence parsed))
                Recurrence = parsed;
        }

        private void SetTargetMode(object? parameter)
        {
            if (parameter is ScheduleTargetMode mode)
                TargetMode = mode;
            else if (parameter is string s && Enum.TryParse(s, ignoreCase: true, out ScheduleTargetMode parsed))
                TargetMode = parsed;
        }

        private void SetLaunchMode(object? parameter)
        {
            if (parameter is ScheduleLaunchMode mode)
                LaunchMode = mode;
            else if (parameter is string s && Enum.TryParse(s, ignoreCase: true, out ScheduleLaunchMode parsed))
                LaunchMode = parsed;
        }

        private void SetQuickDate(object? parameter)
        {
            DateOnly today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
            int addDays = parameter switch
            {
                int offset => offset,
                string s when string.Equals(s, "Tomorrow", StringComparison.OrdinalIgnoreCase) => 1,
                _ => 0
            };
            OneTimeDateText = today.AddDays(addDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private void SetWeekdayPreset(object? parameter)
        {
            ScheduleDaysOfWeek preset = parameter switch
            {
                ScheduleDaysOfWeek d => d,
                string s when Enum.TryParse(s, ignoreCase: true, out ScheduleDaysOfWeek parsed) => parsed,
                _ => ScheduleDaysOfWeek.All
            };
            ApplyDaysOfWeek(preset);
            RefreshLiveDiagnostics();
        }

        private void UpdateSelectedColorSwatch()
        {
            foreach (ScheduleColorSwatchViewModel swatch in _availableColorSwatches)
                swatch.IsSelected = string.Equals(swatch.ColorHex, _colorHex, StringComparison.OrdinalIgnoreCase);
        }
    }
}
