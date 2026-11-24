#nullable enable
using Avalonia;
using Avalonia.Controls;
using Shiro.AvaloniaUI.Controls;

namespace Shiro.AvaloniaUI.Views.Panels
{
    public partial class SchedulerHistoryPanel : UserControl
    {
        public SchedulerHistoryPanel()
        {
            InitializeComponent();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty && change.GetNewValue<bool>() && HistoryCardHost != null)
            {
                SpringCompositionHelper.RunSpringEntrance(
                    HistoryCardHost, offsetX: 0.0, offsetY: -8.0, fromScale: 0.985, durationMs: 200.0);
            }
        }
    }
}
