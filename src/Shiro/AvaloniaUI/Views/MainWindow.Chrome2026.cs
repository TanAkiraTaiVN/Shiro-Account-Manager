#nullable enable
using System;
using System.Numerics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using Shiro.AvaloniaUI.Controls;
using Shiro.Services;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views
{
    /// <summary>
    /// Collapsible 215px/64px icon rail sidebar, Ctrl+B shortcut, notification bell wiring,
    /// and 120Hz SpringCompositionHelper view entrance transitions for <see cref="MainWindow"/>.
    /// </summary>
    public partial class MainWindow
    {
        internal const double ExpandedSidebarWidth = 215.0;
        internal const double CollapsedSidebarWidth = 64.0;
        private static readonly Easing s_railSlideEasing = new SpringEasing(mass: 1.0, stiffness: 260.0, damping: 28.0, initialVelocity: 0.0);

        private bool _isSidebarCollapsed;
        private Action? _visualPreferencesChangedHandler;

        public bool IsSidebarRailCollapsed => _isSidebarCollapsed;

        internal ColumnDefinition? SidebarColumn =>
            ShellBodyGrid?.ColumnDefinitions.Count > 0 ? ShellBodyGrid.ColumnDefinitions[0] : null;

        internal void InitializeChrome2026()
        {
            ApplySidebarRailState(VisualPreferencesService.Instance.SidebarCollapsed, animate: false);

            _visualPreferencesChangedHandler = WeakEventSubscription.CreateAction<MainWindow>(
                this,
                static window => window.OnChromeVisualPreferencesChanged(),
                static handler => VisualPreferencesService.Instance.PreferencesChanged -= handler);
            VisualPreferencesService.Instance.PreferencesChanged += _visualPreferencesChangedHandler;
        }

        internal void CleanupChrome2026()
        {
            if (_visualPreferencesChangedHandler != null)
            {
                VisualPreferencesService.Instance.PreferencesChanged -= _visualPreferencesChangedHandler;
                _visualPreferencesChangedHandler = null;
            }
        }

        public void ToggleSidebarRail()
        {
            bool nextCollapsed = !_isSidebarCollapsed;
            ApplySidebarRailState(nextCollapsed, animate: true);
            VisualPreferencesService.Instance.ApplyAndSave(sidebarCollapsed: nextCollapsed);
        }

        internal void ApplySidebarRailState(bool collapsed, bool animate)
        {
            _isSidebarCollapsed = collapsed;
            double width = collapsed ? CollapsedSidebarWidth : ExpandedSidebarWidth;

            ColumnDefinition? col = SidebarColumn;
            if (col != null)
                col.Width = new GridLength(width);

            SidebarRailBorder?.Classes.Set("collapsed", collapsed);
            if (IconSidebarCollapse != null)
                IconSidebarCollapse.IsVisible = !collapsed;
            if (IconSidebarExpand != null)
                IconSidebarExpand.IsVisible = collapsed;

            UpdateNavActiveIndicator(animate: false);
            if (animate && NavSidebarHost != null)
                RunSidebarRailSlide(NavSidebarHost, collapsed ? -6.0 : 6.0, durationMs: 200.0);
        }

        private static void RunSidebarRailSlide(Visual target, double offsetX, double durationMs = 200.0)
        {
            target.Opacity = 1.0;
            CompositionVisual? visual = ElementComposition.GetElementVisual(target);
            if (visual is null)
                return;

            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
            visual.Opacity = 1.0f;
            if (VisualPreferencesService.Instance.ReducedMotion || durationMs <= 0.0 ||
                Math.Abs(offsetX) <= 0.01 || visual.Compositor is not { } compositor)
            {
                visual.Translation = default;
                return;
            }

            Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
            slide.Target = "Translation";
            slide.Duration = TimeSpan.FromMilliseconds(durationMs);
            slide.StopBehavior = AnimationStopBehavior.SetToFinalValue;
            slide.InsertKeyFrame(0.0f, new Vector3((float)offsetX, 0.0f, 0.0f));
            slide.InsertKeyFrame(1.0f, Vector3.Zero, s_railSlideEasing);
            visual.StartAnimation("Translation", slide);
        }

        internal static void RunChromeViewEntrance(Control targetView)
            => SpringCompositionHelper.RunSpringEntrance(targetView, offsetX: 0.0, offsetY: 10.0, fromScale: 0.985, durationMs: 240.0);

        internal static void ResetChromeViewVisual(Control targetView)
            => SpringCompositionHelper.RunSpringEntrance(targetView, offsetX: 0.0, offsetY: 0.0, fromScale: 1.0, durationMs: 0.0);

        internal bool HandleChromeKeyDown(KeyEventArgs e)
        {
            if (e.Handled)
                return false;

            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.B)
            {
                ToggleSidebarRail();
                e.Handled = true;
                return true;
            }

            if (e.Key == Key.Escape && ViewModel is { } vm && TryDismissTopmostLayer(vm, e))
            {
                e.Handled = true;
                return true;
            }

            return false;
        }

        private bool TryDismissTopmostLayer(MainViewModel vm, KeyEventArgs e)
        {
            if (vm.CommandPalette.IsCommandPaletteOpen) { vm.CommandPalette.IsCommandPaletteOpen = false; return true; }
            if (vm.IsNotificationCenterOpen) { vm.IsNotificationCenterOpen = false; return true; }
            if (vm.Inspector.IsOpen) { vm.Inspector.Close(); return true; }
            if (!vm.IsAccountsActive) return false;
            if (IsDashboardSearchBoxClearing(e, vm)) return true;
            if (!vm.AccountsVm.HasSelection) return false;
            vm.AccountsVm.ClearSelectionCommand.Execute(null);
            return true;
        }

        private bool IsDashboardSearchBoxClearing(KeyEventArgs e, MainViewModel vm)
        {
            TextBox? txtSearch = ViewDashboard?.FindControl<TextBox>("TxtSearch");
            if (txtSearch == null || string.IsNullOrEmpty(vm.AccountsVm.SearchText))
                return false;

            object? focused = FocusManager?.GetFocusedElement();
            if (!ReferenceEquals(focused, txtSearch) && !ReferenceEquals(e.Source, txtSearch))
                return false;

            vm.AccountsVm.ClearSearchCommand.Execute(null);
            return true;
        }

        private void OnChromeVisualPreferencesChanged()
        {
            if (_isClosed)
                return;

            bool desired = VisualPreferencesService.Instance.SidebarCollapsed;
            if (desired == _isSidebarCollapsed)
                return;

            if (Dispatcher.UIThread.CheckAccess())
            {
                ApplySidebarRailState(desired, animate: true);
                return;
            }

            AppDispatcher.Post(() => { if (!_isClosed) ApplySidebarRailState(VisualPreferencesService.Instance.SidebarCollapsed, animate: true); });
        }

        private void OnToggleSidebarClicked(object? sender, RoutedEventArgs e) => ToggleSidebarRail();

        private void OnNotificationBellClicked(object? sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                ViewModel.IsNotificationCenterOpen = !ViewModel.IsNotificationCenterOpen;
        }
    }
}
