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
        public static readonly StyledProperty<double> CardHeightProperty =
            AvaloniaProperty.Register<AccountCardView, double>(nameof(CardHeight), 138.0);

        public static readonly StyledProperty<double> CardBottomSpacingProperty =
            AvaloniaProperty.Register<AccountCardView, double>(nameof(CardBottomSpacing), 12.0);

        private DashboardView? _ownerDashboard;
        private long _lastDoubleTapTimestamp;

        public double CardHeight
        {
            get => GetValue(CardHeightProperty);
            set => SetValue(CardHeightProperty, value);
        }

        public double CardBottomSpacing
        {
            get => GetValue(CardBottomSpacingProperty);
            set => SetValue(CardBottomSpacingProperty, value);
        }

        public AccountCardView()
        {
            InitializeComponent();
            ApplyDensityLayout();
            if (ChkSelect != null)
            {
                ChkSelect.AddHandler(PointerPressedEvent, OnCheckBoxPointerPressed, RoutingStrategies.Tunnel);
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == CardHeightProperty || change.Property == CardBottomSpacingProperty)
            {
                ApplyDensityLayout();
            }
        }

        private void ApplyDensityLayout()
        {
            double h = CardHeight > 0.0 ? CardHeight : 138.0;
            double bottom = CardBottomSpacing >= 0.0 ? CardBottomSpacing : 12.0;
            if (CardRoot != null)
            {
                CardRoot.Height = h;
                CardRoot.Margin = new Thickness(5, 0, 5, bottom);
            }
            if (CardContentGrid != null)
            {
                CardContentGrid.Margin = h <= 126.0
                    ? new Thickness(10, 7)
                    : (h >= 150.0 ? new Thickness(12, 13) : new Thickness(10, 10));
            }
            if (CardTopRow != null)
            {
                CardTopRow.Margin = h <= 126.0
                    ? new Thickness(0, 0, 0, 6)
                    : (h >= 150.0 ? new Thickness(0, 0, 0, 11) : new Thickness(0, 0, 0, 9));
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _ownerDashboard ??= this.FindAncestorOfType<DashboardView>();
            SyncKeyboardFocusRing();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _ownerDashboard = null;
            _lastDoubleTapTimestamp = 0;
            SetKeyboardFocused(false);
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            _lastDoubleTapTimestamp = 0;
            base.OnDataContextChanged(e);
            SyncKeyboardFocusRing();
        }

        internal void SetKeyboardFocused(bool isFocused)
        {
            CardRoot?.Classes.Set("kbd-focused", isFocused);
            if (KeyboardFocusRing != null)
            {
                KeyboardFocusRing.Classes.Set("kbd-focused", isFocused);
                KeyboardFocusRing.IsVisible = isFocused;
            }
        }

        internal void SyncKeyboardFocusRing()
            => SetKeyboardFocused(ResolveOwner()?.IsKeyboardFocusedAccount(DataContext as AccountViewModel) == true);

        private DashboardView? ResolveOwner()
            => _ownerDashboard ??= this.FindAncestorOfType<DashboardView>();

        private void OnCheckBoxPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            if (DataContext is AccountViewModel avm && ResolveOwner() is { } owner)
            {
                owner.SetKeyboardFocusedAccount(avm);
                bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                owner.ViewModel.HandleAccountClickSelection(avm, shift);
                e.Handled = true;
            }
        }

        private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
            => ResolveOwner()?.HandleCardPointerPressed(CardRoot, DataContext as AccountViewModel, e);

        private void OnRootDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (!DashboardView.ShouldTriggerDoubleTap(e.Source, ref _lastDoubleTapTimestamp))
            {
                if (e.Source is not Visual v || !DashboardView.IsInsideInteractiveControl(v))
                    e.Handled = true;
                return;
            }

            e.Handled = true;
            if (DataContext is AccountViewModel avm && !avm.IsLaunching)
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
