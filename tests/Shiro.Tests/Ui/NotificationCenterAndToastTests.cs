#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using Shiro.AvaloniaUI.Controls;
using Shiro.AvaloniaUI.Views;
using Shiro.AvaloniaUI.Views.Panels;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public sealed class NotificationCenterAndToastTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly ILogSink? _prevSink;
        private readonly Action<Action>? _prevDispatcher;
        private readonly AppLanguage _prevLang;
        private readonly MainWindow? _prevWindow;
        private readonly Color? _origAdaptiveColor;
        private readonly AccentTheme _origTheme;
        private readonly SurfacePreset _origPreset;
        private readonly string _origPlaceId;
        private readonly List<IResourceProvider>? _origMergedDictionaries;

        public NotificationCenterAndToastTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "shiro-task10-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _prevSink = Logger.Sink;
            _prevDispatcher = AppDispatcher.CustomDispatcher;
            _prevLang = LocalizationService.CurrentLanguage;
            _prevWindow = MainWindow.Instance;
            _origAdaptiveColor = AccentThemeService.AdaptiveGameColor;
            _origTheme = AccentThemeService.CurrentTheme;
            _origPreset = AccentThemeService.CurrentSurfacePreset;
            _origPlaceId = AppServices.ServerBrowserState.CurrentPlaceId;
            _origMergedDictionaries = Application.Current?.Resources.MergedDictionaries.ToList();
        }

        public void Dispose()
        {
            ToastService.ResetAllForTesting();
            AppDispatcher.CustomDispatcher = _prevDispatcher;
            Logger.Sink = _prevSink;
            AppServices.ServerBrowserState.CurrentPlaceId = _origPlaceId;
            AccentThemeService.SetAdaptiveGameColor(_origAdaptiveColor);
            AccentThemeService.ApplySurfacePreset(_origPreset, save: false);
            AccentThemeService.ApplyTheme(_origTheme, save: false);
            LocalizationService.SetLanguage(_prevLang, saveSetting: false);
            if (Application.Current?.Resources.MergedDictionaries is { } merged && _origMergedDictionaries != null)
            {
                merged.Clear();
                foreach (var dict in _origMergedDictionaries)
                    merged.Add(dict);
            }
            typeof(MainWindow).GetProperty(nameof(MainWindow.Instance))?.SetValue(null, _prevWindow);
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        [Fact]
        public void NotificationHistoryService_CapsAt30Items_TracksUnread_FiltersAlerts_AndMarksAllRead()
        {
            AppDispatcher.CustomDispatcher = a => a();
            var history = NotificationHistoryService.Instance;
            history.ResetForTesting();

            for (int i = 1; i <= 35; i++)
            {
                ToastType type = (i % 5 == 0) ? ToastType.Warning : (i % 7 == 0 ? ToastType.Error : ToastType.Info);
                history.Record(new ToastItem
                {
                    Title = $"Notice {i}",
                    Message = $"Message body {i}",
                    Type = type
                });
            }

            Assert.Equal(30, history.HistoryItems.Count);
            Assert.Equal("Notice 35", history.HistoryItems[0].Title);
            Assert.Equal("Notice 6", history.HistoryItems[^1].Title);
            Assert.DoesNotContain(history.HistoryItems, t => t.Title == "Notice 1" || t.Title == "Notice 5");
            Assert.Equal(30, history.UnreadCount);
            Assert.True(history.HasUnread);

            history.FilterWarningsOnly = true;
            Assert.NotEmpty(history.FilteredHistoryItems);
            Assert.True(history.FilteredHistoryItems.Count < 30);
            Assert.All(history.FilteredHistoryItems, t => Assert.True(t.Type is ToastType.Warning or ToastType.Error));

            history.FilterWarningsOnly = false;
            Assert.Equal(30, history.FilteredHistoryItems.Count);

            history.MarkAllRead();
            Assert.Equal(0, history.UnreadCount);
            Assert.False(history.HasUnread);
            Assert.Equal(30, history.HistoryItems.Count);

            history.ClearHistory();
            Assert.Empty(history.HistoryItems);
            Assert.Empty(history.FilteredHistoryItems);
            Assert.True(history.IsFilteredEmpty);
            Assert.Equal(0, history.UnreadCount);
        }

        [Fact]
        public void ToastService_ShowWithUndo_RecordsHistory_ExecutesUndoIdempotently_AndDismissesToast()
        {
            AppDispatcher.CustomDispatcher = a => a();
            ToastService.ResetAllForTesting();
            var history = NotificationHistoryService.Instance;

            int undoCalls = 0;
            ToastService.ShowWithUndo(
                "Account Unpinned",
                "PlayerOne was unpinned.",
                "Undo",
                () => undoCalls++,
                durationSeconds: 5.0);

            ToastItem toast = Assert.Single(ToastService.ActiveToasts);
            Assert.True(toast.HasAction);
            Assert.Equal("Undo", toast.ActionText);
            Assert.NotNull(toast.ActionCommand);
            Assert.Single(history.HistoryItems);
            Assert.Equal(1, history.UnreadCount);

            var undoCommand = toast.ActionCommand!;
            undoCommand.Execute(null);
            undoCommand.Execute(null);

            Assert.Equal(1, undoCalls);
            Assert.Null(toast.ActionCommand);
            Assert.False(toast.HasAction);
            Assert.Empty(ToastService.ActiveToasts);
        }

        [Fact]
        public void MainViewModel_TwoWaySyncs_IsNotificationCenterOpen_AndUnreadCount_WithNotificationHistoryService()
        {
            AppDispatcher.CustomDispatcher = a => a();
            ToastService.ClearAll();
            var history = NotificationHistoryService.Instance;
            history.ResetForTesting();

            using var vm = new MainViewModel(
                Substitute.For<IAppLogger>(),
                CreateInMemorySettings(),
                CreateMockRepo());

            Assert.False(vm.IsNotificationCenterOpen);
            Assert.False(history.IsCenterOpen);

            vm.IsNotificationCenterOpen = true;
            Assert.True(history.IsCenterOpen);

            history.IsCenterOpen = false;
            Assert.False(vm.IsNotificationCenterOpen);

            ToastService.ShowInfo("Sync Test", "Recorded into history", durationSeconds: 0);
            Assert.Equal(1, vm.UnreadNotificationCount);
            Assert.True(vm.HasUnreadNotifications);

            history.MarkAllRead();
            Assert.Equal(0, vm.UnreadNotificationCount);
            Assert.False(vm.HasUnreadNotifications);
        }

        [Fact]
        public async Task AccountCollectionCommandActions_ReversibleUnpin_AndBatchGroup_EmitUndoToasts()
        {
            AppDispatcher.CustomDispatcher = a => a();
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
            ToastService.ClearAll();
            NotificationHistoryService.Instance.ResetForTesting();

            var acc1 = new Account { UserID = 101, Username = "PinUndoUser", Pinned = true, Group = "Alpha", Valid = true };
            var acc2 = new Account { UserID = 102, Username = "GroupUndoUser", Pinned = false, Group = "Alpha", Valid = true };
            var repo = CreateMockRepo(acc1, acc2);
            var dialogs = Substitute.For<IDialogService>();
            dialogs.ShowInputPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult("Beta"));

            using var repoScope = AppServices.Override<IAccountRepository>(repo);
            using var dialogScope = AppServices.Override<IDialogService>(dialogs);
            using var vm = new MainViewModel(Substitute.For<IAppLogger>(), CreateInMemorySettings(), repo);
            vm.LoadAccounts(new[] { acc1, acc2 });

            // 1. Reversible unpin (and non-undo on pin)
            AccountViewModel pinTarget = vm.AccountsVm.Accounts.First(a => a.Username == "PinUndoUser");
            vm.AccountsVm.TogglePinCommand.Execute(pinTarget);
            Assert.False(pinTarget.Pinned);

            ToastItem unpinToast = Assert.Single(ToastService.ActiveToasts);
            Assert.True(unpinToast.HasAction);
            unpinToast.ActionCommand!.Execute(null);
            Assert.True(pinTarget.Pinned);
            Assert.Empty(ToastService.ActiveToasts);

            AccountViewModel nonPinned = vm.AccountsVm.Accounts.First(a => a.Username == "GroupUndoUser");
            vm.AccountsVm.TogglePinCommand.Execute(nonPinned);
            Assert.True(nonPinned.Pinned);
            Assert.Empty(ToastService.ActiveToasts);

            // 2. Reversible batch group assignment via SetGroupSelectedCommand
            foreach (var a in vm.AccountsVm.Accounts)
                a.IsSelected = true;

            await ((IAsyncRelayCommand)vm.AccountsVm.SetGroupSelectedCommand).ExecuteAsync(null);
            Assert.All(vm.AccountsVm.Accounts, a => Assert.Equal("Beta", a.Group));

            ToastItem groupToast = Assert.Single(ToastService.ActiveToasts);
            Assert.True(groupToast.HasAction);
            groupToast.ActionCommand!.Execute(null);
            Assert.All(vm.AccountsVm.Accounts, a => Assert.Equal("Alpha", a.Group));
            Assert.Empty(ToastService.ActiveToasts);
        }

        [AvaloniaFact]
        public void MainWindowOverlaysPanel_ShiftsToastLeftBy356_WhenInspectorDrawerOpen_AndHostsGlassAndNotificationCenter()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            EnsureBaseThemeResources();
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
            ToastService.ClearAll();
            NotificationHistoryService.Instance.ResetForTesting();

            var account = new Account { UserID = 501, Username = "OverlayInspectUser", Valid = true, SecurityToken = "tok-501" };
            var repo = CreateMockRepo(account);
            using var repoScope = AppServices.Override<IAccountRepository>(repo);
            using var settingsScope = AppServices.Override<ISettingsService>(CreateInMemorySettings());
            using var vm = new MainViewModel(Substitute.For<IAppLogger>(), AppServices.GeneralSettings, repo);
            vm.LoadAccounts(new[] { account });

            var panel = new MainWindowOverlaysPanel { DataContext = vm };
            var window = new Window { Width = 1260, Height = 720, Content = panel };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var toastList = panel.FindControl<ItemsControl>("ToastItemsControl");
                Assert.NotNull(toastList);
                Assert.Equal(348.0, toastList!.Width);
                Assert.Equal(new Thickness(0, 0, 16, 38), toastList.Margin);

                var initialTransform = Assert.IsType<TranslateTransform>(toastList.RenderTransform);
                Assert.Equal(0.0, initialTransform.X);
                Assert.Equal(0.0, panel.ToastHorizontalOffset);

                // Synchronous offset update to -356 (Spec §3.1 + Ruling 7) when Inspector opens
                vm.Inspector.Open(vm.AccountsVm.Accounts[0]);
                Assert.True(vm.Inspector.IsOpen);
                Assert.Equal(-356.0, initialTransform.X);
                Assert.Equal(-356.0, panel.ToastHorizontalOffset);

                // Synchronous restoration to 0 when Inspector closes (preserving centered FastLoginDialog clearance)
                vm.Inspector.Close();
                Assert.False(vm.Inspector.IsOpen);
                Assert.Equal(0.0, initialTransform.X);
                Assert.Equal(0.0, panel.ToastHorizontalOffset);

                // Glass toast card + undo action
                bool undone = false;
                ToastService.ShowWithUndo("Moved Group", "Moved 2 accounts", "Undo", () => undone = true, durationSeconds: 0);
                ToastService.ShowWarning("Alert Notice", "Session nearing expiry", durationSeconds: 0);
                Dispatcher.UIThread.RunJobs();

                var toastGlass = toastList.GetVisualDescendants().OfType<SkiaGlassSurface>().FirstOrDefault();
                Assert.NotNull(toastGlass);
                Assert.Equal(new CornerRadius(12), toastGlass!.CornerRadius);

                var undoBtn = toastList.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(b => Equals(b.Content, "Undo"));
                Assert.NotNull(undoBtn);
                undoBtn!.Command!.Execute(undoBtn.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                Assert.True(undone);

                // Notification Center popover open, filter alerts, mark all read, render & clear
                var notificationPanel = panel.FindControl<NotificationCenterPanel>("ViewNotificationCenter");
                Assert.NotNull(notificationPanel);
                Assert.False(notificationPanel!.IsVisible);

                vm.IsNotificationCenterOpen = true;
                Dispatcher.UIThread.RunJobs();
                Assert.True(notificationPanel.IsVisible);

                var centerGlass = notificationPanel.FindControl<SkiaGlassSurface>("NotificationGlass");
                Assert.NotNull(centerGlass);
                Assert.Equal(new CornerRadius(16), centerGlass!.CornerRadius);

                var btnAlerts = notificationPanel.FindControl<Button>("BtnFilterAlerts");
                var btnMarkAll = notificationPanel.FindControl<Button>("BtnMarkAllRead");
                var btnClearAll = notificationPanel.FindControl<Button>("BtnClearAllNotifications");
                Assert.NotNull(btnAlerts);
                Assert.NotNull(btnMarkAll);
                Assert.NotNull(btnClearAll);

                btnAlerts!.Command!.Execute(btnAlerts.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                Assert.True(NotificationHistoryService.Instance.FilterWarningsOnly);
                Assert.Single(NotificationHistoryService.Instance.FilteredHistoryItems);

                btnMarkAll!.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, vm.UnreadNotificationCount);

                using var rtb = new RenderTargetBitmap(new PixelSize(1260, 720), new Vector(96, 96));
                Assert.Null(Record.Exception(() => rtb.Render(window)));

                btnClearAll!.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(NotificationHistoryService.Instance.HistoryItems);
                Assert.Empty(sink.Errors);
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public void MainWindow_BellButton_AndEscape_CoordinateNotificationCenterPopover()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            EnsureBaseThemeResources();
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
            ToastService.ClearAll();
            NotificationHistoryService.Instance.ResetForTesting();

            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [AppSettingsKeys.General.HasAcceptedRiskDisclaimer] = "true"
            };
            using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
            using var settingsScope = AppServices.Override<ISettingsService>(CreateInMemorySettings(store));
            using var repoScope = AppServices.Override<IAccountRepository>(CreateMockRepo());
            var browserState = Substitute.For<IServerBrowserStateService>();
            browserState.CurrentPlaceId.Returns(string.Empty);
            using var browserScope = AppServices.Override<IServerBrowserStateService>(browserState);

            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var vm = window.ViewModel!;
                ToastService.ShowInfo("Info One", "Details", durationSeconds: 0);
                ToastService.ShowWarning("Warn Two", "Needs check", durationSeconds: 0);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(2, vm.UnreadNotificationCount);
                Assert.True(vm.HasUnreadNotifications);

                var bellBtn = window.FindControl<Button>("BtnNotificationBell");
                var overlays = window.FindControl<MainWindowOverlaysPanel>("ViewOverlays");
                Assert.NotNull(bellBtn);
                Assert.NotNull(overlays);
                var center = overlays!.FindControl<NotificationCenterPanel>("ViewNotificationCenter");
                Assert.NotNull(center);
                Assert.False(center!.IsVisible);

                bellBtn!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.True(vm.IsNotificationCenterOpen);
                Assert.True(center.IsVisible);

                var esc = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                window.RaiseEvent(esc);
                Dispatcher.UIThread.RunJobs();
                Assert.True(esc.Handled);
                Assert.False(vm.IsNotificationCenterOpen);
                Assert.False(center.IsVisible);

                // Close button inside NotificationCenterPanel syncs back to MainViewModel
                bellBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.True(center.IsVisible);
                var btnClose = center.FindControl<Button>("BtnCloseNotifications");
                Assert.NotNull(btnClose);
                btnClose!.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.False(vm.IsNotificationCenterOpen);
                Assert.False(center.IsVisible);

                Assert.Empty(sink.Errors);
            }
            finally
            {
                window?.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [Fact]
        public void ToastService_ClearAll_PreservesNotificationHistory_WhileResetAllClearsBoth()
        {
            AppDispatcher.CustomDispatcher = a => a();
            ToastService.ResetAllForTesting();
            var history = NotificationHistoryService.Instance;

            ToastService.ShowInfo("Persistent History", "Should survive ClearAll", durationSeconds: 0);
            Assert.Single(ToastService.ActiveToasts);
            Assert.Single(history.HistoryItems);
            Assert.Equal(1, history.UnreadCount);

            ToastService.ClearAll();
            Assert.Empty(ToastService.ActiveToasts);
            Assert.Single(history.HistoryItems);
            Assert.Equal(1, history.UnreadCount);

            ToastService.ResetAllForTesting();
            Assert.Empty(ToastService.ActiveToasts);
            Assert.Empty(history.HistoryItems);
            Assert.Equal(0, history.UnreadCount);
        }

        [AvaloniaFact]
        public async Task NotificationHistoryService_IsCenterOpen_MarshalsPropertyChangedToUiThread()
        {
            ToastService.ResetAllForTesting();
            var history = NotificationHistoryService.Instance;
            bool? observedAccess = null;
            string? observedProp = null;
            void Handler(object? _, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(NotificationHistoryService.IsCenterOpen))
                {
                    observedProp = e.PropertyName;
                    observedAccess = Dispatcher.UIThread.CheckAccess();
                }
            }

            history.PropertyChanged += Handler;
            try
            {
                await Task.Run(() => history.IsCenterOpen = true);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(nameof(NotificationHistoryService.IsCenterOpen), observedProp);
                Assert.True(observedAccess);
            }
            finally
            {
                history.PropertyChanged -= Handler;
            }
        }

        private static void EnsureBaseThemeResources()
        {
            var app = Application.Current;
            if (app == null) return;
            if (!app.Resources.TryGetResource("RadiusOuter16", null, out _))
            {
                app.Resources.MergedDictionaries.Add((ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
                    new Uri("avares://Shiro/AvaloniaUI/Styles/ThemeTokens.axaml")));
            }
            AccentThemeService.SetAdaptiveGameColor(null);
            AccentThemeService.ApplySurfacePreset(SurfacePreset.LiquidObsidian, save: false);
            AccentThemeService.ApplyTheme(AccentTheme.Monochrome, save: false);
        }

        private static ISettingsService CreateInMemorySettings(Dictionary<string, string?>? initialStore = null)
        {
            var store = initialStore ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var settings = Substitute.For<ISettingsService>();
            settings.Get(Arg.Any<string>()).Returns(ci => store.TryGetValue(ci.Arg<string>(), out string? v) ? v : null);
            settings.Get<bool>(Arg.Any<string>()).Returns(ci =>
                store.TryGetValue(ci.Arg<string>(), out string? v) && bool.TryParse(v, out bool parsed) && parsed);
            settings.Get(Arg.Any<string>(), Arg.Any<bool>()).Returns(ci =>
                store.TryGetValue(ci.ArgAt<string>(0), out string? v) && bool.TryParse(v, out bool parsed)
                    ? parsed
                    : ci.ArgAt<bool>(1));
            settings.Exists(Arg.Any<string>()).Returns(ci => store.ContainsKey(ci.Arg<string>()));
            settings.When(s => s.Set(Arg.Any<string>(), Arg.Any<string?>()))
                .Do(ci =>
                {
                    string k = ci.ArgAt<string>(0);
                    string? v = ci.ArgAt<string?>(1);
                    if (v == null) store.Remove(k);
                    else store[k] = v;
                });
            return settings;
        }

        private static IAccountRepository CreateMockRepo(params Account[] accounts)
        {
            var list = accounts.ToList();
            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(list);
            repo.Count.Returns(list.Count);
            repo.IsPasswordEncrypted().Returns(false);
            return repo;
        }
    }
}
