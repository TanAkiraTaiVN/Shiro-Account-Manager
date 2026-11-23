#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;
using NavigationPage = Shiro.ViewModels.NavigationPage;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public sealed class SidebarRailAndNavigationTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly ILogSink? _prevSink;
        private readonly AppLanguage _prevLang;
        private readonly string _prevPlaceId;
        private readonly bool _origSidebarCollapsed;
        private readonly bool _origReducedMotion;
        private readonly SurfacePreset _origPreset;
        private readonly AccentTheme _origTheme;
        private readonly Color? _origAdaptiveColor;
        private readonly MainWindow? _prevWindow;

        public SidebarRailAndNavigationTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "shiro-rail-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _prevSink = Logger.Sink;
            _prevLang = LocalizationService.CurrentLanguage;
            _prevPlaceId = AppServices.ServerBrowserState.CurrentPlaceId;
            _origSidebarCollapsed = VisualPreferencesService.Instance.SidebarCollapsed;
            _origReducedMotion = VisualPreferencesService.Instance.ReducedMotion;
            _origPreset = AccentThemeService.CurrentSurfacePreset;
            _origTheme = AccentThemeService.CurrentTheme;
            _origAdaptiveColor = AccentThemeService.AdaptiveGameColor;
            _prevWindow = MainWindow.Instance;
            ToastService.ResetAllForTesting();
        }

        public void Dispose()
        {
            ToastService.ResetAllForTesting();
            VisualPreferencesService.Instance.SidebarCollapsed = _origSidebarCollapsed;
            VisualPreferencesService.Instance.ReducedMotion = _origReducedMotion;
            AccentThemeService.SetAdaptiveGameColor(_origAdaptiveColor);
            AccentThemeService.ApplySurfacePreset(_origPreset, save: false);
            AccentThemeService.ApplyTheme(_origTheme, save: false);
            AppServices.ServerBrowserState.CurrentPlaceId = _prevPlaceId;
            LocalizationService.SetLanguage(_prevLang, saveSetting: false);
            Application.Current?.Resources.MergedDictionaries.Clear();
            Logger.Sink = _prevSink;
            typeof(MainWindow).GetProperty(nameof(MainWindow.Instance))?.SetValue(null, _prevWindow);
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        [AvaloniaFact]
        public void MainWindow_ToggleSidebarRail_UpdatesWidthPersistence_AndPreservesNavButtons()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [AppSettingsKeys.General.HasAcceptedRiskDisclaimer] = "true"
            };
            using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
            using var settingsScope = AppServices.Override<ISettingsService>(CreateMockSettings(store));
            using var repoScope = AppServices.Override<IAccountRepository>(CreateMockRepo());

            AppServices.ServerBrowserState.CurrentPlaceId = string.Empty;
            VisualPreferencesService.Instance.SidebarCollapsed = false;
            VisualPreferencesService.Instance.ReducedMotion = false;

            MainWindow? window = null;
            MainWindow? restoredWindow = null;
            try
            {
                window = new MainWindow();
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var btnToggle = window.FindControl<Button>("BtnToggleSidebar");
                var btnDashboard = window.FindControl<Button>("BtnNavDashboard");
                var btnServerList = window.FindControl<Button>("BtnNavServerList");
                var btnRecentGames = window.FindControl<Button>("BtnNavRecentGames");
                var btnSettings = window.FindControl<Button>("BtnNavSettings");
                var btnAbout = window.FindControl<Button>("BtnNavAbout");
                var sidebarRailBorder = window.FindControl<Border>("SidebarRailBorder");
                var navSidebarHost = window.FindControl<Grid>("NavSidebarHost");
                var sidebarColumn = window.SidebarColumn;

                Assert.NotNull(btnToggle);
                Assert.NotNull(btnDashboard);
                Assert.NotNull(btnServerList);
                Assert.NotNull(btnRecentGames);
                Assert.NotNull(btnSettings);
                Assert.NotNull(btnAbout);
                Assert.NotNull(sidebarRailBorder);
                Assert.NotNull(navSidebarHost);
                Assert.NotNull(sidebarColumn);
                Assert.True(sidebarRailBorder!.ClipToBounds);
                Assert.Equal(1.0, navSidebarHost!.Opacity);
                CompositionVisual? navVisual = ElementComposition.GetElementVisual(navSidebarHost);
                Assert.Equal(1.0f, navVisual?.Opacity ?? 1.0f);

                Assert.Equal(215.0, sidebarColumn!.Width.Value);
                Assert.False(VisualPreferencesService.Instance.SidebarCollapsed);
                var navLabels = new[] { btnDashboard!, btnServerList!, btnRecentGames!, btnSettings!, btnAbout! }
                    .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>())
                    .Where(tb => tb.Classes.Contains("NavLabel"))
                    .ToList();
                Assert.Equal(5, navLabels.Count);
                Assert.All(navLabels, lbl => Assert.True(lbl.IsVisible));
                Assert.Equal(14.0, btnDashboard!.Padding.Left);

                // Collapse 215 -> 64 without opacity flash
                window.ToggleSidebarRail();
                Assert.Equal(1.0, navSidebarHost.Opacity);
                Assert.Equal(1.0f, navVisual?.Opacity ?? 1.0f);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(1.0, navSidebarHost.Opacity);
                Assert.Equal(1.0f, navVisual?.Opacity ?? 1.0f);

                Assert.Equal(64.0, sidebarColumn.Width.Value);
                Assert.True(VisualPreferencesService.Instance.SidebarCollapsed);
                Assert.Equal("true", store[AppSettingsKeys.General.SidebarCollapsed]);
                Assert.All(navLabels, lbl => Assert.False(lbl.IsVisible));
                Assert.True(btnDashboard.Padding.Left < 14.0, $"Expected collapsed NavPill padding < 14, got {btnDashboard.Padding.Left}");

                // All 5 nav buttons stay present and functional while collapsed
                Assert.Same(btnDashboard, window.FindControl<Button>("BtnNavDashboard"));
                Assert.Same(btnServerList, window.FindControl<Button>("BtnNavServerList"));
                Assert.Same(btnRecentGames, window.FindControl<Button>("BtnNavRecentGames"));
                Assert.Same(btnSettings, window.FindControl<Button>("BtnNavSettings"));
                Assert.Same(btnAbout, window.FindControl<Button>("BtnNavAbout"));

                // New MainWindow inherits persisted collapsed preference
                restoredWindow = new MainWindow();
                restoredWindow.Show();
                Dispatcher.UIThread.RunJobs();
                var restoredCol = restoredWindow.SidebarColumn;
                Assert.NotNull(restoredCol);
                Assert.Equal(64.0, restoredCol!.Width.Value);
                restoredWindow.Close();
                Dispatcher.UIThread.RunJobs();
                restoredWindow = null;

                // Toggle back 64 -> 215
                window.ToggleSidebarRail();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(215.0, sidebarColumn.Width.Value);
                Assert.False(VisualPreferencesService.Instance.SidebarCollapsed);
                Assert.Equal("false", store[AppSettingsKeys.General.SidebarCollapsed]);
                Assert.All(navLabels, lbl => Assert.True(lbl.IsVisible));
                Assert.Equal(14.0, btnDashboard.Padding.Left);

                // Ctrl+B KeyDown toggles rail
                window.Focus();
                window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.Control);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(64.0, sidebarColumn.Width.Value);
                Assert.True(VisualPreferencesService.Instance.SidebarCollapsed);

                window.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.B,
                    KeyModifiers = KeyModifiers.Control
                });
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(215.0, sidebarColumn.Width.Value);
                Assert.False(VisualPreferencesService.Instance.SidebarCollapsed);

                // Clicking BtnToggleSidebar toggles rail cleanly via XAML Click handler
                btnToggle!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(64.0, sidebarColumn.Width.Value);
                Assert.True(VisualPreferencesService.Instance.SidebarCollapsed);

                btnToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(215.0, sidebarColumn.Width.Value);
                Assert.False(VisualPreferencesService.Instance.SidebarCollapsed);

                Assert.Empty(sink.Errors);
            }
            finally
            {
                restoredWindow?.Close();
                window?.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public void MainWindow_Navigation_StillUpdatesIsVisibleSynchronously_WithSpringMotion()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [AppSettingsKeys.General.HasAcceptedRiskDisclaimer] = "true"
            };
            using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
            using var settingsScope = AppServices.Override<ISettingsService>(CreateMockSettings(store));
            using var repoScope = AppServices.Override<IAccountRepository>(CreateMockRepo());

            AppServices.ServerBrowserState.CurrentPlaceId = string.Empty;
            VisualPreferencesService.Instance.ReducedMotion = false;

            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                window.Show();
                Dispatcher.UIThread.RunJobs();

                MainViewModel? vm = window.ViewModel;
                Assert.NotNull(vm);

                var viewDashboard = window.FindControl<Control>("ViewDashboard");
                var viewServerList = window.FindControl<Control>("ViewServerList");
                var viewSettings = window.FindControl<Control>("ViewSettings");
                var viewAbout = window.FindControl<Control>("ViewAbout");

                Assert.NotNull(viewDashboard);
                Assert.NotNull(viewServerList);
                Assert.NotNull(viewSettings);
                Assert.NotNull(viewAbout);

                void AssertSyncVisible(Control? expected)
                {
                    Assert.Equal(ReferenceEquals(viewDashboard, expected), viewDashboard!.IsVisible);
                    Assert.Equal(ReferenceEquals(viewServerList, expected), viewServerList!.IsVisible);
                    Assert.Equal(ReferenceEquals(viewSettings, expected), viewSettings!.IsVisible);
                    Assert.Equal(ReferenceEquals(viewAbout, expected), viewAbout!.IsVisible);
                    if (expected != null)
                        Assert.Equal(1.0, expected.Opacity);
                }

                vm!.CurrentPage = NavigationPage.ServerList;
                AssertSyncVisible(viewServerList);

                vm.CurrentPage = NavigationPage.RecentGames;
                AssertSyncVisible(viewServerList);
                Assert.Equal(2, vm.ServerListViewModel.SelectedSubTabIndex);

                vm.CurrentPage = NavigationPage.Settings;
                AssertSyncVisible(viewSettings);

                vm.CurrentPage = NavigationPage.About;
                AssertSyncVisible(viewAbout);

                vm.CurrentPage = NavigationPage.Accounts;
                AssertSyncVisible(viewDashboard);

                Dispatcher.UIThread.RunJobs();
                Assert.Empty(sink.Errors);
            }
            finally
            {
                window?.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public void MainWindow_NotificationBell_TogglesNotificationCenter_AndTracksUnreadBadge()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [AppSettingsKeys.General.HasAcceptedRiskDisclaimer] = "true"
            };
            using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
            using var settingsScope = AppServices.Override<ISettingsService>(CreateMockSettings(store));
            using var repoScope = AppServices.Override<IAccountRepository>(CreateMockRepo());

            AppServices.ServerBrowserState.CurrentPlaceId = string.Empty;
            var app = Application.Current!;
            app.Resources.MergedDictionaries.Clear();
            var baseTokens = (ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
                new Uri("avares://Shiro/AvaloniaUI/Styles/ThemeTokens.axaml"));
            app.Resources.MergedDictionaries.Add(baseTokens);
            AccentThemeService.SetAdaptiveGameColor(null);
            AccentThemeService.ApplySurfacePreset(SurfacePreset.LiquidObsidian, save: false);
            AccentThemeService.ApplyTheme(AccentTheme.Cyan, save: false);

            Assert.True(app.Resources.TryGetResource("TextSecondaryBrush", null, out object? secRes));
            Assert.True(app.Resources.TryGetResource("AccentBrush", null, out object? accRes));
            Color expectedSecondaryColor = Assert.IsAssignableFrom<ISolidColorBrush>(secRes).Color;
            Color expectedAccentColor = Assert.IsAssignableFrom<ISolidColorBrush>(accRes).Color;
            Assert.NotEqual(expectedSecondaryColor, expectedAccentColor);

            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                window.Show();
                Dispatcher.UIThread.RunJobs();

                MainViewModel? vm = window.ViewModel;
                Assert.NotNull(vm);

                var bellBtn = window.FindControl<Button>("BtnNotificationBell");
                var badge = window.FindControl<Border>("NotificationBadge");
                Assert.NotNull(bellBtn);
                Assert.NotNull(badge);
                var bellIcon = bellBtn!.GetVisualDescendants().OfType<LucideAvalonia.Lucide>().Single();
                Assert.Equal(expectedSecondaryColor, Assert.IsAssignableFrom<ISolidColorBrush>(bellIcon.StrokeBrush).Color);

                Assert.False(vm!.IsNotificationCenterOpen);
                Assert.Equal(0, vm.UnreadNotificationCount);
                Assert.False(vm.HasUnreadNotifications);
                Assert.False(badge!.IsVisible);

                vm.UnreadNotificationCount = 4;
                Dispatcher.UIThread.RunJobs();
                Assert.True(vm.HasUnreadNotifications);
                Assert.True(badge.IsVisible);
                var badgeText = badge.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
                Assert.NotNull(badgeText);
                Assert.Equal("4", badgeText!.Text);

                bellBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.True(vm.IsNotificationCenterOpen);
                Assert.Contains("active", bellBtn.Classes);
                Assert.Equal(expectedAccentColor, Assert.IsAssignableFrom<ISolidColorBrush>(bellIcon.StrokeBrush).Color);

                bellBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.False(vm.IsNotificationCenterOpen);
                Assert.DoesNotContain("active", bellBtn.Classes);
                Assert.Equal(expectedSecondaryColor, Assert.IsAssignableFrom<ISolidColorBrush>(bellIcon.StrokeBrush).Color);

                vm.UnreadNotificationCount = -5;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, vm.UnreadNotificationCount);
                Assert.False(vm.HasUnreadNotifications);
                Assert.False(badge.IsVisible);

                Assert.Empty(sink.Errors);
            }
            finally
            {
                window?.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        private static ISettingsService CreateMockSettings(Dictionary<string, string?> store)
        {
            var settings = Substitute.For<ISettingsService>();
            settings.Get(Arg.Any<string>()).Returns(ci =>
                store.TryGetValue(ci.Arg<string>(), out string? val) ? val : null);
            settings.Get<bool>(Arg.Any<string>()).Returns(ci =>
                store.TryGetValue(ci.Arg<string>(), out string? val) && bool.TryParse(val, out bool parsed) && parsed);
            settings.Exists(Arg.Any<string>()).Returns(ci =>
                store.ContainsKey(ci.Arg<string>()));
            settings.When(s => s.Set(Arg.Any<string>(), Arg.Any<string?>()))
                .Do(ci =>
                {
                    string key = ci.ArgAt<string>(0);
                    string? val = ci.ArgAt<string?>(1);
                    if (val is null) store.Remove(key);
                    else store[key] = val;
                });
            return settings;
        }

        private static IAccountRepository CreateMockRepo()
        {
            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(new List<Account>());
            repo.IsPasswordEncrypted().Returns(false);
            return repo;
        }
    }
}
