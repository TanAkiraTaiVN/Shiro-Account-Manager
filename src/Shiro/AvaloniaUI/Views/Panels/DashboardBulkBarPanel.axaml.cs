#nullable enable
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Shiro.AvaloniaUI.Controls;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views.Panels
{
    public partial class DashboardBulkBarPanel : UserControl
    {
        public DashboardBulkBarPanel()
        {
            InitializeComponent();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (IsVisible)
                TriggerBulkBarEntrance();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
                TriggerBulkBarEntrance();
        }

        private void TriggerBulkBarEntrance()
        {
            if (!IsVisible || BulkBarGlass == null)
                return;

            Visual target = (Visual?)BulkBarShadowHost ?? BulkBarGlass;
            if (BulkBarGlass.Bounds.Width <= 0.0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible || BulkBarGlass == null)
                        return;

                    Visual deferredTarget = (Visual?)BulkBarShadowHost ?? BulkBarGlass;
                    if (TopLevel.GetTopLevel(deferredTarget) != null)
                    {
                        SpringCompositionHelper.RunSpringEntrance(
                            deferredTarget, offsetX: 0.0, offsetY: 12.0, fromScale: 0.96, durationMs: 220.0);
                    }
                }, DispatcherPriority.Loaded);
                return;
            }

            SpringCompositionHelper.RunSpringEntrance(
                target, offsetX: 0.0, offsetY: 12.0, fromScale: 0.96, durationMs: 220.0);
        }

        private void OnMenuScheduleSelected(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.OpenNewScheduleForAccounts(vm.AccountsVm.GetSelectedAccounts(), vm.LaunchBar.PlaceId, vm.LaunchBar.JobId);
        }
    }
}
