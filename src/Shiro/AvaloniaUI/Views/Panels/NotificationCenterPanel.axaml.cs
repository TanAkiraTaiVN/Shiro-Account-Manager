#nullable enable
using Avalonia;
using Avalonia.Controls;
using Shiro.AvaloniaUI.Controls;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views.Panels
{
    /// <summary>
    /// Frosted-glass Notification Center popover displaying the 30-item notification history ring buffer,
    /// alert filtering, unread badge state, and Mark-All-Read / Clear-All actions.
    /// </summary>
    public partial class NotificationCenterPanel : UserControl
    {
        public NotificationCenterPanel()
        {
            InitializeComponent();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            DataContext ??= NotificationHistoryService.Instance;
            AttachStaticBindings();
            if (IsVisible)
                PlayPopoverEntrance();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            DetachStaticBindings();
            if (ReferenceEquals(DataContext, NotificationHistoryService.Instance))
                DataContext = null;
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
            {
                HistoryScrollViewer?.ScrollToHome();
                PlayPopoverEntrance();
            }
        }

        internal void AttachStaticBindings()
        {
            if (NotificationHistoryItemsControl != null && NotificationHistoryItemsControl.ItemsSource == null)
                NotificationHistoryItemsControl.ItemsSource = NotificationHistoryService.Instance.FilteredHistoryItems;
        }

        internal void DetachStaticBindings()
        {
            if (NotificationHistoryItemsControl != null)
                NotificationHistoryItemsControl.ItemsSource = null;
        }

        private void PlayPopoverEntrance()
        {
            if (NotificationGlass != null)
            {
                SpringCompositionHelper.RunSpringEntrance(
                    NotificationGlass, offsetX: 0.0, offsetY: -8.0, fromScale: 0.97, durationMs: 220.0);
            }
        }
    }
}
