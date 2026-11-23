#nullable enable
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Shiro.AvaloniaUI.Controls
{
    /// <summary>
    /// Obsidian Studio loading placeholder border with a subtle GPU-animated diagonal sheen
    /// driven by Avalonia KeyFrame style animation (:shimmering pseudo-class).
    /// </summary>
    [PseudoClasses(":shimmering")]
    public class ShimmerSkeleton : Border
    {
        public static readonly StyledProperty<bool> IsShimmeringProperty =
            AvaloniaProperty.Register<ShimmerSkeleton, bool>(
                nameof(IsShimmering),
                defaultValue: true);

        public static readonly StyledProperty<double> ShimmerPhaseProperty =
            AvaloniaProperty.Register<ShimmerSkeleton, double>(
                nameof(ShimmerPhase),
                defaultValue: -0.4);

        public static readonly StyledProperty<Color> ShimmerColorProperty =
            AvaloniaProperty.Register<ShimmerSkeleton, Color>(
                nameof(ShimmerColor),
                defaultValue: Color.FromArgb(20, 255, 255, 255));

        public bool IsShimmering
        {
            get => GetValue(IsShimmeringProperty);
            set => SetValue(IsShimmeringProperty, value);
        }

        public double ShimmerPhase
        {
            get => GetValue(ShimmerPhaseProperty);
            set => SetValue(ShimmerPhaseProperty, value);
        }

        public Color ShimmerColor
        {
            get => GetValue(ShimmerColorProperty);
            set => SetValue(ShimmerColorProperty, value);
        }

        private readonly ShimmerOverlay _overlay;
        private bool _isAttached;
        private Color _cachedColor;
        private ImmutableGradientStop[]? _cachedStops;

        public ShimmerSkeleton()
        {
            _overlay = new ShimmerOverlay(this);
            VisualChildren.Add(_overlay);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _isAttached = true;
            EffectiveViewportChanged += OnEffectiveViewportChanged;
            UpdateShimmerState();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _isAttached = false;
            EffectiveViewportChanged -= OnEffectiveViewportChanged;
            UpdateShimmerState();
            base.OnDetachedFromVisualTree(e);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _overlay.Measure(default);
            return base.MeasureOverride(availableSize);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Size arranged = base.ArrangeOverride(finalSize);
            _overlay.Arrange(new Rect(arranged));
            return arranged;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsShimmeringProperty || change.Property == IsVisibleProperty)
            {
                UpdateShimmerState();
            }
            else if (change.Property == ShimmerColorProperty)
            {
                _cachedStops = null;
                _overlay.InvalidateVisual();
            }
            else if (change.Property == ShimmerPhaseProperty || change.Property == CornerRadiusProperty)
            {
                _overlay.InvalidateVisual();
            }
        }

        private void OnEffectiveViewportChanged(object? sender, EventArgs e)
        {
            UpdateShimmerState();
        }

        private void UpdateShimmerState()
        {
            bool active = IsShimmering && _isAttached && IsEffectivelyVisible;
            PseudoClasses.Set(":shimmering", active);
            _overlay.InvalidateVisual();
        }

        internal void RenderOverlay(DrawingContext context, Size size)
        {
            if (!IsShimmering || !_isAttached || !IsEffectivelyVisible)
                return;

            if (size.Width <= 0.0 || size.Height <= 0.0)
                return;

            double phase = ShimmerPhase;
            if (phase < -0.75 || phase > 1.75)
                return;

            Rect bounds = new Rect(size);
            var stops = GetOrCreateStops(ShimmerColor);
            var sweepBrush = new ImmutableLinearGradientBrush(
                stops,
                startPoint: new RelativePoint(phase - 0.35, 0.0, RelativeUnit.Relative),
                endPoint: new RelativePoint(phase + 0.35, 0.28, RelativeUnit.Relative));

            using (context.PushClip(new RoundedRect(bounds, CornerRadius)))
            {
                context.FillRectangle(sweepBrush, bounds);
            }
        }

        private ImmutableGradientStop[] GetOrCreateStops(Color color)
        {
            if (_cachedStops == null || _cachedColor != color)
            {
                _cachedColor = color;
                Color transparent = Color.FromArgb(0, color.R, color.G, color.B);
                _cachedStops = new ImmutableGradientStop[]
                {
                    new(0.0, transparent),
                    new(0.5, color),
                    new(1.0, transparent)
                };
            }
            return _cachedStops;
        }

        private sealed class ShimmerOverlay : Control
        {
            private readonly ShimmerSkeleton _owner;

            public ShimmerOverlay(ShimmerSkeleton owner)
            {
                _owner = owner;
                IsHitTestVisible = false;
            }

            public override void Render(DrawingContext context)
            {
                base.Render(context);
                _owner.RenderOverlay(context, Bounds.Size);
            }
        }
    }
}
