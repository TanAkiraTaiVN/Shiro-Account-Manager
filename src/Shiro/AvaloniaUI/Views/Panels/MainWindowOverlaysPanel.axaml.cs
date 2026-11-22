using Avalonia;
using Avalonia.Controls;

namespace Shiro.AvaloniaUI.Views.Panels
{
    public partial class MainWindowOverlaysPanel : UserControl
    {
        public MainWindowOverlaysPanel()
        {
            InitializeComponent();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (ToastItemsControl != null && ToastItemsControl.ItemsSource == null)
                ToastItemsControl.ItemsSource = Shiro.Services.ToastService.ActiveToasts;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            DetachStaticBindings();
            base.OnDetachedFromVisualTree(e);
        }

        internal void DetachStaticBindings()
        {
            if (ToastItemsControl != null)
                ToastItemsControl.ItemsSource = null;
        }
    }
}
