#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Shiro.Services
{
    public enum AccentTheme
    {
        Monochrome = 0,
        Violet = 1,
        Cyan = 2,
        Emerald = 3,
        Pink = 4
    }

    public static class AccentThemeService
    {
        private static readonly Lock _themeLock = new();
        private static AccentTheme _currentTheme = AccentTheme.Monochrome;
        private static SurfacePreset _currentSurfacePreset = SurfacePreset.LiquidObsidian;
        private static Color? _adaptiveGameColor;
        private static long _themeGeneration;

        private static ResourceDictionary? _activeThemeDict;
        private static AccentTheme? _activeTheme;
        private static SurfacePreset? _activeSurfacePreset;
        private static Color? _activeAdaptiveColor;
        private static IList<IResourceProvider>? _attachedMergedDictionaries;

        public static AccentTheme CurrentTheme { get { lock (_themeLock) return _currentTheme; } }
        public static SurfacePreset CurrentSurfacePreset { get { lock (_themeLock) return _currentSurfacePreset; } }
        public static Color? AdaptiveGameColor { get { lock (_themeLock) return _adaptiveGameColor; } }

        public static event Action<AccentTheme>? ThemeChanged;
        public static event Action<SurfacePreset>? SurfacePresetChanged;

        public readonly record struct AccentExtendedPalette(
            Color Primary,
            Color Hover,
            Color TextOnAccent,
            Color Subtle,
            Color Border,
            Color Glow,
            Color SoftFill,
            Color Rim,
            Color Ambient,
            Color AmbientSecondary);

        private static ResourceDictionary CreateThemeResourceDictionary(
            AccentTheme theme, SurfacePreset surfacePreset, Color? adaptiveGameColor)
        {
            SurfacePalette s = OklchColorMath.DeriveSurfacePalette(surfacePreset);
            AccentExtendedPalette p = adaptiveGameColor.HasValue
                ? OklchColorMath.DeriveAdaptiveAccentPalette(adaptiveGameColor.Value)
                : GetExtendedColors(theme);

            var topLeft = new RelativePoint(0.0, 0.0, RelativeUnit.Relative);
            var bottomLeft = new RelativePoint(0.0, 1.0, RelativeUnit.Relative);
            var bottomRight = new RelativePoint(1.0, 1.0, RelativeUnit.Relative);

            return new ResourceDictionary
            {
                ["SurfaceCanvasBrush"] = new ImmutableSolidColorBrush(s.SurfaceCanvas),
                ["SurfaceChromeBrush"] = new ImmutableSolidColorBrush(s.SurfaceChrome),
                ["SurfacePanelBrush"] = new ImmutableSolidColorBrush(s.SurfacePanel),
                ["SurfaceCardBrush"] = new ImmutableSolidColorBrush(s.SurfaceCard),
                ["SurfaceCardHoverBrush"] = new ImmutableSolidColorBrush(s.SurfaceCardHover),
                ["SurfaceElevatedBrush"] = new ImmutableSolidColorBrush(s.SurfaceElevated),
                ["SurfaceInputBrush"] = new ImmutableSolidColorBrush(s.SurfaceInput),
                ["SurfaceInputFocusBrush"] = new ImmutableSolidColorBrush(s.SurfaceInputFocus),
                ["SurfaceSubtleChipBrush"] = new ImmutableSolidColorBrush(s.SurfaceSubtleChip),
                ["SurfaceCardGradientBrush"] = new ImmutableLinearGradientBrush(
                    [new ImmutableGradientStop(0.0, s.SurfaceCardGradientTop), new ImmutableGradientStop(1.0, s.SurfaceCardGradientBottom)],
                    startPoint: topLeft, endPoint: bottomLeft),
                ["BorderHairlineBrush"] = new ImmutableSolidColorBrush(s.BorderHairline),
                ["BorderSubtleBrush"] = new ImmutableSolidColorBrush(s.BorderSubtle),
                ["BorderHoverBrush"] = new ImmutableSolidColorBrush(s.BorderHover),
                ["BorderStrongBrush"] = new ImmutableSolidColorBrush(s.BorderStrong),
                ["TextPrimaryBrush"] = new ImmutableSolidColorBrush(s.TextPrimary),
                ["TextSecondaryBrush"] = new ImmutableSolidColorBrush(s.TextSecondary),
                ["TextMutedBrush"] = new ImmutableSolidColorBrush(s.TextMuted),
                ["TextDisabledBrush"] = new ImmutableSolidColorBrush(s.TextDisabled),
                ["AccentColor"] = p.Primary,
                ["AccentHoverColor"] = p.Hover,
                ["AccentForegroundColor"] = p.TextOnAccent,
                ["AccentAmbientColor"] = p.Ambient,
                ["AccentAmbientSecondaryColor"] = p.AmbientSecondary,
                ["AccentBrush"] = new ImmutableSolidColorBrush(p.Primary),
                ["AccentHoverBrush"] = new ImmutableSolidColorBrush(p.Hover),
                ["AccentForegroundBrush"] = new ImmutableSolidColorBrush(p.TextOnAccent),
                ["AccentGlowBrush"] = new ImmutableSolidColorBrush(p.Glow),
                ["AccentSubtleBrush"] = new ImmutableSolidColorBrush(p.Subtle),
                ["AccentBorderBrush"] = new ImmutableSolidColorBrush(p.Border),
                ["AccentSoftFillBrush"] = new ImmutableSolidColorBrush(p.SoftFill),
                ["AccentRimBrush"] = new ImmutableSolidColorBrush(p.Rim),
                ["AccentGradientBrush"] = new ImmutableLinearGradientBrush(
                    [new ImmutableGradientStop(0.0, p.Primary), new ImmutableGradientStop(1.0, p.Hover)],
                    startPoint: topLeft, endPoint: bottomRight),
                ["AccentAmbientBrush"] = CreateRadialGlowBrush(p.Ambient),
                ["AccentAmbientSecondaryBrush"] = CreateRadialGlowBrush(p.AmbientSecondary)
            };
        }

        private static ImmutableRadialGradientBrush CreateRadialGlowBrush(Color peak)
        {
            var center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            var halfRadius = new RelativeScalar(0.5, RelativeUnit.Relative);
            return new ImmutableRadialGradientBrush(
                [
                    new ImmutableGradientStop(0.0, peak),
                    new ImmutableGradientStop(0.55, Color.FromArgb((byte)(peak.A / 3), peak.R, peak.G, peak.B)),
                    new ImmutableGradientStop(1.0, Color.FromArgb(0, peak.R, peak.G, peak.B))
                ],
                center: center, gradientOrigin: center, radiusX: halfRadius, radiusY: halfRadius);
        }

        public static AccentTheme ParseTheme(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return AccentTheme.Monochrome;

            string trimmed = value.Trim();
            if (string.Equals(trimmed, LocalizationService.Get("AccentTheme.White"), StringComparison.OrdinalIgnoreCase))
                return AccentTheme.Monochrome;
            if (string.Equals(trimmed, LocalizationService.Get("AccentTheme.Purple"), StringComparison.OrdinalIgnoreCase))
                return AccentTheme.Violet;
            if (string.Equals(trimmed, LocalizationService.Get("AccentTheme.Green"), StringComparison.OrdinalIgnoreCase))
                return AccentTheme.Emerald;
            if (string.Equals(trimmed, LocalizationService.Get("AccentTheme.Pink"), StringComparison.OrdinalIgnoreCase))
                return AccentTheme.Pink;

            string folded = FoldThemeToken(trimmed);
            return folded switch
            {
                "monochrome" or "trang" or "white" or "default" => AccentTheme.Monochrome,
                "violet" or "purple" or "tim" => AccentTheme.Violet,
                "cyan" or "blue" or "xanh lam" or "xanh" => AccentTheme.Cyan,
                "emerald" or "green" or "xanh la" => AccentTheme.Emerald,
                "pink" or "hong" => AccentTheme.Pink,
                _ => Enum.TryParse<AccentTheme>(trimmed, true, out var parsed) && Enum.IsDefined(parsed)
                    ? parsed
                    : AccentTheme.Monochrome
            };
        }

        private static string FoldThemeToken(string input)
        {
            string normalized = input.Trim().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    char lower = char.ToLowerInvariant(c);
                    sb.Append(lower == '\u0111' ? 'd' : lower);
                }
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        public static (Color primary, Color hover, Color textOnAccent, Color subtle, Color borderCol, Color glow) GetThemeColors(AccentTheme theme)
        {
            Color primary;
            Color hover;
            Color textOnAccent;

            if (theme == AccentTheme.Monochrome || !Enum.IsDefined(theme))
            {
                primary = Color.FromRgb(255, 255, 255);
                hover = Color.FromRgb(228, 228, 231);
                textOnAccent = Color.FromRgb(9, 9, 11);
            }
            else
            {
                (Color basePrimary, Color baseHover, Color preferredFg) = theme switch
                {
                    AccentTheme.Cyan => (Color.FromRgb(6, 182, 212), Color.FromRgb(34, 211, 238), Color.FromRgb(9, 9, 11)),
                    AccentTheme.Emerald => (Color.FromRgb(16, 185, 129), Color.FromRgb(52, 211, 153), Color.FromRgb(9, 9, 11)),
                    AccentTheme.Pink => (Color.FromRgb(219, 39, 119), Color.FromRgb(236, 72, 153), Color.FromRgb(255, 255, 255)),
                    _ => (Color.FromRgb(124, 58, 237), Color.FromRgb(139, 92, 246), Color.FromRgb(255, 255, 255))
                };

                if (Math.Abs(OklchColorMath.CalculateApcaContrast(preferredFg, basePrimary)) >= 60.0)
                {
                    primary = basePrimary;
                    hover = baseHover;
                    textOnAccent = preferredFg;
                }
                else
                {
                    (primary, textOnAccent) = OklchColorMath.EnforceApcaButtonContrast(basePrimary, 60.0);
                    var (pL, pC, pH) = OklchColorMath.FromColor(primary);
                    hover = OklchColorMath.ToColor(Math.Clamp(pL + 0.04, 0.0, 0.96), pC, pH);
                }
            }

            byte subtleAlpha = theme == AccentTheme.Monochrome ? (byte)28 : (byte)40;
            byte borderAlpha = theme == AccentTheme.Monochrome ? (byte)70 : (byte)120;
            byte glowAlpha = 90;

            Color subtle = Color.FromArgb(subtleAlpha, primary.R, primary.G, primary.B);
            Color borderCol = Color.FromArgb(borderAlpha, primary.R, primary.G, primary.B);
            Color glow = Color.FromArgb(glowAlpha, primary.R, primary.G, primary.B);

            return (primary, hover, textOnAccent, subtle, borderCol, glow);
        }

        public static AccentExtendedPalette GetExtendedColors(AccentTheme theme)
        {
            var (primary, hover, textOnAccent, subtle, borderCol, glow) = GetThemeColors(theme);

            Color softFill = Color.FromArgb(31, primary.R, primary.G, primary.B);
            Color rimSource = theme == AccentTheme.Monochrome ? primary : hover;
            Color rim = Color.FromArgb(115, rimSource.R, rimSource.G, rimSource.B);
            Color ambient = Color.FromArgb(28, primary.R, primary.G, primary.B);
            Color secondaryBase = theme switch
            {
                AccentTheme.Violet or AccentTheme.Pink => Color.FromRgb(139, 92, 246),
                _ => Color.FromRgb(99, 102, 241)
            };
            Color ambientSecondary = Color.FromArgb(15, secondaryBase.R, secondaryBase.G, secondaryBase.B);

            return new AccentExtendedPalette(
                primary, hover, textOnAccent, subtle, borderCol,
                glow, softFill, rim, ambient, ambientSecondary);
        }

        public static AccentExtendedPalette GetEffectiveExtendedColors()
        {
            AccentTheme theme;
            Color? adaptive;
            lock (_themeLock)
            {
                theme = _currentTheme;
                adaptive = _adaptiveGameColor;
            }

            return adaptive.HasValue
                ? OklchColorMath.DeriveAdaptiveAccentPalette(adaptive.Value)
                : GetExtendedColors(theme);
        }

        public static void Initialize()
        {
            try
            {
                VisualPreferencesService.Instance.Load();
                string? saved = AppServices.GeneralSettings.Exists(AppSettingsKeys.General.AccentTheme)
                    ? AppServices.GeneralSettings.Get(AppSettingsKeys.General.AccentTheme)
                    : AppServices.Settings.AccentTheme;

                AccentTheme theme = ParseTheme(saved);
                ApplyTheme(theme, save: false);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[AccentThemeService] Initialize theme failed, falling back to Monochrome: {ex.Message}");
                ApplyTheme(AccentTheme.Monochrome, save: false);
            }
        }

        public static void ApplyTheme(AccentTheme theme, bool save = true)
        {
            bool changed;
            long gen;
            SurfacePreset preset;
            Color? adaptive;
            lock (_themeLock)
            {
                changed = _currentTheme != theme;
                _currentTheme = theme;
                preset = _currentSurfacePreset;
                adaptive = _adaptiveGameColor;
                gen = ++_themeGeneration;
            }

            if (save)
            {
                try
                {
                    string themeStr = theme.ToString();
                    AppServices.GeneralSettings.Set(AppSettingsKeys.General.AccentTheme, themeStr);
                    try { AppServices.Settings.AccentTheme = themeStr; }
                    catch (Exception ex) { AppServices.Logger?.Debug($"AccentTheme setter failed: {ex.Message}"); }
                    AppServices.QueueDeferredSaveSettings();
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Warn($"Failed to persist accent theme: {ex.Message}");
                }
            }

            ScheduleResourceSwap(gen, theme, preset, adaptive, raiseThemeChanged: changed || !save, raiseSurfaceChanged: false);
        }

        public static void ApplySurfacePreset(SurfacePreset preset, bool save = true)
        {
            SurfacePreset normalized = Enum.IsDefined(preset) ? preset : SurfacePreset.LiquidObsidian;
            bool changed;
            long gen;
            AccentTheme theme;
            Color? adaptive;
            lock (_themeLock)
            {
                changed = _currentSurfacePreset != normalized;
                _currentSurfacePreset = normalized;
                theme = _currentTheme;
                adaptive = _adaptiveGameColor;
                gen = ++_themeGeneration;
            }

            if (save)
            {
                try
                {
                    AppServices.GeneralSettings.Set(AppSettingsKeys.General.SurfacePreset, normalized.ToString());
                    AppServices.QueueDeferredSaveSettings();
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Warn($"Failed to persist surface preset: {ex.Message}");
                }
            }

            VisualPreferencesService.Instance.SyncSurfacePresetFromThemeService(normalized);
            ScheduleResourceSwap(gen, theme, normalized, adaptive, raiseThemeChanged: changed || !save, raiseSurfaceChanged: changed || !save);
        }

        public static void SetAdaptiveGameColor(Color? gameColor)
        {
            bool changed;
            long gen;
            AccentTheme theme;
            SurfacePreset preset;
            lock (_themeLock)
            {
                changed = _adaptiveGameColor != gameColor;
                _adaptiveGameColor = gameColor;
                theme = _currentTheme;
                preset = _currentSurfacePreset;
                gen = ++_themeGeneration;
            }

            ScheduleResourceSwap(gen, theme, preset, gameColor, raiseThemeChanged: changed, raiseSurfaceChanged: false);
        }

        private static void ScheduleResourceSwap(
            long gen,
            AccentTheme theme,
            SurfacePreset preset,
            Color? adaptiveColor,
            bool raiseThemeChanged,
            bool raiseSurfaceChanged)
        {
            void SwapAndNotify()
            {
                lock (_themeLock)
                {
                    if (_themeGeneration != gen)
                        return;

                    if (AppDispatcher.CanMutateUiResources())
                    {
                        ApplyToAppResources(Application.Current, theme, preset, adaptiveColor);
                    }
                }

                if (Volatile.Read(ref _themeGeneration) != gen)
                    return;

                if (raiseSurfaceChanged)
                    SurfacePresetChanged?.Invoke(preset);

                if (raiseThemeChanged)
                    ThemeChanged?.Invoke(theme);
            }

            if (Application.Current == null || AppDispatcher.HasUiThreadAccess())
            {
                SwapAndNotify();
            }
            else
            {
                AppDispatcher.Post(SwapAndNotify);
            }
        }

        private static void ApplyToAppResources(
            Application? app,
            AccentTheme targetTheme,
            SurfacePreset targetPreset,
            Color? targetAdaptive)
        {
            if (app?.Resources == null) return;
            if (!Enum.IsDefined(targetTheme)) targetTheme = AccentTheme.Monochrome;
            if (!Enum.IsDefined(targetPreset)) targetPreset = SurfacePreset.LiquidObsidian;

            var merged = app.Resources.MergedDictionaries;
            bool sameMergedList = ReferenceEquals(_attachedMergedDictionaries, merged);
            if (sameMergedList &&
                _activeTheme == targetTheme &&
                _activeSurfacePreset == targetPreset &&
                _activeAdaptiveColor == targetAdaptive &&
                _activeThemeDict != null &&
                merged.Contains(_activeThemeDict))
            {
                return;
            }

            if (sameMergedList && _activeThemeDict != null)
            {
                merged.Remove(_activeThemeDict);
            }

            var targetDict = CreateThemeResourceDictionary(targetTheme, targetPreset, targetAdaptive);
            _activeThemeDict = targetDict;
            _activeTheme = targetTheme;
            _activeSurfacePreset = targetPreset;
            _activeAdaptiveColor = targetAdaptive;
            _attachedMergedDictionaries = merged;
            merged.Add(targetDict);
        }
    }
}
