#nullable enable
using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Shiro.AvaloniaUI.Controls;
using Shiro.Services;
using Shiro.ViewModels;
using NavigationPage = Shiro.ViewModels.NavigationPage;

namespace Shiro.AvaloniaUI.Views
{
    /// <summary>
    /// Sidebar navigation, Tool menu actions, Roblox Window Arranger triggers,
    /// and Ctrl+K Command Palette keyboard navigation for <see cref="MainWindow"/>.
    /// </summary>
    public partial class MainWindow
    {
        private bool _isApplyingNavigationPage;
        private bool _hasInitializedNavigation;
        private bool _hasPositionedNavIndicator;
        private Button? _activeNavButton;
        private Control? _lastActiveView;
        private Button? _lastActiveButton;
        private TranslateTransform? _navIndicatorTransform;

        internal void InitializeNavigationChrome()
        {
            if (NavSidebarHost != null) NavSidebarHost.SizeChanged += RefreshNavIndicatorLayout;
            Loaded += RefreshNavIndicatorLayout;
        }

        internal void CleanupNavigationTransitions()
        {
            if (NavSidebarHost != null) NavSidebarHost.SizeChanged -= RefreshNavIndicatorLayout;
            Loaded -= RefreshNavIndicatorLayout;
            if (_navIndicatorTransform != null) _navIndicatorTransform.Transitions = null;
        }

        private void RefreshNavIndicatorLayout(object? sender, EventArgs e) => UpdateNavActiveIndicator(animate: false);

        internal readonly record struct NavigationTargetSpec(
            string ViewName,
            string NavButtonName,
            int? ServerListSubTabIndex,
            bool ReloadRecentGames,
            bool EnsureServersLoaded);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (HandleChromeKeyDown(e))
                return;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.K)
            {
                if (ViewModel != null)
                {
                    bool next = !ViewModel.CommandPalette.IsCommandPaletteOpen;
                    ViewModel.CommandPalette.IsCommandPaletteOpen = next;
                    if (next)
                        FocusCommandPaletteInput();
                    e.Handled = true;
                }
            }
        }

        private void FocusCommandPaletteInput()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_isClosed &&
                    ViewModel?.CommandPalette.IsCommandPaletteOpen == true &&
                    ViewCommandPalette?.SearchTextBox is { } searchBox)
                {
                    if (ViewCommandPalette != null)
                        SpringCompositionHelper.RunSpringEntrance(
                            ViewCommandPalette, offsetX: 0.0, offsetY: 10.0, fromScale: 0.97, durationMs: 220.0);
                    searchBox.Focus();
                    searchBox.SelectAll();
                }
            }, Avalonia.Threading.DispatcherPriority.Render);
        }

        private void OnCommandPaletteKeyDown(object? sender, KeyEventArgs e)
        {
            if (ViewModel == null || !ViewModel.CommandPalette.IsCommandPaletteOpen)
                return;

            switch (e.Key)
            {
                case Key.Down:
                    ViewModel.CommandPalette.MoveCommandPaletteSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    ViewModel.CommandPalette.MoveCommandPaletteSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    bool joinServer = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ||
                                      e.KeyModifiers.HasFlag(KeyModifiers.Control);
                    if (ViewModel.CommandPalette.ExecuteCommandPaletteSelection(joinServer))
                    {
                        e.Handled = true;
                    }
                    break;
                case Key.Escape:
                    ViewModel.CommandPalette.IsCommandPaletteOpen = false;
                    e.Handled = true;
                    break;
            }
        }

        private void SetActiveView(Control? activeView, Button? activeButton)
        {
            bool viewChanged = !ReferenceEquals(_lastActiveView, activeView) ||
                               !ReferenceEquals(_lastActiveButton, activeButton);

            if (ViewDashboard != null) ViewDashboard.IsVisible = (activeView == ViewDashboard);
            if (ViewServerList != null) ViewServerList.IsVisible = (activeView == ViewServerList);
            if (ViewSettings != null) ViewSettings.IsVisible = (activeView == ViewSettings);
            if (ViewAbout != null) ViewAbout.IsVisible = (activeView == ViewAbout);

            ResetNavButtons();
            activeButton?.Classes.Add("active");
            _activeNavButton = activeButton;

            bool shouldAnimate = _hasInitializedNavigation && viewChanged && activeView != null;
            _hasInitializedNavigation = true;
            _lastActiveView = activeView;
            _lastActiveButton = activeButton;

            UpdateNavActiveIndicator(animate: shouldAnimate);
            if (shouldAnimate && activeView != null)
                RunChromeViewEntrance(activeView);
            else if (activeView != null)
                ResetChromeViewVisual(activeView);
        }

        private void UpdateNavActiveIndicator(bool animate)
        {
            if (_isClosed || NavActiveIndicator == null || NavSidebarHost == null)
                return;

            Button? activeBtn = _activeNavButton;
            if (activeBtn == null || activeBtn.Bounds.Height <= 0.0)
                return;

            Point? origin = activeBtn.TranslatePoint(new Point(0, 0), NavSidebarHost);
            if (!origin.HasValue)
                return;

            if (_navIndicatorTransform == null)
            {
                _navIndicatorTransform = NavActiveIndicator.RenderTransform as TranslateTransform ?? new TranslateTransform();
                NavActiveIndicator.RenderTransform = _navIndicatorTransform;
            }

            double indicatorHeight = NavActiveIndicator.Height > 0.0 ? NavActiveIndicator.Height : 18.0;
            double targetY = origin.Value.Y + (activeBtn.Bounds.Height - indicatorHeight) * 0.5;

            if (!_hasPositionedNavIndicator || !animate)
            {
                var saved = _navIndicatorTransform.Transitions;
                _navIndicatorTransform.Transitions = null;
                _navIndicatorTransform.Y = targetY;
                NavActiveIndicator.IsVisible = true;
                _hasPositionedNavIndicator = true;
                _navIndicatorTransform.Transitions = saved ?? CreateNavIndicatorTransitions();
                return;
            }

            NavActiveIndicator.IsVisible = true;
            _navIndicatorTransform.Transitions ??= CreateNavIndicatorTransitions();
            _navIndicatorTransform.Y = targetY;
        }

        private static Transitions CreateNavIndicatorTransitions() => new()
        {
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = TimeSpan.FromMilliseconds(180),
                Easing = new CubicEaseOut()
            }
        };

        private void ResetNavButtons()
        {
            BtnNavDashboard?.Classes.Remove("active");
            BtnNavServerList?.Classes.Remove("active");
            BtnNavRecentGames?.Classes.Remove("active");
            BtnNavTools?.Classes.Remove("active");
            BtnNavSettings?.Classes.Remove("active");
            BtnNavAbout?.Classes.Remove("active");
        }

        internal static NavigationTargetSpec ResolveNavigationTarget(NavigationPage page) => page switch
        {
            NavigationPage.ServerList => new(nameof(ViewServerList), nameof(BtnNavServerList), 0, ReloadRecentGames: false, EnsureServersLoaded: true),
            NavigationPage.RecentGames => new(nameof(ViewServerList), nameof(BtnNavRecentGames), 2, ReloadRecentGames: true, EnsureServersLoaded: false),
            NavigationPage.Settings => new(nameof(ViewSettings), nameof(BtnNavSettings), null, ReloadRecentGames: false, EnsureServersLoaded: false),
            NavigationPage.About => new(nameof(ViewAbout), nameof(BtnNavAbout), null, ReloadRecentGames: false, EnsureServersLoaded: false),
            _ => new(nameof(ViewDashboard), nameof(BtnNavDashboard), null, ReloadRecentGames: false, EnsureServersLoaded: false)
        };

        internal void ApplyNavigationPage(NavigationPage page)
        {
            if (_isClosed) return;

            if (Avalonia.Application.Current != null &&
                Avalonia.Threading.Dispatcher.UIThread != null &&
                !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!_isClosed) ApplyNavigationPage(page); });
                return;
            }

            if (_isApplyingNavigationPage) return;
            _isApplyingNavigationPage = true;
            try
            {
                var spec = ResolveNavigationTarget(page);
                if (spec.ReloadRecentGames)
                    ViewDashboard?.ViewModel?.LaunchBar.ReloadRecentGames();

                if (spec.ServerListSubTabIndex is int subTabIndex &&
                    ViewServerList != null &&
                    ViewDashboard?.ViewModel?.ServerListViewModel is { } slvm)
                {
                    ViewServerList.DataContext = slvm;
                    slvm.SelectedSubTabIndex = subTabIndex;
                    if (spec.EnsureServersLoaded) slvm.BrowserTab.EnsureServersLoadedForCurrentPlaceId();
                    if (spec.ReloadRecentGames) slvm.LibraryTab.NotifyRecentGamesChanged();
                }

                var targetView = spec.ViewName switch
                {
                    nameof(ViewServerList) => (Control?)ViewServerList,
                    nameof(ViewSettings) => ViewSettings,
                    nameof(ViewAbout) => ViewAbout,
                    _ => ViewDashboard
                };
                var targetButton = spec.NavButtonName switch
                {
                    nameof(BtnNavServerList) => BtnNavServerList,
                    nameof(BtnNavRecentGames) => BtnNavRecentGames,
                    nameof(BtnNavSettings) => BtnNavSettings,
                    nameof(BtnNavAbout) => BtnNavAbout,
                    _ => BtnNavDashboard
                };

                SetActiveView(targetView, targetButton);
            }
            finally { _isApplyingNavigationPage = false; }
        }

        private void NavigateFromSidebar(NavigationPage page)
        {
            if (ViewModel != null && ViewModel.CurrentPage != page)
            {
                ViewModel.CurrentPage = page;
                return;
            }

            ApplyNavigationPage(page);
        }

        private void OnNavDashboardClicked(object sender, RoutedEventArgs e) => NavigateFromSidebar(NavigationPage.Accounts);
        private void OnNavServerListClicked(object sender, RoutedEventArgs e) => NavigateFromSidebar(NavigationPage.ServerList);
        private void OnNavRecentGamesClicked(object sender, RoutedEventArgs e) => NavigateFromSidebar(NavigationPage.RecentGames);
        private void OnNavSettingsClicked(object sender, RoutedEventArgs e) => NavigateFromSidebar(NavigationPage.Settings);
        private void OnNavAboutClicked(object sender, RoutedEventArgs e) => NavigateFromSidebar(NavigationPage.About);
        private void OnSidebarCloseRobloxClicked(object sender, RoutedEventArgs e) => _ = HandleSidebarCloseRobloxAsync();

        private async System.Threading.Tasks.Task HandleSidebarCloseRobloxAsync()
        {
            try
            {
                var selected = ViewDashboard?.GetSelectedAccounts();
                if (selected != null && selected.Count > 0)
                {
                    if (selected.Count > 1)
                    {
                        bool confirm = await AppServices.Dialogs.ShowConfirmationAsync(
                            Shiro.Services.LocalizationService.Format("MainWindow.ConfirmCloseRobloxSelectedPrompt", selected.Count),
                            Shiro.Services.LocalizationService.Get("MainWindow.ConfirmCloseRobloxBatchTitle"));
                        if (!confirm) return;
                    }

                    int closed = await System.Threading.Tasks.Task.Run(() =>
                        RobloxProcessService.CloseAccountRobloxProcesses(selected));
                    if (closed > 0)
                        ToastService.ShowSuccess(Shiro.Services.LocalizationService.Format("MainWindow.ClosedRobloxForSelected", closed));
                    else
                        ToastService.ShowInfo(Shiro.Services.LocalizationService.Get("MainWindow.NoRobloxRunningForSelected"));
                    ViewModel?.ToolsStatus.RefreshLiveProcessStatus();
                }
                else
                {
                    bool confirm = await AppServices.Dialogs.ShowConfirmationAsync(
                        Shiro.Services.LocalizationService.Get("MainWindow.ConfirmKillAllRobloxPrompt"),
                        Shiro.Services.LocalizationService.Get("MainWindow.ConfirmKillAllRobloxTitle"));
                    if (confirm)
                    {
                        ViewModel?.ToolsStatus.KillAllClientsCommand.Execute(null);
                    }
                }
            }
            catch (System.Exception ex)
            {
                AppServices.Logger?.Error($"[MainWindow] Sidebar Close Roblox failed: {ex.Message}", ex);
            }
        }

        private void OnToolAntiAfkClicked(object sender, RoutedEventArgs e) => ViewModel?.ToolsStatus.ToggleAntiAfkCommand.Execute(null);
        private void OnToolPotatoClicked(object sender, RoutedEventArgs e) => ViewModel?.ToolsStatus.TogglePotatoModeCommand.Execute(null);
        private void OnToolZeroGpuClicked(object sender, RoutedEventArgs e) => ViewModel?.ToolsStatus.ToggleWindowsCommand.Execute(null);
        private void OnToolOptimizeRamClicked(object sender, RoutedEventArgs e) => ViewModel?.ToolsStatus.AntiLagBoosterCommand.Execute(null);

        private void OnToolMuteAllClicked(object sender, RoutedEventArgs e)
        {
            bool wasUnmuted = RobloxAudioService.ToggleMuteAll();
            string msg = wasUnmuted
                ? Shiro.Services.LocalizationService.Get("MainWindow.MutedAllRobloxClients")
                : Shiro.Services.LocalizationService.Get("MainWindow.UnmutedAllRobloxClients");
            ToastService.ShowSuccess(msg);
        }

        private void OnToolSmartMuteClicked(object sender, RoutedEventArgs e) => ViewModel?.ToolsStatus.ToggleSmartMuteCommand.Execute(null);
        private void OnArrangeAutoClicked(object sender, RoutedEventArgs e) => ViewModel?.ToolsStatus.ArrangeRobloxWindowsCommand.Execute("Auto");
        private void OnArrange2x2Clicked(object sender, RoutedEventArgs e) => ViewModel?.ToolsStatus.ArrangeRobloxWindowsCommand.Execute("2x2");
        private void OnArrange3x2Clicked(object sender, RoutedEventArgs e) => ViewModel?.ToolsStatus.ArrangeRobloxWindowsCommand.Execute("3x2");
        private void OnArrange4x3Clicked(object sender, RoutedEventArgs e) => ViewModel?.ToolsStatus.ArrangeRobloxWindowsCommand.Execute("4x3");
        private void OnArrangeCascadeClicked(object sender, RoutedEventArgs e) => ViewModel?.ToolsStatus.ArrangeRobloxWindowsCommand.Execute("Cascade");
        private void OnToolProxyClicked(object sender, RoutedEventArgs e) => AppServices.Dialogs.OpenProxyManagerDialog();
        private void OnToolCleanTrayClicked(object sender, RoutedEventArgs e) => ViewModel?.ToolsStatus.CleanSystemTrayCommand.Execute(null);

        private void OnToolStreamerModeClicked(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                ViewModel.ToolsStatus.IsStreamerModeEnabled = !ViewModel.ToolsStatus.IsStreamerModeEnabled;
            else
                StreamerModeService.IsEnabled = !StreamerModeService.IsEnabled;

            ToastService.ShowInfo(Shiro.Services.LocalizationService.Get("Main.ToggleStreamerModeTitle"), StreamerModeService.IsActive
                ? Shiro.Services.LocalizationService.Get("MainWindow.StreamerModeEnabled")
                : Shiro.Services.LocalizationService.Get("MainWindow.StreamerModeDisabled"));
        }

        private void OnCommandPaletteBarClicked(object? sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.CommandPalette.IsCommandPaletteOpen = true;
                FocusCommandPaletteInput();
            }
        }

        private void OnCommandPaletteBackdropPressed(object sender, PointerPressedEventArgs e)
        {
            if (ReferenceEquals(e.Source, sender) && ViewModel != null)
            {
                ViewModel.CommandPalette.IsCommandPaletteOpen = false;
            }
        }
    }
}
