#nullable enable
using System;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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
    public sealed class CompositionAndShaderTests : IDisposable
    {
        private readonly AccentTheme _origTheme = AccentThemeService.CurrentTheme;
        private readonly SurfacePreset _origPreset = AccentThemeService.CurrentSurfacePreset;
        private readonly Color? _origAdaptive = AccentThemeService.AdaptiveGameColor;
        private readonly bool _origGlass = VisualPreferencesService.Instance.EnableGlassEffects;
        private readonly bool _origReducedMotion = VisualPreferencesService.Instance.ReducedMotion;

        public void Dispose()
        {
            AccentThemeService.SetAdaptiveGameColor(_origAdaptive);
            AccentThemeService.ApplySurfacePreset(_origPreset, save: false);
            AccentThemeService.ApplyTheme(_origTheme, save: false);
            VisualPreferencesService.Instance.EnableGlassEffects = _origGlass;
            VisualPreferencesService.Instance.ReducedMotion = _origReducedMotion;
            Application.Current?.Resources.MergedDictionaries.Clear();
        }

        [Fact]
        public void SkslAmbientMesh_ShaderCompilesCleanly_WithoutErrors()
        {
            using SKRuntimeEffect? effect = SkslAmbientMesh.TryCompileShader(out string? error);

            Assert.Null(error);
            Assert.NotNull(effect);
            Assert.Contains("u_resolution", effect!.Uniforms);
            Assert.Contains("u_orb1", effect.Uniforms);
            Assert.Contains("u_orb2", effect.Uniforms);
            Assert.Contains("u_base", effect.Uniforms);
            Assert.Contains("u_phase", effect.Uniforms);
        }

        [Fact]
        public void SkslAmbientMesh_ColorTransition_SettlesToIdleAfter450Ms()
        {
            TimeSpan clockNow = TimeSpan.Zero;
            var handler = new SkslAmbientMeshHandler(() => clockNow);

            handler.OnMessage(new MeshSizeMessage(800, 600));
            handler.OnMessage(new MeshTransitionMessage(
                Orb1: new SKColorF(0.2f, 0.2f, 0.2f, 0.1f),
                Orb2: new SKColorF(0.1f, 0.1f, 0.3f, 0.05f),
                Base: new SKColorF(0.03f, 0.03f, 0.05f, 1.0f),
                DurationMs: 0.0));

            Assert.False(handler.IsAnimating);
            Assert.Equal(0, handler.AnimationFrameRequestsForTesting);

            var targetOrb1 = new SKColorF(0.05f, 0.72f, 0.85f, 0.18f);
            var targetOrb2 = new SKColorF(0.39f, 0.40f, 0.95f, 0.10f);
            var targetBase = new SKColorF(0.04f, 0.05f, 0.09f, 1.0f);

            handler.OnMessage(new MeshTransitionMessage(targetOrb1, targetOrb2, targetBase, DurationMs: 450.0));

            Assert.True(handler.IsAnimating);
            Assert.Equal(1, handler.AnimationFrameRequestsForTesting);

            clockNow = TimeSpan.FromMilliseconds(16);
            handler.OnAnimationFrameUpdate();
            Assert.True(handler.IsAnimating);
            Assert.InRange(handler.TransitionProgress, 0.01, 0.10);

            clockNow = TimeSpan.FromMilliseconds(225);
            handler.OnAnimationFrameUpdate();
            Assert.True(handler.IsAnimating);
            Assert.InRange(handler.CurrentOrb1.Green, 0.25f, 0.80f);
            Assert.Equal(3, handler.AnimationFrameRequestsForTesting);

            clockNow = TimeSpan.FromMilliseconds(449);
            handler.OnAnimationFrameUpdate();
            Assert.True(handler.IsAnimating);
            Assert.Equal(4, handler.AnimationFrameRequestsForTesting);

            clockNow = TimeSpan.FromMilliseconds(500);
            handler.OnAnimationFrameUpdate();
            Assert.False(handler.IsAnimating);
            Assert.Equal(1.0, handler.TransitionProgress);
            Assert.Equal(targetOrb1.Red, handler.CurrentOrb1.Red, 3);
            Assert.Equal(targetOrb1.Green, handler.CurrentOrb1.Green, 3);
            Assert.Equal(targetOrb1.Blue, handler.CurrentOrb1.Blue, 3);
            Assert.Equal(targetBase.Blue, handler.CurrentBase.Blue, 3);

            int settledFrameRequests = handler.AnimationFrameRequestsForTesting;
            Assert.Equal(4, settledFrameRequests);

            // Further idle ticks must not schedule additional animation frames.
            clockNow = TimeSpan.FromMilliseconds(650);
            handler.OnAnimationFrameUpdate();
            Assert.False(handler.IsAnimating);
            Assert.Equal(settledFrameRequests, handler.AnimationFrameRequestsForTesting);
        }

        [Fact]
        public void SkslAmbientMesh_HiDpiScaleAndTranslate_MapsUvCoordinatesAccurately()
        {
            AccentThemeService.ApplyTheme(AccentTheme.Cyan, save: false);
            AccentThemeService.ApplySurfacePreset(SurfacePreset.AmoledVoid, save: false);

            var mesh = new SkslAmbientMesh
            {
                PrimaryOrbOpacityScale = 2.0,
                SecondaryOrbOpacityScale = 2.0
            };

            const float dipW = 200f;
            const float dipH = 120f;
            const float scale = 1.5f;
            const float dx = 40f;
            const float dy = 20f;

            // Render unscaled 1.0x reference at origin (200x120 pixels).
            var info1x = new SKImageInfo(200, 120, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface1x = SKSurface.Create(info1x);
            Assert.NotNull(surface1x);
            surface1x!.Canvas.Clear(SKColors.Magenta);
            Assert.True(mesh.RenderToSkia(surface1x.Canvas, dipW, dipH));

            // Render 1.5x HiDPI + translation (40, 20) -> device rect (60..360, 30..210).
            var infoHiDpi = new SKImageInfo(420, 260, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surfaceHiDpi = SKSurface.Create(infoHiDpi);
            Assert.NotNull(surfaceHiDpi);
            SKCanvas hiCanvas = surfaceHiDpi!.Canvas;
            hiCanvas.Clear(SKColors.Magenta);

            hiCanvas.Save();
            hiCanvas.Scale(scale, scale);
            hiCanvas.Translate(dx, dy);
            bool rendered = mesh.RenderToSkia(hiCanvas, dipW, dipH);
            hiCanvas.Restore();

            Assert.True(rendered);
            Assert.False(mesh.LastRenderUsedFallback);

            using SKImage img1x = surface1x.Snapshot();
            using SKBitmap bmp1x = SKBitmap.FromImage(img1x);
            using SKImage imgHi = surfaceHiDpi.Snapshot();
            using SKBitmap bmpHi = SKBitmap.FromImage(imgHi);

            // Outer regions outside mapped device rect remain untouched.
            Assert.Equal(SKColors.Magenta, bmpHi.GetPixel(10, 10));
            Assert.Equal(SKColors.Magenta, bmpHi.GetPixel(390, 230));
            Assert.Equal(SKColors.Magenta, bmpHi.GetPixel(58, 120));
            Assert.Equal(SKColors.Magenta, bmpHi.GetPixel(362, 120));

            // Orb 1 center (0.18, 0.14) -> 1x (36, 17), 1.5x (60 + 54, 30 + 25) = (114, 55).
            SKColor orb1At1x = bmp1x.GetPixel(36, 17);
            SKColor orb1AtHi = bmpHi.GetPixel(114, 55);
            SKColor centerAtHi = bmpHi.GetPixel(210, 120);
            Assert.True(orb1AtHi.Blue > centerAtHi.Blue + 12,
                $"Expected cyan primary orb peak at HiDPI (114,55) > center (210,120), got orb1={orb1AtHi} center={centerAtHi}");
            Assert.InRange(Math.Abs(orb1AtHi.Blue - orb1At1x.Blue), 0, 3);

            // Orb 2 center (0.84, 0.82) -> 1x (168, 98), 1.5x (60 + 252, 30 + 148) = (312, 178).
            SKColor orb2At1x = bmp1x.GetPixel(168, 98);
            SKColor orb2AtHi = bmpHi.GetPixel(312, 178);
            Assert.True(orb2AtHi.Blue > centerAtHi.Blue + 5,
                $"Expected secondary orb peak at HiDPI (312,178) > center (210,120), got orb2={orb2AtHi} center={centerAtHi}");
            Assert.InRange(Math.Abs(orb2AtHi.Blue - orb2At1x.Blue), 0, 3);

            mesh.Handler.OnMessage(MeshDisposeMessage.Instance);
            Assert.False(mesh.Handler.HasLiveNativeResources);
        }

        [AvaloniaFact]
        public void SkslAmbientMesh_DisposeAndFallback_HandlesHeadlessAndDisabledGlassCleanly()
        {
            var handler = new SkslAmbientMeshHandler(() => TimeSpan.Zero);
            var info = new SKImageInfo(120, 80, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var skSurface = SKSurface.Create(info))
            {
                Assert.NotNull(skSurface);
                handler.OnMessage(new MeshSizeMessage(120, 80));
                handler.OnMessage(new MeshTransitionMessage(
                    new SKColorF(0.5f, 0.4f, 0.9f, 0.15f),
                    new SKColorF(0.3f, 0.4f, 0.9f, 0.08f),
                    new SKColorF(0.03f, 0.03f, 0.05f, 1.0f),
                    DurationMs: 0.0));

                Assert.True(handler.RenderToSkia(skSurface!.Canvas, 120f, 80f));
                Assert.True(handler.HasLiveNativeResources);
            }

            handler.OnMessage(MeshDisposeMessage.Instance);
            Assert.False(handler.HasLiveNativeResources);
            Assert.False(handler.IsAnimating);

            var mesh = new SkslAmbientMesh { Width = 240, Height = 160 };
            var window = new Window { Width = 260, Height = 180, Content = mesh };

            try
            {
                VisualPreferencesService.Instance.EnableGlassEffects = false;
                window.Show();
                Dispatcher.UIThread.RunJobs();

                using (var rtbDisabled = new RenderTargetBitmap(new PixelSize(260, 180), new Vector(96, 96)))
                {
                    rtbDisabled.Render(window);
                }
                Assert.True(mesh.LastRenderUsedFallback);

                VisualPreferencesService.Instance.EnableGlassEffects = true;
                Dispatcher.UIThread.RunJobs();

                var freshHeadlessMesh = new SkslAmbientMesh { Width = 240, Height = 160 };
                window.Content = freshHeadlessMesh;
                Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                Dispatcher.UIThread.RunJobs();

                VisualPreferencesService.Instance.ReducedMotion = true;
                AccentThemeService.ApplyTheme(AccentTheme.Pink, save: false);
                Dispatcher.UIThread.RunJobs();
                Assert.False(freshHeadlessMesh.Handler.IsAnimating);

                using (var rtbHeadless = new RenderTargetBitmap(new PixelSize(260, 180), new Vector(96, 96)))
                {
                    rtbHeadless.Render(window);
                }
                Assert.True(freshHeadlessMesh.LastRenderUsedFallback);
            }
            finally
            {
                window.Content = null;
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }

            Assert.False(mesh.Handler.HasLiveNativeResources);
        }

        [AvaloniaFact]
        public void SpringCompositionHelper_RespectsReducedMotion_AndNullVisualSafe()
        {
            var prefs = VisualPreferencesService.Instance;

            var nullEx = Record.Exception(() =>
            {
                SpringCompositionHelper.AttachImplicitOffsetAndOpacity(null);
                SpringCompositionHelper.RunSpringEntrance(null, 0, 14, 0.96, 260);
            });
            Assert.Null(nullEx);

            var unattached = new Border { Width = 120, Height = 60, Opacity = 0.25 };
            var unattachedEx = Record.Exception(() =>
            {
                SpringCompositionHelper.AttachImplicitOffsetAndOpacity(unattached);
                SpringCompositionHelper.RunSpringEntrance(unattached, 0, 12, 0.96, 260);
            });
            Assert.Null(unattachedEx);
            Assert.Equal(1.0, unattached.Opacity);

            var attached = new Border { Width = 140, Height = 80, Opacity = 0.2 };
            var window = new Window { Width = 240, Height = 160, Content = attached };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                prefs.ReducedMotion = false;
                SpringCompositionHelper.AttachImplicitOffsetAndOpacity(attached, 240);
                SpringCompositionHelper.RunSpringEntrance(attached, 0, 16, 0.96, 260);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(1.0, attached.Opacity);

                attached.Opacity = 0.35;
                prefs.ReducedMotion = true;
                SpringCompositionHelper.AttachImplicitOffsetAndOpacity(attached, 240);
                SpringCompositionHelper.RunSpringEntrance(attached, 24, 16, 0.94, 260);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(1.0, attached.Opacity);

                var nanEx = Record.Exception(() =>
                {
                    SpringCompositionHelper.AttachImplicitOffsetAndOpacity(attached, double.NaN);
                    SpringCompositionHelper.RunSpringEntrance(attached, double.NaN, double.NaN, double.NaN, double.NaN);
                });
                Assert.Null(nanEx);
                Assert.Equal(1.0, attached.Opacity);
            }
            finally
            {
                window.Content = null;
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public void AmbientBackdropLayer_AttachDetach_UnsubscribesThemeChanged()
        {
            int baseThemeSubs = GetStaticEventSubscriberCount(typeof(AccentThemeService), nameof(AccentThemeService.ThemeChanged));
            int basePresetSubs = GetStaticEventSubscriberCount(typeof(AccentThemeService), nameof(AccentThemeService.SurfacePresetChanged));
            int basePrefSubs = GetPreferencesSubscriberCount();

            var layer = new AmbientBackdropLayer
            {
                PrimaryOrbOpacityScale = 1.2,
                SecondaryOrbOpacityScale = 0.9,
                VignetteOpacity = 0.45
            };
            Assert.Equal(1.2, layer.PrimaryOrbOpacityScale);
            Assert.Equal(0.9, layer.SecondaryOrbOpacityScale);
            Assert.Equal(0.45, layer.VignetteOpacity);

            var window = new Window { Width = 320, Height = 200, Content = layer };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(baseThemeSubs + 1, GetStaticEventSubscriberCount(typeof(AccentThemeService), nameof(AccentThemeService.ThemeChanged)));
                Assert.Equal(basePresetSubs + 1, GetStaticEventSubscriberCount(typeof(AccentThemeService), nameof(AccentThemeService.SurfacePresetChanged)));
                Assert.Equal(basePrefSubs + 1, GetPreferencesSubscriberCount());

                AccentThemeService.ApplyTheme(AccentTheme.Violet, save: false);
                AccentThemeService.ApplySurfacePreset(SurfacePreset.MidnightGlass, save: false);
                AccentThemeService.SetAdaptiveGameColor(Color.FromRgb(0, 140, 255));
                VisualPreferencesService.Instance.EnableGlassEffects = false;
                Dispatcher.UIThread.RunJobs();

                using (var rtbFallback = new RenderTargetBitmap(new PixelSize(320, 200), new Vector(96, 96)))
                {
                    rtbFallback.Render(window);
                }

                VisualPreferencesService.Instance.EnableGlassEffects = true;
                Dispatcher.UIThread.RunJobs();

                using (var rtbGlass = new RenderTargetBitmap(new PixelSize(320, 200), new Vector(96, 96)))
                {
                    rtbGlass.Render(window);
                }

                window.Content = null;
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(baseThemeSubs, GetStaticEventSubscriberCount(typeof(AccentThemeService), nameof(AccentThemeService.ThemeChanged)));
                Assert.Equal(basePresetSubs, GetStaticEventSubscriberCount(typeof(AccentThemeService), nameof(AccentThemeService.SurfacePresetChanged)));
                Assert.Equal(basePrefSubs, GetPreferencesSubscriberCount());

                // Re-attach and detach a second time to verify clean subscription lifecycle.
                window.Content = layer;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(baseThemeSubs + 1, GetStaticEventSubscriberCount(typeof(AccentThemeService), nameof(AccentThemeService.ThemeChanged)));

                window.Content = null;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(baseThemeSubs, GetStaticEventSubscriberCount(typeof(AccentThemeService), nameof(AccentThemeService.ThemeChanged)));
                Assert.Equal(basePresetSubs, GetStaticEventSubscriberCount(typeof(AccentThemeService), nameof(AccentThemeService.SurfacePresetChanged)));
                Assert.Equal(basePrefSubs, GetPreferencesSubscriberCount());
            }
            finally
            {
                window.Content = null;
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public void SkslAmbientMesh_DetachReattach_CreatesFreshHandler_AndIgnoresStaleDisposeFromOldVisual()
        {
            VisualPreferencesService.Instance.EnableGlassEffects = true;
            var mesh = new SkslAmbientMesh { Width = 240, Height = 160 };
            mesh.OverrideSkiaFeatureForTesting(true);
            var window = new Window { Width = 260, Height = 180, Content = mesh };

            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                SkslAmbientMeshHandler firstHandler = mesh.Handler;
                Assert.False(firstHandler.IsDisposed);

                // Detach and reattach, then toggle glass off/on: each attachment must own a fresh handler.
                window.Content = null;
                Dispatcher.UIThread.RunJobs();
                Assert.True(firstHandler.IsDisposed);

                window.Content = mesh;
                Dispatcher.UIThread.RunJobs();
                SkslAmbientMeshHandler secondHandler = mesh.Handler;
                Assert.NotSame(firstHandler, secondHandler);
                Assert.False(secondHandler.IsDisposed);

                VisualPreferencesService.Instance.EnableGlassEffects = false;
                Dispatcher.UIThread.RunJobs();
                Assert.True(secondHandler.IsDisposed);

                VisualPreferencesService.Instance.EnableGlassEffects = true;
                Dispatcher.UIThread.RunJobs();
                SkslAmbientMeshHandler thirdHandler = mesh.Handler;
                Assert.NotSame(secondHandler, thirdHandler);
                Assert.False(thirdHandler.IsDisposed);

                // Simulate delayed MeshDisposeMessage and stale outcome arriving from earlier visuals.
                firstHandler.OnMessage(MeshDisposeMessage.Instance);
                secondHandler.OnMessage(MeshDisposeMessage.Instance);
                Assert.False(thirdHandler.IsDisposed);

                AccentThemeService.ApplyTheme(AccentTheme.Cyan, save: false);
                Dispatcher.UIThread.RunJobs();

                var info = new SKImageInfo(120, 80, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var skSurface = SKSurface.Create(info);
                Assert.NotNull(skSurface);
                Assert.True(thirdHandler.RenderToSkia(skSurface!.Canvas, 120f, 80f));
                Assert.True(thirdHandler.HasLiveNativeResources);

                mesh.ReportRenderOutcome(thirdHandler, usedFallback: false);
                mesh.ReportRenderOutcome(firstHandler, usedFallback: true);
                Assert.False(mesh.LastRenderUsedFallback);
            }
            finally
            {
                window.Content = null;
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }

            Assert.True(mesh.Handler.IsDisposed);
            Assert.False(mesh.Handler.HasLiveNativeResources);
        }

        [AvaloniaFact]
        public async Task SkslAmbientMesh_UiFallbackRender_DoesNotMutateCompositorHandlerDuringAsyncProbe()
        {
            VisualPreferencesService.Instance.EnableGlassEffects = true;
            var mesh = new SkslAmbientMesh { Width = 240, Height = 160 };
            mesh.OverrideSkiaFeatureForTesting(true);
            var window = new Window { Width = 260, Height = 180, Content = mesh };

            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                SkslAmbientMeshHandler activeHandler = mesh.Handler;
                Assert.False(activeHandler.IsDisposed);

                // Flush pending compositor messages, then simulate pre-first-Skia-frame window (_skiaFeatureAvailable == null).
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                Dispatcher.UIThread.RunJobs();
                mesh.OverrideSkiaFeatureForTesting(null);
                int beforeRenderInvalidations = activeHandler.InvalidationCountForTesting;
                SKColorF beforeRenderOrb1 = activeHandler.CurrentOrb1;

                // UI fallback render must use UI-owned brushes without mutating activeHandler state.
                using (var rtb = new RenderTargetBitmap(new PixelSize(260, 180), new Vector(96, 96)))
                    rtb.Render(window);
                Assert.True(mesh.LastRenderUsedFallback);
                Assert.Equal(beforeRenderInvalidations, activeHandler.InvalidationCountForTesting);
                Assert.Equal(beforeRenderOrb1, activeHandler.CurrentOrb1);

                // Drive transitions concurrently while UI thread renders fallback frames.
                var clockNow = TimeSpan.Zero;
                var concurrentHandler = new SkslAmbientMeshHandler(() => clockNow);
                concurrentHandler.OnMessage(new MeshSizeMessage(240, 160));
                Task worker = Task.Run(() =>
                {
                    for (int i = 0; i < 60; i++)
                    {
                        concurrentHandler.OnMessage(new MeshTransitionMessage(
                            new SKColorF(0.1f, 0.6f, 0.9f, 0.12f),
                            new SKColorF(0.4f, 0.4f, 0.9f, 0.08f),
                            new SKColorF(0.03f, 0.03f, 0.05f, 1.0f),
                            DurationMs: 450.0));
                        clockNow += TimeSpan.FromMilliseconds(20);
                        concurrentHandler.OnAnimationFrameUpdate();
                    }
                });

                for (int i = 0; i < 10; i++)
                {
                    using var rtb = new RenderTargetBitmap(new PixelSize(260, 180), new Vector(96, 96));
                    rtb.Render(window);
                }

                await worker;
                Assert.True(mesh.LastRenderUsedFallback);
                Assert.Equal(beforeRenderInvalidations, activeHandler.InvalidationCountForTesting);
                Assert.Equal(beforeRenderOrb1, activeHandler.CurrentOrb1);
            }
            finally
            {
                window.Content = null;
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public void SkslAmbientMesh_DefaultAttachWithoutOverride_AttachesFreshHandlerAndClearsFallbackOnSkiaReport()
        {
            VisualPreferencesService.Instance.EnableGlassEffects = true;
            var mesh = new SkslAmbientMesh { Width = 240, Height = 160 };
            var window = new Window { Width = 260, Height = 180, Content = mesh };
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs();
                SkslAmbientMeshHandler h1 = mesh.Handler;
                Assert.False(h1.IsDisposed);
                mesh.ReportRenderOutcome(h1, usedFallback: false);
                Assert.False(mesh.LastRenderUsedFallback);
                window.Content = null; Dispatcher.UIThread.RunJobs();
                Assert.True(h1.IsDisposed);
                window.Content = mesh; Dispatcher.UIThread.RunJobs();
                Assert.NotSame(h1, mesh.Handler);
                Assert.False(mesh.Handler.IsDisposed);
            }
            finally { window.Content = null; window.Close(); Dispatcher.UIThread.RunJobs(); }
        }

        private static int GetStaticEventSubscriberCount(Type ownerType, string eventFieldName)
        {
            FieldInfo? field = ownerType.GetField(eventFieldName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var del = field!.GetValue(null) as Delegate;
            return del?.GetInvocationList().Length ?? 0;
        }

        private static int GetPreferencesSubscriberCount()
        {
            FieldInfo? field = typeof(VisualPreferencesService).GetField(
                nameof(VisualPreferencesService.PreferencesChanged),
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var del = field!.GetValue(VisualPreferencesService.Instance) as Delegate;
            return del?.GetInvocationList().Length ?? 0;
        }
    }
}
