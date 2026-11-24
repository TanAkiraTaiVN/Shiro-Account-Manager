#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests.Ui
{
    public sealed partial class SchedulerViewModelTests
    {
        [Fact]
        public void TimelineBlock_LateNightStart_ClampsSafelyWithoutOverflowOrException()
        {
            Account alpha = MakeAccount("NightUser", 201);
            var (svc, repo, settings, time, _) = CreateServiceHarness(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero), alpha);
            using (svc)
            {
                Assert.True(svc.UpsertSchedule(new LaunchSchedule
                {
                    Id = "late-overnight",
                    Name = "Late Overnight",
                    IsEnabled = true,
                    StartTime = "23:45",
                    HasEndTime = true,
                    EndTime = "02:00",
                    TargetMode = ScheduleTargetMode.SpecificAccounts,
                    TargetAccounts = new List<ScheduleAccountRef> { new() { UserId = 201, Username = "NightUser" } },
                    LaunchMode = ScheduleLaunchMode.OpenHome
                }).IsValid);

                Assert.True(svc.UpsertSchedule(new LaunchSchedule
                {
                    Id = "late-open-only",
                    Name = "Late Open Only",
                    IsEnabled = true,
                    StartTime = "23:50",
                    HasEndTime = false,
                    TargetMode = ScheduleTargetMode.SpecificAccounts,
                    TargetAccounts = new List<ScheduleAccountRef> { new() { UserId = 201, Username = "NightUser" } },
                    LaunchMode = ScheduleLaunchMode.OpenHome
                }).IsValid);

                using var vm = CreateVm(svc, repo, settings, time);
                Assert.Contains(vm.TimelineSegments, s => s.ScheduleId == "late-overnight" && !s.IsCarryoverFromPreviousDay);
                Assert.Contains(vm.TimelineSegments, s => s.ScheduleId == "late-open-only" && s.IsOpenOnly);

                foreach (SchedulerTimelineBlockViewModel block in vm.TimelineSegments)
                {
                    Assert.InRange(block.LeftFraction, 0.0, 1.0);
                    Assert.True(block.WidthFraction >= 0.015);
                    Assert.True(block.LeftFraction + block.WidthFraction <= 1.0001);
                }

                var extremeBlock = new SchedulerTimelineBlockViewModel(new ScheduleTimelineSegment(
                    "extreme", "Extreme", "#38BDF8", 1439, 1440, 0, false, false, false));
                Assert.InRange(extremeBlock.LeftFraction, 0.0, 1.0);
                Assert.True(extremeBlock.WidthFraction >= 0.015);
                Assert.True(extremeBlock.LeftFraction + extremeBlock.WidthFraction <= 1.0001);
            }
        }

        [Fact]
        public void LanguageSwitch_EnToVi_UpdatesAndNotifiesAllFourFilterChipLabels()
        {
            Account alpha = MakeAccount("LangUser", 211);
            var (svc, repo, settings, _, _) = CreateServiceHarness(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero), alpha);
            using (svc)
            using (AppServices.Override<ILaunchSchedulerService>(svc))
            using (var mainVm = new MainViewModel(Substitute.For<IAppLogger>(), settings, repo))
            {
                DateTimeOffset localNow = TimeProvider.System.GetLocalNow();
                TimeOnly activeStart = TimeOnly.FromDateTime(localNow.DateTime).AddHours(-1);
                TimeOnly activeEnd = TimeOnly.FromDateTime(localNow.DateTime).AddHours(1);
                TimeOnly laterStart = TimeOnly.FromDateTime(localNow.DateTime).AddHours(2);
                TimeOnly laterEnd = TimeOnly.FromDateTime(localNow.DateTime).AddHours(3);

                Assert.True(svc.UpsertSchedule(new LaunchSchedule
                {
                    Id = "lang-active",
                    Name = "Active Shift",
                    IsEnabled = true,
                    StartTime = activeStart.ToString("HH:mm", CultureInfo.InvariantCulture),
                    HasEndTime = true,
                    EndTime = activeEnd.ToString("HH:mm", CultureInfo.InvariantCulture),
                    TargetMode = ScheduleTargetMode.SpecificAccounts,
                    TargetAccounts = new List<ScheduleAccountRef> { new() { UserId = 211, Username = "LangUser" } },
                    LaunchMode = ScheduleLaunchMode.OpenHome
                }).IsValid);

                Assert.True(svc.UpsertSchedule(new LaunchSchedule
                {
                    Id = "lang-later",
                    Name = "Later Shift",
                    IsEnabled = true,
                    StartTime = laterStart.ToString("HH:mm", CultureInfo.InvariantCulture),
                    HasEndTime = true,
                    EndTime = laterEnd.ToString("HH:mm", CultureInfo.InvariantCulture),
                    TargetMode = ScheduleTargetMode.SpecificAccounts,
                    TargetAccounts = new List<ScheduleAccountRef> { new() { UserId = 211, Username = "LangUser" } },
                    LaunchMode = ScheduleLaunchMode.OpenHome
                }).IsValid);

                Assert.True(svc.UpsertSchedule(new LaunchSchedule
                {
                    Id = "lang-paused",
                    Name = "Paused Shift",
                    IsEnabled = false,
                    StartTime = "08:00",
                    HasEndTime = true,
                    EndTime = "12:00",
                    TargetMode = ScheduleTargetMode.SpecificAccounts,
                    TargetAccounts = new List<ScheduleAccountRef> { new() { UserId = 211, Username = "LangUser" } },
                    LaunchMode = ScheduleLaunchMode.OpenHome
                }).IsValid);

                Assert.Equal(LocalizationService.Format("Sched_Filter_All", 3), mainVm.Scheduler.FilterAllText);
                Assert.Equal(LocalizationService.Format("Sched_Filter_Enabled", 2), mainVm.Scheduler.FilterEnabledText);
                Assert.Equal(LocalizationService.Format("Sched_Filter_ActiveNow", 1), mainVm.Scheduler.FilterActiveNowText);
                Assert.Equal(LocalizationService.Format("Sched_Filter_Paused", 1), mainVm.Scheduler.FilterPausedText);

                var raisedProperties = new List<string?>();
                mainVm.Scheduler.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);

                Assert.Contains(nameof(SchedulerViewModel.FilterAllText), raisedProperties);
                Assert.Contains(nameof(SchedulerViewModel.FilterEnabledText), raisedProperties);
                Assert.Contains(nameof(SchedulerViewModel.FilterActiveNowText), raisedProperties);
                Assert.Contains(nameof(SchedulerViewModel.FilterPausedText), raisedProperties);

                Assert.Equal(LocalizationService.Format("Sched_Filter_All", 3), mainVm.Scheduler.FilterAllText);
                Assert.Equal(LocalizationService.Format("Sched_Filter_Enabled", 2), mainVm.Scheduler.FilterEnabledText);
                Assert.Equal(LocalizationService.Format("Sched_Filter_ActiveNow", 1), mainVm.Scheduler.FilterActiveNowText);
                Assert.Equal(LocalizationService.Format("Sched_Filter_Paused", 1), mainVm.Scheduler.FilterPausedText);
                Assert.Contains("Tất cả", mainVm.Scheduler.FilterAllText, StringComparison.Ordinal);
                Assert.Contains("Đang bật", mainVm.Scheduler.FilterEnabledText, StringComparison.Ordinal);
                Assert.Contains("Đang chạy", mainVm.Scheduler.FilterActiveNowText, StringComparison.Ordinal);
                Assert.Contains("Tạm dừng", mainVm.Scheduler.FilterPausedText, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void AccountsChanged_WhileEditingCustomTargetGroup_PreservesSelectedGroupAndMatchesCaseInsensitively()
        {
            Account alpha = MakeAccount("GroupAlpha", 221, "Default");
            var (svc, repo, settings, time, _) = CreateServiceHarness(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), alpha);
            using (svc)
            using (var vm = CreateVm(svc, repo, settings, time))
            {
                Assert.True(svc.UpsertSchedule(new LaunchSchedule
                {
                    Id = "grp-custom",
                    Name = "Custom Group Shift",
                    StartTime = "08:00",
                    HasEndTime = true,
                    EndTime = "12:00",
                    TargetMode = ScheduleTargetMode.Group,
                    TargetGroup = "CustomRaid",
                    LaunchMode = ScheduleLaunchMode.OpenHome
                }).IsValid);

                ScheduleItemViewModel card = Assert.Single(vm.FilteredSchedules);
                card.EditCommand.Execute(null);
                Assert.True(vm.Editor.IsOpen);
                Assert.Equal("CustomRaid", vm.Editor.SelectedGroup);
                Assert.Contains("CustomRaid", vm.Editor.AvailableGroups);

                Account beta = MakeAccount("GroupBeta", 222, "FarmSquad");
                repo.ReplaceAll(new[] { alpha, beta });

                Assert.Equal("CustomRaid", vm.Editor.SelectedGroup);
                Assert.Contains("CustomRaid", vm.Editor.AvailableGroups);
                Assert.Contains("FarmSquad", vm.Editor.AvailableGroups);

                vm.Editor.SaveCommand.Execute(null);
                Assert.False(vm.Editor.IsOpen);
                Assert.Equal("CustomRaid", svc.GetSchedules().Single().TargetGroup);

                Assert.True(svc.UpsertSchedule(new LaunchSchedule
                {
                    Id = "grp-case",
                    Name = "Case Match Shift",
                    StartTime = "13:00",
                    HasEndTime = true,
                    EndTime = "15:00",
                    TargetMode = ScheduleTargetMode.Group,
                    TargetGroup = "farmsquad",
                    LaunchMode = ScheduleLaunchMode.OpenHome
                }).IsValid);

                ScheduleItemViewModel caseCard = vm.FilteredSchedules.Single(c => c.Id == "grp-case");
                caseCard.EditCommand.Execute(null);
                Assert.Equal("FarmSquad", vm.Editor.SelectedGroup);
                Assert.Single(vm.Editor.AvailableGroups, g => string.Equals(g, "FarmSquad", StringComparison.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public void SelectRecentOrFavoriteGame_SwitchingFromPrivateServerToRecentOrPublic_ClearsJobIdText()
        {
            Account alpha = MakeAccount("GamePickUser", 231);
            var (svc, repo, settings, time, _) = CreateServiceHarness(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), alpha);
            using (svc)
            using (var vm = CreateVm(svc, repo, settings, time))
            {
                vm.NewScheduleCommand.Execute(null);
                Assert.True(vm.Editor.IsOpen);

                vm.Editor.SelectRecentOrFavoriteGameCommand.Execute(new ScheduleGameQuickPickViewModel
                {
                    PlaceId = 2753915549,
                    Name = "Private Fav",
                    JobId = "vip-server-77",
                    IsFavorite = true
                });
                Assert.Equal("2753915549", vm.Editor.PlaceIdText);
                Assert.Equal("vip-server-77", vm.Editor.JobIdText);

                vm.Editor.SelectRecentOrFavoriteGameCommand.Execute(new ScheduleGameQuickPickViewModel
                {
                    PlaceId = 920587237,
                    Name = "Public Recent",
                    JobId = string.Empty,
                    IsFavorite = false
                });
                Assert.Equal("920587237", vm.Editor.PlaceIdText);
                Assert.Equal(string.Empty, vm.Editor.JobIdText);

                vm.Editor.JobIdText = "stale-job-id";
                vm.Editor.SelectRecentOrFavoriteGameCommand.Execute(4924922222L);
                Assert.Equal("4924922222", vm.Editor.PlaceIdText);
                Assert.Equal(string.Empty, vm.Editor.JobIdText);
            }
        }
    }
}
