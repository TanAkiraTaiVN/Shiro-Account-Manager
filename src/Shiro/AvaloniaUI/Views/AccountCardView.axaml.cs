#nullable enable
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views
{
    /// <summary>
    /// Reusable, zero-allocation virtualized Account Card control.
    /// Bound exclusively via DataContext so VirtualizingStackPanel recycles card instances permanently
    /// without ever destroying or rebuilding visual trees while scrolling.
    /// </summary>
    public partial class AccountCardView : UserControl
    {
        private DashboardView? _ownerDashboard;

        public AccountCardView()
        {
            InitializeComponent();
            if (ChkSelect != null)
            {
                ChkSelect.AddHandler(PointerPressedEvent, OnCheckBoxPointerPressed, RoutingStrategies.Tunnel);
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _ownerDashboard ??= this.FindAncestorOfType<DashboardView>();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _ownerDashboard = null;
            base.OnDetachedFromVisualTree(e);
        }

        private DashboardView? ResolveOwner()
            => _ownerDashboard ??= this.FindAncestorOfType<DashboardView>();

        private void OnCheckBoxPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            if (DataContext is AccountViewModel avm && ResolveOwner() is { } owner)
            {
                bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                owner.ViewModel.HandleAccountClickSelection(avm, shift);
                e.Handled = true;
            }
        }

        private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
            => ResolveOwner()?.HandleCardPointerPressed(CardRoot, DataContext as AccountViewModel, e);

        private void OnRootDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (DataContext is AccountViewModel avm)
                ResolveOwner()?.ViewModel.LaunchAccountCommand.Execute(avm);
        }

        private void OnLaunchOrReloginClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is AccountViewModel avm)
                ResolveOwner()?.ViewModel.PrimaryCardActionCommand.Execute(avm);
        }

        private void OnFocusWindowClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is AccountViewModel avm)
                ResolveOwner()?.ViewModel.FocusAccountWindowCommand.Execute(avm);
        }

        private void OnCloseClientClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is AccountViewModel avm)
                ResolveOwner()?.ViewModel.CloseAccountRobloxCommand.Execute(avm);
        }

        private void OnViewBanClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is AccountViewModel avm)
                ResolveOwner()?.ViewModel.ViewBanAccountCommand.Execute(avm);
        }

        private void OnDeleteClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is AccountViewModel avm)
                ResolveOwner()?.ViewModel.RemoveAccountCommand.Execute(avm);
        }

        private void OnOpenProfileClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is AccountViewModel avm)
                ResolveOwner()?.ViewModel.OpenProfileCommand.Execute(avm);
        }

        private void OnTogglePinClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is AccountViewModel avm)
                ResolveOwner()?.ViewModel.TogglePinCommand.Execute(avm);
        }

        private void OnMoreClicked(object? sender, RoutedEventArgs e)
        {
            if (sender is Control anchor && DataContext is AccountViewModel avm)
                ResolveOwner()?.ShowAccountContextMenu(anchor, avm);
        }
    }
}
