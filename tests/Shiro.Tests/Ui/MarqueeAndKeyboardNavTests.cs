#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
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
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public sealed partial class MarqueeAndKeyboardNavTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly ILogSink? _origSink;
        private readonly Action<Action>? _origDispatcher;
        private readonly AppLanguage _origLang;

        public MarqueeAndKeyboardNavTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "shiro-task7-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _origSink = Logger.Sink;
            _origDispatcher = AppDispatcher.CustomDispatcher;
            _origLang = LocalizationService.CurrentLanguage;
        }

        public void Dispose()
        {
            Logger.Sink = _origSink;
            AppDispatcher.CustomDispatcher = _origDispatcher;
            LocalizationService.SetLanguage(_origLang, saveSetting: false);
            ToastService.ClearAll();
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        [Fact]
        public void ApplySelectionBatch_Updates200Accounts_WithSingleSelectedCountNotification()
        {
            AppDispatcher.CustomDispatcher = a => a();
            var accounts = Enumerable.Range(1, 200)
                .Select(i => new Account { UserID = 10000 + i, Username = $"BatchUser_{i:D3}", Valid = true, SecurityToken = "tok" })
                .ToList();

            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(accounts);
            int selectionChangedCallbacks = 0;

            using var vm = new AccountCollectionViewModel(
                logger: Substitute.For<IAppLogger>(),
                settings: CreateInMemorySettings(),
                accountRepository: repo,
                onSelectionChanged: () => selectionChangedCallbacks++);

            vm.LoadAccounts(accounts);
            Assert.Equal(200, vm.Accounts.Count);

            int selectedCountNotifications = 0;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AccountCollectionViewModel.SelectedCount))
                    selectedCountNotifications++;
            };

            vm.ApplySelectionBatch(vm.Accounts.Take(100).ToList(), additive: false);

            Assert.Equal(100, vm.SelectedCount);
            Assert.Equal(1, selectedCountNotifications);
            Assert.Equal(1, selectionChangedCallbacks);
            Assert.All(vm.Accounts.Take(100), a => Assert.True(a.IsSelected));
            Assert.All(vm.Accounts.Skip(100), a => Assert.False(a.IsSelected));

            selectedCountNotifications = 0;
            selectionChangedCallbacks = 0;
            vm.ApplySelectionBatch(vm.Accounts.Skip(100).Take(25).ToList(), additive: true);

            Assert.Equal(125, vm.SelectedCount);
            Assert.Equal(1, selectedCountNotifications);
            Assert.Equal(1, selectionChangedCallbacks);

            // Swapping to an equal-sized different set still invokes selection callback once.
            selectionChangedCallbacks = 0;
            vm.ApplySelectionBatch(vm.Accounts.Skip(75).Take(125).ToList(), additive: false);
            Assert.Equal(125, vm.SelectedCount);
            Assert.Equal(1, selectionChangedCallbacks);
        }

        [AvaloniaFact]
        public void MarqueeSelectionOverlay_IgnoresSub6PixelClick_AndInteractiveControls()
        {
            Assert.Equal(6.0, MarqueeSelectionOverlay.ThresholdPixels);
            var overlay = new MarqueeSelectionOverlay { Width = 600, Height = 400 };
            Assert.False(overlay.IsHitTestVisible);

            // Interactive controls must never arm marquee
            var buttonChild = new TextBlock { Text = "Play" };
            var button = new Button { Content = buttonChild };
            var checkBox = new CheckBox();
            var textBox = new TextBox();
            var comboBox = new ComboBox();
            var scrollBar = new ScrollBar();
            var thumb = new Thumb();

            Assert.True(MarqueeSelectionOverlay.IsInteractiveSource(button));
            Assert.True(MarqueeSelectionOverlay.IsInteractiveSource(buttonChild));
            Assert.True(MarqueeSelectionOverlay.IsInteractiveSource(checkBox));
            Assert.True(MarqueeSelectionOverlay.IsInteractiveSource(textBox));
            Assert.True(MarqueeSelectionOverlay.IsInteractiveSource(comboBox));
            Assert.True(MarqueeSelectionOverlay.IsInteractiveSource(scrollBar));
            Assert.True(MarqueeSelectionOverlay.IsInteractiveSource(thumb));

            Assert.False(overlay.TryArm(new Point(10, 10), buttonChild));
            Assert.False(overlay.IsSelecting);
            Assert.False(overlay.TryArm(new Point(10, 10), scrollBar));
            Assert.False(overlay.TryArm(new Point(10, 10), thumb));

            // Sub-6px move must keep IsSelecting == false
            var emptyBorder = new Border();
            Assert.False(MarqueeSelectionOverlay.IsInteractiveSource(emptyBorder));
            Assert.True(overlay.TryArm(new Point(100, 100), emptyBorder));
            bool movedBelowThreshold = overlay.UpdateDrag(new Point(103, 103.5), out bool justActivatedBelow);
            Assert.False(movedBelowThreshold);
            Assert.False(justActivatedBelow);
            Assert.False(overlay.IsSelecting);
            overlay.CompleteSelection();

            // 24px drag across all 4 quadrants normalizes SelectionRect accurately
            (Point End, Rect Expected)[] quadrants =
            {
                (new Point(124, 130), new Rect(100, 100, 24, 30)),
                (new Point(76, 70), new Rect(76, 70, 24, 30)),
                (new Point(76, 130), new Rect(76, 100, 24, 30)),
                (new Point(124, 70), new Rect(100, 70, 24, 30))
            };

            foreach (var (end, expected) in quadrants)
            {
                Assert.True(overlay.TryArm(new Point(100, 100), emptyBorder));
                Assert.True(overlay.UpdateDrag(end, out bool activated));
                Assert.True(activated);
                Assert.True(overlay.IsSelecting);
                Assert.Equal(expected, overlay.SelectionRect);
                overlay.CompleteSelection();
                Assert.False(overlay.IsSelecting);
                Assert.Equal(default, overlay.SelectionRect);
            }

            // HiDPI RenderScaling 100%, 125%, 150% pixel-grid snapping and render smoke
            foreach (double scale in new[] { 1.0, 1.25, 1.5 })
            {
                Rect snapped = MarqueeSelectionOverlay.SnapToPixelGrid(new Rect(10.3, 20.3, 40.4, 30.4), scale);
                Assert.Equal(0.0, Math.Abs(snapped.X * scale - Math.Round(snapped.X * scale)), 6);
                Assert.Equal(0.0, Math.Abs(snapped.Y * scale - Math.Round(snapped.Y * scale)), 6);
                Assert.Equal(0.0, Math.Abs(snapped.Right * scale - Math.Round(snapped.Right * scale)), 6);
                Assert.Equal(0.0, Math.Abs(snapped.Bottom * scale - Math.Round(snapped.Bottom * scale)), 6);
            }

            overlay.SelectionRect = new Rect(12, 16, 120, 80);
            overlay.IsSelecting = true;
            var host = new Window { Width = 320, Height = 200, Content = overlay };
            host.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                using var rtb = new RenderTargetBitmap(new PixelSize(320, 200), new Vector(96, 96));
                var renderEx = Record.Exception(() => rtb.Render(host));
                Assert.Null(renderEx);
            }
            finally
            {
                host.Close();
            }
        }

        [AvaloniaFact]
        public void DashboardView_KeyboardArrowAndSpace_NavigatesAndTogglesSelection()
        {
            var accounts = Enumerable.Range(1, 7)
                .Select(i => new Account { UserID = 2000 + i, Username = $"KbdUser_{i:D2}", Valid = true, SecurityToken = "tok" })
                .ToList();

            using var host = CreateMountedDashboard(accounts, width: 1020, height: 760);
            var view = host.View;
            var vm = view.ViewModel;
            vm.AccountsVm.CardColumns = 3;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(3, view.ResolveGridColumnCount());
            Assert.Equal(1, DashboardView.StepGridIndex(0, Key.Right, 3, 7));
            Assert.Equal(4, DashboardView.StepGridIndex(1, Key.Down, 3, 7));
            Assert.Equal(3, DashboardView.StepGridIndex(4, Key.Left, 3, 7));
            Assert.Equal(0, DashboardView.StepGridIndex(3, Key.Up, 3, 7));
            Assert.Equal(6, DashboardView.StepGridIndex(4, Key.Down, 3, 7));
            Assert.Equal(6, DashboardView.StepGridIndex(6, Key.Down, 3, 7));
            Assert.Equal(1, DashboardView.StepGridIndex(1, Key.Up, 3, 7));

            AccountViewModel? inspected = null;
            AccountViewModel? launched = null;
            view.AccountInspectRequested += (_, avm) => inspected = avm;
            vm.OnLaunchRequested = avm => launched = avm;

            var listHost = view.FindControl<Border>("ListViewportHost");
            Assert.NotNull(listHost);
            listHost!.Focus();
            Dispatcher.UIThread.RunJobs();

            // First Down focuses index 0, Right -> 1, Down -> 4
            view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
            Assert.Equal(0, view.FocusedAccountIndex);
            view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            Assert.Equal(1, view.FocusedAccountIndex);
            view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
            Assert.Equal(4, view.FocusedAccountIndex);
            Assert.Same(vm.AccountsVm.FilteredAccounts[4], view.FocusedAccount);

            // Space toggles selection of index 4
            view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
            Assert.True(vm.AccountsVm.FilteredAccounts[4].IsSelected);
            Assert.Equal(1, vm.AccountsVm.SelectedCount);

            view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
            Assert.False(vm.AccountsVm.FilteredAccounts[4].IsSelected);
            Assert.Equal(0, vm.AccountsVm.SelectedCount);

            // Enter requests inspect (without launching)
            view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Same(vm.AccountsVm.FilteredAccounts[4], inspected);
            Assert.Null(launched);

            // Ctrl+Enter verifies CanExecute and launches focused account
            Assert.NotNull(vm.LaunchBar.LaunchAccountCommand);
            Assert.True(vm.LaunchBar.LaunchAccountCommand.CanExecute(vm.AccountsVm.FilteredAccounts[4]));
            view.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
                KeyModifiers = KeyModifiers.Control
            });
            Assert.Same(vm.AccountsVm.FilteredAccounts[4], launched);

            // Ctrl+A selects all filtered accounts & card focus ring updates cleanly on recycling
            view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A, KeyModifiers = KeyModifiers.Control });
            Assert.Equal(7, vm.AccountsVm.SelectedCount);
            var focusedCard = view.GetVisualDescendants().OfType<AccountCardView>()
                .First(c => c.IsEffectivelyVisible && ReferenceEquals(c.DataContext, vm.AccountsVm.FilteredAccounts[4]));
            var ring = focusedCard.FindControl<Border>("KeyboardFocusRing")!;
            Assert.True(ring.IsVisible);
            Assert.Contains("kbd-focused", ring.Classes);
            focusedCard.DataContext = vm.AccountsVm.FilteredAccounts[0];
            Assert.False(ring.IsVisible);
            Assert.DoesNotContain("kbd-focused", ring.Classes);
            focusedCard.DataContext = vm.AccountsVm.FilteredAccounts[4];
            Assert.True(ring.IsVisible);

            // Compact view uses 1 column stepping and updates .kbd-focused on CompactTableRow
            vm.AccountsVm.IsCompactView = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, view.ResolveGridColumnCount());
            view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up });
            Assert.Equal(3, view.FocusedAccountIndex);
            view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
            Assert.Equal(4, view.FocusedAccountIndex);
            var tableControl = view.FindControl<ItemsControl>("TableItemsControl")!;
            var row4Border = tableControl.ContainerFromIndex(4)!.GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("CompactTableRow"));
            var row3Border = tableControl.ContainerFromIndex(3)!.GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("CompactTableRow"));
            Assert.Contains("kbd-focused", row4Border.Classes);
            Assert.DoesNotContain("kbd-focused", row3Border.Classes);
            Assert.Equal(new Thickness(1.5), row4Border.BorderThickness);
            Assert.Equal(new Thickness(1, 0, 1, 1), row3Border.BorderThickness);
            Assert.NotNull(row4Border.BorderBrush);
            Assert.NotEqual(row3Border.BorderBrush, row4Border.BorderBrush);
            Assert.Empty(host.Sink.Errors);
        }

        [AvaloniaFact]
        public void DashboardView_KeyboardNavigation_IgnoresTextBoxesAndOpenComboBox()
        {
            var accounts = Enumerable.Range(1, 4)
                .Select(i => new Account { UserID = 3000 + i, Username = $"GuardUser_{i}", Valid = true, SecurityToken = "tok" })
                .ToList();

            using var host = CreateMountedDashboard(accounts, width: 1020, height: 760);
            var view = host.View;
            var vm = view.ViewModel;

            var txtSearch = view.FindControl<TextBox>("TxtSearch");
            var txtPlaceId = view.FindControl<TextBox>("TxtPlaceId");
            var txtJobId = view.FindControl<TextBox>("TxtJobId");
            var cmbGroups = view.FindControl<ComboBox>("CmbGroups");
            Assert.NotNull(txtSearch);
            Assert.NotNull(txtPlaceId);
            Assert.NotNull(txtJobId);
            Assert.NotNull(cmbGroups);

            foreach (var tb in new[] { txtSearch!, txtPlaceId!, txtJobId! })
            {
                tb.Focus();
                Dispatcher.UIThread.RunJobs();
                var ctrlA = new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.A,
                    KeyModifiers = KeyModifiers.Control,
                    Source = tb
                };
                view.RaiseEvent(ctrlA);
                Assert.Equal(0, vm.AccountsVm.SelectedCount);

                var down = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, Source = tb };
                view.RaiseEvent(down);
                Assert.Null(view.FocusedAccount);
            }

            cmbGroups!.Focus();
            cmbGroups.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            var comboDown = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, Source = cmbGroups };
            view.RaiseEvent(comboDown);
            Assert.Null(view.FocusedAccount);
            cmbGroups.IsDropDownOpen = false;
        }

        [AvaloniaFact]
        public void DashboardView_MarqueeDrag_SelectsCards_AndPreservesNormalClickDoubleTapAndScrollBar()
        {
            var accounts = Enumerable.Range(1, 24)
                .Select(i => new Account { UserID = 4000 + i, Username = $"MarqueeUser_{i:D2}", Valid = true, SecurityToken = "tok" })
                .ToList();

            using var host = CreateMountedDashboard(accounts, width: 1020, height: 640);
            var view = host.View;
            var vm = view.ViewModel;
            int launches = 0;
            vm.OnLaunchRequested = _ => launches++;
            Dispatcher.UIThread.RunJobs();

            var overlay = view.FindControl<MarqueeSelectionOverlay>("MarqueeOverlay");
            var listHost = view.FindControl<Border>("ListViewportHost");
            Assert.NotNull(overlay);
            Assert.NotNull(listHost);
            Assert.False(overlay!.IsHitTestVisible);

            var visibleCards = view.GetVisualDescendants()
                .OfType<AccountCardView>()
                .Where(c => c.IsEffectivelyVisible && c.DataContext is AccountViewModel)
                .ToList();
            AccountCardView CardFor(int index) => visibleCards.First(c => ReferenceEquals(c.DataContext, vm.AccountsVm.FilteredAccounts[index]));
            var card0 = CardFor(0);
            var card1 = CardFor(1);
            var card2 = CardFor(2);

            // Normal click & Shift+Click on cards still work
            Point firstCenter = card0.TranslatePoint(new Point(20, 60), host.Window)!.Value;
            Point thirdCenter = card2.TranslatePoint(new Point(20, 60), host.Window)!.Value;
            host.Window.MouseDown(firstCenter, MouseButton.Left);
            host.Window.MouseUp(firstCenter, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.AccountsVm.FilteredAccounts[0].IsSelected);

            host.Window.MouseDown(thirdCenter, MouseButton.Left, RawInputModifiers.Shift);
            host.Window.MouseUp(thirdCenter, MouseButton.Left, RawInputModifiers.Shift);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, vm.AccountsVm.SelectedCount);
            Assert.False(overlay.IsSelecting);

            // Non-Ctrl marquee drag across first two cards replaces prior selection
            Point hostOrigin = listHost!.TranslatePoint(default, host.Window)!.Value;
            Point card0Pt = card0.TranslatePoint(new Point(15, 25), host.Window)!.Value;
            Point card1Pt = card1.TranslatePoint(new Point(60, 80), host.Window)!.Value;
            Point startPt = new Point(hostOrigin.X + 2, card0Pt.Y);

            host.Window.MouseDown(startPt, MouseButton.Left);
            host.Window.MouseMove(new Point(startPt.X + 3, startPt.Y + 2), RawInputModifiers.LeftMouseButton);
            Assert.False(overlay.IsSelecting);

            host.Window.MouseMove(card1Pt, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            Assert.True(overlay.IsSelecting);
            Assert.True(vm.AccountsVm.FilteredAccounts[0].IsSelected);
            Assert.True(vm.AccountsVm.FilteredAccounts[1].IsSelected);
            Assert.False(vm.AccountsVm.FilteredAccounts[2].IsSelected);

            host.Window.MouseUp(card1Pt, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.False(overlay.IsSelecting);

            // Ctrl+Drag starting inside UNSELECTED CardRoot does not mutate baseline selection before threshold, then preserves {0,1} + adds card2
            Point card2Inside = card2.FindControl<Border>("CardRoot")!.TranslatePoint(new Point(45, 65), host.Window)!.Value;
            Point card2End = card2.TranslatePoint(new Point(75, 80), host.Window)!.Value;
            host.Window.MouseDown(card2Inside, MouseButton.Left, RawInputModifiers.Control);
            host.Window.MouseMove(new Point(card2Inside.X + 2, card2Inside.Y + 2), RawInputModifiers.LeftMouseButton | RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.False(overlay.IsSelecting);
            Assert.True(vm.AccountsVm.FilteredAccounts[0].IsSelected);
            Assert.True(vm.AccountsVm.FilteredAccounts[1].IsSelected);
            Assert.False(vm.AccountsVm.FilteredAccounts[2].IsSelected);

            host.Window.MouseMove(card2End, RawInputModifiers.LeftMouseButton | RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.True(overlay.IsSelecting);
            Assert.True(vm.AccountsVm.FilteredAccounts[0].IsSelected);
            Assert.True(vm.AccountsVm.FilteredAccounts[1].IsSelected);
            Assert.True(vm.AccountsVm.FilteredAccounts[2].IsSelected);
            host.Window.MouseUp(card2End, MouseButton.Left, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.False(overlay.IsSelecting);

            // Shrink back to just {0,1} via non-Ctrl drag, then verify Ctrl+Drag starting inside ALREADY-SELECTED CardRoot[0]
            host.Window.MouseDown(startPt, MouseButton.Left);
            host.Window.MouseMove(card1Pt, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            host.Window.MouseUp(card1Pt, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, vm.AccountsVm.SelectedCount);

            Point card0Inside = card0.FindControl<Border>("CardRoot")!.TranslatePoint(new Point(45, 65), host.Window)!.Value;
            host.Window.MouseDown(card0Inside, MouseButton.Left, RawInputModifiers.Control);
            host.Window.MouseMove(card2End, RawInputModifiers.LeftMouseButton | RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.True(overlay.IsSelecting);
            Assert.True(vm.AccountsVm.FilteredAccounts[0].IsSelected);
            Assert.True(vm.AccountsVm.FilteredAccounts[1].IsSelected);
            Assert.True(vm.AccountsVm.FilteredAccounts[2].IsSelected);
            host.Window.MouseUp(card2End, MouseButton.Left, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.False(overlay.IsSelecting);

            // Dragging on ScrollBar does not arm or activate marquee
            var scrollBar = view.FindControl<ItemsControl>("CardsItemsControl")?
                .GetVisualDescendants().OfType<ScrollBar>()
                .FirstOrDefault(sb => sb.IsEffectivelyVisible && sb.Bounds.Height > 40);
            Assert.NotNull(scrollBar);
            Point sbStart = scrollBar!.TranslatePoint(new Point(scrollBar.Bounds.Width * 0.5, 20), host.Window)!.Value;
            host.Window.MouseDown(sbStart, MouseButton.Left);
            host.Window.MouseMove(new Point(sbStart.X, sbStart.Y + 40), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            Assert.False(overlay.IsSelecting);
            host.Window.MouseUp(new Point(sbStart.X, sbStart.Y + 40), MouseButton.Left);

            // DoubleTap on card still launches
            var cardRoot = card0.FindControl<Border>("CardRoot")!;
            cardRoot.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!) { Source = cardRoot });
            Assert.Equal(1, launches);
        }

        private MountedDashboard CreateMountedDashboard(List<Account> accounts, double width, double height)
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(accounts);
            var browserState = Substitute.For<IServerBrowserStateService>();
            browserState.CurrentPlaceId.Returns(string.Empty);
            var repoScope = AppServices.Override<IAccountRepository>(repo);
            var settingsScope = AppServices.Override<ISettingsService>(CreateInMemorySettings());
            var browserScope = AppServices.Override<IServerBrowserStateService>(browserState);
            var view = new DashboardView();
            var window = new Window { Width = width, Height = height, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return new MountedDashboard(view, window, sink, repoScope, settingsScope, browserScope);
        }

        private static ISettingsService CreateInMemorySettings()
        {
            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var settings = Substitute.For<ISettingsService>();
            settings.Get(Arg.Any<string>()).Returns(ci => store.TryGetValue(ci.Arg<string>(), out string? v) ? v : null);
            settings.Exists(Arg.Any<string>()).Returns(ci => store.ContainsKey(ci.Arg<string>()));
            settings.When(s => s.Set(Arg.Any<string>(), Arg.Any<string?>()))
                .Do(ci => { string k = ci.ArgAt<string>(0); string? v = ci.ArgAt<string?>(1); if (v == null) store.Remove(k); else store[k] = v; });
            return settings;
        }

        private sealed class MountedDashboard(DashboardView view, Window window, BindingErrorSink sink,
            IDisposable repoScope, IDisposable settingsScope, IDisposable browserScope) : IDisposable
        {
            public DashboardView View => view;
            public Window Window => window;
            public BindingErrorSink Sink => sink;
            public void Dispose()
            {
                window.Close(); view.ViewModel.Dispose(); Dispatcher.UIThread.RunJobs();
                browserScope.Dispose(); settingsScope.Dispose(); repoScope.Dispose();
            }
        }
    }
}
