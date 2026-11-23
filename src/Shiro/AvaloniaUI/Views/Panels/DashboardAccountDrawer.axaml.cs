#nullable enable
using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Shiro.AvaloniaUI.Controls;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views.Panels
{
    /// <summary>
    /// Non-modal 340px frosted-glass right-hand account inspector drawer with 120Hz spring slide-in
    /// entrance, clean focus restoration on close, and zero pointer interference when closed.
    /// </summary>
    public partial class DashboardAccountDrawer : UserControl
    {
        private AccountInspectorViewModel? _subscribedVm;

        public DashboardAccountDrawer()
        {
            InitializeComponent();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            SyncViewModelSubscription();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            SyncViewModelSubscription();
            if (IsVisible)
                PlayDrawerEntrance(offsetX: 28.0, durationMs: 240.0);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            DetachViewModel();
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty)
            {
                if (change.GetNewValue<bool>())
                {
                    DrawerScrollViewer?.ScrollToHome();
                    PlayDrawerEntrance(offsetX: 28.0, durationMs: 240.0);
                }
                else
                {
                    RestoreDashboardFocusIfNeeded();
                }
            }
        }

        private void SyncViewModelSubscription()
        {
            var nextVm = DataContext as AccountInspectorViewModel;
            if (ReferenceEquals(_subscribedVm, nextVm))
                return;

            DetachViewModel();
            if (nextVm != null)
            {
                _subscribedVm = nextVm;
                _subscribedVm.PropertyChanged += OnInspectorPropertyChanged;
            }
        }

        private void DetachViewModel()
        {
            if (_subscribedVm != null)
            {
                _subscribedVm.PropertyChanged -= OnInspectorPropertyChanged;
                _subscribedVm = null;
            }
        }

        private void OnInspectorPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AccountInspectorViewModel.InspectedAccount) &&
                IsVisible &&
                _subscribedVm?.InspectedAccount != null &&
                DrawerContentHost != null)
            {
                DrawerScrollViewer?.ScrollToHome();
                SpringCompositionHelper.RunSpringEntrance(
                    DrawerContentHost, offsetX: 12.0, offsetY: 0.0, fromScale: 0.992, durationMs: 180.0);
            }
        }

        private void PlayDrawerEntrance(double offsetX, double durationMs)
        {
            if (DrawerGlass != null)
            {
                SpringCompositionHelper.RunSpringEntrance(
                    DrawerGlass, offsetX: offsetX, offsetY: 0.0, fromScale: 0.985, durationMs: durationMs);
            }
        }

        private void RestoreDashboardFocusIfNeeded()
        {
            var focusMgr = TopLevel.GetTopLevel(this)?.FocusManager;
            if (focusMgr?.GetFocusedElement() is Visual focused &&
                (ReferenceEquals(focused, this) || this.IsVisualAncestorOf(focused)))
            {
                this.FindAncestorOfType<DashboardView>()?.FindControl<Border>("ListViewportHost")?.Focus();
            }
        }
    }
}
