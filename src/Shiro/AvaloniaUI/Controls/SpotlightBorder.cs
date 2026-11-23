#nullable enable
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Shiro.AvaloniaUI.Controls
{
    /// <summary>
    /// Fluent/Linear-inspired Border that renders a crisp top specular rim and a pointer-tracking
    /// radial spotlight across both surface and hairline border with zero idle CPU usage.
    /// </summary>
    public class SpotlightBorder : Border
    {
        public static readonly StyledProperty<Color> SpotlightColorProperty =
            AvaloniaProperty.Register<SpotlightBorder, Color>(
                nameof(SpotlightColor),
                defaultValue: Colors.White);

        public static readonly StyledProperty<double> SpotlightRadiusProperty =
            AvaloniaProperty.Register<SpotlightBorder, double>(
                nameof(SpotlightRadius),
                defaultValue: 180.0,
                coerce: (_, val) => Math.Max(1.0, val));

        public static readonly StyledProperty<double> SpotlightOpacityProperty =
            AvaloniaProperty.Register<SpotlightBorder, double>(
                nameof(SpotlightOpacity),
                defaultValue: 0.06,
                coerce: (_, val) => Math.Clamp(val, 0.0, 1.0));

        public static readonly StyledProperty<double> BorderGlowOpacityProperty =
            AvaloniaProperty.Register<SpotlightBorder, double>(
                nameof(BorderGlowOpacity),
                defaultValue: 0.22,
                coerce: (_, val) => Math.Clamp(val, 0.0, 1.0));

        public static readonly StyledProperty<bool> TopRimEnabledProperty =
            AvaloniaProperty.Register<SpotlightBorder, bool>(
                nameof(TopRimEnabled),
                defaultValue: true);

        public Color SpotlightColor
        {
            get => GetValue(SpotlightColorProperty);
            set => SetValue(SpotlightColorProperty, value);
        }

        public double SpotlightRadius
        {
            get => GetValue(SpotlightRadiusProperty);
            set => SetValue(SpotlightRadiusProperty, value);
        }

        public double SpotlightOpacity
        {
            get => GetValue(SpotlightOpacityProperty);
            set => SetValue(SpotlightOpacityProperty, value);
        }

        public double BorderGlowOpacity
        {
            get => GetValue(BorderGlowOpacityProperty);
            set => SetValue(BorderGlowOpacityProperty, value);
        }

        public bool TopRimEnabled
        {
            get => GetValue(TopRimEnabledProperty);
            set => SetValue(TopRimEnabledProperty, value);
        }

        private static readonly ImmutablePen s_topRimPen = CreateTopRimPen();

        private readonly SpotlightOverlay _overlay;
        private bool _isPointerOver;
        private Point _pointerPosition;

        private Size _cachedSize;
        private Point _cachedPointer;
        private Color _cachedColor;
        private double _cachedRadius;
        private double _cachedSurfaceOpacity;
        private double _cachedBorderOpacity;
        private ImmutableRadialGradientBrush? _cachedSurfaceBrush;
        private ImmutablePen? _cachedBorderPen;

        public SpotlightBorder()
        {
            _overlay = new SpotlightOverlay(this);
            VisualChildren.Add(_overlay);
        }

        private static ImmutablePen CreateTopRimPen()
        {
            var stops = new ImmutableGradientStop[]
            {
                new(0.0, Color.FromArgb(38, 255, 255, 255)),
                new(0.25, Color.FromArgb(10, 255, 255, 255)),
                new(0.65, Color.FromArgb(0, 255, 255, 255))
            };
            var brush = new ImmutableLinearGradientBrush(
                stops,
                startPoint: new RelativePoint(0.0, 0.0, RelativeUnit.Relative),
                endPoint: new RelativePoint(0.0, 1.0, RelativeUnit.Relative));
            return new ImmutablePen(brush, 1.0);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == SpotlightColorProperty ||
                change.Property == SpotlightRadiusProperty ||
                change.Property == SpotlightOpacityProperty ||
                change.Property == BorderGlowOpacityProperty ||
                change.Property == TopRimEnabledProperty ||
                change.Property == CornerRadiusProperty)
            {
                _overlay.InvalidateVisual();
            }
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

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            base.OnPointerEntered(e);
            _isPointerOver = true;
            _pointerPosition = e.GetPosition(this);
            _overlay.InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            Point pos = e.GetPosition(this);
            if (!_isPointerOver ||
                Math.Abs(pos.X - _pointerPosition.X) > 0.25 ||
                Math.Abs(pos.Y - _pointerPosition.Y) > 0.25)
            {
                _isPointerOver = true;
                _pointerPosition = pos;
                _overlay.InvalidateVisual();
            }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (_isPointerOver)
            {
                _isPointerOver = false;
                _overlay.InvalidateVisual();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _isPointerOver = false;
            base.OnDetachedFromVisualTree(e);
        }

        internal void RenderOverlay(DrawingContext context, Size size)
        {
            if (size.Width <= 1.0 || size.Height <= 1.0)
                return;

            Rect bounds = new Rect(size);
            CornerRadius corner = CornerRadius;

            if (TopRimEnabled)
            {
                Rect rimRect = bounds.Deflate(0.5);
                RoundedRect rimRounded = new RoundedRect(rimRect, DeflateCornerRadius(corner, 0.5));
                context.DrawRectangle(null, s_topRimPen, rimRounded);
            }

            if (!_isPointerOver)
                return;

            double surfaceOpacity = SpotlightOpacity;
            double borderOpacity = BorderGlowOpacity;
            if (surfaceOpacity <= 0.001 && borderOpacity <= 0.001)
                return;

            EnsurePointerBrushes(size, _pointerPosition, SpotlightColor, SpotlightRadius, surfaceOpacity, borderOpacity);

            if (_cachedSurfaceBrush != null)
            {
                using (context.PushClip(new RoundedRect(bounds, corner)))
                {
                    context.FillRectangle(_cachedSurfaceBrush, bounds);
                }
            }

            if (_cachedBorderPen != null)
            {
                Rect borderRect = bounds.Deflate(0.5);
                RoundedRect borderRounded = new RoundedRect(borderRect, DeflateCornerRadius(corner, 0.5));
                context.DrawRectangle(null, _cachedBorderPen, borderRounded);
            }
        }

        private void EnsurePointerBrushes(
            Size size,
            Point pointer,
            Color color,
            double radius,
            double surfaceOpacity,
            double borderOpacity)
        {
            if (_cachedSize == size &&
                _cachedPointer == pointer &&
                _cachedColor == color &&
                Math.Abs(_cachedRadius - radius) < 0.01 &&
                Math.Abs(_cachedSurfaceOpacity - surfaceOpacity) < 0.0001 &&
                Math.Abs(_cachedBorderOpacity - borderOpacity) < 0.0001)
            {
                return;
            }

            _cachedSize = size;
            _cachedPointer = pointer;
            _cachedColor = color;
            _cachedRadius = radius;
            _cachedSurfaceOpacity = surfaceOpacity;
            _cachedBorderOpacity = borderOpacity;

            var center = new RelativePoint(pointer.X / size.Width, pointer.Y / size.Height, RelativeUnit.Relative);
            var rx = new RelativeScalar(radius / size.Width, RelativeUnit.Relative);
            var ry = new RelativeScalar(radius / size.Height, RelativeUnit.Relative);

            _cachedSurfaceBrush = surfaceOpacity > 0.001
                ? BuildRadialBrush(color, surfaceOpacity, center, rx, ry)
                : null;

            _cachedBorderPen = borderOpacity > 0.001
                ? new ImmutablePen(BuildRadialBrush(color, borderOpacity, center, rx, ry), 1.0)
                : null;
        }

        private static ImmutableRadialGradientBrush BuildRadialBrush(
            Color color,
            double opacity,
            RelativePoint center,
            RelativeScalar rx,
            RelativeScalar ry)
        {
            byte peakAlpha = (byte)Math.Clamp(Math.Round(color.A * opacity), 0, 255);
            byte midAlpha = (byte)Math.Clamp(Math.Round(peakAlpha * 0.35), 0, 255);
            var stops = new ImmutableGradientStop[]
            {
                new(0.0, Color.FromArgb(peakAlpha, color.R, color.G, color.B)),
                new(0.55, Color.FromArgb(midAlpha, color.R, color.G, color.B)),
                new(1.0, Color.FromArgb(0, color.R, color.G, color.B))
            };
            return new ImmutableRadialGradientBrush(
                stops,
                center: center,
                gradientOrigin: center,
                radiusX: rx,
                radiusY: ry);
        }

        private static CornerRadius DeflateCornerRadius(CornerRadius cr, double inset)
        {
            return new CornerRadius(
                Math.Max(0.0, cr.TopLeft - inset),
                Math.Max(0.0, cr.TopRight - inset),
                Math.Max(0.0, cr.BottomRight - inset),
                Math.Max(0.0, cr.BottomLeft - inset));
        }

        private sealed class SpotlightOverlay : Control
        {
            private readonly SpotlightBorder _owner;

            public SpotlightOverlay(SpotlightBorder owner)
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
