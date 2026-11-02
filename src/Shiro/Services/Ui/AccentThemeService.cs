#nullable enable
using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Shiro.Classes;

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

        private static readonly ResourceDictionary[] _themeResourceDicts = BuildThemeResourceDictionaries();
        private static ResourceDictionary? _activeThemeDict;

        public static event Action<AccentTheme>? ThemeChanged;

        private static ResourceDictionary[] BuildThemeResourceDictionaries()
        {
            var themes = new[]
            {
                AccentTheme.Monochrome,
                AccentTheme.Violet,
                AccentTheme.Cyan,
                AccentTheme.Emerald,
                AccentTheme.Pink
            };

            var dicts = new ResourceDictionary[themes.Length];
            for (int i = 0; i < themes.Length; i++)
            {
                var (primary, hover, textOnAccent, subtle, borderCol, glow) = GetThemeColors(themes[i]);
                var dict = new ResourceDictionary
                {
                    ["AccentColor"] = primary,
                    ["AccentHoverColor"] = hover,
                    ["AccentForegroundColor"] = textOnAccent,
                    ["AccentBrush"] = new SolidColorBrush(primary),
                    ["AccentHoverBrush"] = new SolidColorBrush(hover),
                    ["AccentForegroundBrush"] = new SolidColorBrush(textOnAccent),
                    ["AccentGlowBrush"] = new SolidColorBrush(glow),
                    ["AccentSubtleBrush"] = new SolidColorBrush(subtle),
                    ["AccentBorderBrush"] = new SolidColorBrush(borderCol)
                };
                dicts[(int)themes[i]] = dict;
            }
            return dicts;
        }

        public static AccentTheme ParseTheme(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return AccentTheme.Monochrome;

            string clean = value.Trim().ToLowerInvariant();
            return clean switch
            {
                "monochrome" or "trắng" or "trang" or "white" or "default" => AccentTheme.Monochrome,
                "violet" or "purple" or "tím" or "tim" => AccentTheme.Violet,
                "cyan" or "blue" or "xanh lam" or "xanh" => AccentTheme.Cyan,
                "emerald" or "green" or "xanh lá" or "xanh la" => AccentTheme.Emerald,
                "pink" or "hồng" or "hong" => AccentTheme.Pink,
                _ => Enum.TryParse<AccentTheme>(value, true, out var parsed) ? parsed : AccentTheme.Monochrome
            };
        }

        public static (Color primary, Color hover, Color textOnAccent, Color subtle, Color borderCol, Color glow) GetThemeColors(AccentTheme theme)
        {
            var (primary, hover, textOnAccent) = theme switch
            {
                AccentTheme.Cyan => (Color.FromRgb(6, 182, 212), Color.FromRgb(34, 211, 238), Color.FromRgb(9, 9, 11)),
                AccentTheme.Emerald => (Color.FromRgb(16, 185, 129), Color.FromRgb(52, 211, 153), Color.FromRgb(9, 9, 11)),
                AccentTheme.Pink => (Color.FromRgb(236, 72, 153), Color.FromRgb(244, 114, 182), Color.FromRgb(255, 255, 255)),
                AccentTheme.Violet => (Color.FromRgb(139, 92, 246), Color.FromRgb(167, 139, 250), Color.FromRgb(255, 255, 255)),
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
                if (AppSettings.General.Exists("AccentTheme"))
                {
                    saved = AppSettings.General.Get("AccentTheme");
                }
                else
                {
                    saved = AppSettings.Current.AccentTheme;
                }

                AccentTheme theme = ParseTheme(saved);
                ApplyTheme(theme, save: false);
            }
            catch
            {
                ApplyTheme(AccentTheme.Monochrome, save: false);
            }
        }

        public static void ApplyTheme(AccentTheme theme, bool save = true)
        {
            bool changed = _currentTheme != theme;
            _currentTheme = theme;

            static void ApplyToAppResources(AccentTheme targetTheme)
            {
                var app = Avalonia.Application.Current;
                if (app?.Resources == null) return;

                int idx = (int)targetTheme;
                if ((uint)idx >= (uint)_themeResourceDicts.Length) idx = 0;
                var targetDict = _themeResourceDicts[idx];

                var merged = app.Resources.MergedDictionaries;
                if (ReferenceEquals(_activeThemeDict, targetDict) && merged.Contains(targetDict))
                    return;

                if (_activeThemeDict != null)
                {
                    merged.Remove(_activeThemeDict);
                }

                merged.Add(targetDict);
                _activeThemeDict = targetDict;
            }

            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                ApplyToAppResources(theme);
            }
            else
            {
                AppDispatcher.Post(() => ApplyToAppResources(theme));
            }

            if (save)
            {
                try
                {
                    string themeStr = theme.ToString();
                    AppSettings.General.Set("AccentTheme", themeStr);
                    try { AppSettings.Current.AccentTheme = themeStr; }
                    catch (Exception ex) { Program.Logger?.Debug($"AppSettings.AccentTheme setter failed: {ex.Message}"); }
                    AppSettings.QueueDeferredSave();
                }
                catch (Exception ex)
                {
                    Program.Logger?.Warn($"Failed to persist accent theme: {ex.Message}");
                }
            }

            if (changed || !save)
            {
                ThemeChanged?.Invoke(theme);
            }
        }
    }
}
