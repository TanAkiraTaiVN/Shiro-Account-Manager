#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Shiro.Services
{
    public enum AppLanguage
    {
        Vietnamese,
        English
    }

    public static partial class LocalizationService
    {
        public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.Vietnamese;

        public static string CurrentLanguageCode => CurrentLanguage == AppLanguage.English ? "en" : "vi";

        public static bool IsEnglish => CurrentLanguage == AppLanguage.English;

        public static bool IsVietnamese => CurrentLanguage == AppLanguage.Vietnamese;

        public static event Action<AppLanguage>? LanguageChanged;

        private static bool _initialized;

        public static AppLanguage ResolveInitialLanguage(string? processName, string[]? args, string? savedSettingLanguage, string? cultureCode = null)
        {
            string procName = processName ?? string.Empty;

            bool hasEnFlag = args != null && Array.Exists(args, a =>
                string.Equals(a, "--en", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-en", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--lang=en", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-lang:en", StringComparison.OrdinalIgnoreCase));

            bool hasVnFlag = args != null && Array.Exists(args, a =>
                string.Equals(a, "--vn", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-vn", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--vi", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-vi", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--lang=vi", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--lang=vn", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-lang:vn", StringComparison.OrdinalIgnoreCase));

            if (hasEnFlag || procName.IndexOf("(EN)", StringComparison.OrdinalIgnoreCase) >= 0 || procName.EndsWith("_EN", StringComparison.OrdinalIgnoreCase))
            {
                return AppLanguage.English;
            }

            if (hasVnFlag || procName.IndexOf("(VN)", StringComparison.OrdinalIgnoreCase) >= 0 || procName.EndsWith("_VN", StringComparison.OrdinalIgnoreCase))
            {
                return AppLanguage.Vietnamese;
            }

            if (string.Equals(savedSettingLanguage, "en", StringComparison.OrdinalIgnoreCase))
            {
                return AppLanguage.English;
            }

            if (string.Equals(savedSettingLanguage, "vi", StringComparison.OrdinalIgnoreCase))
            {
                return AppLanguage.Vietnamese;
            }

            string cult = cultureCode ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return cult.Equals("vi", StringComparison.OrdinalIgnoreCase) ? AppLanguage.Vietnamese : AppLanguage.English;
        }

        public static void Initialize(Application? app = null)
        {
            if (_initialized)
                return;

            _initialized = true;

            try
            {
                string procName = string.Empty;
                try
                {
                    string? processPath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(processPath))
                    {
                        procName = System.IO.Path.GetFileNameWithoutExtension(processPath);
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"ProcessPath lookup threw: {ex.Message}");
                }

                string[] args = Environment.GetCommandLineArgs();
                string savedLang = AppServices.GeneralSettings.Get<string>(AppSettingsKeys.General.Language) ?? string.Empty;
                CurrentLanguage = ResolveInitialLanguage(procName, args, savedLang);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"ResolveInitialLanguage threw: {ex.Message}");
                CurrentLanguage = AppLanguage.Vietnamese;
            }

            ApplyResources(app ?? Application.Current);
        }

        public static void SetLanguage(AppLanguage language, bool saveSetting = true)
        {
            bool changed = CurrentLanguage != language;
            CurrentLanguage = language;

            if (saveSetting)
            {
                try
                {
                    AppServices.GeneralSettings.Set(AppSettingsKeys.General.Language, language == AppLanguage.English ? "en" : "vi");
                    AppServices.QueueDeferredSaveSettings();
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Warn($"Failed to save Language setting: {ex.Message}");
                }
            }

            ApplyResources(Application.Current);

            if (changed || !saveSetting)
            {
                LanguageChanged?.Invoke(language);
            }
        }

        public static void ToggleLanguage()
        {
            SetLanguage(CurrentLanguage == AppLanguage.English ? AppLanguage.Vietnamese : AppLanguage.English);
        }

        public static string Get(string key, string? fallback = null)
        {
            var dict = CurrentLanguage == AppLanguage.English ? _frozenEnStrings : _frozenViStrings;
            if (dict.TryGetValue(key, out string? value))
                return value;

            if (_frozenViStrings.TryGetValue(key, out string? viValue))
                return viValue;

            return fallback ?? key;
        }

        public static string Format(string key, params object?[] args)
        {
            string template = Get(key);
            if (string.Equals(template, key, StringComparison.Ordinal) &&
                args != null && args.Length >= 2 &&
                args[0] is string fallbackTemplate && fallbackTemplate.Contains('{'))
            {
                var rest = new object?[args.Length - 1];
                Array.Copy(args, 1, rest, 0, rest.Length);
                return SafeFormat(fallbackTemplate, rest);
            }
            return SafeFormat(template, args ?? Array.Empty<object?>());
        }

        private static string SafeFormat(string template, object?[] args)
        {
            if (args == null || args.Length == 0)
                return template;

            try
            {
                return string.Format(template, args);
            }
            catch (FormatException ex)
            {
                AppServices.Logger.Debug("[LocalizationService] SafeFormat failed with FormatException", ex);
                string result = template;
                for (int i = 0; i < args.Length; i++)
                {
                    result = result.Replace("{" + i + "}", args[i]?.ToString() ?? string.Empty, StringComparison.Ordinal);
                }
                return result;
            }
        }

        private static void ApplyResources(Application? app)
        {
            if (app?.Resources == null)
                return;

            EnsureCoreKeys();
            var targetDict = CurrentLanguage == AppLanguage.English ? _enResourceDict : _viResourceDict;
            var merged = app.Resources.MergedDictionaries;
            if (ReferenceEquals(_activeResourceDict, targetDict) && merged.Contains(targetDict))
                return;

            if (_activeResourceDict != null)
            {
                merged.Remove(_activeResourceDict);
            }

            merged.Add(targetDict);
            _activeResourceDict = targetDict;
        }
    }

    public static class Loc
    {
        public static string Get(string key, string? fallback = null) => LocalizationService.Get(key, fallback);
        public static string Format(string key, params object?[] args) => LocalizationService.Format(key, args);
    }
}
