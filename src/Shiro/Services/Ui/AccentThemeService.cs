#nullable enable
using System;
using System.Globalization;
using System.Text;
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
        private static AccentTheme _currentTheme = AccentTheme.Monochrome;
        public static AccentTheme CurrentTheme => _currentTheme;

        private static ResourceDictionary? _activeThemeDict;
        private static AccentTheme? _activeTheme;
        private static System.Collections.Generic.IList<IResourceProvider>? _attachedMergedDictionaries;

        public static event Action<AccentTheme>? ThemeChanged;

        private static ResourceDictionary CreateThemeResourceDictionary(AccentTheme theme)
        {
            var (primary, hover, textOnAccent, subtle, borderCol, glow) = GetThemeColors(theme);
            return new ResourceDictionary
            {
                ["AccentColor"] = primary,
                ["AccentHoverColor"] = hover,
                ["AccentForegroundColor"] = textOnAccent,
                ["AccentBrush"] = new ImmutableSolidColorBrush(primary),
                ["AccentHoverBrush"] = new ImmutableSolidColorBrush(hover),
                ["AccentForegroundBrush"] = new ImmutableSolidColorBrush(textOnAccent),
                ["AccentGlowBrush"] = new ImmutableSolidColorBrush(glow),
                ["AccentSubtleBrush"] = new ImmutableSolidColorBrush(subtle),
                ["AccentBorderBrush"] = new ImmutableSolidColorBrush(borderCol)
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
            bool changed = _currentTheme != theme;
            _currentTheme = theme;

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
                if (AppDispatcher.CanMutateUiResources())
                {
                    ApplyToAppResources(Avalonia.Application.Current, theme);
                }
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
