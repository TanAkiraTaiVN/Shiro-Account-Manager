#nullable enable
using System;
using Avalonia;
using Avalonia.Controls;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views.Panels
{
    public partial class SchedulerTimelinePanel : UserControl
    {
        public SchedulerTimelinePanel()
        {
            InitializeComponent();
        }
    }

    /// <summary>
    /// Arranges 24-hour <see cref="SchedulerTimelineBlockViewModel"/> blocks proportionally by
    /// normalized <c>LeftFraction</c>/<c>WidthFraction</c> and non-overlapping <c>Lane</c> rows.
    /// </summary>
    public sealed class TimelineTrackPanel : Panel
    {
        private const double LaneHeight = 34.0;
        private const double BlockHeight = 28.0;
        private const double MinBlockWidth = 18.0;

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0.0
                ? 600.0
                : availableSize.Width;

            int maxLane = 0;
            foreach (Control child in Children)
            {
                if (child.DataContext is SchedulerTimelineBlockViewModel block)
                {
                    maxLane = Math.Max(maxLane, block.Lane);
                    double targetWidth = Math.Max(MinBlockWidth, Math.Clamp(block.WidthFraction, 0.0, 1.0) * width);
                    child.Measure(new Size(targetWidth, BlockHeight));
                }
                else
                {
                    child.Measure(new Size(width, BlockHeight));
                }
            }

            double height = Math.Max(1, maxLane + 1) * LaneHeight + 6.0;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double trackWidth = Math.Max(0.0, finalSize.Width);
            foreach (Control child in Children)
            {
                if (child.DataContext is SchedulerTimelineBlockViewModel block)
                {
                    double x = Math.Clamp(block.LeftFraction, 0.0, 1.0) * trackWidth;
                    double availableRight = Math.Max(0.0, trackWidth - x);
                    double rawWidth = Math.Clamp(block.WidthFraction, 0.0, 1.0) * trackWidth;
                    double w = Math.Min(availableRight, Math.Max(Math.Min(MinBlockWidth, availableRight), rawWidth));
                    double y = block.Lane * LaneHeight + 3.0;
                    child.Arrange(new Rect(x, y, w, BlockHeight));
                }
                else
                {
                    child.Arrange(new Rect(0.0, 3.0, trackWidth, BlockHeight));
                }
            }

            return finalSize;
        }
    }

    /// <summary>
    /// Positions the live current-time needle across the 24-hour timeline at <see cref="NowFraction"/>.
    /// </summary>
    public sealed class TimelineNowNeedlePanel : Panel
    {
        public static readonly StyledProperty<double> NowFractionProperty =
            AvaloniaProperty.Register<TimelineNowNeedlePanel, double>(nameof(NowFraction), defaultValue: 0.0);

        static TimelineNowNeedlePanel()
        {
            AffectsArrange<TimelineNowNeedlePanel>(NowFractionProperty);
        }

        public double NowFraction
        {
            get => GetValue(NowFractionProperty);
            set => SetValue(NowFractionProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            Size childAvail = new(
                double.IsInfinity(availableSize.Width) ? 600.0 : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? 40.0 : availableSize.Height);
            foreach (Control child in Children)
                child.Measure(childAvail);
            return new Size(0.0, 0.0);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double width = Math.Max(0.0, finalSize.Width);
            double height = Math.Max(0.0, finalSize.Height);
            double fraction = Math.Clamp(NowFraction, 0.0, 1.0);
            double anchorX = fraction * width;

            foreach (Control child in Children)
            {
                double childW = Math.Max(2.0, child.DesiredSize.Width);
                double x = Math.Clamp(anchorX - (childW * 0.5), 0.0, Math.Max(0.0, width - childW));
                child.Arrange(new Rect(x, 0.0, childW, height));
            }

            return finalSize;
        }
    }
}
