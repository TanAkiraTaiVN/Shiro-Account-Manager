#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Shiro.Services;

namespace Shiro.ViewModels
{
    internal static class SchedulerBrushHelper
    {
        public static readonly ImmutableSolidColorBrush HealthyBg = Freeze("#11261B");
        public static readonly ImmutableSolidColorBrush HealthyBorder = Freeze("#1E4931");
        public static readonly ImmutableSolidColorBrush HealthyFg = Freeze("#4ADE80");

        public static readonly ImmutableSolidColorBrush InfoBg = Freeze("#102131");
        public static readonly ImmutableSolidColorBrush InfoBorder = Freeze("#1E3E5C");
        public static readonly ImmutableSolidColorBrush InfoFg = Freeze("#38BDF8");

        public static readonly ImmutableSolidColorBrush WarnBg = Freeze("#261D0E");
        public static readonly ImmutableSolidColorBrush WarnBorder = Freeze("#593C14");
        public static readonly ImmutableSolidColorBrush WarnFg = Freeze("#FBBF24");

        public static readonly ImmutableSolidColorBrush DangerBg = Freeze("#271217");
        public static readonly ImmutableSolidColorBrush DangerBorder = Freeze("#541D28");
        public static readonly ImmutableSolidColorBrush DangerFg = Freeze("#F87171");

        public static readonly ImmutableSolidColorBrush DefaultBg = Freeze("#1A1A26");
        public static readonly ImmutableSolidColorBrush DefaultBorder = Freeze("#282838");
        public static readonly ImmutableSolidColorBrush DefaultFg = Freeze("#A1A1B2");

        public static readonly string[] DefaultColorSwatches =
        {
            "#38BDF8",
            "#4ADE80",
            "#A855F7",
            "#FBBF24",
            "#FB7185",
            "#2DD4BF"
        };

        private static readonly ConcurrentDictionary<string, ImmutableSolidColorBrush> s_accentBrushes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, ImmutableSolidColorBrush> s_fillBrushes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, ImmutableSolidColorBrush> s_borderBrushes = new(StringComparer.OrdinalIgnoreCase);

        private static ImmutableSolidColorBrush Freeze(string hex) => new(Color.Parse(hex));

        public static Color ParseAccentColor(string? colorHex)
        {
            if (!string.IsNullOrWhiteSpace(colorHex) && Color.TryParse(colorHex.Trim(), out Color parsed))
                return parsed;

            return Color.Parse("#38BDF8");
        }

        public static string NormalizeColorHex(string? colorHex) =>
            !string.IsNullOrWhiteSpace(colorHex) && Color.TryParse(colorHex.Trim(), out _)
                ? colorHex.Trim()
                : "#38BDF8";

        public static IBrush GetAccentBrush(string? colorHex) =>
            s_accentBrushes.GetOrAdd(NormalizeColorHex(colorHex), static hex => new ImmutableSolidColorBrush(ParseAccentColor(hex)));

        public static IBrush GetBlockFillBrush(string? colorHex, bool hasConflict)
        {
            string key = NormalizeColorHex(colorHex) + (hasConflict ? "|C" : "|N");
            return s_fillBrushes.GetOrAdd(key, static k =>
            {
                Color c = ParseAccentColor(k[..^2]);
                byte alpha = k.EndsWith("|C", StringComparison.Ordinal) ? (byte)64 : (byte)46;
                return new ImmutableSolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            });
        }

        public static IBrush GetBlockBorderBrush(string? colorHex, bool hasConflict)
        {
            if (hasConflict)
                return WarnFg;

            return s_borderBrushes.GetOrAdd(NormalizeColorHex(colorHex), static hex =>
            {
                Color c = ParseAccentColor(hex);
                return new ImmutableSolidColorBrush(Color.FromArgb(150, c.R, c.G, c.B));
            });
        }

        public static string FormatMinutes(int minutes)
        {
            int clamped = Math.Clamp(minutes, 0, 1440);
            if (clamped == 1440)
                return "24:00";
            return $"{clamped / 60:D2}:{clamped % 60:D2}";
        }

        public static string FormatWindowBadge(LaunchSchedule schedule)
        {
            string start = schedule.StartTime ?? "00:00";
            if (!schedule.HasEndTime || string.IsNullOrWhiteSpace(schedule.EndTime))
                return LocalizationService.Format("Sched_Window_OpenOnlyFormat", start);

            if (LaunchScheduleEvaluatorHelper.IsOvernight(schedule))
                return LocalizationService.Format("Sched_Window_OvernightFormat", start, schedule.EndTime);

            return LocalizationService.Format("Sched_Window_RangeFormat", start, schedule.EndTime);
        }

        public static string FormatRecurrence(LaunchSchedule schedule)
        {
            switch (schedule.Recurrence)
            {
                case ScheduleRecurrence.OneTime:
                    return LocalizationService.Format("Sched_Recurrence_OneTime", schedule.OneTimeDate ?? string.Empty);
                case ScheduleRecurrence.Weekly:
                    ScheduleDaysOfWeek masked = schedule.DaysOfWeek & ScheduleDaysOfWeek.All;
                    if (masked == ScheduleDaysOfWeek.All)
                        return LocalizationService.Get("Sched_Recurrence_Daily");
                    if (masked == ScheduleDaysOfWeek.Weekdays)
                        return LocalizationService.Get("Sched_Recurrence_Weekdays");
                    if (masked == ScheduleDaysOfWeek.Weekends)
                        return LocalizationService.Get("Sched_Recurrence_Weekends");

                    var dayLabels = new List<string>(7);
                    if (masked.HasFlag(ScheduleDaysOfWeek.Monday)) dayLabels.Add(LocalizationService.Get("Sched_Day_Mon"));
                    if (masked.HasFlag(ScheduleDaysOfWeek.Tuesday)) dayLabels.Add(LocalizationService.Get("Sched_Day_Tue"));
                    if (masked.HasFlag(ScheduleDaysOfWeek.Wednesday)) dayLabels.Add(LocalizationService.Get("Sched_Day_Wed"));
                    if (masked.HasFlag(ScheduleDaysOfWeek.Thursday)) dayLabels.Add(LocalizationService.Get("Sched_Day_Thu"));
                    if (masked.HasFlag(ScheduleDaysOfWeek.Friday)) dayLabels.Add(LocalizationService.Get("Sched_Day_Fri"));
                    if (masked.HasFlag(ScheduleDaysOfWeek.Saturday)) dayLabels.Add(LocalizationService.Get("Sched_Day_Sat"));
                    if (masked.HasFlag(ScheduleDaysOfWeek.Sunday)) dayLabels.Add(LocalizationService.Get("Sched_Day_Sun"));
                    return LocalizationService.Format("Sched_Recurrence_WeeklyFormat", string.Join(", ", dayLabels));
                default:
                    return LocalizationService.Get("Sched_Recurrence_Daily");
            }
        }

        public static List<Account> ResolveScheduleAccounts(LaunchSchedule schedule, IReadOnlyList<Account> allAccounts)
        {
            var matched = new List<Account>();
            if (allAccounts.Count == 0)
                return matched;

            var seen = new HashSet<Account>(ReferenceEqualityComparer.Instance);
            if (schedule.TargetMode == ScheduleTargetMode.Group)
            {
                foreach (Account acc in allAccounts)
                {
                    if (acc != null && string.Equals(acc.Group, schedule.TargetGroup, StringComparison.OrdinalIgnoreCase) && seen.Add(acc))
                        matched.Add(acc);
                }
                return matched;
            }

            foreach (ScheduleAccountRef target in schedule.TargetAccounts)
            {
                Account? found = target.UserId > 0
                    ? allAccounts.FirstOrDefault(a => a != null && a.UserID == target.UserId)
                    : null;
                found ??= !string.IsNullOrWhiteSpace(target.Username)
                    ? allAccounts.FirstOrDefault(a => a != null && string.Equals(a.Username, target.Username, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (found != null && seen.Add(found))
                    matched.Add(found);
            }

            return matched;
        }

        public static IReadOnlyCollection<string> ResolveAccountKeys(LaunchSchedule schedule, IReadOnlyList<Account> allAccounts)
        {
            List<Account> resolved = ResolveScheduleAccounts(schedule, allAccounts);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Account acc in resolved)
            {
                if (acc.UserID > 0)
                    keys.Add($"id:{acc.UserID}");
                else if (!string.IsNullOrWhiteSpace(acc.Username))
                    keys.Add($"name:{acc.Username.Trim()}");
            }

            if (schedule.TargetMode == ScheduleTargetMode.SpecificAccounts && resolved.Count == 0)
            {
                foreach (ScheduleAccountRef target in schedule.TargetAccounts)
                {
                    if (target.UserId > 0)
                        keys.Add($"id:{target.UserId}");
                    else if (!string.IsNullOrWhiteSpace(target.Username))
                        keys.Add($"name:{target.Username.Trim()}");
                }
            }

            return keys;
        }

        public static string FormatConflictSummary(ScheduleConflictInfo conflict)
        {
            string dateText = conflict.Date.ToString("MM-dd", CultureInfo.InvariantCulture);
            return conflict.Kind == ScheduleConflictKind.ExclusiveCloseCollision
                ? LocalizationService.Format("Sched_Conflict_ExclusiveFormat", conflict.ScheduleBName, conflict.ScheduleAName, dateText)
                : LocalizationService.Format("Sched_Conflict_OverlapFormat", conflict.ScheduleAName, conflict.ScheduleBName, conflict.SharedAccountCount, dateText);
        }

        public static string BuildConflictBannerText(IReadOnlyList<ScheduleConflictInfo> conflicts)
        {
            if (conflicts.Count == 0)
                return string.Empty;

            string firstSummary = FormatConflictSummary(conflicts[0]);
            return conflicts.Count == 1
                ? LocalizationService.Format("Sched_Conflict_BannerSingle", firstSummary)
                : LocalizationService.Format("Sched_Conflict_BannerMultiple", conflicts.Count, firstSummary);
        }

        public static bool IsAccountReady(Account acc) =>
            acc.Valid && !acc.IsBannedOrLocked && string.IsNullOrWhiteSpace(acc.AccountStatusIssue);

        public static string FormatTargetSummary(LaunchSchedule schedule, IReadOnlyList<Account> allAccounts, ISettingsService? settings)
        {
            List<Account> resolved = ResolveScheduleAccounts(schedule, allAccounts);
            int readyCount = resolved.Count(IsAccountReady);

            if (schedule.TargetMode == ScheduleTargetMode.Group)
                return LocalizationService.Format("Sched_Target_GroupSummary", schedule.TargetGroup, readyCount);

            if (schedule.TargetAccounts.Count == 0)
                return LocalizationService.Get("Sched_Target_NoAccounts");

            var displayNames = new List<string>(schedule.TargetAccounts.Count);
            foreach (ScheduleAccountRef target in schedule.TargetAccounts)
            {
                Account? matched = target.UserId > 0
                    ? resolved.FirstOrDefault(a => a.UserID == target.UserId)
                    : null;
                matched ??= resolved.FirstOrDefault(a => string.Equals(a.Username, target.Username, StringComparison.OrdinalIgnoreCase));
                string rawName = !string.IsNullOrWhiteSpace(matched?.Username) ? matched.Username : target.Username;
                if (!string.IsNullOrWhiteSpace(rawName))
                    displayNames.Add(StreamerModeService.GetDisplayUsername(rawName, settings));
            }

            if (displayNames.Count == 0)
                return LocalizationService.Get("Sched_Target_NoAccounts");

            if (displayNames.Count <= 3)
                return LocalizationService.Format("Sched_Target_AccountsNamedSummary", string.Join(", ", displayNames), readyCount);

            string firstTwo = string.Join(", ", displayNames.Take(2));
            return LocalizationService.Format("Sched_Target_AccountsManySummary", firstTwo, displayNames.Count - 2, readyCount);
        }

        public static string FormatDestinationSummary(LaunchSchedule schedule)
        {
            if (schedule.LaunchMode == ScheduleLaunchMode.OpenHome)
                return LocalizationService.Get("Sched_Dest_Home");

            string placeLabel = GameInfoService.TryGetCachedName(schedule.PlaceId, out string gameName) && !string.IsNullOrWhiteSpace(gameName)
                ? $"{gameName} ({schedule.PlaceId})"
                : LocalizationService.Format("Sched_Dest_Place", schedule.PlaceId);

            if (string.IsNullOrWhiteSpace(schedule.JobId))
                return LocalizationService.Format("Sched_Dest_PublicFormat", placeLabel);

            string shownJob = StreamerModeService.IsActive
                ? StreamerModeService.MaskJobId(schedule.JobId)
                : schedule.JobId.Trim();
            return LocalizationService.Format("Sched_Dest_JobFormat", placeLabel, shownJob);
        }

        public static string FormatOptionsSummary(LaunchSchedule schedule)
        {
            var parts = new List<string>(5);
            if (schedule.PostLaunchArrange != ScheduleArrangeMode.None)
                parts.Add(LocalizationService.Format("Sched_Opt_TileFormat", FormatArrangeLabel(schedule.PostLaunchArrange)));
            if (schedule.CloseOtherTabsBeforeLaunch)
                parts.Add(LocalizationService.Get("Sched_Opt_CloseOthers"));
            if (schedule.CloseOtherTabsBeforeLaunch || schedule.HandoffDelaySeconds != 2)
                parts.Add(LocalizationService.Format("Sched_Opt_HandoffFormat", schedule.HandoffDelaySeconds));
            if (schedule.CatchUpIfActiveOnStartup)
                parts.Add(LocalizationService.Get("Sched_Opt_CatchUp"));
            if (schedule.SendWebhookNotification)
                parts.Add(LocalizationService.Get("Sched_Opt_Webhook"));
            return string.Join(" • ", parts);
        }

        public static string FormatArrangeLabel(ScheduleArrangeMode mode) => mode switch
        {
            ScheduleArrangeMode.Auto => LocalizationService.Get("Sched_Arrange_Auto"),
            ScheduleArrangeMode.Grid2x2 => LocalizationService.Get("Sched_Arrange_Grid2x2"),
            ScheduleArrangeMode.Grid3x2 => LocalizationService.Get("Sched_Arrange_Grid3x2"),
            ScheduleArrangeMode.Grid4x3 => LocalizationService.Get("Sched_Arrange_Grid4x3"),
            ScheduleArrangeMode.Cascade => LocalizationService.Get("Sched_Arrange_Cascade"),
            _ => LocalizationService.Get("Sched_Arrange_None")
        };

        public static string MaskHistorySummary(string rawSummary, IReadOnlyList<string> knownUsernames, ISettingsService? settings)
        {
            if (string.IsNullOrEmpty(rawSummary))
                return string.Empty;

            if (!StreamerModeService.ShouldMaskUsernames(settings))
                return rawSummary;

            string masked = rawSummary;
            foreach (string username in knownUsernames.OrderByDescending(u => u.Length))
            {
                if (string.IsNullOrWhiteSpace(username))
                    continue;

                string replacement = StreamerModeService.MaskUsername(username);
                masked = Regex.Replace(
                    masked,
                    $@"(?<![A-Za-z0-9_]){Regex.Escape(username)}(?![A-Za-z0-9_])",
                    replacement,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            return masked;
        }
    }
}
