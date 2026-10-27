#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.Services
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
                    Program.Logger?.Debug($"ProcessPath lookup threw: {ex.Message}");
                }

                string[] args = Environment.GetCommandLineArgs();
                string savedLang = AccountManager.General?.Get<string>(AppSettingsKeys.General.Language) ?? string.Empty;
                CurrentLanguage = ResolveInitialLanguage(procName, args, savedLang);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"ResolveInitialLanguage threw: {ex.Message}");
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
                    AccountManager.General?.Set(AppSettingsKeys.General.Language, language == AppLanguage.English ? "en" : "vi");
                    AccountManager.QueueDeferredSaveSettings();
                }
                catch (Exception ex)
                {
                    Program.Logger?.Warn($"Failed to save Language setting: {ex.Message}");
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

        public static string Format(string key, params object[] args)
        {
            string template = Get(key);
            try
            {
                return string.Format(template, args);
            }
            catch
            {
                return template;
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
        public static string Format(string key, params object[] args) => LocalizationService.Format(key, args);
    }
}
