#nullable enable
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public class OklchAndVisualPreferencesTests
    {
        [Theory]
        [InlineData(11, 11, 15)]
        [InlineData(124, 58, 237)]
        [InlineData(6, 182, 212)]
        [InlineData(16, 185, 129)]
        [InlineData(219, 39, 119)]
        [InlineData(255, 140, 0)]
        [InlineData(255, 255, 255)]
        [InlineData(0, 0, 0)]
        public void OklchRoundTrip_PreservesSrgbChannelWithinTolerance(byte r, byte g, byte b)
        {
            Color original = Color.FromRgb(r, g, b);
            var (l, c, h) = OklchColorMath.FromColor(original);
            Color roundTripped = OklchColorMath.ToColor(l, c, h);

            Assert.InRange(Math.Abs(roundTripped.R - original.R), 0, 2);
            Assert.InRange(Math.Abs(roundTripped.G - original.G), 0, 2);
            Assert.InRange(Math.Abs(roundTripped.B - original.B), 0, 2);
            Assert.Equal(original.A, roundTripped.A);
        }

        [Fact]
        public void ApcaContrast_AllPresetsAndAccents_MeetMinimumButtonAndTextContrast()
        {
            foreach (SurfacePreset preset in Enum.GetValues<SurfacePreset>())
            {
                SurfacePalette surface = OklchColorMath.DeriveSurfacePalette(preset);
                double textContrast = Math.Abs(OklchColorMath.CalculateApcaContrast(surface.TextPrimary, surface.SurfaceCard));
                Assert.True(
                    textContrast >= 75.0,
                    $"Preset {preset} card text APCA |Lc|={textContrast:F2} < 75.0");

                double canvasLum = RelativeLuminance(surface.SurfaceCanvas);
                double chromeLum = RelativeLuminance(surface.SurfaceChrome);
                double panelLum = RelativeLuminance(surface.SurfacePanel);
                double cardLum = RelativeLuminance(surface.SurfaceCard);
                double elevatedLum = RelativeLuminance(surface.SurfaceElevated);

                Assert.True(canvasLum < chromeLum, $"{preset}: Canvas >= Chrome");
                Assert.True(chromeLum < panelLum, $"{preset}: Chrome >= Panel");
                Assert.True(panelLum < cardLum, $"{preset}: Panel >= Card");
                Assert.True(cardLum < elevatedLum, $"{preset}: Card >= Elevated");
            }

            foreach (AccentTheme theme in Enum.GetValues<AccentTheme>())
            {
                AccentThemeService.AccentExtendedPalette palette = AccentThemeService.GetExtendedColors(theme);
                double buttonContrast = Math.Abs(OklchColorMath.CalculateApcaContrast(palette.TextOnAccent, palette.Primary));
                double hoverContrast = Math.Abs(OklchColorMath.CalculateApcaContrast(palette.TextOnAccent, palette.Hover));
                Assert.True(
                    buttonContrast >= 60.0,
                    $"AccentTheme {theme} button APCA |Lc|={buttonContrast:F2} < 60.0");
                Assert.True(
                    hoverContrast >= 60.0,
                    $"AccentTheme {theme} hover APCA |Lc|={hoverContrast:F2} < 60.0");
            }

            Assert.Equal(Color.FromRgb(9, 9, 11), OklchColorMath.SelectReadableForeground(Color.FromRgb(255, 255, 255)));
            Assert.Equal(Color.FromRgb(245, 245, 248), OklchColorMath.SelectReadableForeground(Color.FromRgb(18, 18, 28)));
        }

        [Theory]
        [InlineData(37, 99, 235)]   // #2563EB
        [InlineData(59, 130, 246)]  // #3B82F6
        [InlineData(0, 120, 255)]   // Vivid blue
        [InlineData(245, 158, 11)]  // Gold
        [InlineData(16, 185, 129)]  // Emerald
        [InlineData(6, 182, 212)]   // Cyan
        [InlineData(124, 58, 237)]  // Violet
        public void DeriveAdaptiveAccentPalette_PreservesApcaContrastOnPrimaryAndHover(byte r, byte g, byte b)
        {
            AccentThemeService.AccentExtendedPalette palette =
                OklchColorMath.DeriveAdaptiveAccentPalette(Color.FromRgb(r, g, b));

            double primaryContrast = Math.Abs(OklchColorMath.CalculateApcaContrast(palette.TextOnAccent, palette.Primary));
            double hoverContrast = Math.Abs(OklchColorMath.CalculateApcaContrast(palette.TextOnAccent, palette.Hover));

            Assert.True(primaryContrast >= 60.0, $"Adaptive RGB({r},{g},{b}) Primary |Lc|={primaryContrast:F2} < 60.0");
            Assert.True(hoverContrast >= 60.0, $"Adaptive RGB({r},{g},{b}) Hover |Lc|={hoverContrast:F2} < 60.0");
        }

        [Fact]
        public void AdaptivePaletteExtractor_HandlesSolidAndNeutralBitmaps_AndClampsChroma()
        {
            Assert.Null(AdaptivePaletteExtractor.ExtractAccentFromPixels(ReadOnlySpan<byte>.Empty, 0, 0));
            Assert.Null(AdaptivePaletteExtractor.ExtractAccentFromPixels(new byte[16], 4, 4));

            byte[] neutralGrey = CreateSolidBgra32(64, 64, r: 128, g: 128, b: 128, a: 255);
            Assert.Null(AdaptivePaletteExtractor.ExtractAccentFromPixels(neutralGrey, 64, 64));

            byte[] solidBlack = CreateSolidBgra32(64, 64, r: 0, g: 0, b: 0, a: 255);
            Assert.Null(AdaptivePaletteExtractor.ExtractAccentFromPixels(solidBlack, 64, 64));

            byte[] transparentBlue = CreateSolidBgra32(64, 64, r: 0, g: 120, b: 255, a: 0);
            Assert.Null(AdaptivePaletteExtractor.ExtractAccentFromPixels(transparentBlue, 64, 64));

            byte[] vividBlue = CreateSolidBgra32(128, 128, r: 0, g: 102, b: 255, a: 255);
            Color? extractedBlue = AdaptivePaletteExtractor.ExtractAccentFromPixels(vividBlue, 128, 128);
            Assert.NotNull(extractedBlue);
            var (blueL, blueC, blueH) = OklchColorMath.FromColor(extractedBlue!.Value);
            Assert.InRange(blueL, 0.55, 0.75);
            Assert.InRange(blueC, 0.04, 0.14);
            Assert.InRange(blueH, 230.0, 285.0);

            byte[] vividGold = CreateSolidBgra32(128, 128, r: 255, g: 200, b: 0, a: 255);
            Color? extractedGold = AdaptivePaletteExtractor.ExtractAccentFromPixels(vividGold, 128, 128);
            Assert.NotNull(extractedGold);
            var (goldL, goldC, goldH) = OklchColorMath.FromColor(extractedGold!.Value);
            Assert.InRange(goldL, 0.55, 0.75);
            Assert.InRange(goldC, 0.04, 0.14);
            Assert.InRange(goldH, 60.0, 115.0);
        }

        [Fact]
        public void VisualPreferencesService_PersistsAndRestoresPreferences_ViaISettingsService()
        {
            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var mockSettings = Substitute.For<ISettingsService>();
            mockSettings.Get(Arg.Any<string>()).Returns(ci =>
                store.TryGetValue(ci.Arg<string>(), out string? val) ? val : null);
            mockSettings.Exists(Arg.Any<string>()).Returns(ci =>
                store.ContainsKey(ci.Arg<string>()));
            mockSettings.When(x => x.Set(Arg.Any<string>(), Arg.Any<string?>()))
                .Do(ci =>
                {
                    string key = ci.ArgAt<string>(0);
                    string? val = ci.ArgAt<string?>(1);
                    if (val is null) store.Remove(key);
                    else store[key] = val;
                });

            using var scope = AppServices.Override<ISettingsService>(mockSettings);
            var svc = VisualPreferencesService.Instance;
            SurfacePreset origPreset = svc.SurfacePreset;
            UiDensityMode origDensity = svc.DensityMode;
            bool origGlass = svc.EnableGlassEffects;
            bool origTint = svc.EnableAdaptiveGameTint;
            bool origMotion = svc.ReducedMotion;
            bool origSidebar = svc.SidebarCollapsed;
            int changedEvents = 0;
            void OnChanged() => changedEvents++;

            svc.PreferencesChanged += OnChanged;
            try
            {
                svc.ApplyAndSave(
                    SurfacePreset.AmoledVoid,
                    UiDensityMode.Compact,
                    enableGlassEffects: false,
                    enableAdaptiveGameTint: false,
                    reducedMotion: true,
                    sidebarCollapsed: true);

                Assert.True(changedEvents >= 1);
                mockSettings.Received().Set(AppSettingsKeys.General.SurfacePreset, "AmoledVoid");
                mockSettings.Received().Set(AppSettingsKeys.General.UiDensityMode, "Compact");
                mockSettings.Received().Set(AppSettingsKeys.General.EnableGlassEffects, "false");
                mockSettings.Received().Set(AppSettingsKeys.General.EnableAdaptiveGameTint, "false");
                mockSettings.Received().Set(AppSettingsKeys.General.ReducedMotion, "true");
                mockSettings.Received().Set(AppSettingsKeys.General.SidebarCollapsed, "true");

                svc.SurfacePreset = SurfacePreset.WarmGraphite;
                svc.DensityMode = UiDensityMode.Comfortable;
                svc.EnableGlassEffects = true;
                svc.EnableAdaptiveGameTint = true;
                svc.ReducedMotion = false;
                svc.SidebarCollapsed = false;

                svc.Load();

                Assert.Equal(SurfacePreset.AmoledVoid, svc.SurfacePreset);
                Assert.Equal(UiDensityMode.Compact, svc.DensityMode);
                Assert.False(svc.EnableGlassEffects);
                Assert.False(svc.EnableAdaptiveGameTint);
                Assert.True(svc.ReducedMotion);
                Assert.True(svc.SidebarCollapsed);

                store[AppSettingsKeys.General.SurfacePreset] = "InvalidPreset";
                store[AppSettingsKeys.General.UiDensityMode] = "999";
                store.Remove(AppSettingsKeys.General.EnableGlassEffects);
                store[AppSettingsKeys.General.ReducedMotion] = "not-a-bool";
                svc.Load();

                Assert.Equal(SurfacePreset.LiquidObsidian, svc.SurfacePreset);
                Assert.Equal(UiDensityMode.Balanced, svc.DensityMode);
                Assert.True(svc.EnableGlassEffects);
                Assert.False(svc.ReducedMotion);
            }
            finally
            {
                svc.PreferencesChanged -= OnChanged;
                svc.SurfacePreset = origPreset;
                svc.DensityMode = origDensity;
                svc.EnableGlassEffects = origGlass;
                svc.EnableAdaptiveGameTint = origTint;
                svc.ReducedMotion = origMotion;
                svc.SidebarCollapsed = origSidebar;
            }
        }

        [AvaloniaFact]
        public void AccentThemeService_ApplySurfacePreset_UpdatesSurfaceBrushesAndAdaptiveOverride()
        {
            AccentTheme origTheme = AccentThemeService.CurrentTheme;
            SurfacePreset origPreset = AccentThemeService.CurrentSurfacePreset;
            Color? origAdaptive = AccentThemeService.AdaptiveGameColor;

            try
            {
                var app = Application.Current!;
                app.Resources.MergedDictionaries.Clear();
                var baseTokens = (ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
                    new Uri("avares://Shiro/AvaloniaUI/Styles/ThemeTokens.axaml"));
                app.Resources.MergedDictionaries.Add(baseTokens);

                AccentThemeService.ApplyTheme(AccentTheme.Cyan, save: false);

                AccentThemeService.ApplySurfacePreset(SurfacePreset.WarmGraphite, save: false);
                Assert.True(app.Resources.TryGetResource("SurfaceCanvasBrush", null, out object? warmRes));
                var warmBrush = Assert.IsAssignableFrom<ISolidColorBrush>(warmRes);
                double warmLuminance = RelativeLuminance(warmBrush.Color);

                AccentThemeService.ApplySurfacePreset(SurfacePreset.AmoledVoid, save: false);
                Assert.True(app.Resources.TryGetResource("SurfaceCanvasBrush", null, out object? amoledRes));
                var amoledBrush = Assert.IsAssignableFrom<ISolidColorBrush>(amoledRes);
                double amoledLuminance = RelativeLuminance(amoledBrush.Color);

                Assert.True(amoledLuminance < warmLuminance, $"AmoledVoid ({amoledLuminance:F5}) must be darker than WarmGraphite ({warmLuminance:F5})");

                string[] expectedImmutableBrushKeys =
                {
                    "SurfaceCanvasBrush", "SurfaceChromeBrush", "SurfacePanelBrush", "SurfaceCardBrush",
                    "SurfaceCardHoverBrush", "SurfaceElevatedBrush", "SurfaceInputBrush",
                    "SurfaceInputFocusBrush", "SurfaceSubtleChipBrush",
                    "BorderHairlineBrush", "BorderSubtleBrush", "BorderHoverBrush", "BorderStrongBrush",
                    "TextPrimaryBrush", "TextSecondaryBrush", "TextMutedBrush", "TextDisabledBrush",
                    "AccentBrush", "AccentHoverBrush", "AccentForegroundBrush"
                };
                foreach (string key in expectedImmutableBrushKeys)
                {
                    Assert.True(app.Resources.TryGetResource(key, null, out object? res), $"Missing resource key: {key}");
                    Assert.IsType<ImmutableSolidColorBrush>(res);
                }

                Assert.True(app.Resources.TryGetResource("SurfaceCardGradientBrush", null, out object? cardGradRes));
                Assert.IsType<ImmutableLinearGradientBrush>(cardGradRes);

                Assert.True(app.Resources.TryGetResource("AccentAmbientBrush", null, out object? baseAmbientObj));
                var baseAmbient = Assert.IsType<ImmutableRadialGradientBrush>(baseAmbientObj);
                Color baseAmbientPeak = baseAmbient.GradientStops[0].Color;

                AccentThemeService.SetAdaptiveGameColor(Color.FromRgb(255, 140, 0));
                Assert.True(app.Resources.TryGetResource("AccentAmbientBrush", null, out object? orangeAmbientObj));
                var orangeAmbient = Assert.IsType<ImmutableRadialGradientBrush>(orangeAmbientObj);
                Color orangeAmbientPeak = orangeAmbient.GradientStops[0].Color;

                Assert.NotEqual(baseAmbientPeak, orangeAmbientPeak);
                Assert.True(orangeAmbientPeak.R > orangeAmbientPeak.B);

                Assert.True(app.Resources.TryGetResource("AccentBrush", null, out object? adaptiveAccentObj));
                Assert.True(app.Resources.TryGetResource("AccentForegroundBrush", null, out object? adaptiveFgObj));
                double adaptiveContrast = Math.Abs(OklchColorMath.CalculateApcaContrast(
                    ((ISolidColorBrush)adaptiveFgObj!).Color,
                    ((ISolidColorBrush)adaptiveAccentObj!).Color));
                Assert.True(adaptiveContrast >= 60.0, $"Adaptive accent APCA |Lc|={adaptiveContrast:F2} < 60.0");

                AccentThemeService.SetAdaptiveGameColor(null);
                Assert.True(app.Resources.TryGetResource("AccentAmbientBrush", null, out object? restoredAmbientObj));
                var restoredAmbient = Assert.IsType<ImmutableRadialGradientBrush>(restoredAmbientObj);
                Assert.Equal(baseAmbientPeak, restoredAmbient.GradientStops[0].Color);
            }
            finally
            {
                AccentThemeService.SetAdaptiveGameColor(origAdaptive);
                AccentThemeService.ApplySurfacePreset(origPreset, save: false);
                AccentThemeService.ApplyTheme(origTheme, save: false);
                Application.Current?.Resources.MergedDictionaries.Clear();
            }
        }

        [Fact]
        public void AccentThemeService_AndVisualPreferences_StaySynchronizedWithoutSpuriousEvents()
        {
            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var mockSettings = Substitute.For<ISettingsService>();
            mockSettings.Get(Arg.Any<string>()).Returns(ci =>
                store.TryGetValue(ci.Arg<string>(), out string? val) ? val : null);
            mockSettings.Exists(Arg.Any<string>()).Returns(ci =>
                store.ContainsKey(ci.Arg<string>()));
            mockSettings.When(x => x.Set(Arg.Any<string>(), Arg.Any<string?>()))
                .Do(ci =>
                {
                    string key = ci.ArgAt<string>(0);
                    string? val = ci.ArgAt<string?>(1);
                    if (val is null) store.Remove(key);
                    else store[key] = val;
                });

            using var scope = AppServices.Override<ISettingsService>(mockSettings);
            var svc = VisualPreferencesService.Instance;
            AccentTheme origTheme = AccentThemeService.CurrentTheme;
            SurfacePreset origPreset = AccentThemeService.CurrentSurfacePreset;
            UiDensityMode origDensity = svc.DensityMode;
            bool origSidebar = svc.SidebarCollapsed;

            var observedThemes = new List<AccentTheme>();
            var observedPresets = new List<SurfacePreset>();
            void OnThemeChanged(AccentTheme t) => observedThemes.Add(t);
            void OnPresetChanged(SurfacePreset p) => observedPresets.Add(p);

            try
            {
                AccentThemeService.ApplyTheme(AccentTheme.Cyan, save: false);
                AccentThemeService.ApplySurfacePreset(SurfacePreset.LiquidObsidian, save: false);

                AccentThemeService.ThemeChanged += OnThemeChanged;
                AccentThemeService.SurfacePresetChanged += OnPresetChanged;

                // 1. Direct property mutation keeps AccentThemeService synchronized and does not fire ThemeChanged.
                svc.SurfacePreset = SurfacePreset.MidnightGlass;
                Assert.Equal(SurfacePreset.MidnightGlass, AccentThemeService.CurrentSurfacePreset);
                Assert.Empty(observedThemes);
                Assert.Equal(new[] { SurfacePreset.MidnightGlass }, observedPresets);

                // 2. Unrelated preference changes via ApplyAndSave emit zero theme/surface events.
                observedThemes.Clear();
                observedPresets.Clear();
                svc.ApplyAndSave(densityMode: UiDensityMode.Compact, sidebarCollapsed: true);
                Assert.Empty(observedThemes);
                Assert.Empty(observedPresets);

                // 3. Initialize loads both preferences cleanly in one pass without intermediate Monochrome ThemeChanged.
                observedThemes.Clear();
                observedPresets.Clear();
                store[AppSettingsKeys.General.AccentTheme] = "Violet";
                store[AppSettingsKeys.General.SurfacePreset] = "WarmGraphite";
                AccentThemeService.Initialize();

                Assert.Equal(AccentTheme.Violet, AccentThemeService.CurrentTheme);
                Assert.Equal(SurfacePreset.WarmGraphite, AccentThemeService.CurrentSurfacePreset);
                Assert.Equal(SurfacePreset.WarmGraphite, svc.SurfacePreset);
                Assert.Equal(new[] { AccentTheme.Violet }, observedThemes);
                Assert.Equal(new[] { SurfacePreset.WarmGraphite }, observedPresets);
            }
            finally
            {
                AccentThemeService.ThemeChanged -= OnThemeChanged;
                AccentThemeService.SurfacePresetChanged -= OnPresetChanged;
                svc.DensityMode = origDensity;
                svc.SidebarCollapsed = origSidebar;
                AccentThemeService.ApplySurfacePreset(origPreset, save: false);
                AccentThemeService.ApplyTheme(origTheme, save: false);
            }
        }

        [AvaloniaFact]
        public void ThemeTokens_ExposesConcentricRadiiKeys()
        {
            var dict = (ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
                new Uri("avares://Shiro/AvaloniaUI/Styles/ThemeTokens.axaml"));

            Assert.Equal(new CornerRadius(16), Assert.IsType<CornerRadius>(dict["RadiusOuter16"]));
            Assert.Equal(new CornerRadius(12), Assert.IsType<CornerRadius>(dict["RadiusMid12"]));
            Assert.Equal(new CornerRadius(8), Assert.IsType<CornerRadius>(dict["RadiusInner8"]));
            Assert.Equal(new CornerRadius(6), Assert.IsType<CornerRadius>(dict["RadiusChip6"]));
        }

        private static byte[] CreateSolidBgra32(int width, int height, byte r, byte g, byte b, byte a)
        {
            byte[] data = new byte[width * height * 4];
            for (int i = 0; i < data.Length; i += 4)
            {
                data[i] = b;
                data[i + 1] = g;
                data[i + 2] = r;
                data[i + 3] = a;
            }
            return data;
        }

        private static double RelativeLuminance(Color c)
        {
            static double Linearize(byte ch)
            {
                double s = ch / 255.0;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linearize(c.R) + 0.7152 * Linearize(c.G) + 0.0722 * Linearize(c.B);
        }

        [Fact]
        public void VisualPreferencesService_ApplyAndSave_PersistsOnlySpecifiedKeys()
        {
            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [AppSettingsKeys.General.SurfacePreset] = "LiquidObsidian",
                [AppSettingsKeys.General.UiDensityMode] = "Balanced",
                [AppSettingsKeys.General.EnableGlassEffects] = "true",
                [AppSettingsKeys.General.EnableAdaptiveGameTint] = "true",
                [AppSettingsKeys.General.ReducedMotion] = "false",
                [AppSettingsKeys.General.SidebarCollapsed] = "false"
            };
            var mockSettings = Substitute.For<ISettingsService>();
            mockSettings.Get(Arg.Any<string>()).Returns(ci => store.TryGetValue(ci.Arg<string>(), out var v) ? v : null);
            mockSettings.Exists(Arg.Any<string>()).Returns(ci => store.ContainsKey(ci.Arg<string>()));
            mockSettings.When(s => s.Set(Arg.Any<string>(), Arg.Any<string?>()))
                .Do(ci => { var k = ci.ArgAt<string>(0); var v = ci.ArgAt<string?>(1); if (v is null) store.Remove(k); else store[k] = v; });

            using var scope = AppServices.Override<ISettingsService>(mockSettings);
            var svc = VisualPreferencesService.Instance;
            SurfacePreset origPreset = svc.SurfacePreset;
            UiDensityMode origDensity = svc.DensityMode;
            bool origGlass = svc.EnableGlassEffects;
            bool origTint = svc.EnableAdaptiveGameTint;
            bool origMotion = svc.ReducedMotion;
            bool origSidebar = svc.SidebarCollapsed;
            try
            {
                svc.LoadPreferencesOnly();
                // Unsaved in-memory Appearance Studio preview edits
                svc.SurfacePreset = SurfacePreset.AmoledVoid;
                svc.EnableGlassEffects = false;
                svc.EnableAdaptiveGameTint = false;
                svc.ReducedMotion = true;

                svc.ApplyAndSave(densityMode: UiDensityMode.Compact);
                Assert.Equal("Compact", store[AppSettingsKeys.General.UiDensityMode]);
                Assert.Equal("LiquidObsidian", store[AppSettingsKeys.General.SurfacePreset]);
                Assert.Equal("true", store[AppSettingsKeys.General.EnableGlassEffects]);
                Assert.Equal("true", store[AppSettingsKeys.General.EnableAdaptiveGameTint]);
                Assert.Equal("false", store[AppSettingsKeys.General.ReducedMotion]);

                svc.ApplyAndSave(sidebarCollapsed: true);
                Assert.Equal("true", store[AppSettingsKeys.General.SidebarCollapsed]);
                Assert.Equal("LiquidObsidian", store[AppSettingsKeys.General.SurfacePreset]);
                mockSettings.DidNotReceive().Set(AppSettingsKeys.General.SurfacePreset, Arg.Any<string?>());
                mockSettings.DidNotReceive().Set(AppSettingsKeys.General.ReducedMotion, Arg.Any<string?>());
            }
            finally
            {
                svc.SurfacePreset = origPreset;
                svc.DensityMode = origDensity;
                svc.EnableGlassEffects = origGlass;
                svc.EnableAdaptiveGameTint = origTint;
                svc.ReducedMotion = origMotion;
                svc.SidebarCollapsed = origSidebar;
            }
        }

        [Fact]
        public void AdaptivePaletteExtractor_RejectsNegativeStride_AndAvoidsPowerOf2Aliasing()
        {
            byte[] src = new byte[4 * 4 * 4];
            byte[] dst = new byte[4 * 4 * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(
                src, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                Assert.False(AdaptivePaletteExtractor.TryNormalizeFramebufferToBgra32(
                    handle.AddrOfPinnedObject(), 4, 4, -16, PixelFormats.Bgra8888, AlphaFormat.Unpremul, dst));
            }
            finally
            {
                handle.Free();
            }

            // 512x512 image where every 16th column (x % 16 == 0) is neutral gray; old linear stride=64 hit ONLY neutral pixels!
            const int size = 512;
            byte[] img = CreateSolidBgra32(size, size, r: 0, g: 120, b: 255, a: 255);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x += 16)
                {
                    int idx = (y * size + x) * 4;
                    img[idx] = 128; img[idx + 1] = 128; img[idx + 2] = 128; img[idx + 3] = 255;
                }
            }

            Color? extracted = AdaptivePaletteExtractor.ExtractAccentFromPixels(img, size, size);
            Assert.NotNull(extracted);
            var (_, _, hue) = OklchColorMath.FromColor(extracted!.Value);
            Assert.InRange(hue, 225.0, 285.0);
        }
    }
}
