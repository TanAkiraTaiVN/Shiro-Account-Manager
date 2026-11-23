#nullable enable
using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Shiro.Services;
using SkiaSharp;

namespace Shiro.AvaloniaUI.Controls
{
    /// <summary>
    /// Localized GPU frosted glass container supporting downscaled Skia backdrop blur,
    /// fractal Perlin micro-noise grain, pointer-aware specular bevels, and matte fallback.
    /// </summary>
    public class SkiaGlassSurface : Border
    {
        public static readonly StyledProperty<double> BlurSigmaProperty = AvaloniaProperty.Register<SkiaGlassSurface, double>(nameof(BlurSigma), 18.0, coerce: (_, v) => CoerceRange(v, 0.0, 48.0));
        public static readonly StyledProperty<IBrush?> TintBrushProperty = AvaloniaProperty.Register<SkiaGlassSurface, IBrush?>(nameof(TintBrush), null);
        public static readonly StyledProperty<double> NoiseOpacityProperty = AvaloniaProperty.Register<SkiaGlassSurface, double>(nameof(NoiseOpacity), 0.024, coerce: (_, v) => CoerceRange(v, 0.0, 0.15));
        public static readonly StyledProperty<double> SpecularHighlightOpacityProperty = AvaloniaProperty.Register<SkiaGlassSurface, double>(nameof(SpecularHighlightOpacity), 0.20, coerce: (_, v) => CoerceRange(v, 0.0, 1.0));
        public static readonly StyledProperty<bool> EnablePointerSpecularProperty = AvaloniaProperty.Register<SkiaGlassSurface, bool>(nameof(EnablePointerSpecular), true);

        public double BlurSigma { get => GetValue(BlurSigmaProperty); set => SetValue(BlurSigmaProperty, value); }
        public IBrush? TintBrush { get => GetValue(TintBrushProperty); set => SetValue(TintBrushProperty, value); }
        public double NoiseOpacity { get => GetValue(NoiseOpacityProperty); set => SetValue(NoiseOpacityProperty, value); }
        public double SpecularHighlightOpacity { get => GetValue(SpecularHighlightOpacityProperty); set => SetValue(SpecularHighlightOpacityProperty, value); }
        public bool EnablePointerSpecular { get => GetValue(EnablePointerSpecularProperty); set => SetValue(EnablePointerSpecularProperty, value); }
        internal bool LastRenderUsedFallback { get => _lastRenderUsedFallback; private set => _lastRenderUsedFallback = value; }

        private static readonly Lazy<SKShader?> s_noiseShader = new(() => SKShader.CreatePerlinNoiseFractalNoise(0.65f, 0.65f, 2, 0), LazyThreadSafetyMode.ExecutionAndPublication);
        private static readonly ImmutableSolidColorBrush s_defaultFallbackTint = new(Color.FromArgb(236, 18, 18, 26));
        private static readonly ImmutablePen s_defaultSpecularPen = BuildSpecularPen(0.20);

        private readonly GlassBackdropVisual _backdrop;
        private readonly Lock _filterLock = new();
        private SKImageFilter? _cachedBlurFilter;
        private float _cachedBlurSigma = float.NaN;
        private volatile bool _lastRenderUsedFallback;
        private bool _isAttached, _detached, _isPointerOver;
        private double _pointerNormalizedX = 0.5;

        static SkiaGlassSurface() => BackgroundProperty.OverrideDefaultValue<SkiaGlassSurface>(Brushes.Transparent);
        public SkiaGlassSurface() { _backdrop = new GlassBackdropVisual(this); VisualChildren.Add(_backdrop); }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            lock (_filterLock) { _detached = false; }
            if (!_isAttached) { _isAttached = true; VisualPreferencesService.Instance.PreferencesChanged += OnPreferencesChanged; }
            _backdrop.InvalidateVisual();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_isAttached) { _isAttached = false; VisualPreferencesService.Instance.PreferencesChanged -= OnPreferencesChanged; }
            _isPointerOver = false;
            _pointerNormalizedX = 0.5;
            DisposeCachedBlurFilter(markDetached: true);
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == BlurSigmaProperty) { DisposeCachedBlurFilter(markDetached: false); _backdrop.InvalidateVisual(); }
            else if (change.Property == TintBrushProperty || change.Property == NoiseOpacityProperty ||
                     change.Property == SpecularHighlightOpacityProperty || change.Property == EnablePointerSpecularProperty ||
                     change.Property == CornerRadiusProperty) { _backdrop.InvalidateVisual(); }
        }

        protected override Size MeasureOverride(Size availableSize) { _backdrop.Measure(default); return base.MeasureOverride(availableSize); }
        protected override Size ArrangeOverride(Size finalSize) { Size arranged = base.ArrangeOverride(finalSize); _backdrop.Arrange(new Rect(arranged)); return arranged; }
        protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); UpdatePointerPosition(e, true); }
        protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); UpdatePointerPosition(e, false); }
        protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); if (!_isPointerOver) return; _isPointerOver = false; _pointerNormalizedX = 0.5; _backdrop.InvalidateVisual(); }

        private void UpdatePointerPosition(PointerEventArgs e, bool forceEnter)
        {
            if (!EnablePointerSpecular) return;
            double nextX = Bounds.Width > 1.0 ? Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0.0, 1.0) : 0.5;
            if (forceEnter || !_isPointerOver || Math.Abs(nextX - _pointerNormalizedX) > 0.002)
            {
                _isPointerOver = true;
                _pointerNormalizedX = nextX;
                _backdrop.InvalidateVisual();
            }
        }

        private void OnPreferencesChanged()
        {
            if (Dispatcher.UIThread.CheckAccess()) { if (_isAttached) _backdrop.InvalidateVisual(); return; }
            AppDispatcher.Post(() => { if (_isAttached) _backdrop.InvalidateVisual(); });
        }

        private void RenderBackdrop(DrawingContext context, Size size)
        {
            if (size.Width <= 1.0 || size.Height <= 1.0) return;
            context.Custom(new GlassDrawOperation(this, CaptureSnapshot(size, _isPointerOver, _pointerNormalizedX)));
        }

        internal bool RenderToSkia(SKCanvas canvas, SKSurface? surface, GRContext? grContext, Size size, bool pointerOver, double pointerNormalizedX) =>
            RenderToSkia(canvas, surface, grContext, CaptureSnapshot(size, pointerOver, pointerNormalizedX));

        private GlassRenderSnapshot CaptureSnapshot(Size size, bool pointerOver, double pointerNormalizedX)
        {
            double spec = SpecularHighlightOpacity;
            return new GlassRenderSnapshot(size, CornerRadius, BlurSigma, ResolveSkiaTintColor(TintBrush), NoiseOpacity,
                spec, EnablePointerSpecular && pointerOver, pointerNormalizedX, ResolveFallbackTintBrush(TintBrush), ResolveSpecularPen(spec));
        }

        private bool RenderToSkia(SKCanvas canvas, SKSurface? surface, GRContext? grContext, in GlassRenderSnapshot snap)
        {
            if (snap.Size.Width <= 1.0 || snap.Size.Height <= 1.0) return false;
            float w = (float)snap.Size.Width, h = (float)snap.Size.Height;
            var skRect = new SKRect(0f, 0f, w, h);
            using var clipRRect = CreateSkRoundRect(skRect, snap.Corner, w, h);

            int saveCount = canvas.Save();
            try
            {
                canvas.ClipRoundRect(clipRRect, SKClipOperation.Intersect, antialias: true);
                if (snap.BlurSigma > 0.05) DrawBlurredBackdrop(canvas, surface, grContext, skRect, (float)snap.BlurSigma);

                using (var tintPaint = new SKPaint { Color = snap.TintColor, Style = SKPaintStyle.Fill })
                    canvas.DrawRect(skRect, tintPaint);

                byte noiseAlpha = (byte)Math.Clamp(Math.Round(snap.NoiseOpacity * 255.0), 0, 255);
                if (noiseAlpha > 0 && s_noiseShader.Value is { } noiseShader)
                {
                    using var noisePaint = new SKPaint { Shader = noiseShader, Color = new SKColor(255, 255, 255, noiseAlpha), BlendMode = SKBlendMode.SrcOver };
                    canvas.DrawRect(skRect, noisePaint);
                }

                DrawSkiaSpecularBevel(canvas, skRect, snap.Corner, w, h, snap.SpecularOpacity, snap.PointerSpecularActive, snap.PointerNormalizedX);
                LastRenderUsedFallback = false;
                return true;
            }
            finally { canvas.RestoreToCount(saveCount); }
        }

        private void DrawBlurredBackdrop(SKCanvas canvas, SKSurface? surface, GRContext? grContext, SKRect skRect, float sigma)
        {
            if (surface != null && TryDrawDownscaledBackdrop(canvas, surface, grContext, skRect, sigma)) return;

            WithBlurFilter(sigma, filter =>
            {
                int layerSave = canvas.SaveLayer(new SKCanvasSaveLayerRec { Bounds = skRect, Backdrop = filter });
                canvas.RestoreToCount(layerSave);
            });
        }

        private bool TryDrawDownscaledBackdrop(SKCanvas canvas, SKSurface surface, GRContext? grContext, SKRect localRect, float sigma)
        {
            using SKImage? snapshot = surface.Snapshot();
            if (snapshot == null || !canvas.TotalMatrix.TryInvert(out SKMatrix invMatrix)) return false;

            SKRect deviceRect = canvas.TotalMatrix.MapRect(localRect);
            float pad = MathF.Min(48f, sigma);
            SKRectI srcRectI = SKRectI.Round(new SKRect(MathF.Max(0f, deviceRect.Left - pad), MathF.Max(0f, deviceRect.Top - pad),
                MathF.Min(snapshot.Width, deviceRect.Right + pad), MathF.Min(snapshot.Height, deviceRect.Bottom + pad)));
            if (srcRectI.Width <= 0 || srcRectI.Height <= 0) return false;

            int downW = Math.Max(1, (int)Math.Ceiling(srcRectI.Width * 0.5f));
            int downH = Math.Max(1, (int)Math.Ceiling(srcRectI.Height * 0.5f));
            var downInfo = new SKImageInfo(downW, downH,
                snapshot.ColorType == SKColorType.Unknown ? SKColorType.Rgba8888 : snapshot.ColorType,
                snapshot.AlphaType == SKAlphaType.Unknown ? SKAlphaType.Premul : snapshot.AlphaType, snapshot.ColorSpace);

            using SKSurface? downSurface = grContext != null ? SKSurface.Create(grContext, false, downInfo) ?? SKSurface.Create(downInfo) : SKSurface.Create(downInfo);
            if (downSurface == null) return false;

            var linear = new SKSamplingOptions(SKFilterMode.Linear);
            var srcRectF = new SKRect(srcRectI.Left, srcRectI.Top, srcRectI.Right, srcRectI.Bottom);
            var downRectF = new SKRect(0f, 0f, downW, downH);
            downSurface.Canvas.DrawImage(snapshot, srcRectF, downRectF, linear, null);

            using SKImage downImage = downSurface.Snapshot();
            SKRect destLocal = invMatrix.MapRect(srcRectF);
            WithBlurFilter(sigma, filter =>
            {
                using var blurPaint = new SKPaint { IsAntialias = true, ImageFilter = filter };
                canvas.DrawImage(downImage, downRectF, destLocal, linear, blurPaint);
            });
            return true;
        }

        private void WithBlurFilter(float sigma, Action<SKImageFilter> drawAction)
        {
            lock (_filterLock)
            {
                bool canCache = !_detached;
                SKImageFilter? filter;
                if (canCache)
                {
                    if (_cachedBlurFilter == null || Math.Abs(_cachedBlurSigma - sigma) >= 0.01f)
                    {
                        _cachedBlurFilter?.Dispose();
                        _cachedBlurFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
                        _cachedBlurSigma = sigma;
                    }
                    filter = _cachedBlurFilter;
                }
                else
                    filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);

                if (filter == null) return;
                try { drawAction(filter); }
                finally { if (!canCache) filter.Dispose(); }
            }
        }

        private void DisposeCachedBlurFilter(bool markDetached)
        {
            lock (_filterLock) { if (markDetached) _detached = true; _cachedBlurFilter?.Dispose(); _cachedBlurFilter = null; _cachedBlurSigma = float.NaN; }
        }

        private static void DrawSkiaSpecularBevel(SKCanvas canvas, SKRect skRect, CornerRadius corner, float width, float height, double specularOpacity, bool pointerActive, double pointerNormX)
        {
            byte peakAlpha = (byte)Math.Clamp(Math.Round(specularOpacity * 255.0), 0, 255);
            if (peakAlpha == 0) return;

            SKRect bevelRect = skRect;
            bevelRect.Inflate(-0.5f, -0.5f);
            if (bevelRect.Width <= 0f || bevelRect.Height <= 0f) return;

            using var bevelRRect = CreateSkRoundRect(bevelRect, DeflateCorner(corner, 0.5), bevelRect.Width, bevelRect.Height);
            var rimColors = new[] { new SKColor(255, 255, 255, peakAlpha), new SKColor(255, 255, 255, (byte)(peakAlpha / 4)), new SKColor(255, 255, 255, 0) };
            using (SKShader rimShader = SKShader.CreateLinearGradient(new SKPoint(0f, 0f), new SKPoint(0f, MathF.Max(1f, height * 0.48f)), rimColors, new[] { 0f, 0.35f, 1f }, SKShaderTileMode.Clamp))
            using (var rimPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true, Shader = rimShader })
                canvas.DrawRoundRect(bevelRRect, rimPaint);

            if (!pointerActive) return;

            float px = (float)(Math.Clamp(pointerNormX, 0.0, 1.0) * width);
            byte gleamAlpha = (byte)Math.Min(255, Math.Round(peakAlpha * 1.15));
            using SKShader gleamShader = SKShader.CreateRadialGradient(new SKPoint(px, 0f), MathF.Max(48f, width * 0.38f), new[] { new SKColor(255, 255, 255, gleamAlpha), new SKColor(255, 255, 255, 0) }, SKShaderTileMode.Clamp);
            using var gleamPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true, Shader = gleamShader };
            canvas.DrawRoundRect(bevelRRect, gleamPaint);
        }

        private static IImmutableBrush ResolveFallbackTintBrush(IBrush? tintBrush)
        {
            if (tintBrush is ISolidColorBrush solid)
            {
                byte alpha = (byte)Math.Clamp(Math.Round(solid.Color.A * solid.Opacity), 0, 255);
                if (alpha == 0) return new ImmutableSolidColorBrush(Colors.Transparent);
                byte matteAlpha = Math.Max(alpha, (byte)232);
                return alpha == matteAlpha && solid is IImmutableBrush imm ? imm : new ImmutableSolidColorBrush(Color.FromArgb(matteAlpha, solid.Color.R, solid.Color.G, solid.Color.B));
            }
            return tintBrush as IImmutableBrush ?? s_defaultFallbackTint;
        }

        private static SKColor ResolveSkiaTintColor(IBrush? tintBrush) =>
            tintBrush is ISolidColorBrush s
                ? new SKColor(s.Color.R, s.Color.G, s.Color.B, (byte)Math.Clamp(Math.Round(s.Color.A * s.Opacity), 0, 255))
                : new SKColor(18, 18, 26, 196);

        private static ImmutablePen? ResolveSpecularPen(double specularOpacity)
        {
            byte peak = (byte)Math.Clamp(Math.Round(specularOpacity * 255.0), 0, 255);
            if (peak == 0) return null;
            return Math.Abs(specularOpacity - 0.20) < 0.001 ? s_defaultSpecularPen : BuildSpecularPen(specularOpacity);
        }

        private static ImmutablePen BuildSpecularPen(double specularOpacity)
        {
            byte peak = (byte)Math.Clamp(Math.Round(specularOpacity * 255.0), 0, 255);
            var stops = new ImmutableGradientStop[] { new(0.0, Color.FromArgb(peak, 255, 255, 255)), new(0.35, Color.FromArgb((byte)(peak / 4), 255, 255, 255)), new(0.75, Color.FromArgb(0, 255, 255, 255)) };
            return new ImmutablePen(new ImmutableLinearGradientBrush(stops, startPoint: new RelativePoint(0.0, 0.0, RelativeUnit.Relative), endPoint: new RelativePoint(0.0, 1.0, RelativeUnit.Relative)), 1.0);
        }

        private static SKRoundRect CreateSkRoundRect(SKRect rect, CornerRadius corner, float width, float height)
        {
            float maxR = MathF.Max(0f, MathF.Min(width, height) * 0.5f);
            static SKPoint CornerPt(double r, float max) { float v = Math.Clamp((float)r, 0f, max); return new(v, v); }
            var rrect = new SKRoundRect();
            rrect.SetRectRadii(rect, new[] { CornerPt(corner.TopLeft, maxR), CornerPt(corner.TopRight, maxR), CornerPt(corner.BottomRight, maxR), CornerPt(corner.BottomLeft, maxR) });
            return rrect;
        }

        private static CornerRadius DeflateCorner(CornerRadius c, double inset) =>
            new(Math.Max(0.0, c.TopLeft - inset), Math.Max(0.0, c.TopRight - inset), Math.Max(0.0, c.BottomRight - inset), Math.Max(0.0, c.BottomLeft - inset));

        private static double CoerceRange(double value, double min, double max) => double.IsNaN(value) ? min : Math.Clamp(value, min, max);

        private sealed class GlassBackdropVisual : Control
        {
            private readonly SkiaGlassSurface _owner;
            public GlassBackdropVisual(SkiaGlassSurface owner) { _owner = owner; IsHitTestVisible = false; ZIndex = -1; }
            public override void Render(DrawingContext context) { base.Render(context); _owner.RenderBackdrop(context, Bounds.Size); }
        }

        private readonly record struct GlassRenderSnapshot(
            Size Size, CornerRadius Corner, double BlurSigma, SKColor TintColor, double NoiseOpacity,
            double SpecularOpacity, bool PointerSpecularActive, double PointerNormalizedX,
            IImmutableBrush FallbackTint, ImmutablePen? FallbackSpecularPen);

        private sealed class GlassDrawOperation(SkiaGlassSurface owner, GlassRenderSnapshot snap) : ICustomDrawOperation
        {
            public Rect Bounds { get; } = new(snap.Size);
            public bool HitTest(Point p) => false;
            public bool Equals(ICustomDrawOperation? other) => false;
            public void Dispose() { }

            public void Render(ImmediateDrawingContext context)
            {
                if (snap.Size.Width <= 1.0 || snap.Size.Height <= 1.0) return;

                if (VisualPreferencesService.Instance.EnableGlassEffects && context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is { } leaseFeature)
                {
                    using ISkiaSharpApiLease? lease = leaseFeature.Lease();
                    if (lease?.SkCanvas != null) { owner.RenderToSkia(lease.SkCanvas, lease.SkSurface, lease.GrContext, snap); return; }
                }

                owner.LastRenderUsedFallback = true;
                double r = Math.Clamp(Math.Max(Math.Max(snap.Corner.TopLeft, snap.Corner.TopRight), Math.Max(snap.Corner.BottomLeft, snap.Corner.BottomRight)), 0.0, Math.Min(snap.Size.Width, snap.Size.Height) * 0.5);
                context.DrawRectangle(snap.FallbackTint, null, Bounds, r, r, default);
                if (snap.FallbackSpecularPen != null)
                    context.DrawRectangle(null, snap.FallbackSpecularPen, Bounds.Deflate(0.5), Math.Max(0.0, r - 0.5), Math.Max(0.0, r - 0.5), default);
            }
        }
    }
}
