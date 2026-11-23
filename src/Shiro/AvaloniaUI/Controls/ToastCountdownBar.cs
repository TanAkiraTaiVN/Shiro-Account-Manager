#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Shiro.AvaloniaUI.Controls
{
    /// <summary>
    /// Thin semantic countdown hairline that smoothly shrinks horizontally across a toast lifetime
    /// via GPU-composited ScaleX animation and cancels cleanly when detached or superseded.
    /// </summary>
    public class ToastCountdownBar : Border
    {
        public static readonly StyledProperty<double> DurationSecondsProperty =
            AvaloniaProperty.Register<ToastCountdownBar, double>(
                nameof(DurationSeconds),
                defaultValue: 0.0);

        public static readonly StyledProperty<long> TimerGenerationProperty =
            AvaloniaProperty.Register<ToastCountdownBar, long>(
                nameof(TimerGeneration),
                defaultValue: 0L);

        public double DurationSeconds
        {
            get => GetValue(DurationSecondsProperty);
            set => SetValue(DurationSecondsProperty, value);
        }

        public long TimerGeneration
        {
            get => GetValue(TimerGenerationProperty);
            set => SetValue(TimerGenerationProperty, value);
        }

        private static readonly LinearEasing s_linearEasing = new();
        private readonly ScaleTransform _scaleTransform = new(1.0, 1.0);
        private CancellationTokenSource? _animCts;
        private bool _isAttached;

        public ToastCountdownBar()
        {
            RenderTransformOrigin = new RelativePoint(0.0, 0.5, RelativeUnit.Relative);
            RenderTransform = _scaleTransform;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _isAttached = true;
            RestartCountdownIfActive();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _isAttached = false;
            CancelAnimation();
            _scaleTransform.ScaleX = 1.0;
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == DurationSecondsProperty ||
                change.Property == TimerGenerationProperty ||
                change.Property == IsVisibleProperty)
            {
                RestartCountdownIfActive();
            }
        }

        private void CancelAnimation()
        {
            CancellationTokenSource? cts = _animCts;
            _animCts = null;
            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException ex)
                {
                    Shiro.Services.AppServices.Logger?.Debug($"ToastCountdownBar cancel ignored: {ex.Message}");
                }
                finally
                {
                    cts.Dispose();
                }
            }
        }

        private void RestartCountdownIfActive()
        {
            CancelAnimation();
            _scaleTransform.ScaleX = 1.0;

            double duration = DurationSeconds;
            if (!_isAttached || !IsEffectivelyVisible || !double.IsFinite(duration) || duration <= 0.0)
                return;

            double clamped = Math.Clamp(duration, 0.25, 60.0);
            var cts = new CancellationTokenSource();
            _animCts = cts;
            _ = RunCountdownAsync(clamped, cts.Token);
        }

        private async Task RunCountdownAsync(double seconds, CancellationToken token)
        {
            try
            {
                var anim = new Animation
                {
                    Duration = TimeSpan.FromSeconds(seconds),
                    FillMode = FillMode.Forward,
                    Easing = s_linearEasing,
                    Children =
                    {
                        new KeyFrame
                        {
                            Cue = new Cue(0.0),
                            Setters = { new Setter(ScaleTransform.ScaleXProperty, 1.0) }
                        },
                        new KeyFrame
                        {
                            Cue = new Cue(1.0),
                            Setters = { new Setter(ScaleTransform.ScaleXProperty, 0.0) }
                        }
                    }
                };

                await anim.RunAsync(this, token);
            }
            catch (OperationCanceledException)
            {
                // Expected when toast is dismissed, coalesced, or detached.
            }
            catch (Exception ex)
            {
                Shiro.Services.AppServices.Logger?.Debug($"ToastCountdownBar animation stopped: {ex.Message}");
            }
        }
    }
}
