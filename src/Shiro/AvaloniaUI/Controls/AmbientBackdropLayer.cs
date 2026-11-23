#nullable enable
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Controls
{
    /// <summary>
    /// Hardware-accelerated ambient radial lighting canvas painted directly via DrawingContext.
    /// Redraws strictly on resize or AccentThemeService.ThemeChanged with zero idle overhead.
    /// </summary>
    public class AmbientBackdropLayer : Control
    {
        public static readonly StyledProperty<double> PrimaryOrbOpacityScaleProperty =
            AvaloniaProperty.Register<AmbientBackdropLayer, double>(
                nameof(PrimaryOrbOpacityScale),
                defaultValue: 1.0,
                coerce: (_, val) => Math.Clamp(val, 0.0, 2.0));

        public static readonly StyledProperty<double> SecondaryOrbOpacityScaleProperty =
            AvaloniaProperty.Register<AmbientBackdropLayer, double>(
                nameof(SecondaryOrbOpacityScale),
                defaultValue: 1.0,
                coerce: (_, val) => Math.Clamp(val, 0.0, 2.0));

        public static readonly StyledProperty<double> VignetteOpacityProperty =
            AvaloniaProperty.Register<AmbientBackdropLayer, double>(
                nameof(VignetteOpacity),
                defaultValue: 0.45,
                coerce: (_, val) => Math.Clamp(val, 0.0, 1.0));

        public double PrimaryOrbOpacityScale
        {
            get => GetValue(PrimaryOrbOpacityScaleProperty);
            set => SetValue(PrimaryOrbOpacityScaleProperty, value);
        }

        public double SecondaryOrbOpacityScale
        {
            get => GetValue(SecondaryOrbOpacityScaleProperty);
            set => SetValue(SecondaryOrbOpacityScaleProperty, value);
        }

        public double VignetteOpacity
        {
            get => GetValue(VignetteOpacityProperty);
            set => SetValue(VignetteOpacityProperty, value);
        }

        private bool _isAttached;
        private AccentTheme? _cachedTheme;
        private double _cachedPrimaryScale = double.NaN;
        private double _cachedSecondaryScale = double.NaN;
        private double _cachedVignetteOpacity = double.NaN;

        private ImmutableRadialGradientBrush? _primaryOrbBrush;
        private ImmutableRadialGradientBrush? _secondaryOrbBrush;
        private ImmutableLinearGradientBrush? _vignetteBrush;

        static AmbientBackdropLayer()
        {
            AffectsRender<AmbientBackdropLayer>(
                PrimaryOrbOpacityScaleProperty,
                SecondaryOrbOpacityScaleProperty,
                VignetteOpacityProperty);
        }

        public AmbientBackdropLayer()
        {
            IsHitTestVisible = false;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (!_isAttached)
            {
                _isAttached = true;
                AccentThemeService.ThemeChanged += OnThemeChanged;
            }
            InvalidateBrushes();
            InvalidateVisual();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_isAttached)
            {
                _isAttached = false;
                AccentThemeService.ThemeChanged -= OnThemeChanged;
            }
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            InvalidateVisual();
        }

        private void OnThemeChanged(AccentTheme theme)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                if (_isAttached)
                {
                    InvalidateBrushes();
                    InvalidateVisual();
                }
                return;
            }

            AppDispatcher.Post(() =>
            {
                if (_isAttached)
                {
                    InvalidateBrushes();
                    InvalidateVisual();
                }
            });
        }

        private void InvalidateBrushes()
        {
            _cachedTheme = null;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            Size size = Bounds.Size;
            if (size.Width <= 0.0 || size.Height <= 0.0)
                return;

            EnsureBrushes();

            Rect bounds = new Rect(size);
            if (_primaryOrbBrush != null)
                context.FillRectangle(_primaryOrbBrush, bounds);
            if (_secondaryOrbBrush != null)
                context.FillRectangle(_secondaryOrbBrush, bounds);
            if (_vignetteBrush != null)
                context.FillRectangle(_vignetteBrush, bounds);
        }

        private void EnsureBrushes()
        {
            AccentTheme theme = AccentThemeService.CurrentTheme;
            double primaryScale = PrimaryOrbOpacityScale;
            double secondaryScale = SecondaryOrbOpacityScale;
            double vignetteOpacity = VignetteOpacity;

            if (_cachedTheme == theme &&
                Math.Abs(_cachedPrimaryScale - primaryScale) < 0.0001 &&
                Math.Abs(_cachedSecondaryScale - secondaryScale) < 0.0001 &&
                Math.Abs(_cachedVignetteOpacity - vignetteOpacity) < 0.0001)
            {
                return;
            }

            _cachedTheme = theme;
            _cachedPrimaryScale = primaryScale;
            _cachedSecondaryScale = secondaryScale;
            _cachedVignetteOpacity = vignetteOpacity;

            var palette = AccentThemeService.GetExtendedColors(theme);

            _primaryOrbBrush = BuildOrbBrush(
                palette.Ambient,
                primaryScale,
                new RelativePoint(0.14, 0.08, RelativeUnit.Relative),
                new RelativeScalar(0.54, RelativeUnit.Relative),
                new RelativeScalar(0.48, RelativeUnit.Relative));

            _secondaryOrbBrush = BuildOrbBrush(
                palette.AmbientSecondary,
                secondaryScale,
                new RelativePoint(0.86, 0.12, RelativeUnit.Relative),
                new RelativeScalar(0.46, RelativeUnit.Relative),
                new RelativeScalar(0.44, RelativeUnit.Relative));

            _vignetteBrush = BuildVignetteBrush(vignetteOpacity);
        }

        private static ImmutableRadialGradientBrush? BuildOrbBrush(
            Color baseColor,
            double scale,
            RelativePoint center,
            RelativeScalar radiusX,
            RelativeScalar radiusY)
        {
            byte peakAlpha = (byte)Math.Clamp(Math.Round(baseColor.A * scale), 0, 255);
            if (peakAlpha == 0)
                return null;

            byte midAlpha = (byte)Math.Clamp(Math.Round(peakAlpha * 0.36), 0, 255);
            var stops = new ImmutableGradientStop[]
            {
                new(0.0, Color.FromArgb(peakAlpha, baseColor.R, baseColor.G, baseColor.B)),
                new(0.5, Color.FromArgb(midAlpha, baseColor.R, baseColor.G, baseColor.B)),
                new(1.0, Color.FromArgb(0, baseColor.R, baseColor.G, baseColor.B))
            };
            return new ImmutableRadialGradientBrush(
                stops,
                center: center,
                gradientOrigin: center,
                radiusX: radiusX,
                radiusY: radiusY);
        }

        private static ImmutableLinearGradientBrush? BuildVignetteBrush(double opacity)
        {
            if (opacity <= 0.001)
                return null;

            byte bottomAlpha = (byte)Math.Clamp(Math.Round(opacity * 255.0), 0, 255);
            byte midAlpha = (byte)Math.Clamp(Math.Round(bottomAlpha * 0.25), 0, 255);
            var stops = new ImmutableGradientStop[]
            {
                new(0.0, Color.FromArgb(0, 8, 8, 12)),
                new(0.58, Color.FromArgb(midAlpha, 8, 8, 12)),
                new(1.0, Color.FromArgb(bottomAlpha, 8, 8, 12))
            };
            return new ImmutableLinearGradientBrush(
                stops,
                startPoint: new RelativePoint(0.0, 0.0, RelativeUnit.Relative),
                endPoint: new RelativePoint(0.0, 1.0, RelativeUnit.Relative));
        }
    }
}
