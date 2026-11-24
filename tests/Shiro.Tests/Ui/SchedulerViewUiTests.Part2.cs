#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.AvaloniaUI.Views.Panels;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Shiro.ViewModels;
using Xunit;
using NavigationPage = Shiro.ViewModels.NavigationPage;

namespace Shiro.Tests.Ui
{
    public sealed partial class SchedulerViewUiTests
    {
        [AvaloniaFact]
        public void TimelineBlockClick_OpensScheduleInEditorDrawer()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            Account alpha = MakeAccount("BlockClickUser", 504);
            var (svc, repo, settings, time) = CreateHarness(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), alpha);
            using (svc)
            {
                Assert.True(svc.UpsertSchedule(BuildSchedule("blk-1", "Morning Raid", "08:00", "12:00", alpha)).IsValid);
                using var vm = new SchedulerViewModel(svc, repo, settings, timeProvider: time);
                var view = new SchedulerView { DataContext = vm };
                var win = new Window { Width = 1080, Height = 720, Content = view };
                try
                {
                    win.Show();
                    win.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();

                    var timeline = view.FindControl<SchedulerTimelinePanel>("ViewSchedulerTimeline")!;
                    var blocksControl = timeline.FindControl<ItemsControl>("TimelineBlocksItemsControl")!;
                    var blockButton = blocksControl.GetVisualDescendants().OfType<Button>().Single();

                    Assert.True(blockButton.Bounds.Width > 30.0, $"Expected arranged block width > 30, got {blockButton.Bounds.Width}");
                    Assert.True(blockButton.Bounds.Height >= 24.0, $"Expected block height >= 24, got {blockButton.Bounds.Height}");

                    ClickButton(blockButton);
                    Dispatcher.UIThread.RunJobs();

                    Assert.True(vm.Editor.IsOpen);
                    Assert.True(vm.Editor.IsEditingExisting);
                    Assert.Equal("Morning Raid", vm.Editor.Name);
                    Assert.True(view.FindControl<SchedulerEditorDrawer>("ViewSchedulerEditor")!.IsVisible);
                    Assert.Empty(sink.Errors);
                }
                finally
                {
                    CloseWindowSafely(win);
                    Dispatcher.UIThread.RunJobs();
                }
            }
        }

        [AvaloniaFact]
        public void DashboardContextMenuAndBulkBar_OpenSchedulerWithPrefilledTargets()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            Account alice = MakeAccount("AliceSched", 505);
            Account bob = MakeAccount("BobSched", 506);
            var (svc, repo, settings, _) = CreateHarness(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), alice, bob);
            using (svc)
            using (AppServices.Override<ISettingsService>(settings))
            using (AppServices.Override<IAccountRepository>(repo))
            using (AppServices.Override<ILaunchSchedulerService>(svc))
            {
                MainWindow? window = null;
                try
                {
                    window = new MainWindow();
                    window.Show();
                    Dispatcher.UIThread.RunJobs();

                    MainViewModel vm = window.ViewModel!;
                    DashboardView dash = window.DashboardView!;
                    vm.LaunchBar.PlaceId = "920587237";
                    vm.LaunchBar.JobId = "job-ctx-1";

                    AccountViewModel aliceVm = vm.AccountsVm.Accounts.Single(a => a.Model.UserID == 505);
                    Assert.True(dash.Resources.TryGetResource("AccountSharedContextMenu", null, out object? cmObj));
                    var cm = Assert.IsType<ContextMenu>(cmObj);
                    cm.PlacementTarget = dash;
                    cm.DataContext = aliceVm;
                    MenuItem ctxScheduleItem = cm.Items.OfType<MenuItem>().Single(m => m.Name == "MenuScheduleLaunch");
                    ctxScheduleItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Dispatcher.UIThread.RunJobs();

                    Assert.Equal(NavigationPage.Scheduler, vm.CurrentPage);
                    Assert.True(vm.Scheduler.Editor.IsOpen);
                    Assert.Equal("920587237", vm.Scheduler.Editor.PlaceIdText);
                    Assert.Equal("job-ctx-1", vm.Scheduler.Editor.JobIdText);
                    Assert.True(vm.Scheduler.Editor.FilteredAccountChecklist.Single(a => a.UserId == 505).IsChecked);
                    Assert.False(vm.Scheduler.Editor.FilteredAccountChecklist.Single(a => a.UserId == 506).IsChecked);

                    vm.Scheduler.Editor.CloseCommand.Execute(null);
                    vm.CurrentPage = NavigationPage.Accounts;
                    vm.LaunchBar.PlaceId = "123456789";
                    vm.LaunchBar.JobId = "job-bulk-2";
                    foreach (AccountViewModel accVm in vm.AccountsVm.Accounts)
                        accVm.IsSelected = true;
                    Dispatcher.UIThread.RunJobs();

                    var bulkBar = dash.GetVisualDescendants().OfType<DashboardBulkBarPanel>().Single();
                    Assert.True(bulkBar.IsVisible);
                    var batchBtn = bulkBar.FindControl<Button>("BtnBulkBatchActions")!;
                    var flyout = Assert.IsType<MenuFlyout>(batchBtn.Flyout);
                    MenuItem bulkScheduleItem = flyout.Items.OfType<MenuItem>().Single(m => m.Name == "MenuScheduleSelected");
                    bulkScheduleItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Dispatcher.UIThread.RunJobs();

                    Assert.Equal(NavigationPage.Scheduler, vm.CurrentPage);
                    Assert.True(vm.Scheduler.Editor.IsOpen);
                    Assert.Equal("123456789", vm.Scheduler.Editor.PlaceIdText);
                    Assert.Equal("job-bulk-2", vm.Scheduler.Editor.JobIdText);
                    Assert.True(vm.Scheduler.Editor.FilteredAccountChecklist.Single(a => a.UserId == 505).IsChecked);
                    Assert.True(vm.Scheduler.Editor.FilteredAccountChecklist.Single(a => a.UserId == 506).IsChecked);
                    Assert.Empty(sink.Errors);
                }
                finally
                {
                    CloseWindowSafely(window);
                    Dispatcher.UIThread.RunJobs();
                }
            }
        }

        [AvaloniaFact]
        public async Task StreamerModeToggle_UpdatesVisibleSchedulerControlsWithZeroBindingErrors()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            Account secret = MakeAccount("SecretPlayerOne", 507);
            var (svc, repo, settings, time, exec) = CreateHarnessWithExecutor(
                new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), secret);
            exec.LaunchServerAsync(Arg.Any<IReadOnlyList<Account>>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns((0, 1, false, "Failed: SecretPlayerOne"));

            using (svc)
            {
                LaunchSchedule sched = BuildSchedule("str-1", "Stealth Shift", "08:00", "12:00", secret);
                sched.JobId = "vip-secret-999";
                Assert.True(svc.UpsertSchedule(sched).IsValid);

                using var vm = new SchedulerViewModel(svc, repo, settings, timeProvider: time);
                vm.FilteredSchedules[0].RunNowCommand.Execute(null);
                await Task.Delay(50);
                Dispatcher.UIThread.RunJobs();
                vm.FilteredSchedules[0].EditCommand.Execute(null);
                vm.IsHistoryOpen = true;

                var view = new SchedulerView { DataContext = vm };
                var win = new Window { Width = 1120, Height = 760, Content = view };
                try
                {
                    win.Show();
                    win.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();

                    var editor = view.FindControl<SchedulerEditorDrawer>("ViewSchedulerEditor")!;
                    var history = view.FindControl<SchedulerHistoryPanel>("ViewSchedulerHistory")!;
                    var timeline = view.FindControl<SchedulerTimelinePanel>("ViewSchedulerTimeline")!;
                    var txtJobId = editor.FindControl<TextBox>("TxtScheduleJobId")!;
                    var blockBtn = timeline.FindControl<ItemsControl>("TimelineBlocksItemsControl")!
                        .GetVisualDescendants().OfType<Button>().Single();

                    Assert.Equal('\0', txtJobId.PasswordChar);
                    Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                        tb => tb.Text != null && tb.Text.Contains("SecretPlayerOne", StringComparison.Ordinal));

                    StreamerModeService.IsEnabled = true;
                    Dispatcher.UIThread.RunJobs();

                    string expectedMasked = StreamerModeService.MaskUsername("SecretPlayerOne");
                    Assert.Equal('\u2022', txtJobId.PasswordChar);
                    Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(),
                        tb => tb.Text != null && tb.Text.Contains("SecretPlayerOne", StringComparison.Ordinal));
                    Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(),
                        tb => tb.Text != null && tb.Text.Contains("vip-secret-999", StringComparison.Ordinal));
                    Assert.Contains(editor.GetVisualDescendants().OfType<TextBlock>(),
                        tb => tb.Text != null && tb.Text.Contains(expectedMasked, StringComparison.Ordinal));
                    Assert.Contains(history.GetVisualDescendants().OfType<TextBlock>(),
                        tb => tb.Text != null && tb.Text.Contains(expectedMasked, StringComparison.Ordinal));

                    string? tooltip = ToolTip.GetTip(blockBtn) as string;
                    Assert.NotNull(tooltip);
                    Assert.DoesNotContain("SecretPlayerOne", tooltip!, StringComparison.Ordinal);
                    Assert.DoesNotContain("vip-secret-999", tooltip!, StringComparison.Ordinal);

                    StreamerModeService.IsEnabled = false;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal('\0', txtJobId.PasswordChar);
                    Assert.Empty(sink.Errors);
                }
                finally
                {
                    CloseWindowSafely(win);
                    Dispatcher.UIThread.RunJobs();
                }
            }
        }

        private static Account MakeAccount(string username, long userId)
        {
            using var _ = Account.BeginSuppressDeferredSaveScope();
            return new Account
            {
                Username = username,
                UserID = userId,
                Group = "Default",
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + username,
                Valid = true
            };
        }

        private static LaunchSchedule BuildSchedule(string id, string name, string start, string end, params Account[] targets) => new()
        {
            Id = id,
            Name = name,
            IsEnabled = true,
            StartTime = start,
            HasEndTime = true,
            EndTime = end,
            TargetMode = ScheduleTargetMode.SpecificAccounts,
            TargetAccounts = targets.Select(t => new ScheduleAccountRef { UserId = t.UserID, Username = t.Username }).ToList(),
            LaunchMode = ScheduleLaunchMode.JoinServer,
            PlaceId = 2753915549
        };

        private (LaunchSchedulerService Service, AccountRepository Repo, ISettingsService Settings, TestTimeProvider Time) CreateHarness(
            DateTimeOffset utcNow,
            params Account[] accounts)
        {
            var (svc, repo, settings, time, _) = CreateHarnessWithExecutor(utcNow, accounts);
            return (svc, repo, settings, time);
        }

        private (LaunchSchedulerService Service, AccountRepository Repo, ISettingsService Settings, TestTimeProvider Time, ISchedulerActionExecutor Executor) CreateHarnessWithExecutor(
            DateTimeOffset utcNow,
            params Account[] accounts)
        {
            string storePath = Path.Combine(_tempDir, "LaunchSchedules_" + Guid.NewGuid().ToString("N") + ".json");
            var store = new LaunchScheduleStore(() => storePath);
            var repo = new AccountRepository();
            if (accounts.Length > 0)
                repo.ReplaceAll(accounts);

            var settings = new IniFile()["General"];
            settings.Set(AppSettingsKeys.General.HasAcceptedRiskDisclaimer, "true");
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

            var svc = new LaunchSchedulerService(store, repo, settings, toast, uiBridge, () => false, time, logger);
            svc.AttachExecutor(executor);
            return (svc, repo, settings, time, executor);
        }

        private static void EnsureBaseThemeTokens()
        {
            if (Application.Current?.Resources is { } resources && !resources.TryGetResource("RadiusOuter16", null, out _))
                resources.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://Shiro/AvaloniaUI/Styles/ThemeTokens.axaml")));
        }

        private static void ClickButton(Button button)
        {
            var peer = new Avalonia.Automation.Peers.ButtonAutomationPeer(button);
            ((Avalonia.Automation.Provider.IInvokeProvider)peer).Invoke();
        }

        private static void CloseWindowSafely(Window? win)
        {
            if (win == null) return;
            try { win.Close(); }
            catch (InvalidOperationException) { }
        }
    }
}
