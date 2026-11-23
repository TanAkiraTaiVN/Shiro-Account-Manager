#nullable enable
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;

namespace Shiro.AvaloniaUI.Controls
{
    /// <summary>
    /// Non-hit-tested rubber-band selection rectangle overlay for Dashboard grids and lists.
    /// Renders <c>AccentSoftFillBrush</c> and a 1px <c>AccentBorderBrush</c> with 6px rounded corners,
    /// snapping coordinates to the device pixel grid across 100%/125%/150% DPI scaling.
    /// </summary>
    public class MarqueeSelectionOverlay : Control
    {
        public const double ThresholdPixels = 6.0;
        public const double CornerRadiusPixels = 6.0;

        public static readonly StyledProperty<Rect> SelectionRectProperty =
            AvaloniaProperty.Register<MarqueeSelectionOverlay, Rect>(nameof(SelectionRect));

        public static readonly StyledProperty<bool> IsSelectingProperty =
            AvaloniaProperty.Register<MarqueeSelectionOverlay, bool>(nameof(IsSelecting));

        public static readonly StyledProperty<IBrush?> FillBrushProperty =
            AvaloniaProperty.Register<MarqueeSelectionOverlay, IBrush?>(nameof(FillBrush));

        public static readonly StyledProperty<IBrush?> BorderBrushProperty =
            AvaloniaProperty.Register<MarqueeSelectionOverlay, IBrush?>(nameof(BorderBrush));

        private static readonly ImmutableSolidColorBrush s_defaultFillBrush = new(Color.FromArgb(31, 255, 255, 255));
        private static readonly ImmutableSolidColorBrush s_defaultBorderBrush = new(Color.FromArgb(70, 255, 255, 255));
        private static readonly ImmutablePen s_defaultBorderPen = new(s_defaultBorderBrush, 1.0);

        private bool _isArmed;
        private Point _anchorPoint;
        private IBrush? _cachedPenSourceBrush;
        private IPen? _cachedPen;

        static MarqueeSelectionOverlay()
        {
            AffectsRender<MarqueeSelectionOverlay>(
                SelectionRectProperty,
                IsSelectingProperty,
                FillBrushProperty,
                BorderBrushProperty);
        }

        public MarqueeSelectionOverlay()
        {
            IsHitTestVisible = false;
            ClipToBounds = true;
        }

        public Rect SelectionRect
        {
            get => GetValue(SelectionRectProperty);
            set => SetValue(SelectionRectProperty, value);
        }

        public bool IsSelecting
        {
            get => GetValue(IsSelectingProperty);
            set => SetValue(IsSelectingProperty, value);
        }

        public IBrush? FillBrush
        {
            get => GetValue(FillBrushProperty);
            set => SetValue(FillBrushProperty, value);
        }

        public IBrush? BorderBrush
        {
            get => GetValue(BorderBrushProperty);
            set => SetValue(BorderBrushProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == BorderBrushProperty)
            {
                _cachedPenSourceBrush = null;
                _cachedPen = null;
            }
        }

        public bool TryArm(Point startPoint, object? pointerSource)
        {
            if (IsInteractiveSource(pointerSource))
                return false;

            _isArmed = true;
            _anchorPoint = startPoint;
            IsSelecting = false;
            SelectionRect = default;
            return true;
        }

        public bool UpdateDrag(Point currentPoint, out bool justActivated)
        {
            justActivated = false;
            if (!_isArmed)
                return false;

            if (!IsSelecting)
            {
                if (!ExceedsDragThreshold(_anchorPoint, currentPoint))
                    return false;

                IsSelecting = true;
                justActivated = true;
            }

            Point anchor = _anchorPoint;
            Point clampedCurrent = currentPoint;
            if (Bounds.Width > 0.0 && Bounds.Height > 0.0)
            {
                anchor = ClampPoint(anchor, Bounds.Size);
                clampedCurrent = ClampPoint(clampedCurrent, Bounds.Size);
            }

            SelectionRect = ComputeNormalizedRect(anchor, clampedCurrent);
            return true;
        }

        public void CompleteSelection()
        {
            _isArmed = false;
            IsSelecting = false;
            SelectionRect = default;
        }

        public static bool IsInteractiveSource(object? source)
            => source is Visual visual && IsInteractiveControl(visual);

        public static bool IsInteractiveControl(Visual? visual)
        {
            static bool Match(object? node) =>
                node is Button or CheckBox or TextBox or ComboBox or ScrollBar or Thumb;

            for (Visual? cur = visual; cur != null; cur = cur.GetVisualParent())
                if (Match(cur)) return true;

            for (StyledElement? cur = visual; cur != null; cur = cur.Parent)
                if (Match(cur)) return true;

            return false;
        }

        public static bool ExceedsDragThreshold(Point start, Point current, double threshold = ThresholdPixels)
        {
            double dx = current.X - start.X, dy = current.Y - start.Y;
            return (dx * dx + dy * dy) > (threshold * threshold);
        }

        public static Rect ComputeNormalizedRect(Point start, Point current)
            => new(Math.Min(start.X, current.X), Math.Min(start.Y, current.Y),
                   Math.Abs(current.X - start.X), Math.Abs(current.Y - start.Y));

        public static Rect SnapToPixelGrid(Rect rect, double renderScaling)
        {
            double scale = double.IsFinite(renderScaling) && renderScaling > 0.0 ? renderScaling : 1.0;
            double left = Math.Round(rect.X * scale, MidpointRounding.AwayFromZero) / scale;
            double top = Math.Round(rect.Y * scale, MidpointRounding.AwayFromZero) / scale;
            double right = Math.Round(rect.Right * scale, MidpointRounding.AwayFromZero) / scale;
            double bottom = Math.Round(rect.Bottom * scale, MidpointRounding.AwayFromZero) / scale;
            return new Rect(left, top, Math.Max(0.0, right - left), Math.Max(0.0, bottom - top));
        }

        public static bool Intersects(Rect selectionRect, Rect itemRect)
            => selectionRect.Width > 0.0 && selectionRect.Height > 0.0 &&
               itemRect.Width > 0.0 && itemRect.Height > 0.0 &&
               selectionRect.Intersects(itemRect);

        private static Point ClampPoint(Point point, Size size)
            => new(Math.Clamp(point.X, 0.0, size.Width), Math.Clamp(point.Y, 0.0, size.Height));

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (!IsSelecting)
                return;

            Rect rect = SelectionRect;
            if (rect.Width <= 0.5 || rect.Height <= 0.5)
                return;

            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            double safeScale = double.IsFinite(scaling) && scaling > 0.0 ? scaling : 1.0;
            Rect snapped = SnapToPixelGrid(rect, safeScale);
            double halfPx = 0.5 / safeScale;
            Rect drawRect = snapped.Width > halfPx * 2.0 && snapped.Height > halfPx * 2.0
                ? snapped.Deflate(halfPx)
                : snapped;

            context.DrawRectangle(
                ResolveFillBrush(),
                ResolveBorderPen(),
                new RoundedRect(drawRect, CornerRadiusPixels));
        }

        private IBrush ResolveFillBrush()
            => FillBrush ?? TryFindResourceBrush("AccentSoftFillBrush") ?? s_defaultFillBrush;

        private IPen ResolveBorderPen()
        {
            IBrush effective = BorderBrush ?? TryFindResourceBrush("AccentBorderBrush") ?? s_defaultBorderBrush;
            if (ReferenceEquals(effective, s_defaultBorderBrush))
                return s_defaultBorderPen;

            if (_cachedPen != null && ReferenceEquals(_cachedPenSourceBrush, effective))
                return _cachedPen;

            _cachedPenSourceBrush = effective;
            _cachedPen = effective is IImmutableBrush immutable
                ? new ImmutablePen(immutable, 1.0)
                : effective is ISolidColorBrush solid
                    ? new ImmutablePen(new ImmutableSolidColorBrush(solid.Color), 1.0)
                    : new Pen(effective, 1.0);
            return _cachedPen;
        }

        private IBrush? TryFindResourceBrush(string key)
            => this.TryFindResource(key, ActualThemeVariant, out object? res) && res is IBrush brush ? brush : null;
    }
}
