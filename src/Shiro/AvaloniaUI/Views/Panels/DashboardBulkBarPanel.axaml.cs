#nullable enable
using Avalonia;
using Avalonia.Controls;
using Shiro.AvaloniaUI.Controls;

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
            if (IsVisible && BulkBarGlass != null)
            {
                SpringCompositionHelper.RunSpringEntrance(
                    BulkBarGlass, offsetX: 0.0, offsetY: 12.0, fromScale: 0.96, durationMs: 220.0);
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty && change.GetNewValue<bool>() && BulkBarGlass != null)
            {
                SpringCompositionHelper.RunSpringEntrance(
                    BulkBarGlass, offsetX: 0.0, offsetY: 12.0, fromScale: 0.96, durationMs: 220.0);
            }
        }
    }
}
