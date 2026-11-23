#nullable enable
using System;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Shiro.Services;
using SkiaSharp;

namespace Shiro.AvaloniaUI.Controls
{
    internal readonly record struct MeshSizeMessage(double Width, double Height);

    internal readonly record struct MeshTransitionMessage(
        SKColorF Orb1,
        SKColorF Orb2,
        SKColorF Base,
        double DurationMs);

    internal sealed class MeshDisposeMessage
    {
        public static readonly MeshDisposeMessage Instance = new();
        private MeshDisposeMessage() { }
    }

    internal readonly record struct MeshColors(SKColorF Orb1, SKColorF Orb2, SKColorF Base)
    {
        public static MeshColors Lerp(in MeshColors a, in MeshColors b, float t) => new(
            LerpColor(a.Orb1, b.Orb1, t),
            LerpColor(a.Orb2, b.Orb2, t),
            LerpColor(a.Base, b.Base, t));

        public bool IsCloseTo(in MeshColors other) =>
            Close(Orb1, other.Orb1) && Close(Orb2, other.Orb2) && Close(Base, other.Base);

        private static SKColorF LerpColor(SKColorF a, SKColorF b, float t) => new(
            a.Red + (b.Red - a.Red) * t,
            a.Green + (b.Green - a.Green) * t,
            a.Blue + (b.Blue - a.Blue) * t,
            a.Alpha + (b.Alpha - a.Alpha) * t);

        private static bool Close(SKColorF a, SKColorF b) =>
            Math.Abs(a.Red - b.Red) < 0.0005f &&
            Math.Abs(a.Green - b.Green) < 0.0005f &&
            Math.Abs(a.Blue - b.Blue) < 0.0005f &&
            Math.Abs(a.Alpha - b.Alpha) < 0.0005f;
    }

    /// <summary>
    /// Render-thread CompositionCustomVisualHandler driving an SKRuntimeEffect 2-orb ambient mesh
    /// shader with 450ms cubic color transitions and zero idle frame updates when settled.
    /// </summary>
    public sealed class SkslAmbientMeshHandler : CompositionCustomVisualHandler
    {
        private readonly SkslAmbientMesh? _owner;
        private readonly Func<TimeSpan>? _clock;
        private readonly Lock _skiaLock = new();

        private volatile bool _hostAttached;
        private volatile bool _disposed;
        private volatile bool _isAnimating;
        private bool _hasColorState;
        private double _width;
        private double _height;

        private MeshColors _from;
        private MeshColors _to;
        private MeshColors _current;
        private float _fromPhase;
        private float _toPhase;
        private float _currentPhase;
        private TimeSpan? _transitionStart;
        private double _durationMs;

        private SKRuntimeEffect? _effect;
        private bool _effectCompileFailed;
        private SKShader? _cachedShader;
        private SKPaint? _cachedPaint;
        private float _cachedW;
        private float _cachedH;
        private MeshColors _cachedColors;
        private float _cachedPhase;

        private IImmutableBrush? _fallbackBaseBrush;
        private IImmutableBrush? _fallbackOrb1Brush;
        private IImmutableBrush? _fallbackOrb2Brush;
        private MeshColors _fallbackColors;

        public bool IsAnimating { get => _isAnimating; private set => _isAnimating = value; }
        public double TransitionProgress { get; private set; } = 1.0;
        public SKColorF CurrentOrb1 => _current.Orb1;
        public SKColorF CurrentOrb2 => _current.Orb2;
        public SKColorF CurrentBase => _current.Base;

        internal int AnimationFrameRequestsForTesting { get; private set; }
        internal int InvalidationCountForTesting { get; private set; }
        internal bool HasColorState => _hasColorState;

        internal bool HasLiveNativeResources
        {
            get
            {
                lock (_skiaLock)
                    return _effect != null || _cachedShader != null || _cachedPaint != null;
            }
        }

        public SkslAmbientMeshHandler() : this(owner: null, clock: null) { }

        internal SkslAmbientMeshHandler(Func<TimeSpan> clock) : this(owner: null, clock: clock) { }

        internal SkslAmbientMeshHandler(SkslAmbientMesh owner) : this(owner: owner, clock: null) { }

        private SkslAmbientMeshHandler(SkslAmbientMesh? owner, Func<TimeSpan>? clock)
        {
            _owner = owner;
            _clock = clock;
        }

        internal void SetHostAttached(bool attached)
        {
            lock (_skiaLock)
            {
                _hostAttached = attached;
                if (attached)
                    _disposed = false;
            }
        }

        internal void DisposeFromDetach()
        {
            IsAnimating = false;
            _transitionStart = null;
            DisposeNativeResources(markDisposed: true);
        }

        public override Rect GetRenderBounds()
        {
            double w = _width > 0.0 ? _width : (_hostAttached ? EffectiveSize.X : 0.0);
            double h = _height > 0.0 ? _height : (_hostAttached ? EffectiveSize.Y : 0.0);
            return new Rect(0.0, 0.0, Math.Max(0.0, w), Math.Max(0.0, h));
        }

        public override void OnMessage(object message)
        {
            if (message is MeshDisposeMessage)
            {
                IsAnimating = false;
                _transitionStart = null;
                DisposeNativeResources(markDisposed: true);
                return;
            }

            if (message is MeshSizeMessage sizeMsg)
            {
                double nextW = Math.Max(0.0, sizeMsg.Width);
                double nextH = Math.Max(0.0, sizeMsg.Height);
                if (Math.Abs(_width - nextW) < 0.01 && Math.Abs(_height - nextH) < 0.01)
                    return;

                _width = nextW;
                _height = nextH;
                SafeInvalidate();
                return;
            }

            if (message is MeshTransitionMessage transition)
            {
                lock (_skiaLock)
                    _disposed = false;

                ApplyTransition(transition);
            }
        }

        public override void OnAnimationFrameUpdate()
        {
            if (!_isAnimating || _disposed || (!_hostAttached && _clock == null))
                return;

            TimeSpan now = GetCurrentTime();
            TimeSpan start = _transitionStart ?? now;
            _transitionStart ??= start;

            double elapsedMs = Math.Max(0.0, (now - start).TotalMilliseconds);
            double raw = _durationMs > 0.0 ? Math.Clamp(elapsedMs / _durationMs, 0.0, 1.0) : 1.0;
            TransitionProgress = raw;

            if (raw >= 1.0)
            {
                _current = _to;
                _currentPhase = _toPhase;
                IsAnimating = false;
                _transitionStart = null;
                SafeInvalidate();
                return;
            }

            float inv = 1.0f - (float)raw;
            float eased = 1.0f - inv * inv * inv;
            _current = MeshColors.Lerp(_from, _to, eased);
            _currentPhase = _fromPhase + (_toPhase - _fromPhase) * eased;

            SafeInvalidate();
            SafeRegisterNextFrame();
        }

        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
            double width = _width > 0.0 ? _width : (_hostAttached ? EffectiveSize.X : 0.0);
            double height = _height > 0.0 ? _height : (_hostAttached ? EffectiveSize.Y : 0.0);
            if (width <= 1.0 || height <= 1.0)
                return;

            if (VisualPreferencesService.Instance.EnableGlassEffects &&
                drawingContext.TryGetFeature<ISkiaSharpApiLeaseFeature>() is { } leaseFeature)
            {
                using ISkiaSharpApiLease? lease = leaseFeature.Lease();
                if (lease?.SkCanvas != null && RenderToSkia(lease.SkCanvas, (float)width, (float)height))
                {
                    _owner?.SetLastRenderUsedFallback(false);
                    return;
                }
            }

            _owner?.SetLastRenderUsedFallback(true);
            RenderFallbackImmediate(drawingContext, width, height);
        }

        internal bool RenderToSkia(SKCanvas canvas, float width, float height)
        {
            if (width <= 1.0f || height <= 1.0f)
                return false;

            lock (_skiaLock)
            {
                if (_disposed)
                    return false;

                if (_effect == null)
                {
                    if (_effectCompileFailed)
                        return false;

                    _effect = SkslAmbientMesh.TryCompileShader(out _);
                    if (_effect == null)
                    {
                        _effectCompileFailed = true;
                        return false;
                    }
                }

                bool dirty = _cachedShader == null ||
                             _cachedPaint == null ||
                             Math.Abs(_cachedW - width) > 0.25f ||
                             Math.Abs(_cachedH - height) > 0.25f ||
                             !_cachedColors.IsCloseTo(_current) ||
                             Math.Abs(_cachedPhase - _currentPhase) > 0.0005f;

                if (dirty)
                {
                    _cachedPaint?.Dispose();
                    _cachedPaint = null;
                    _cachedShader?.Dispose();
                    _cachedShader = null;

                    using var uniforms = new SKRuntimeEffectUniforms(_effect);
                    uniforms.Add("u_resolution", new SKPoint(width, height));
                    uniforms.Add("u_orb1", ToFloat4(_current.Orb1));
                    uniforms.Add("u_orb2", ToFloat4(_current.Orb2));
                    uniforms.Add("u_base", ToFloat4(_current.Base));
                    uniforms.Add("u_phase", _currentPhase);

                    _cachedShader = _effect.ToShader(uniforms);
                    if (_cachedShader == null)
                        return false;

                    _cachedPaint = new SKPaint { Shader = _cachedShader, IsAntialias = false };
                    _cachedW = width;
                    _cachedH = height;
                    _cachedColors = _current;
                    _cachedPhase = _currentPhase;
                }

                canvas.DrawRect(new SKRect(0.0f, 0.0f, width, height), _cachedPaint!);
                return true;
            }
        }

        internal void RenderFallbackImmediate(ImmediateDrawingContext context, double width, double height)
        {
            EnsureFallbackBrushes();
            var rect = new Rect(0.0, 0.0, width, height);
            if (_fallbackBaseBrush != null) context.DrawRectangle(_fallbackBaseBrush, null, rect);
            if (_fallbackOrb1Brush != null) context.DrawRectangle(_fallbackOrb1Brush, null, rect);
            if (_fallbackOrb2Brush != null) context.DrawRectangle(_fallbackOrb2Brush, null, rect);
        }

        internal void RenderFallback(DrawingContext context, double width, double height)
        {
            EnsureFallbackBrushes();
            var rect = new Rect(0.0, 0.0, width, height);
            if (_fallbackBaseBrush != null) context.FillRectangle(_fallbackBaseBrush, rect);
            if (_fallbackOrb1Brush != null) context.FillRectangle(_fallbackOrb1Brush, rect);
            if (_fallbackOrb2Brush != null) context.FillRectangle(_fallbackOrb2Brush, rect);
        }

        private void ApplyTransition(MeshTransitionMessage msg)
        {
            var target = new MeshColors(msg.Orb1, msg.Orb2, msg.Base);
            if (!_hasColorState || msg.DurationMs <= 0.0)
            {
                _hasColorState = true;
                _from = target;
                _to = target;
                if (msg.DurationMs <= 0.0)
                {
                    _current = target;
                    _fromPhase = 0.0f;
                    _toPhase = 0.0f;
                    _currentPhase = 0.0f;
                    TransitionProgress = 1.0;
                    IsAnimating = false;
                    _transitionStart = null;
                    SafeInvalidate();
                    return;
                }
            }
            else
            {
                _from = _current;
            }

            _to = target;
            _fromPhase = _currentPhase;
            _toPhase = _currentPhase < 0.5f ? 1.0f : 0.0f;
            _durationMs = msg.DurationMs;
            _transitionStart = GetCurrentTime();
            TransitionProgress = 0.0;
            IsAnimating = true;

            SafeInvalidate();
            SafeRegisterNextFrame();
        }

        private void EnsureFallbackBrushes()
        {
            if (_fallbackBaseBrush != null && _fallbackColors.IsCloseTo(_current))
                return;

            _fallbackColors = _current;
            _fallbackBaseBrush = new ImmutableSolidColorBrush(ToAvaloniaColor(_current.Base, opaque: true));
            _fallbackOrb1Brush = BuildOrbBrush(_current.Orb1, new RelativePoint(0.18, 0.14, RelativeUnit.Relative), 0.52, 0.44);
            _fallbackOrb2Brush = BuildOrbBrush(_current.Orb2, new RelativePoint(0.84, 0.82, RelativeUnit.Relative), 0.48, 0.44);
        }

        private static IImmutableBrush? BuildOrbBrush(SKColorF color, RelativePoint center, double rx, double ry)
        {
            byte peakAlpha = (byte)Math.Clamp(Math.Round(color.Alpha * 255.0f), 0, 255);
            if (peakAlpha == 0)
                return null;

            byte r = ToByte(color.Red);
            byte g = ToByte(color.Green);
            byte b = ToByte(color.Blue);
            byte mid = (byte)Math.Clamp(Math.Round(peakAlpha * 0.36), 0, 255);
            return new ImmutableRadialGradientBrush(
                new ImmutableGradientStop[]
                {
                    new(0.0, Color.FromArgb(peakAlpha, r, g, b)),
                    new(0.5, Color.FromArgb(mid, r, g, b)),
                    new(1.0, Color.FromArgb(0, r, g, b))
                },
                center: center,
                gradientOrigin: center,
                radiusX: new RelativeScalar(rx, RelativeUnit.Relative),
                radiusY: new RelativeScalar(ry, RelativeUnit.Relative));
        }

        private static float[] ToFloat4(SKColorF c) => new[] { c.Red, c.Green, c.Blue, c.Alpha };
        private static byte ToByte(float ch) => (byte)Math.Clamp(Math.Round(ch * 255.0f), 0, 255);
        private static Color ToAvaloniaColor(SKColorF c, bool opaque) =>
            Color.FromArgb(opaque ? (byte)255 : ToByte(c.Alpha), ToByte(c.Red), ToByte(c.Green), ToByte(c.Blue));

        private void DisposeNativeResources(bool markDisposed)
        {
            lock (_skiaLock)
            {
                if (markDisposed)
                    _disposed = true;

                _cachedPaint?.Dispose();
                _cachedPaint = null;
                _cachedShader?.Dispose();
                _cachedShader = null;
                _effect?.Dispose();
                _effect = null;
                _effectCompileFailed = false;
            }
        }

        private TimeSpan GetCurrentTime() => _clock?.Invoke() ?? (_hostAttached ? CompositionNow : TimeSpan.Zero);

        private void SafeInvalidate()
        {
            InvalidationCountForTesting++;
            if (_hostAttached && _clock == null)
                Invalidate();
        }

        private void SafeRegisterNextFrame()
        {
            AnimationFrameRequestsForTesting++;
            if (_hostAttached && _clock == null)
                RegisterForNextAnimationFrameUpdate();
        }
    }
}
