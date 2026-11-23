#nullable enable
using System;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        public void SkiaGlassSurface_BorderInset_PreservesBorderBrushPerimeter_InBackdropBounds()
        {
            var surface = new SkiaGlassSurface
            {
                Width = 220,
                Height = 120,
                CornerRadius = new CornerRadius(16),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2)
            };

            var window = new Window
            {
                Width = 260,
                Height = 160,
                Content = surface
            };

            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Control backdrop = surface.GetVisualChildren().OfType<Control>().Single(c => c.ZIndex == -1);
                Assert.Equal(new Rect(2, 2, 216, 116), backdrop.Bounds);

                surface.BorderThickness = new Thickness(1, 3, 2, 4);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(new Rect(1, 3, 217, 113), backdrop.Bounds);

                surface.BorderBrush = null;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(new Rect(0, 0, 220, 120), backdrop.Bounds);
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public void SkiaGlassSurface_SkiaCanvasRenderPath_BlursTranslatedHiDpiAndSaveLayerAccurately()
        {
            var prefs = VisualPreferencesService.Instance;
            bool originalGlass = prefs.EnableGlassEffects;
            prefs.EnableGlassEffects = true;

            try
            {
                var surface = new SkiaGlassSurface
                {
                    Width = 160,
                    Height = 100,
                    BlurSigma = 0.0,
                    CornerRadius = new CornerRadius(16),
                    TintBrush = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
                    NoiseOpacity = 0.0,
                    SpecularHighlightOpacity = 0.0,
                    EnablePointerSpecular = true
                };

                var info = new SKImageInfo(400, 240, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var skSurface = SKSurface.Create(info);
                Assert.NotNull(skSurface);
                SKCanvas canvas = skSurface!.Canvas;

                // 1. Baseline zero-blur render at 150% HiDPI + non-zero translation (80, 40) -> device rect (120..360, 60..210), seam at x=240.
                PaintSplitBackdrop(canvas, width: 400, height: 240, seamX: 240, withCornerMarker: true);
                RenderTranslatedHiDpi(surface, canvas, skSurface, scale: 1.5f, dx: 80f, dy: 40f, pointerOver: false);
                Assert.False(surface.LastRenderUsedFallback);

                SKColor unblurredLeft;
                SKColor unblurredRight;
                using (SKImage img0 = skSurface.Snapshot())
                using (SKBitmap bmp0 = SKBitmap.FromImage(img0))
                {
                    unblurredLeft = bmp0.GetPixel(228, 135);
                    unblurredRight = bmp0.GetPixel(252, 135);
                    Assert.Equal(0, unblurredLeft.Red);
                    Assert.True(unblurredLeft.Blue > 150);
                    Assert.Equal(0, unblurredRight.Blue);
                    Assert.True(unblurredRight.Red > 150);
                }

                // 2. Blurred render (BlurSigma = 24) at same translated 150% HiDPI coords proves diffusion across seam and strict clipping.
                PaintSplitBackdrop(canvas, width: 400, height: 240, seamX: 240, withCornerMarker: true);
                surface.BlurSigma = 24.0;
                surface.NoiseOpacity = 0.025;
                surface.SpecularHighlightOpacity = 0.25;
                RenderTranslatedHiDpi(surface, canvas, skSurface, scale: 1.5f, dx: 80f, dy: 40f, pointerOver: true);
                Assert.False(surface.LastRenderUsedFallback);

                using (SKImage imgBlur = skSurface.Snapshot())
                using (SKBitmap bmpBlur = SKBitmap.FromImage(imgBlur))
                {
                    // Outer regions and rounded corner outside clip remain untouched.
                    Assert.Equal(new SKColor(255, 255, 0, 255), bmpBlur.GetPixel(10, 10));
                    Assert.Equal(new SKColor(255, 40, 0, 255), bmpBlur.GetPixel(385, 225));
                    Assert.Equal(new SKColor(0, 80, 255, 255), bmpBlur.GetPixel(121, 61));

                    // Far sides inside translated glass preserve blue/red dominance (invMatrix alignment verified).
                    SKColor farLeft = bmpBlur.GetPixel(140, 135);
                    SKColor farRight = bmpBlur.GetPixel(340, 135);
                    Assert.True(farLeft.Blue > farLeft.Red + 50, $"Expected blue-dominant left inside translated glass, got {farLeft}");
                    Assert.True(farRight.Red > farRight.Blue + 50, $"Expected red-dominant right inside translated glass, got {farRight}");

                    // Seam-adjacent pixels diffuse red/blue across x=240 significantly compared to BlurSigma = 0.
                    SKColor blurredLeft = bmpBlur.GetPixel(228, 135);
                    SKColor blurredRight = bmpBlur.GetPixel(252, 135);
                    Assert.True(blurredLeft.Red > unblurredLeft.Red + 15, $"Expected blur red bleed at x=228 (> {unblurredLeft.Red + 15}), got {blurredLeft.Red}");
                    Assert.True(blurredRight.Blue > unblurredRight.Blue + 15, $"Expected blur blue bleed at x=252 (> {unblurredRight.Blue + 15}), got {blurredRight.Blue}");
                }

                // 3. Verify surface: null (SaveLayer Backdrop fallback) modifies interior pixels & blurs seam while preserving outer pixels.
                var saveInfo = new SKImageInfo(300, 160, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var saveSurface = SKSurface.Create(saveInfo);
                Assert.NotNull(saveSurface);
                SKCanvas saveCanvas = saveSurface!.Canvas;
                PaintSplitBackdrop(saveCanvas, width: 300, height: 160, seamX: 150, withCornerMarker: false);

                surface.NoiseOpacity = 0.0;
                surface.BlurSigma = 24.0;
                saveCanvas.Save();
                saveCanvas.Translate(70f, 40f);
                bool renderedSaveLayer = surface.RenderToSkia(saveCanvas, surface: null, grContext: null, new Size(160, 80), pointerOver: false, pointerNormalizedX: 0.5);
                saveCanvas.Restore();

                Assert.True(renderedSaveLayer);
                Assert.False(surface.LastRenderUsedFallback);

                using SKImage saveImg = saveSurface.Snapshot();
                using SKBitmap saveBmp = SKBitmap.FromImage(saveImg);
                Assert.Equal(new SKColor(0, 80, 255, 255), saveBmp.GetPixel(20, 20));
                Assert.Equal(new SKColor(255, 40, 0, 255), saveBmp.GetPixel(280, 140));
                Assert.Equal(new SKColor(0, 80, 255, 255), saveBmp.GetPixel(71, 41));

                SKColor saveInterior = saveBmp.GetPixel(100, 80);
                SKColor saveSeamLeft = saveBmp.GetPixel(140, 80);
                SKColor saveSeamRight = saveBmp.GetPixel(160, 80);
                Assert.NotEqual(new SKColor(0, 80, 255, 255), saveInterior);
                Assert.True(saveSeamLeft.Red > 15, $"Expected SaveLayer blur red bleed at x=140, got {saveSeamLeft}");
                Assert.True(saveSeamRight.Blue > 15, $"Expected SaveLayer blur blue bleed at x=160, got {saveSeamRight}");
            }
            finally
            {
                prefs.EnableGlassEffects = originalGlass;
            }
        }

        private static void RenderTranslatedHiDpi(SkiaGlassSurface surface, SKCanvas canvas, SKSurface skSurface, float scale, float dx, float dy, bool pointerOver)
        {
            canvas.Save();
            canvas.Scale(scale, scale);
            canvas.Translate(dx, dy);
            bool rendered = surface.RenderToSkia(canvas, skSurface, grContext: null, new Size(160, 100), pointerOver, pointerNormalizedX: 0.72);
            canvas.Restore();
            Assert.True(rendered);
        }

        private static void PaintSplitBackdrop(SKCanvas canvas, int width, int height, int seamX, bool withCornerMarker)
        {
            using var leftPaint = new SKPaint { Color = new SKColor(0, 80, 255, 255) };
            using var rightPaint = new SKPaint { Color = new SKColor(255, 40, 0, 255) };
            canvas.DrawRect(new SKRect(0, 0, seamX, height), leftPaint);
            canvas.DrawRect(new SKRect(seamX, 0, width, height), rightPaint);
            if (withCornerMarker)
            {
                using var markerPaint = new SKPaint { Color = new SKColor(255, 255, 0, 255) };
                canvas.DrawRect(new SKRect(0, 0, 100, 50), markerPaint);
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
