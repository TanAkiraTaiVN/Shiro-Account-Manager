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
using Avalonia.Input;
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
    [Collection("SerialGlobalState")]
    public sealed partial class SchedulerViewUiTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly IDisposable _dirScope;
        private readonly ILogSink? _prevSink;
        private readonly Action<Action>? _prevDispatcher;
        private readonly AppLanguage _prevLang;
        private readonly bool _prevSidebarCollapsed;
        private readonly bool _prevReducedMotion;
        private readonly bool _prevStreamer;
        private readonly bool _prevStreamerAuto;
        private readonly string _prevPlaceId;
        private readonly MainWindow? _prevWindow;
        private readonly List<IResourceProvider>? _origMergedDictionaries;

        public SchedulerViewUiTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "shiro-sched-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _dirScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
            _prevSink = Logger.Sink;
            _prevDispatcher = AppDispatcher.CustomDispatcher;
            _prevLang = LocalizationService.CurrentLanguage;
            _prevSidebarCollapsed = VisualPreferencesService.Instance.SidebarCollapsed;
            _prevReducedMotion = VisualPreferencesService.Instance.ReducedMotion;
            _prevStreamer = StreamerModeService.IsEnabled;
            _prevStreamerAuto = StreamerModeService.AutoDetectEnabled;
            _prevPlaceId = AppServices.ServerBrowserState.CurrentPlaceId;
            _prevWindow = MainWindow.Instance;
            _origMergedDictionaries = Application.Current?.Resources.MergedDictionaries.ToList();

            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
            StreamerModeService.AutoDetectEnabled = false;
            StreamerModeService.IsEnabled = false;
            VisualPreferencesService.Instance.SidebarCollapsed = false;
            VisualPreferencesService.Instance.ReducedMotion = true;
            AppServices.ServerBrowserState.CurrentPlaceId = string.Empty;
            ToastService.ResetAllForTesting();
            EnsureBaseThemeTokens();
        }

        public void Dispose()
        {
            try
            {
                Logger.Sink = _prevSink;
                AppDispatcher.CustomDispatcher = _prevDispatcher;
                ToastService.ResetAllForTesting();
                StreamerModeService.IsEnabled = _prevStreamer;
                StreamerModeService.AutoDetectEnabled = _prevStreamerAuto;
                VisualPreferencesService.Instance.SidebarCollapsed = _prevSidebarCollapsed;
                VisualPreferencesService.Instance.ReducedMotion = _prevReducedMotion;
                AppServices.ServerBrowserState.CurrentPlaceId = _prevPlaceId;
                LocalizationService.SetLanguage(_prevLang, saveSetting: false);
                AccountManager.CancelDeferredSaveSettings();
                typeof(MainWindow).GetProperty(nameof(MainWindow.Instance))?.SetValue(null, _prevWindow);
                if (Application.Current?.Resources.MergedDictionaries is { } merged && _origMergedDictionaries != null)
                {
                    merged.Clear();
                    foreach (IResourceProvider dict in _origMergedDictionaries)
                        merged.Add(dict);
                }
            }
            finally
            {
                _dirScope.Dispose();
                if (Directory.Exists(_tempDir))
                {
                    try { Directory.Delete(_tempDir, recursive: true); }
                    catch (IOException) { }
                }
            }
        }

        [AvaloniaFact]
        public void MainWindow_NavigatesToScheduler_UpdatesActiveIndicator_AndRespectsCollapsedRail()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            Account alpha = MakeAccount("RailUser", 501);
            var (svc, repo, settings, _) = CreateHarness(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), alpha);
            using (svc)
            using (AppServices.Override<ISettingsService>(settings))
            using (AppServices.Override<IAccountRepository>(repo))
            using (AppServices.Override<ILaunchSchedulerService>(svc))
            {
                Assert.True(svc.UpsertSchedule(BuildSchedule("rail-1", "Rail Shift", "08:00", "12:00", alpha)).IsValid);

                MainWindow? window = null;
                try
                {
                    window = new MainWindow();
                    window.Show();
                    Dispatcher.UIThread.RunJobs();

                    MainViewModel vm = window.ViewModel!;
                    var btnNavScheduler = window.FindControl<Button>("BtnNavScheduler");
                    var btnNavDashboard = window.FindControl<Button>("BtnNavDashboard");
                    var viewScheduler = window.FindControl<SchedulerView>("ViewScheduler");
                    var viewDashboard = window.FindControl<DashboardView>("ViewDashboard");
                    var activeIndicator = window.FindControl<Border>("NavActiveIndicator");
                    var schedulerBadge = window.FindControl<Border>("SchedulerActiveBadge");

                    Assert.NotNull(btnNavScheduler);
                    Assert.NotNull(viewScheduler);
                    Assert.NotNull(schedulerBadge);
                    Assert.False(viewScheduler!.IsVisible);
                    Assert.True(schedulerBadge!.IsVisible);
                    Assert.NotNull(viewScheduler.FindControl<SchedulerTimelinePanel>("ViewSchedulerTimeline"));
                    Assert.NotNull(viewScheduler.FindControl<SchedulerEditorDrawer>("ViewSchedulerEditor"));
                    Assert.NotNull(viewScheduler.FindControl<SchedulerHistoryPanel>("ViewSchedulerHistory"));
                    Assert.NotNull(viewScheduler.FindControl<Button>("BtnNewSchedule"));

                    btnNavScheduler!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Dispatcher.UIThread.RunJobs();

                    Assert.Equal(NavigationPage.Scheduler, vm.CurrentPage);
                    Assert.True(viewScheduler.IsVisible);
                    Assert.False(viewDashboard!.IsVisible);
                    Assert.Contains("active", btnNavScheduler.Classes);
                    Assert.DoesNotContain("active", btnNavDashboard!.Classes);
                    Assert.True(activeIndicator!.IsVisible);

                    window.ToggleSidebarRail();
                    Dispatcher.UIThread.RunJobs();

                    Assert.Equal(64.0, window.SidebarColumn!.Width.Value);
                    Assert.True(btnNavScheduler.IsVisible);
                    var navLabels = btnNavScheduler.GetVisualDescendants().OfType<Control>()
                        .Where(c => c.Classes.Contains("NavLabel")).ToList();
                    Assert.NotEmpty(navLabels);
                    Assert.All(navLabels, lbl => Assert.False(lbl.IsVisible));

                    btnNavDashboard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(NavigationPage.Accounts, vm.CurrentPage);

                    btnNavScheduler.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(NavigationPage.Scheduler, vm.CurrentPage);
                    Assert.True(viewScheduler.IsVisible);
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
        public async Task SchedulerView_RendersTimelineCardsEditorAndHistory_WithZeroBindingErrors()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            Account alpha = MakeAccount("RenderUser", 502);
            var (svc, repo, settings, time) = CreateHarness(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), alpha);
            using (svc)
            {
                Assert.True(svc.UpsertSchedule(BuildSchedule("ren-1", "Alpha Shift", "08:00", "14:00", alpha)).IsValid);
                Assert.True(svc.UpsertSchedule(BuildSchedule("ren-2", "Beta Overlap", "10:00", "16:00", alpha)).IsValid);

                using var vm = new SchedulerViewModel(svc, repo, settings, timeProvider: time);
                vm.FilteredSchedules[0].RunNowCommand.Execute(null);
                await Task.Delay(50);
                Dispatcher.UIThread.RunJobs();
                vm.SetPageVisible(true);

                var view = new SchedulerView { DataContext = vm };
                var win = new Window { Width = 1120, Height = 760, Content = view };
                try
                {
                    win.Show();
                    win.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();

                    var timeline = view.FindControl<SchedulerTimelinePanel>("ViewSchedulerTimeline")!;
                    var editor = view.FindControl<SchedulerEditorDrawer>("ViewSchedulerEditor")!;
                    var history = view.FindControl<SchedulerHistoryPanel>("ViewSchedulerHistory")!;
                    var btnNew = view.FindControl<Button>("BtnNewSchedule")!;
                    var btnHist = view.FindControl<Button>("BtnToggleHistory")!;
                    var conflictBanner = timeline.FindControl<Border>("ConflictBanner");
                    var nowNeedle = timeline.FindControl<Control>("NowNeedleHost");

                    Assert.True(vm.HasConflicts);
                    Assert.NotNull(conflictBanner);
                    Assert.True(conflictBanner!.IsVisible);
                    Assert.NotNull(nowNeedle);
                    Assert.True(nowNeedle!.IsVisible);
                    Assert.Equal(2, vm.FilteredSchedules.Count);
                    Assert.False(editor.IsVisible);
                    Assert.False(history.IsVisible);

                    ClickButton(btnNew);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(vm.Editor.IsOpen);
                    Assert.True(editor.IsVisible);

                    ClickButton(editor.FindControl<Button>("BtnCloseScheduleEditor")!);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(vm.Editor.IsOpen);
                    Assert.False(editor.IsVisible);

                    ClickButton(btnHist);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(vm.IsHistoryOpen);
                    Assert.True(history.IsVisible);
                    Assert.NotEmpty(vm.HistoryEntries);
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
        public void EscapeKey_DismissesSchedulerEditorDrawer_BeforeDashboardSelection()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            Account alpha = MakeAccount("EscSchedUser", 503);
            var (svc, repo, settings, _) = CreateHarness(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), alpha);
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
                    vm.AccountsVm.Accounts[0].IsSelected = true;
                    vm.CurrentPage = NavigationPage.Scheduler;
                    vm.Scheduler.NewScheduleCommand.Execute(null);
                    vm.IsNotificationCenterOpen = true;
                    vm.CommandPalette.IsCommandPaletteOpen = true;
                    Dispatcher.UIThread.RunJobs();

                    Assert.True(vm.AccountsVm.HasSelection);
                    Assert.True(vm.Scheduler.Editor.IsOpen);

                    var esc1 = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                    window.RaiseEvent(esc1);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(esc1.Handled);
                    Assert.False(vm.CommandPalette.IsCommandPaletteOpen);
                    Assert.True(vm.IsNotificationCenterOpen);
                    Assert.True(vm.Scheduler.Editor.IsOpen);
                    Assert.True(vm.AccountsVm.HasSelection);

                    var esc2 = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                    window.RaiseEvent(esc2);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(esc2.Handled);
                    Assert.False(vm.IsNotificationCenterOpen);
                    Assert.True(vm.Scheduler.Editor.IsOpen);
                    Assert.True(vm.AccountsVm.HasSelection);

                    var esc3 = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                    window.RaiseEvent(esc3);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(esc3.Handled);
                    Assert.False(vm.Scheduler.Editor.IsOpen);
                    Assert.True(vm.AccountsVm.HasSelection);

                    var escOnSched = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                    window.RaiseEvent(escOnSched);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(escOnSched.Handled);
                    Assert.True(vm.AccountsVm.HasSelection);

                    vm.CurrentPage = NavigationPage.Accounts;
                    Dispatcher.UIThread.RunJobs();
                    var escOnDash = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                    window.RaiseEvent(escOnDash);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(escOnDash.Handled);
                    Assert.False(vm.AccountsVm.HasSelection);
                    Assert.Empty(sink.Errors);
                }
                finally
                {
                    CloseWindowSafely(window);
                    Dispatcher.UIThread.RunJobs();
                }
            }
        }
    }
}
