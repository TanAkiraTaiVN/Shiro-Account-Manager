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
    public partial class SchedulerEditorDrawer : UserControl
    {
        private ScheduleEditorViewModel? _subscribedVm;

        public SchedulerEditorDrawer()
        {
            InitializeComponent();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            SyncSubscription();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            SyncSubscription();
            if (IsVisible)
                PlayDrawerEntrance();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            DetachSubscription();
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
                    PlayDrawerEntrance();
                }
                else
                {
                    RestoreFocusIfNeeded();
                }
            }
        }

        private void SyncSubscription()
        {
            var nextVm = DataContext as ScheduleEditorViewModel;
            if (ReferenceEquals(_subscribedVm, nextVm))
                return;

            DetachSubscription();
            if (nextVm != null)
            {
                _subscribedVm = nextVm;
                _subscribedVm.PropertyChanged += OnEditorPropertyChanged;
            }
        }

        private void DetachSubscription()
        {
            if (_subscribedVm != null)
            {
                _subscribedVm.PropertyChanged -= OnEditorPropertyChanged;
                _subscribedVm = null;
            }
        }

        private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ScheduleEditorViewModel.EditingScheduleId) &&
                IsVisible && DrawerContentHost != null)
            {
                DrawerScrollViewer?.ScrollToHome();
                SpringCompositionHelper.RunSpringEntrance(
                    DrawerContentHost, offsetX: 10.0, offsetY: 0.0, fromScale: 0.992, durationMs: 180.0);
            }
        }

        private void PlayDrawerEntrance()
        {
            Visual? target = (Visual?)DrawerShadowHost ?? DrawerGlass;
            if (target != null)
            {
                SpringCompositionHelper.RunSpringEntrance(
                    target, offsetX: 28.0, offsetY: 0.0, fromScale: 0.985, durationMs: 240.0);
            }
        }

        private void RestoreFocusIfNeeded()
        {
            var focusMgr = TopLevel.GetTopLevel(this)?.FocusManager;
            if (focusMgr?.GetFocusedElement() is Visual focused &&
                (ReferenceEquals(focused, this) || this.IsVisualAncestorOf(focused)))
            {
                this.FindAncestorOfType<SchedulerView>()?.FindControl<Button>("BtnNewSchedule")?.Focus();
            }
        }
    }
}
