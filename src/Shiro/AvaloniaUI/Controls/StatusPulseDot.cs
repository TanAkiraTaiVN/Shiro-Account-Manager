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
    /// Crisp status indicator dot with an optional GPU-animated pulsing halo ring driven by
    /// Avalonia Style KeyFrames (:pulsing pseudo-class) when attached, visible, and pulsing.
    /// </summary>
    [PseudoClasses(":pulsing")]
    public class StatusPulseDot : Control
    {
        public static readonly StyledProperty<IBrush?> DotBrushProperty =
            AvaloniaProperty.Register<StatusPulseDot, IBrush?>(
                nameof(DotBrush),
                defaultValue: new ImmutableSolidColorBrush(Color.FromRgb(74, 222, 128)));

        public static readonly StyledProperty<double> DotSizeProperty =
            AvaloniaProperty.Register<StatusPulseDot, double>(
                nameof(DotSize),
                defaultValue: 6.0,
                coerce: (_, val) => Math.Max(2.0, val));

        public static readonly StyledProperty<bool> IsPulsingProperty =
            AvaloniaProperty.Register<StatusPulseDot, bool>(
                nameof(IsPulsing),
                defaultValue: false);

        public static readonly StyledProperty<double> PulseProgressProperty =
            AvaloniaProperty.Register<StatusPulseDot, double>(
                nameof(PulseProgress),
                defaultValue: 0.0,
                coerce: (_, val) => Math.Clamp(val, 0.0, 1.0));

        public IBrush? DotBrush
        {
            get => GetValue(DotBrushProperty);
            set => SetValue(DotBrushProperty, value);
        }

        public double DotSize
        {
            get => GetValue(DotSizeProperty);
            set => SetValue(DotSizeProperty, value);
        }

        public bool IsPulsing
        {
            get => GetValue(IsPulsingProperty);
            set => SetValue(IsPulsingProperty, value);
        }

        public double PulseProgress
        {
            get => GetValue(PulseProgressProperty);
            set => SetValue(PulseProgressProperty, value);
        }

        private bool _isAttached;
        private Pen? _cachedHaloPen;
        private IBrush? _cachedPenBrush;

        static StatusPulseDot()
        {
            AffectsMeasure<StatusPulseDot>(DotSizeProperty);
            AffectsRender<StatusPulseDot>(
                DotBrushProperty,
                DotSizeProperty,
                IsPulsingProperty,
                PulseProgressProperty);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _isAttached = true;
            EffectiveViewportChanged += OnEffectiveViewportChanged;
            UpdatePulseState();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _isAttached = false;
            EffectiveViewportChanged -= OnEffectiveViewportChanged;
            UpdatePulseState();
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsPulsingProperty || change.Property == IsVisibleProperty)
            {
                UpdatePulseState();
            }
            else if (change.Property == DotBrushProperty)
            {
                _cachedHaloPen = null;
                _cachedPenBrush = null;
            }
        }

        private void OnEffectiveViewportChanged(object? sender, EventArgs e)
        {
            UpdatePulseState();
        }

        private void UpdatePulseState()
        {
            bool active = IsPulsing && _isAttached && IsEffectivelyVisible;
            PseudoClasses.Set(":pulsing", active);
            if (!active)
                InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double size = Math.Max(2.0, DotSize);
            double w = !double.IsNaN(Width) && Width > 0.0 ? Width : size;
            double h = !double.IsNaN(Height) && Height > 0.0 ? Height : size;
            return new Size(w, h);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            IBrush? brush = DotBrush;
            if (brush == null)
                return;

            Rect bounds = new Rect(Bounds.Size);
            if (bounds.Width <= 0.0 || bounds.Height <= 0.0)
                return;

            Point center = bounds.Center;
            double size = Math.Max(2.0, DotSize);
            double coreRadius = Math.Min(size, Math.Min(bounds.Width, bounds.Height)) * 0.5;

            if (IsPulsing && _isAttached && IsEffectivelyVisible)
            {
                double p = Math.Clamp(PulseProgress, 0.0, 1.0);
                if (p > 0.001 && p < 0.999)
                {
                    double haloRadius = coreRadius * (1.0 + 1.35 * p);
                    double haloOpacity = 0.55 * (1.0 - p);

                    using (context.PushOpacity(haloOpacity * 0.35))
                    {
                        context.DrawEllipse(brush, null, center, haloRadius, haloRadius);
                    }

                    using (context.PushOpacity(haloOpacity))
                    {
                        context.DrawEllipse(null, GetHaloPen(brush), center, haloRadius, haloRadius);
                    }
                }
            }

            context.DrawEllipse(brush, null, center, coreRadius, coreRadius);
        }

        private Pen GetHaloPen(IBrush brush)
        {
            if (_cachedHaloPen == null || !ReferenceEquals(_cachedPenBrush, brush))
            {
                _cachedPenBrush = brush;
                _cachedHaloPen = new Pen(brush, 1.2);
            }
            return _cachedHaloPen;
        }
    }
}
