#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public sealed partial class SchedulerViewModelTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly IDisposable _dirScope;
        private readonly Action<Action>? _prevDispatcher;
        private readonly AppLanguage _prevLang;
        private readonly bool _prevStreamer;
        private readonly bool _prevStreamerAuto;

        public SchedulerViewModelTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "shiro-sched-vm-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _dirScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
            _prevDispatcher = AppDispatcher.CustomDispatcher;
            _prevLang = LocalizationService.CurrentLanguage;
            _prevStreamer = StreamerModeService.IsEnabled;
            _prevStreamerAuto = StreamerModeService.AutoDetectEnabled;
            AppDispatcher.CustomDispatcher = a => a();
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
            StreamerModeService.AutoDetectEnabled = false;
            StreamerModeService.IsEnabled = false;
            ToastService.ResetAllForTesting();
        }

        public void Dispose()
        {
            ToastService.ResetAllForTesting();
            StreamerModeService.IsEnabled = _prevStreamer;
            StreamerModeService.AutoDetectEnabled = _prevStreamerAuto;
            LocalizationService.SetLanguage(_prevLang, saveSetting: false);
            AppDispatcher.CustomDispatcher = _prevDispatcher;
            AccountManager.CancelDeferredSaveSettings();
            _dirScope.Dispose();
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup
            }
        }

        private static Account MakeAccount(string username, long userId, string group = "Default", bool valid = true)
        {
            using var _ = Account.BeginSuppressDeferredSaveScope();
            return new Account
            {
                Username = username,
                UserID = userId,
                Group = group,
                SecurityToken = valid ? "_|WARNING:-DO-NOT-SHARE-THIS.--" + username : string.Empty,
                Valid = valid
            };
        }

        private (LaunchSchedulerService Service, AccountRepository Repo, ISettingsService Settings, TestTimeProvider Time, ISchedulerActionExecutor Executor) CreateServiceHarness(
            DateTimeOffset utcNow,
            params Account[] accounts)
        {
            string storePath = Path.Combine(_tempDir, "LaunchSchedules_" + Guid.NewGuid().ToString("N") + ".json");
            var store = new LaunchScheduleStore(() => storePath);
            var repo = new AccountRepository();
            if (accounts.Length > 0)
                repo.ReplaceAll(accounts);
            var settings = new IniFile()["General"];
            var toast = Substitute.For<IToastNotifier>();
            var uiBridge = Substitute.For<IHostUiBridgeService>();
            var time = new TestTimeProvider(utcNow, TimeZoneInfo.Utc);
            var logger = Substitute.For<IAppLogger>();
            var executor = Substitute.For<ISchedulerActionExecutor>();
            executor.GetLiveRunningAccounts(Arg.Any<IReadOnlyList<Account>>()).Returns(new HashSet<Account>(ReferenceEqualityComparer.Instance));
            executor.WaitForLaunchQueueIdleAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
            executor.TryEnsureMultiRoblox(Arg.Any<int>()).Returns(true);
            executor.LaunchServerAsync(Arg.Any<IReadOnlyList<Account>>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(ci => (ci.ArgAt<IReadOnlyList<Account>>(0).Count, ci.ArgAt<IReadOnlyList<Account>>(0).Count, false, string.Empty));
            executor.LaunchHomeAsync(Arg.Any<IReadOnlyList<Account>>(), Arg.Any<CancellationToken>())
                .Returns(ci => (ci.ArgAt<IReadOnlyList<Account>>(0).Count, ci.ArgAt<IReadOnlyList<Account>>(0).Count, false, string.Empty));

            var service = new LaunchSchedulerService(store, repo, settings, toast, uiBridge, () => false, time, logger);
            service.AttachExecutor(executor);
            return (service, repo, settings, time, executor);
        }

        private static SchedulerViewModel CreateVm(
            ILaunchSchedulerService service,
            IAccountRepository repo,
            ISettingsService settings,
            TimeProvider time) =>
            new(service, repo, settings, timeProvider: time);

        [Fact]
        public void CreateEditDuplicateAndDeleteWithUndo_UpdatesSchedulesAndRestoresOnUndo()
        {
            Account alpha = MakeAccount("AlphaUser", 101);
            Account beta = MakeAccount("BetaUser", 102);
            var (svc, repo, settings, time, _) = CreateServiceHarness(new DateTimeOffset(2026, 11, 24, 7, 0, 0, TimeSpan.Zero), alpha, beta);
            using (svc)
            using (var vm = CreateVm(svc, repo, settings, time))
            {
                vm.NewScheduleCommand.Execute(null);
                Assert.True(vm.Editor.IsOpen);
                Assert.False(vm.Editor.IsEditingExisting);

                vm.Editor.Name = "Morning Farm";
                vm.Editor.StartTimeText = "08:00";
                vm.Editor.HasEndTime = true;
                vm.Editor.EndTimeText = "12:00";
                vm.Editor.TargetMode = ScheduleTargetMode.SpecificAccounts;
                vm.Editor.FilteredAccountChecklist.First(a => a.UserId == 101).IsChecked = true;
                vm.Editor.LaunchMode = ScheduleLaunchMode.JoinServer;
                vm.Editor.PlaceIdText = "2753915549";
                vm.Editor.SaveCommand.Execute(null);

                Assert.False(vm.Editor.IsOpen);
                Assert.Single(vm.FilteredSchedules);
                Assert.Equal("Morning Farm", vm.FilteredSchedules[0].Name);
                Assert.Single(svc.GetSchedules());

                vm.FilteredSchedules[0].EditCommand.Execute(null);
                Assert.True(vm.Editor.IsOpen);
                Assert.True(vm.Editor.IsEditingExisting);
                vm.Editor.Name = "Morning Farm Updated";
                vm.Editor.SaveCommand.Execute(null);

                Assert.False(vm.Editor.IsOpen);
                Assert.Single(vm.FilteredSchedules);
                Assert.Equal("Morning Farm Updated", vm.FilteredSchedules[0].Name);

                vm.FilteredSchedules[0].DuplicateCommand.Execute(null);
                Assert.Equal(2, vm.FilteredSchedules.Count);
                Assert.Equal(2, svc.GetSchedules().Count);
                Assert.NotEqual(vm.FilteredSchedules[0].Id, vm.FilteredSchedules[1].Id);
                Assert.Contains("Copy", vm.FilteredSchedules[1].Name, StringComparison.OrdinalIgnoreCase);

                ToastService.ClearAll();
                vm.FilteredSchedules[1].DeleteCommand.Execute(null);
                Assert.Single(vm.FilteredSchedules);
                Assert.Single(svc.GetSchedules());

                ToastItem undoToast = Assert.Single(ToastService.ActiveToasts);
                Assert.NotNull(undoToast.ActionCommand);
                undoToast.ActionCommand!.Execute(null);

                Assert.Equal(2, vm.FilteredSchedules.Count);
                Assert.Equal(2, svc.GetSchedules().Count);
            }
        }

        [Fact]
        public void DurationQuickChips_ComputeEndTimeAndFlagOvernightWraparound()
        {
            Account alpha = MakeAccount("AlphaDur", 111);
            var (svc, repo, settings, time, _) = CreateServiceHarness(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), alpha);
            using (svc)
            using (var vm = CreateVm(svc, repo, settings, time))
            {
                vm.NewScheduleCommand.Execute(null);
                vm.Editor.Name = "Duration Test";
                vm.Editor.StartTimeText = "08:00";
                vm.Editor.HasEndTime = false;

                vm.Editor.ApplyDurationHoursCommand.Execute(4);
                Assert.True(vm.Editor.HasEndTime);
                Assert.Equal("12:00", vm.Editor.EndTimeText);
                Assert.False(vm.Editor.IsOvernightPreview);

                vm.Editor.StartTimeText = "22:00";
                vm.Editor.ApplyDurationHoursCommand.Execute("8");
                Assert.True(vm.Editor.HasEndTime);
                Assert.Equal("06:00", vm.Editor.EndTimeText);
                Assert.True(vm.Editor.IsOvernightPreview);

                vm.Editor.FilteredAccountChecklist[0].IsChecked = true;
                vm.Editor.PlaceIdText = "2753915549";
                vm.Editor.SaveCommand.Execute(null);

                ScheduleItemViewModel card = Assert.Single(vm.FilteredSchedules);
                Assert.Contains("+1d", card.WindowBadgeText, StringComparison.Ordinal);
                Assert.Contains(vm.TimelineSegments, s => s.StartMinute == 22 * 60 && s.EndMinute == 24 * 60 && !s.IsCarryoverFromPreviousDay);
                Assert.Contains(vm.TimelineSegments, s => s.StartMinute == 0 && s.EndMinute == 6 * 60 && s.IsCarryoverFromPreviousDay);
            }
        }

        [Fact]
        public void AddNextShift_ChainsStartTimeFromPreviousEndTimeAndClonesGameTarget()
        {
            Account alpha = MakeAccount("ChainUser", 121);
            var (svc, repo, settings, time, _) = CreateServiceHarness(new DateTimeOffset(2026, 11, 24, 7, 0, 0, TimeSpan.Zero), alpha);
            using (svc)
            using (var vm = CreateVm(svc, repo, settings, time))
            {
                Assert.True(svc.UpsertSchedule(new LaunchSchedule
                {
                    Id = "shift-1",
                    Name = "Shift 1",
                    Recurrence = ScheduleRecurrence.Weekly,
                    DaysOfWeek = ScheduleDaysOfWeek.Weekdays,
                    StartTime = "08:00",
                    HasEndTime = true,
                    EndTime = "14:00",
                    TargetMode = ScheduleTargetMode.SpecificAccounts,
                    TargetAccounts = new List<ScheduleAccountRef> { new() { UserId = 121, Username = "ChainUser" } },
                    LaunchMode = ScheduleLaunchMode.JoinServer,
                    PlaceId = 2753915549,
                    JobId = "VIP:private-77"
                }).IsValid);

                ScheduleItemViewModel card = Assert.Single(vm.FilteredSchedules);
                card.AddNextShiftCommand.Execute(null);

                Assert.True(vm.Editor.IsOpen);
                Assert.False(vm.Editor.IsEditingExisting);
                Assert.Equal("14:00", vm.Editor.StartTimeText);
                Assert.True(vm.Editor.HasEndTime);
                Assert.Equal("20:00", vm.Editor.EndTimeText);
                Assert.Equal(ScheduleRecurrence.Weekly, vm.Editor.Recurrence);
                Assert.True(vm.Editor.DayMon && vm.Editor.DayFri && !vm.Editor.DaySat);
                Assert.Equal(ScheduleLaunchMode.JoinServer, vm.Editor.LaunchMode);
                Assert.Equal("2753915549", vm.Editor.PlaceIdText);
                Assert.Equal("VIP:private-77", vm.Editor.JobIdText);
                Assert.NotEqual("Shift 1", vm.Editor.Name);
            }
        }

        [Fact]
        public async Task StreamerModeToggle_MasksAccountUsernamesAndJobIdsInCardsEditorAndHistory()
        {
            Account secret = MakeAccount("SecretPlayerOne", 131);
            var (svc, repo, settings, time, exec) = CreateServiceHarness(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), secret);
            exec.LaunchServerAsync(Arg.Any<IReadOnlyList<Account>>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns((0, 1, false, "Failed: SecretPlayerOne"));

            using (svc)
            using (var vm = CreateVm(svc, repo, settings, time))
            {
                Assert.True(svc.UpsertSchedule(new LaunchSchedule
                {
                    Id = "s-stream",
                    Name = "Stealth Shift",
                    StartTime = "08:00",
                    HasEndTime = true,
                    EndTime = "12:00",
                    TargetMode = ScheduleTargetMode.SpecificAccounts,
                    TargetAccounts = new List<ScheduleAccountRef> { new() { UserId = 131, Username = "SecretPlayerOne" } },
                    LaunchMode = ScheduleLaunchMode.JoinServer,
                    PlaceId = 2753915549,
                    JobId = "secret-job-uuid-999"
                }).IsValid);

                ScheduleItemViewModel card = Assert.Single(vm.FilteredSchedules);
                card.RunNowCommand.Execute(null);
                await Task.Delay(50);

                card.EditCommand.Execute(null);
                Assert.Contains("SecretPlayerOne", card.TargetSummaryText, StringComparison.Ordinal);
                Assert.Contains("secret-job-uuid-999", card.DestinationSummaryText, StringComparison.Ordinal);
                Assert.Equal("SecretPlayerOne", vm.Editor.FilteredAccountChecklist[0].DisplayName);
                Assert.Equal('\0', vm.Editor.JobIdMaskChar);
                Assert.Contains("SecretPlayerOne", vm.HistoryEntries[0].SummaryText, StringComparison.Ordinal);

                StreamerModeService.IsEnabled = true;

                string expectedMasked = StreamerModeService.MaskUsername("SecretPlayerOne");
                Assert.DoesNotContain("SecretPlayerOne", card.TargetSummaryText, StringComparison.Ordinal);
                Assert.Contains(expectedMasked, card.TargetSummaryText, StringComparison.Ordinal);
                Assert.DoesNotContain("secret-job-uuid-999", card.DestinationSummaryText, StringComparison.Ordinal);
                Assert.Contains("***-***", card.DestinationSummaryText, StringComparison.Ordinal);
                Assert.DoesNotContain("SecretPlayerOne", vm.TimelineSegments[0].TooltipText, StringComparison.Ordinal);
                Assert.DoesNotContain("secret-job-uuid-999", vm.TimelineSegments[0].TooltipText, StringComparison.Ordinal);
                Assert.Equal(expectedMasked, vm.Editor.FilteredAccountChecklist[0].DisplayName);
                Assert.Equal('\u2022', vm.Editor.JobIdMaskChar);
                Assert.DoesNotContain("SecretPlayerOne", vm.HistoryEntries[0].SummaryText, StringComparison.Ordinal);
                Assert.Contains(expectedMasked, vm.HistoryEntries[0].SummaryText, StringComparison.Ordinal);

                StreamerModeService.IsEnabled = false;
                Assert.Contains("SecretPlayerOne", card.TargetSummaryText, StringComparison.Ordinal);
                Assert.Equal("SecretPlayerOne", vm.Editor.FilteredAccountChecklist[0].DisplayName);
                Assert.Equal('\0', vm.Editor.JobIdMaskChar);
            }
        }

        [Fact]
        public void Editor_ShowsLiveValidationAndNonBlockingConflictPreview()
        {
            Account alpha = MakeAccount("ConflictAlpha", 141);
            var (svc, repo, settings, time, _) = CreateServiceHarness(new DateTimeOffset(2026, 11, 24, 7, 0, 0, TimeSpan.Zero), alpha);
            using (svc)
            using (var vm = CreateVm(svc, repo, settings, time))
            {
                Assert.True(svc.UpsertSchedule(new LaunchSchedule
                {
                    Id = "shift-a",
                    Name = "Shift A",
                    StartTime = "08:00",
                    HasEndTime = true,
                    EndTime = "14:00",
                    TargetMode = ScheduleTargetMode.SpecificAccounts,
                    TargetAccounts = new List<ScheduleAccountRef> { new() { UserId = 141, Username = "ConflictAlpha" } },
                    LaunchMode = ScheduleLaunchMode.JoinServer,
                    PlaceId = 2753915549
                }).IsValid);

                vm.NewScheduleCommand.Execute(null);
                vm.Editor.Name = string.Empty;
                Assert.True(vm.Editor.HasValidationError);
                Assert.False(string.IsNullOrWhiteSpace(vm.Editor.ValidationError));

                vm.Editor.Name = "Shift B";
                vm.Editor.StartTimeText = "99:99";
                Assert.True(vm.Editor.HasValidationError);

                vm.Editor.StartTimeText = "10:00";
                vm.Editor.EndTimeText = "10:00";
                Assert.True(vm.Editor.HasValidationError);

                vm.Editor.EndTimeText = "16:00";
                vm.Editor.FilteredAccountChecklist[0].IsChecked = true;
                vm.Editor.PlaceIdText = "2753915549";

                Assert.False(vm.Editor.HasValidationError);
                Assert.True(vm.Editor.HasLiveConflictWarning);
                Assert.False(string.IsNullOrWhiteSpace(vm.Editor.LiveConflictWarning));

                vm.Editor.SaveCommand.Execute(null);
                Assert.False(vm.Editor.IsOpen);
                Assert.Equal(2, svc.GetSchedules().Count);
                Assert.True(vm.HasConflicts);
                Assert.All(vm.FilteredSchedules, card => Assert.True(card.HasConflict));
                Assert.Contains(vm.TimelineSegments, seg => seg.HasConflict);
            }
        }

        [Fact]
        public void OpenNewScheduleForAccounts_NavigatesToSchedulerAndPrefillsAccountsPlaceAndJobId()
        {
            Account alice = MakeAccount("AlicePre", 151);
            Account bob = MakeAccount("BobPre", 152);
            var (svc, repo, settings, _, _) = CreateServiceHarness(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero), alice, bob);
            using (svc)
            using (AppServices.Override<ILaunchSchedulerService>(svc))
            using (var mainVm = new MainViewModel(Substitute.For<IAppLogger>(), settings, repo))
            {
                mainVm.LoadAccounts(new[] { alice, bob });
                mainVm.OpenNewScheduleForAccounts(new[] { alice }, "920587237", "job-prefill-42");

                Assert.Equal(NavigationPage.Scheduler, mainVm.CurrentPage);
                Assert.True(mainVm.IsSchedulerActive);
                Assert.True(mainVm.Scheduler.Editor.IsOpen);
                Assert.False(mainVm.Scheduler.Editor.IsEditingExisting);
                Assert.Equal(ScheduleLaunchMode.JoinServer, mainVm.Scheduler.Editor.LaunchMode);
                Assert.Equal("920587237", mainVm.Scheduler.Editor.PlaceIdText);
                Assert.Equal("job-prefill-42", mainVm.Scheduler.Editor.JobIdText);
                Assert.True(mainVm.Scheduler.Editor.FilteredAccountChecklist.First(a => a.UserId == 151).IsChecked);
                Assert.False(mainVm.Scheduler.Editor.FilteredAccountChecklist.First(a => a.UserId == 152).IsChecked);
            }
        }

        [Fact]
        public void FilterAndSearch_FilterByAllEnabledActiveNowPausedAndName()
        {
            Account alpha = MakeAccount("FilterAlpha", 161);
            var (svc, repo, settings, time, _) = CreateServiceHarness(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero), alpha);
            using (svc)
            using (var vm = CreateVm(svc, repo, settings, time))
            {
                Assert.True(svc.UpsertSchedule(new LaunchSchedule
                {
                    Id = "f-run",
                    Name = "Alpha Running",
                    IsEnabled = true,
                    StartTime = "08:00",
                    HasEndTime = true,
                    EndTime = "12:00",
                    TargetMode = ScheduleTargetMode.SpecificAccounts,
                    TargetAccounts = new List<ScheduleAccountRef> { new() { UserId = 161, Username = "FilterAlpha" } },
                    LaunchMode = ScheduleLaunchMode.OpenHome
                }).IsValid);
                Assert.True(svc.UpsertSchedule(new LaunchSchedule
                {
                    Id = "f-next",
                    Name = "Beta Upcoming",
                    IsEnabled = true,
                    StartTime = "14:00",
                    HasEndTime = true,
                    EndTime = "18:00",
                    TargetMode = ScheduleTargetMode.SpecificAccounts,
                    TargetAccounts = new List<ScheduleAccountRef> { new() { UserId = 161, Username = "FilterAlpha" } },
                    LaunchMode = ScheduleLaunchMode.OpenHome
                }).IsValid);
                Assert.True(svc.UpsertSchedule(new LaunchSchedule
                {
                    Id = "f-off",
                    Name = "Gamma Paused",
                    IsEnabled = false,
                    StartTime = "08:00",
                    HasEndTime = true,
                    EndTime = "12:00",
                    TargetMode = ScheduleTargetMode.SpecificAccounts,
                    TargetAccounts = new List<ScheduleAccountRef> { new() { UserId = 161, Username = "FilterAlpha" } },
                    LaunchMode = ScheduleLaunchMode.OpenHome
                }).IsValid);

                vm.SetFilterCommand.Execute(SchedulerFilterMode.All);
                Assert.Equal(3, vm.FilteredSchedules.Count);

                vm.SetFilterCommand.Execute(SchedulerFilterMode.Enabled);
                Assert.Equal(2, vm.FilteredSchedules.Count);
                Assert.DoesNotContain(vm.FilteredSchedules, s => s.Id == "f-off");

                vm.SetFilterCommand.Execute(SchedulerFilterMode.ActiveNow);
                Assert.Equal("f-run", Assert.Single(vm.FilteredSchedules).Id);

                vm.SetFilterCommand.Execute(SchedulerFilterMode.Paused);
                Assert.Equal("f-off", Assert.Single(vm.FilteredSchedules).Id);

                vm.SetFilterCommand.Execute(SchedulerFilterMode.All);
                vm.SearchText = "Beta";
                Assert.Equal("f-next", Assert.Single(vm.FilteredSchedules).Id);
            }
        }

        [Fact]
        public void CommandPalette_IncludesNavSchedulerAndToolSchedulerCommands()
        {
            Account alpha = MakeAccount("CmdUser", 171);
            var (svc, repo, settings, _, _) = CreateServiceHarness(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero), alpha);
            using (svc)
            using (AppServices.Override<ILaunchSchedulerService>(svc))
            using (var mainVm = new MainViewModel(Substitute.For<IAppLogger>(), settings, repo))
            {
                mainVm.CommandPalette.IsCommandPaletteOpen = true;
                mainVm.CommandPalette.ActiveScope = CommandPaletteScope.Commands;
                mainVm.CommandPalette.CommandPaletteSearchText = "scheduler";

                CommandPaletteItemViewModel? navCmd = mainVm.CommandPalette.CommandPaletteItems.FirstOrDefault(i => i.CommandId == "nav.scheduler");
                CommandPaletteItemViewModel? toolCmd = mainVm.CommandPalette.CommandPaletteItems.FirstOrDefault(i => i.CommandId == "tool.scheduler");

                Assert.NotNull(navCmd);
                Assert.NotNull(toolCmd);
                Assert.Equal(CommandPaletteItemType.Navigation, navCmd!.ItemType);
                Assert.Equal(CommandPaletteItemType.Command, toolCmd!.ItemType);

                int navIndex = mainVm.CommandPalette.CommandPaletteItems.IndexOf(navCmd);
                mainVm.CommandPalette.SetSelectedCommandPaletteIndex(navIndex);
                Assert.True(mainVm.CommandPalette.ExecuteCommandPaletteSelection());
                Assert.Equal(NavigationPage.Scheduler, mainVm.CurrentPage);

                bool initialEnabled = mainVm.Scheduler.IsEngineEnabled;
                mainVm.CommandPalette.IsCommandPaletteOpen = true;
                mainVm.CommandPalette.ActiveScope = CommandPaletteScope.Commands;
                mainVm.CommandPalette.CommandPaletteSearchText = "scheduler";
                int toolIndex = mainVm.CommandPalette.CommandPaletteItems.ToList().FindIndex(i => i.CommandId == "tool.scheduler");
                mainVm.CommandPalette.SetSelectedCommandPaletteIndex(toolIndex);
                Assert.True(mainVm.CommandPalette.ExecuteCommandPaletteSelection());
                Assert.Equal(!initialEnabled, mainVm.Scheduler.IsEngineEnabled);
            }
        }
    }
}
