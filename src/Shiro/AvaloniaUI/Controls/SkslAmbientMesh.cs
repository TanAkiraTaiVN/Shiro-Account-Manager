#nullable enable
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using Shiro.Services;
using SkiaSharp;

namespace Shiro.AvaloniaUI.Controls
{
    /// <summary>
    /// GPU-accelerated SkSL ambient mesh backdrop driven by CompositionCustomVisualHandler.
    /// Animates color transitions for 450ms only on theme/surface changes and settles to 0% idle GPU.
    /// </summary>
    public class SkslAmbientMesh : Control
    {
        private const string AmbientMeshSksl = """
        uniform float2 u_resolution;
        uniform float4 u_orb1;
        uniform float4 u_orb2;
        uniform float4 u_base;
        uniform float  u_phase;

        vec4 main(vec2 fragCoord) {
            vec2 uv = fragCoord / max(u_resolution, vec2(1.0));
            float d1 = length((uv - vec2(0.18 + 0.03 * sin(u_phase * 3.14159), 0.14)) * vec2(1.0, 1.3));
            float d2 = length((uv - vec2(0.84, 0.82 + 0.03 * cos(u_phase * 3.14159))) * vec2(1.1, 1.2));
            float w1 = exp(-d1 * d1 * 4.2);
            float w2 = exp(-d2 * d2 * 3.6);
            vec3 col = u_base.rgb + u_orb1.rgb * (u_orb1.a * w1) + u_orb2.rgb * (u_orb2.a * w2);
            float dither = (fract(sin(dot(fragCoord, vec2(12.9898, 78.233))) * 43758.5453) - 0.5) / 255.0;
            return vec4(clamp(col + dither, 0.0, 1.0), 1.0);
        }
        """;

        public static readonly StyledProperty<double> PrimaryOrbOpacityScaleProperty =
            AvaloniaProperty.Register<SkslAmbientMesh, double>(
                nameof(PrimaryOrbOpacityScale),
                defaultValue: 1.0,
                coerce: (_, val) => double.IsNaN(val) ? 1.0 : Math.Clamp(val, 0.0, 2.0));

        public static readonly StyledProperty<double> SecondaryOrbOpacityScaleProperty =
            AvaloniaProperty.Register<SkslAmbientMesh, double>(
                nameof(SecondaryOrbOpacityScale),
                defaultValue: 1.0,
                coerce: (_, val) => double.IsNaN(val) ? 1.0 : Math.Clamp(val, 0.0, 2.0));

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

        private readonly SkslAmbientMeshHandler _handler;
        private CompositionCustomVisual? _customVisual;
        private volatile bool _lastRenderUsedFallback;
        private volatile bool _hasRenderedSkiaOnCompositor;
        private bool? _skiaFeatureAvailable;
        private bool _probeInFlight;
        private bool _isAttached;

        internal bool AutoSubscribeToThemeServices { get; set; } = true;
        internal SkslAmbientMeshHandler Handler => _handler;
        internal bool LastRenderUsedFallback { get => _lastRenderUsedFallback; private set => _lastRenderUsedFallback = value; }

        public SkslAmbientMesh()
        {
            IsHitTestVisible = false;
            _handler = new SkslAmbientMeshHandler(this);
        }

        public static SKRuntimeEffect? TryCompileShader(out string? error)
        {
            SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(AmbientMeshSksl, out string? compileError);
            error = string.IsNullOrEmpty(compileError) ? null : compileError;
            if (effect == null && error == null)
                error = "Unknown SkSL compilation error.";
            return effect;
        }

        internal void SetLastRenderUsedFallback(bool usedFallback)
        {
            if (!usedFallback)
                _hasRenderedSkiaOnCompositor = true;
            _lastRenderUsedFallback = usedFallback;
        }

        internal void NotifyPaletteChanged(bool animate)
        {
            if (!_isAttached)
                return;

            double durationMs = animate && !VisualPreferencesService.Instance.ReducedMotion ? 450.0 : 0.0;
            MeshTransitionMessage msg = BuildTransitionMessage(durationMs);

            if (_customVisual != null)
            {
                _customVisual.SendHandlerMessage(msg);
            }
            else
            {
                _handler.OnMessage(msg);
                InvalidateVisual();
            }
        }

        internal void ReleaseShaderResources()
        {
            DetachCompositionVisual();
            _handler.DisposeFromDetach();
            InvalidateVisual();
        }

        internal bool RenderToSkia(SKCanvas canvas, float width, float height)
        {
            if (width <= 1.0f || height <= 1.0f)
                return false;

            _handler.OnMessage(BuildTransitionMessage(durationMs: 0.0));
            bool rendered = _handler.RenderToSkia(canvas, width, height);
            LastRenderUsedFallback = !rendered;
            return rendered;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (!_isAttached)
            {
                _isAttached = true;
                if (AutoSubscribeToThemeServices)
                {
                    AccentThemeService.ThemeChanged += OnThemeChanged;
                    AccentThemeService.SurfacePresetChanged += OnSurfacePresetChanged;
                    VisualPreferencesService.Instance.PreferencesChanged += OnPreferencesChanged;
                }
            }

            SyncCompositionAttachment(animate: false);
            InvalidateVisual();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_isAttached)
            {
                _isAttached = false;
                if (AutoSubscribeToThemeServices)
                {
                    AccentThemeService.ThemeChanged -= OnThemeChanged;
                    AccentThemeService.SurfacePresetChanged -= OnSurfacePresetChanged;
                    VisualPreferencesService.Instance.PreferencesChanged -= OnPreferencesChanged;
                }
            }

            ReleaseShaderResources();
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == PrimaryOrbOpacityScaleProperty || change.Property == SecondaryOrbOpacityScaleProperty)
            {
                NotifyPaletteChanged(animate: false);
            }
            else if (change.Property == IsVisibleProperty && _isAttached)
            {
                SyncCompositionAttachment(animate: false);
            }
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateVisualSize(e.NewSize);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Size arranged = base.ArrangeOverride(finalSize);
            UpdateVisualSize(arranged);
            return arranged;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (!IsVisible)
                return;
            if (_customVisual != null && (_skiaFeatureAvailable == true || _hasRenderedSkiaOnCompositor))
                return;

            Size size = Bounds.Size;
            if (size.Width <= 1.0 || size.Height <= 1.0)
                return;

            if (!_handler.HasColorState)
                _handler.OnMessage(BuildTransitionMessage(durationMs: 0.0));

            LastRenderUsedFallback = true;
            _handler.RenderFallback(context, size.Width, size.Height);
        }

        private void SyncCompositionAttachment(bool animate)
        {
            if (!_isAttached || !IsVisible || !VisualPreferencesService.Instance.EnableGlassEffects || _skiaFeatureAvailable == false)
            {
                ReleaseShaderResources();
                return;
            }

            if (_customVisual == null && ElementComposition.GetElementVisual(this)?.Compositor is { } compositor)
            {
                _handler.SetHostAttached(true);
                _customVisual = compositor.CreateCustomVisual(_handler);
                _customVisual.Size = new Vector(Bounds.Width, Bounds.Height);
                ElementComposition.SetElementChildVisual(this, _customVisual);
                ProbeSkiaFeature(compositor);
            }

            UpdateVisualSize(Bounds.Size);
            NotifyPaletteChanged(animate);
        }

        private void ProbeSkiaFeature(Compositor compositor)
        {
            if (_skiaFeatureAvailable.HasValue || _probeInFlight)
                return;

            ValueTask<object?> featureTask = compositor.TryGetRenderInterfaceFeature(typeof(ISkiaSharpApiLeaseFeature));
            if (featureTask.IsCompletedSuccessfully)
            {
                ApplySkiaProbeResult(featureTask.Result is ISkiaSharpApiLeaseFeature);
                return;
            }

            _probeInFlight = true;
            _ = featureTask.AsTask().ContinueWith(t =>
            {
                bool hasSkia = t.IsCompletedSuccessfully && t.Result is ISkiaSharpApiLeaseFeature;
                AppDispatcher.Post(() =>
                {
                    _probeInFlight = false;
                    ApplySkiaProbeResult(hasSkia);
                });
            }, TaskScheduler.Default);
        }

        private void ApplySkiaProbeResult(bool hasSkia)
        {
            _skiaFeatureAvailable = hasSkia;
            if (!hasSkia)
            {
                DetachCompositionVisual();
                _handler.DisposeFromDetach();
            }
            if (_isAttached)
                InvalidateVisual();
        }

        private void DetachCompositionVisual()
        {
            if (_customVisual != null)
            {
                _customVisual.SendHandlerMessage(MeshDisposeMessage.Instance);
                ElementComposition.SetElementChildVisual(this, null);
                _customVisual = null;
            }
            _handler.SetHostAttached(false);
        }

        private void UpdateVisualSize(Size size)
        {
            if (_customVisual == null)
                return;

            _customVisual.Size = new Vector(Math.Max(0.0, size.Width), Math.Max(0.0, size.Height));
            _customVisual.SendHandlerMessage(new MeshSizeMessage(size.Width, size.Height));
        }

        private void OnThemeChanged(AccentTheme _) => DispatchAction(() => NotifyPaletteChanged(animate: true));
        private void OnSurfacePresetChanged(SurfacePreset _) => DispatchAction(() => NotifyPaletteChanged(animate: true));
        private void OnPreferencesChanged() => DispatchAction(() => SyncCompositionAttachment(animate: false));

        private void DispatchAction(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                if (_isAttached)
                    action();
                return;
            }

            AppDispatcher.Post(() =>
            {
                if (_isAttached)
                    action();
            });
        }

        private MeshTransitionMessage BuildTransitionMessage(double durationMs)
        {
            var palette = AccentThemeService.GetEffectiveExtendedColors();
            var surface = OklchColorMath.DeriveSurfacePalette(AccentThemeService.CurrentSurfacePreset);

            float primaryAlpha = Math.Clamp((palette.Ambient.A / 255.0f) * (float)PrimaryOrbOpacityScale, 0.0f, 1.0f);
            float secondaryAlpha = Math.Clamp((palette.AmbientSecondary.A / 255.0f) * (float)SecondaryOrbOpacityScale, 0.0f, 1.0f);

            var orb1 = new SKColorF(palette.Ambient.R / 255.0f, palette.Ambient.G / 255.0f, palette.Ambient.B / 255.0f, primaryAlpha);
            var orb2 = new SKColorF(palette.AmbientSecondary.R / 255.0f, palette.AmbientSecondary.G / 255.0f, palette.AmbientSecondary.B / 255.0f, secondaryAlpha);
            var baseCol = new SKColorF(surface.SurfaceCanvas.R / 255.0f, surface.SurfaceCanvas.G / 255.0f, surface.SurfaceCanvas.B / 255.0f, 1.0f);
            return new MeshTransitionMessage(orb1, orb2, baseCol, durationMs);
        }
    }
}
