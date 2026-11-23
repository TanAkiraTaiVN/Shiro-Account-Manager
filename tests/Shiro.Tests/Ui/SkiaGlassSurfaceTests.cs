#nullable enable
using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Shiro.AvaloniaUI.Controls;
using Shiro.Services;
using SkiaSharp;
using Xunit;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public class SkiaGlassSurfaceTests
    {
        [AvaloniaFact]
        public void SkiaGlassSurface_AttachDetachAndPointerCycle_DoesNotThrow_AndTracksFallback()
        {
            var prefs = VisualPreferencesService.Instance;
            bool originalGlass = prefs.EnableGlassEffects;
            Window? window = null;

            try
            {
                var surface = new SkiaGlassSurface
                {
                    Width = 220,
                    Height = 120,
                    BlurSigma = 20,
                    CornerRadius = new CornerRadius(16),
                    TintBrush = new SolidColorBrush(Color.FromArgb(180, 20, 20, 32)),
                    NoiseOpacity = 0.03,
                    SpecularHighlightOpacity = 0.24,
                    EnablePointerSpecular = true,
                    Child = new TextBlock { Text = "Glass Content" }
                };

                var host = new Grid();
                host.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(40, 80, 160))
                });
                host.Children.Add(surface);

                window = new Window
                {
                    Width = 320,
                    Height = 200,
                    Content = host
                };

                var cycleEx = Record.Exception(() =>
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();

                    window.MouseMove(new Point(110, 100));
                    Dispatcher.UIThread.RunJobs();

                    window.MouseMove(new Point(200, 100));
                    Dispatcher.UIThread.RunJobs();

                    window.MouseMove(new Point(2, 2));
                    Dispatcher.UIThread.RunJobs();

                    prefs.EnableGlassEffects = false;
                    Dispatcher.UIThread.RunJobs();

                    using (var rtbDisabled = new RenderTargetBitmap(new PixelSize(320, 200), new Vector(96, 96)))
                    {
                        rtbDisabled.Render(window);
                    }

                    Assert.True(surface.LastRenderUsedFallback);

                    prefs.EnableGlassEffects = true;
                    Dispatcher.UIThread.RunJobs();

                    using (var rtbHeadless = new RenderTargetBitmap(new PixelSize(320, 200), new Vector(96, 96)))
                    {
                        rtbHeadless.Render(window);
                    }

                    // HeadlessTestApp runs with UseHeadlessDrawing = true (no ISkiaSharpApiLeaseFeature).
                    Assert.True(surface.LastRenderUsedFallback);

                    host.Children.Remove(surface);
                    window.Content = null;
                    window.Close();
                    Dispatcher.UIThread.RunJobs();

                    prefs.EnableGlassEffects = false;
                    prefs.EnableGlassEffects = true;
                    Dispatcher.UIThread.RunJobs();
                });

                Assert.Null(cycleEx);
                Assert.True(surface.LastRenderUsedFallback);
            }
            finally
            {
                window?.Close();
                prefs.EnableGlassEffects = originalGlass;
            }
        }

        [AvaloniaFact]
        public void SkiaGlassSurface_CoercesPropertyBounds_AndHandlesZeroSize()
        {
            var surface = new SkiaGlassSurface();

            Assert.Equal(18.0, surface.BlurSigma);
            Assert.Equal(0.024, surface.NoiseOpacity);
            Assert.Equal(0.20, surface.SpecularHighlightOpacity);
            Assert.True(surface.EnablePointerSpecular);

            surface.BlurSigma = -5.0;
            Assert.Equal(0.0, surface.BlurSigma);

            surface.BlurSigma = 96.0;
            Assert.Equal(48.0, surface.BlurSigma);

            surface.BlurSigma = double.NaN;
            Assert.Equal(0.0, surface.BlurSigma);

            surface.NoiseOpacity = -0.5;
            Assert.Equal(0.0, surface.NoiseOpacity);

            surface.NoiseOpacity = 2.0;
            Assert.Equal(0.15, surface.NoiseOpacity);

            surface.SpecularHighlightOpacity = -1.0;
            Assert.Equal(0.0, surface.SpecularHighlightOpacity);

            surface.SpecularHighlightOpacity = 3.5;
            Assert.Equal(1.0, surface.SpecularHighlightOpacity);

            var renderZeroEx = Record.Exception(() =>
            {
                surface.Measure(new Size(0, 0));
                surface.Arrange(new Rect(0, 0, 0, 0));

                var zeroWindow = new Window
                {
                    Width = 1,
                    Height = 1,
                    Content = surface
                };

                try
                {
                    zeroWindow.Show();
                    surface.Measure(new Size(0, 0));
                    surface.Arrange(new Rect(0, 0, 0, 0));
                    Dispatcher.UIThread.RunJobs();

                    using var rtb = new RenderTargetBitmap(new PixelSize(1, 1), new Vector(96, 96));
                    rtb.Render(surface);
                }
                finally
                {
                    zeroWindow.Close();
                    Dispatcher.UIThread.RunJobs();
                }
            });

            Assert.Null(renderZeroEx);
        }

        [AvaloniaFact]
        public void SkiaGlassSurface_AttachDetachReattach_ManagesPreferencesSubscriptionWithoutLeak()
        {
            var prefs = VisualPreferencesService.Instance;
            int baselineSubscribers = GetPreferencesSubscriberCount(prefs);

            var surface = new SkiaGlassSurface
            {
                Width = 180,
                Height = 80
            };

            var window = new Window
            {
                Width = 240,
                Height = 160,
                Content = surface
            };

            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(baselineSubscribers + 1, GetPreferencesSubscriberCount(prefs));

                window.Content = null;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(baselineSubscribers, GetPreferencesSubscriberCount(prefs));

                window.Content = surface;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(baselineSubscribers + 1, GetPreferencesSubscriberCount(prefs));

                window.Content = null;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(baselineSubscribers, GetPreferencesSubscriberCount(prefs));
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public void SkiaGlassSurface_SkiaCanvasRenderPath_BlursDrawsNoiseAndSpecular_WithoutFallback()
        {
            var prefs = VisualPreferencesService.Instance;
            bool originalGlass = prefs.EnableGlassEffects;
            prefs.EnableGlassEffects = true;

            try
            {
                var surface = new SkiaGlassSurface
                {
                    Width = 200,
                    Height = 100,
                    BlurSigma = 18.0,
                    CornerRadius = new CornerRadius(16, 16, 12, 12),
                    TintBrush = new SolidColorBrush(Color.FromArgb(140, 20, 22, 34)),
                    NoiseOpacity = 0.03,
                    SpecularHighlightOpacity = 0.25,
                    EnablePointerSpecular = true
                };

                var info = new SKImageInfo(300, 150, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var skSurface = SKSurface.Create(info);
                Assert.NotNull(skSurface);

                SKCanvas canvas = skSurface!.Canvas;
                canvas.Clear(new SKColor(10, 12, 18, 255));
                using (var bgPaint = new SKPaint { Color = new SKColor(59, 130, 246, 255) })
                {
                    canvas.DrawRect(new SKRect(0, 0, 150, 150), bgPaint);
                }
                using (var bgPaint2 = new SKPaint { Color = new SKColor(219, 39, 119, 255) })
                {
                    canvas.DrawRect(new SKRect(150, 0, 300, 150), bgPaint2);
                }

                canvas.Save();
                canvas.Scale(1.5f, 1.5f);
                bool rendered = surface.RenderToSkia(
                    canvas,
                    skSurface,
                    grContext: null,
                    new Size(200, 100),
                    pointerOver: true,
                    pointerNormalizedX: 0.72);
                canvas.Restore();

                Assert.True(rendered);
                Assert.False(surface.LastRenderUsedFallback);

                using SKImage renderedImg = skSurface.Snapshot();
                using SKBitmap bmp = SKBitmap.FromImage(renderedImg);
                SKColor centerPixel = bmp.GetPixel(150, 75);
                Assert.Equal(255, centerPixel.Alpha);
                Assert.InRange(centerPixel.Red, 20, 220);
                Assert.InRange(centerPixel.Blue, 20, 220);

                // Changing BlurSigma disposes previous cached SKImageFilter and still renders cleanly on surface-less canvas.
                surface.BlurSigma = 28.0;
                bool renderedSaveLayer = surface.RenderToSkia(
                    canvas,
                    surface: null,
                    grContext: null,
                    new Size(200, 100),
                    pointerOver: false,
                    pointerNormalizedX: 0.5);
                Assert.True(renderedSaveLayer);
                Assert.False(surface.LastRenderUsedFallback);
            }
            finally
            {
                prefs.EnableGlassEffects = originalGlass;
            }
        }

        private static int GetPreferencesSubscriberCount(VisualPreferencesService prefs)
        {
            FieldInfo? field = typeof(VisualPreferencesService).GetField(
                nameof(VisualPreferencesService.PreferencesChanged),
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var del = field!.GetValue(prefs) as Delegate;
            return del?.GetInvocationList().Length ?? 0;
        }
    }
}
