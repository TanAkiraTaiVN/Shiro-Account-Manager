#nullable enable
using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
            {
                SpringCompositionHelper.RunSpringEntrance(
                    NavSidebarHost,
                    offsetX: collapsed ? -6.0 : 6.0,
                    offsetY: 0.0,
                    fromScale: 1.0,
                    durationMs: 200.0);
            }
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

            if (e.Key == Key.Escape &&
                ViewModel != null &&
                !ViewModel.CommandPalette.IsCommandPaletteOpen &&
                ViewModel.IsNotificationCenterOpen)
            {
                ViewModel.IsNotificationCenterOpen = false;
                e.Handled = true;
                return true;
            }

            return false;
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

            AppDispatcher.Post(() =>
            {
                if (!_isClosed)
                    ApplySidebarRailState(VisualPreferencesService.Instance.SidebarCollapsed, animate: true);
            });
        }

        private void OnToggleSidebarClicked(object? sender, RoutedEventArgs e) => ToggleSidebarRail();

        private void OnNotificationBellClicked(object? sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                ViewModel.IsNotificationCenterOpen = !ViewModel.IsNotificationCenterOpen;
        }
    }
}
