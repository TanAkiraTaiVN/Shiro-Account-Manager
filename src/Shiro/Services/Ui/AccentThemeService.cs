#nullable enable
using System;
using System.Globalization;
using System.Text;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
        private static long _themeGeneration;

        public static AccentTheme CurrentTheme
        {
            get
            {
                lock (_themeLock)
                {
                    return _currentTheme;
                }
            }
        }

        private static ResourceDictionary? _activeThemeDict;
        private static AccentTheme? _activeTheme;
        private static System.Collections.Generic.IList<IResourceProvider>? _attachedMergedDictionaries;

        public static event Action<AccentTheme>? ThemeChanged;

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

        private static ResourceDictionary CreateThemeResourceDictionary(AccentTheme theme)
        {
            AccentExtendedPalette p = GetExtendedColors(theme);
            var gradientStops = new ImmutableGradientStop[]
            {
                new(0.0, p.Primary),
                new(1.0, p.Hover)
            };
            var ambientStops = new ImmutableGradientStop[]
            {
                new(0.0, p.Ambient),
                new(0.55, Color.FromArgb((byte)(p.Ambient.A / 3), p.Ambient.R, p.Ambient.G, p.Ambient.B)),
                new(1.0, Color.FromArgb(0, p.Ambient.R, p.Ambient.G, p.Ambient.B))
            };
            var secondaryStops = new ImmutableGradientStop[]
            {
                new(0.0, p.AmbientSecondary),
                new(0.55, Color.FromArgb((byte)(p.AmbientSecondary.A / 3), p.AmbientSecondary.R, p.AmbientSecondary.G, p.AmbientSecondary.B)),
                new(1.0, Color.FromArgb(0, p.AmbientSecondary.R, p.AmbientSecondary.G, p.AmbientSecondary.B))
            };
            var center = new Avalonia.RelativePoint(0.5, 0.5, Avalonia.RelativeUnit.Relative);
            var halfRadius = new Avalonia.RelativeScalar(0.5, Avalonia.RelativeUnit.Relative);

            return new ResourceDictionary
            {
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
                    gradientStops,
                    startPoint: new Avalonia.RelativePoint(0.0, 0.0, Avalonia.RelativeUnit.Relative),
                    endPoint: new Avalonia.RelativePoint(1.0, 1.0, Avalonia.RelativeUnit.Relative)),
                ["AccentAmbientBrush"] = new ImmutableRadialGradientBrush(
                    ambientStops,
                    center: center,
                    gradientOrigin: center,
                    radiusX: halfRadius,
                    radiusY: halfRadius),
                ["AccentAmbientSecondaryBrush"] = new ImmutableRadialGradientBrush(
                    secondaryStops,
                    center: center,
                    gradientOrigin: center,
                    radiusX: halfRadius,
                    radiusY: halfRadius)
            };
        }

        public static AccentTheme ParseTheme(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return AccentTheme.Monochrome;

            string trimmed = value.Trim();
            if (string.Equals(trimmed, Shiro.Services.LocalizationService.Get("AccentTheme.White"), StringComparison.OrdinalIgnoreCase))
                return AccentTheme.Monochrome;
            if (string.Equals(trimmed, Shiro.Services.LocalizationService.Get("AccentTheme.Purple"), StringComparison.OrdinalIgnoreCase))
                return AccentTheme.Violet;
            if (string.Equals(trimmed, Shiro.Services.LocalizationService.Get("AccentTheme.Green"), StringComparison.OrdinalIgnoreCase))
                return AccentTheme.Emerald;
            if (string.Equals(trimmed, Shiro.Services.LocalizationService.Get("AccentTheme.Pink"), StringComparison.OrdinalIgnoreCase))
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
            var (primary, hover, textOnAccent) = theme switch
            {
                AccentTheme.Cyan => (Color.FromRgb(6, 182, 212), Color.FromRgb(34, 211, 238), Color.FromRgb(9, 9, 11)),
                AccentTheme.Emerald => (Color.FromRgb(16, 185, 129), Color.FromRgb(52, 211, 153), Color.FromRgb(9, 9, 11)),
                AccentTheme.Pink => (Color.FromRgb(219, 39, 119), Color.FromRgb(236, 72, 153), Color.FromRgb(255, 255, 255)),
                AccentTheme.Violet => (Color.FromRgb(124, 58, 237), Color.FromRgb(139, 92, 246), Color.FromRgb(255, 255, 255)),
                _ => (Color.FromRgb(255, 255, 255), Color.FromRgb(228, 228, 231), Color.FromRgb(9, 9, 11)) // Monochrome (Default)
            };

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

            Color softFill = Color.FromArgb(31, primary.R, primary.G, primary.B); // 12% tint
            Color rimSource = theme == AccentTheme.Monochrome ? primary : hover;
            Color rim = Color.FromArgb(115, rimSource.R, rimSource.G, rimSource.B); // 45% highlight
            Color ambient = Color.FromArgb(28, primary.R, primary.G, primary.B); // 11% radial glow
            Color secondaryBase = theme switch
            {
                AccentTheme.Violet or AccentTheme.Pink => Color.FromRgb(139, 92, 246),
                _ => Color.FromRgb(99, 102, 241)
            };
            Color ambientSecondary = Color.FromArgb(15, secondaryBase.R, secondaryBase.G, secondaryBase.B); // 6% cool indigo/violet tint

            return new AccentExtendedPalette(
                primary,
                hover,
                textOnAccent,
                subtle,
                borderCol,
                glow,
                softFill,
                rim,
                ambient,
                ambientSecondary);
        }

        public static void Initialize()
        {
            try
            {
                string? saved = null;
                if (AppServices.GeneralSettings.Exists("AccentTheme"))
                {
                    saved = AppServices.GeneralSettings.Get("AccentTheme");
                }
                else
                {
                    saved = AppServices.Settings.AccentTheme;
                }

                AccentTheme theme = ParseTheme(saved);
                ApplyTheme(theme, save: false);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[AccentThemeService] Initialize theme failed, falling back to Monochrome", ex);
                ApplyTheme(AccentTheme.Monochrome, save: false);
            }
        }

        public static void ApplyTheme(AccentTheme theme, bool save = true)
        {
            bool changed;
            long gen;
            lock (_themeLock)
            {
                changed = _currentTheme != theme;
                _currentTheme = theme;
                gen = ++_themeGeneration;
            }

            if (save)
            {
                try
                {
                    string themeStr = theme.ToString();
                    AppServices.GeneralSettings.Set("AccentTheme", themeStr);
                    try { AppServices.Settings.AccentTheme = themeStr; }
                    catch (Exception ex) { AppServices.Logger?.Debug($"AccentTheme setter failed: {ex.Message}"); }
                    AppServices.QueueDeferredSaveSettings();
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Warn($"Failed to persist accent theme: {ex.Message}");
                }
            }

            static void ApplyToAppResources(Avalonia.Application? app, AccentTheme targetTheme)
            {
                if (app?.Resources == null) return;
                if (!Enum.IsDefined(targetTheme)) targetTheme = AccentTheme.Monochrome;

                var merged = app.Resources.MergedDictionaries;
                bool sameMergedList = ReferenceEquals(_attachedMergedDictionaries, merged);
                if (sameMergedList &&
                    _activeTheme == targetTheme &&
                    _activeThemeDict != null &&
                    merged.Contains(_activeThemeDict))
                {
                    return;
                }

                if (sameMergedList && _activeThemeDict != null)
                {
                    merged.Remove(_activeThemeDict);
                }

                var targetDict = CreateThemeResourceDictionary(targetTheme);
                _activeThemeDict = targetDict;
                _activeTheme = targetTheme;
                _attachedMergedDictionaries = merged;
                merged.Add(targetDict);
            }

            void SwapAndNotify()
            {
                lock (_themeLock)
                {
                    if (_themeGeneration != gen)
                        return;

                    if (AppDispatcher.CanMutateUiResources())
                    {
                        ApplyToAppResources(Avalonia.Application.Current, theme);
                    }
                }

                if (Volatile.Read(ref _themeGeneration) != gen)
                    return;

                if (changed || !save)
                {
                    ThemeChanged?.Invoke(theme);
                }
            }

            if (Avalonia.Application.Current == null || AppDispatcher.HasUiThreadAccess())
            {
                SwapAndNotify();
            }
            else
            {
                AppDispatcher.Post(SwapAndNotify);
            }
        }
    }
}
