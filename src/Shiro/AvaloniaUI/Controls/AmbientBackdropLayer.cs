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
    /// Ambient radial lighting backdrop that delegates to the render-thread SkSL mesh shader
    /// when glass shaders are enabled and falls back cleanly to DrawingContext gradients otherwise.
    /// </summary>
    public class AmbientBackdropLayer : Control
    {
        public static readonly StyledProperty<double> PrimaryOrbOpacityScaleProperty =
            AvaloniaProperty.Register<AmbientBackdropLayer, double>(
                nameof(PrimaryOrbOpacityScale),
                defaultValue: 1.0,
                coerce: (_, val) => double.IsNaN(val) ? 1.0 : Math.Clamp(val, 0.0, 2.0));

        public static readonly StyledProperty<double> SecondaryOrbOpacityScaleProperty =
            AvaloniaProperty.Register<AmbientBackdropLayer, double>(
                nameof(SecondaryOrbOpacityScale),
                defaultValue: 1.0,
                coerce: (_, val) => double.IsNaN(val) ? 1.0 : Math.Clamp(val, 0.0, 2.0));

        public static readonly StyledProperty<double> VignetteOpacityProperty =
            AvaloniaProperty.Register<AmbientBackdropLayer, double>(
                nameof(VignetteOpacity),
                defaultValue: 0.45,
                coerce: (_, val) => double.IsNaN(val) ? 0.45 : Math.Clamp(val, 0.0, 1.0));

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

        private readonly SkslAmbientMesh _mesh;
        private readonly VignetteOverlay _vignetteOverlay;

        private bool _isAttached;
        private AccentTheme? _cachedTheme;
        private SurfacePreset? _cachedPreset;
        private Color? _cachedAdaptive;
        private double _cachedPrimaryScale = double.NaN;
        private double _cachedSecondaryScale = double.NaN;
        private double _cachedVignetteOpacity = double.NaN;

        private ImmutableRadialGradientBrush? _primaryOrbBrush;
        private ImmutableRadialGradientBrush? _secondaryOrbBrush;
        private ImmutableLinearGradientBrush? _vignetteBrush;

        internal bool IsUsingShaderChild => _mesh.IsVisible;

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
            _mesh = new SkslAmbientMesh
            {
                AutoSubscribeToThemeServices = false,
                ZIndex = 0
            };
            _vignetteOverlay = new VignetteOverlay(this);
            VisualChildren.Add(_mesh);
            VisualChildren.Add(_vignetteOverlay);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (!_isAttached)
            {
                _isAttached = true;
                AccentThemeService.ThemeChanged += OnThemeChanged;
                AccentThemeService.SurfacePresetChanged += OnSurfacePresetChanged;
                VisualPreferencesService.Instance.PreferencesChanged += OnPreferencesChanged;
            }

            SyncShaderMode(animate: false);
            InvalidateBrushes();
            InvalidateVisual();
            _vignetteOverlay.InvalidateVisual();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_isAttached)
            {
                _isAttached = false;
                AccentThemeService.ThemeChanged -= OnThemeChanged;
                AccentThemeService.SurfacePresetChanged -= OnSurfacePresetChanged;
                VisualPreferencesService.Instance.PreferencesChanged -= OnPreferencesChanged;
            }

            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == PrimaryOrbOpacityScaleProperty)
            {
                _mesh.PrimaryOrbOpacityScale = PrimaryOrbOpacityScale;
                InvalidateBrushes();
            }
            else if (change.Property == SecondaryOrbOpacityScaleProperty)
            {
                _mesh.SecondaryOrbOpacityScale = SecondaryOrbOpacityScale;
                InvalidateBrushes();
            }
            else if (change.Property == VignetteOpacityProperty)
            {
                _vignetteOverlay.IsVisible = _mesh.IsVisible && VignetteOpacity > 0.001;
                InvalidateBrushes();
                _vignetteOverlay.InvalidateVisual();
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _mesh.Measure(availableSize);
            _vignetteOverlay.Measure(availableSize);
            return base.MeasureOverride(availableSize);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var rect = new Rect(finalSize);
            _mesh.Arrange(rect);
            _vignetteOverlay.Arrange(rect);
            return base.ArrangeOverride(finalSize);
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            InvalidateVisual();
            _vignetteOverlay.InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_mesh.IsVisible)
                return;

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

        private void RenderVignette(DrawingContext context, Size size)
        {
            if (!_mesh.IsVisible || size.Width <= 0.0 || size.Height <= 0.0)
                return;

            EnsureBrushes();
            if (_vignetteBrush != null)
                context.FillRectangle(_vignetteBrush, new Rect(size));
        }

        private void OnThemeChanged(AccentTheme _) => DispatchPaletteUpdate(animate: true);
        private void OnSurfacePresetChanged(SurfacePreset _) => DispatchPaletteUpdate(animate: true);

        private void OnPreferencesChanged()
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                if (_isAttached)
                    HandlePreferencesUpdated();
                return;
            }

            AppDispatcher.Post(() =>
            {
                if (_isAttached)
                    HandlePreferencesUpdated();
            });
        }

        private void HandlePreferencesUpdated()
        {
            SyncShaderMode(animate: false);
            InvalidateBrushes();
            InvalidateVisual();
            _vignetteOverlay.InvalidateVisual();
        }

        private void DispatchPaletteUpdate(bool animate)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                ApplyPaletteUpdate(animate);
                return;
            }

            AppDispatcher.Post(() => ApplyPaletteUpdate(animate));
        }

        private void ApplyPaletteUpdate(bool animate)
        {
            if (!_isAttached)
                return;

            InvalidateBrushes();
            if (_mesh.IsVisible)
                _mesh.NotifyPaletteChanged(animate);
            else
                InvalidateVisual();

            _vignetteOverlay.InvalidateVisual();
        }

        private void SyncShaderMode(bool animate)
        {
            bool useShader = VisualPreferencesService.Instance.EnableGlassEffects;
            _mesh.PrimaryOrbOpacityScale = PrimaryOrbOpacityScale;
            _mesh.SecondaryOrbOpacityScale = SecondaryOrbOpacityScale;
            _mesh.IsVisible = useShader;
            _vignetteOverlay.IsVisible = useShader && VignetteOpacity > 0.001;

            if (!useShader)
                _mesh.ReleaseShaderResources();
            else
                _mesh.NotifyPaletteChanged(animate);
        }

        private void InvalidateBrushes()
        {
            _cachedTheme = null;
        }

        private void EnsureBrushes()
        {
            AccentTheme theme = AccentThemeService.CurrentTheme;
            SurfacePreset preset = AccentThemeService.CurrentSurfacePreset;
            Color? adaptive = AccentThemeService.AdaptiveGameColor;
            double primaryScale = PrimaryOrbOpacityScale;
            double secondaryScale = SecondaryOrbOpacityScale;
            double vignetteOpacity = VignetteOpacity;

            if (_cachedTheme == theme &&
                _cachedPreset == preset &&
                _cachedAdaptive == adaptive &&
                Math.Abs(_cachedPrimaryScale - primaryScale) < 0.0001 &&
                Math.Abs(_cachedSecondaryScale - secondaryScale) < 0.0001 &&
                Math.Abs(_cachedVignetteOpacity - vignetteOpacity) < 0.0001)
            {
                return;
            }

            _cachedTheme = theme;
            _cachedPreset = preset;
            _cachedAdaptive = adaptive;
            _cachedPrimaryScale = primaryScale;
            _cachedSecondaryScale = secondaryScale;
            _cachedVignetteOpacity = vignetteOpacity;

            var palette = AccentThemeService.GetEffectiveExtendedColors();

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

        private sealed class VignetteOverlay : Control
        {
            private readonly AmbientBackdropLayer _owner;

            public VignetteOverlay(AmbientBackdropLayer owner)
            {
                _owner = owner;
                IsHitTestVisible = false;
                ZIndex = 1;
            }

            public override void Render(DrawingContext context)
            {
                base.Render(context);
                _owner.RenderVignette(context, Bounds.Size);
            }
        }
    }
}
