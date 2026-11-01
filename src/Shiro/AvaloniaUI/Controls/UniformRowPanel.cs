#nullable enable
using System;
using Avalonia;
using Avalonia.Controls;

namespace Shiro.AvaloniaUI.Controls
{
    /// <summary>
    /// Ultra-lightweight horizontal row panel that divides available row width equally across <see cref="Columns"/> slots.
    /// Enables responsive 2/3/4-column virtualized card grids while keeping 100% permanent child container recycling.
    /// </summary>
    public sealed class UniformRowPanel : Panel
    {
        public static readonly StyledProperty<int> ColumnsProperty =
            AvaloniaProperty.Register<UniformRowPanel, int>(
                nameof(Columns),
                defaultValue: 3,
                coerce: (_, val) => Math.Clamp(val, 1, 6));

        public int Columns
        {
            get => GetValue(ColumnsProperty);
            set => SetValue(ColumnsProperty, value);
        }

        static UniformRowPanel()
        {
            AffectsMeasure<UniformRowPanel>(ColumnsProperty);
            AffectsArrange<UniformRowPanel>(ColumnsProperty);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            int cols = Math.Clamp(Columns, 1, 6);
            double totalWidth = double.IsInfinity(availableSize.Width) ? 900.0 : Math.Max(0.0, availableSize.Width);
            double slotWidth = totalWidth / cols;
            double maxHeight = 0.0;

            int childCount = Children.Count;
            for (int i = 0; i < childCount; i++)
            {
                var child = Children[i];
                if (child == null)
                    continue;

                if (i >= cols || !child.IsVisible)
                {
                    child.Measure(new Size(0, 0));
                    continue;
                }

                child.Measure(new Size(slotWidth, availableSize.Height));
                if (child.DesiredSize.Height > maxHeight)
                    maxHeight = child.DesiredSize.Height;
            }

            double finalHeight = !double.IsNaN(Height) && Height > 0 ? Height : maxHeight;
            return new Size(totalWidth, finalHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int cols = Math.Clamp(Columns, 1, 6);
            double slotWidth = Math.Max(0.0, finalSize.Width) / cols;
            double rowHeight = finalSize.Height;

            int childCount = Children.Count;
            for (int i = 0; i < childCount; i++)
            {
                var child = Children[i];
                if (child == null)
                    continue;

                if (i >= cols || !child.IsVisible)
                {
                    child.Arrange(new Rect(0, 0, 0, 0));
                    continue;
                }

                child.Arrange(new Rect(i * slotWidth, 0, slotWidth, rowHeight));
            }

            return finalSize;
        }
    }
}
