#nullable enable
using System;
using System.Threading;

namespace Shiro.Services
{
    public enum UiDensityMode
    {
        Compact = 0,
        Balanced = 1,
        Comfortable = 2
    }

    /// <summary>
    /// Thread-safe visual appearance and accessibility preferences store backed by ISettingsService.
    /// </summary>
    public sealed class VisualPreferencesService
    {
        public static VisualPreferencesService Instance { get; } = new();

        private readonly Lock _lock = new();
        private SurfacePreset _surfacePreset = SurfacePreset.LiquidObsidian;
        private UiDensityMode _densityMode = UiDensityMode.Balanced;
        private bool _enableGlassEffects = true;
        private bool _enableAdaptiveGameTint = true;
        private bool _reducedMotion;
        private bool _sidebarCollapsed;

        private VisualPreferencesService()
        {
        }

        public event Action? PreferencesChanged;

        public SurfacePreset SurfacePreset
        {
            get
            {
                lock (_lock)
                    return _surfacePreset;
            }
            set
            {
                SurfacePreset normalized = Enum.IsDefined(value) ? value : SurfacePreset.LiquidObsidian;
                bool changed;
                lock (_lock)
                {
                    changed = _surfacePreset != normalized;
                    _surfacePreset = normalized;
                }
                if (AccentThemeService.CurrentSurfacePreset != normalized)
                    AccentThemeService.ApplySurfacePreset(normalized, save: false);
                if (changed)
                    PreferencesChanged?.Invoke();
            }
        }

        public UiDensityMode DensityMode
        {
            get
            {
                lock (_lock)
                    return _densityMode;
            }
            set
            {
                UiDensityMode normalized = Enum.IsDefined(value) ? value : UiDensityMode.Balanced;
                bool changed;
                lock (_lock)
                {
                    changed = _densityMode != normalized;
                    _densityMode = normalized;
                }
                if (changed)
                    PreferencesChanged?.Invoke();
            }
        }

        public bool EnableGlassEffects
        {
            get
            {
                lock (_lock)
                    return _enableGlassEffects;
            }
            set
            {
                bool changed;
                lock (_lock)
                {
                    changed = _enableGlassEffects != value;
                    _enableGlassEffects = value;
                }
                if (changed)
                    PreferencesChanged?.Invoke();
            }
        }

        public bool EnableAdaptiveGameTint
        {
            get
            {
                lock (_lock)
                    return _enableAdaptiveGameTint;
            }
            set
            {
                bool changed;
                lock (_lock)
                {
                    changed = _enableAdaptiveGameTint != value;
                    _enableAdaptiveGameTint = value;
                }
                if (changed)
                    PreferencesChanged?.Invoke();
            }
        }

        public bool ReducedMotion
        {
            get
            {
                lock (_lock)
                    return _reducedMotion;
            }
            set
            {
                bool changed;
                lock (_lock)
                {
                    changed = _reducedMotion != value;
                    _reducedMotion = value;
                }
                if (changed)
                    PreferencesChanged?.Invoke();
            }
        }

        public bool SidebarCollapsed
        {
            get
            {
                lock (_lock)
                    return _sidebarCollapsed;
            }
            set
            {
                bool changed;
                lock (_lock)
                {
                    changed = _sidebarCollapsed != value;
                    _sidebarCollapsed = value;
                }
                if (changed)
                    PreferencesChanged?.Invoke();
            }
        }

        public void Load()
        {
            LoadPreferencesOnly();
            SurfacePreset preset = SurfacePreset;
            if (AccentThemeService.CurrentSurfacePreset != preset)
                AccentThemeService.ApplySurfacePreset(preset, save: false);
            PreferencesChanged?.Invoke();
        }

        internal void LoadPreferencesOnly()
        {
            SurfacePreset preset = SurfacePreset.LiquidObsidian;
            UiDensityMode density = UiDensityMode.Balanced;
            bool glass = true;
            bool adaptiveTint = true;
            bool reducedMotion = false;
            bool sidebarCollapsed = false;

            try
            {
                ISettingsService settings = AppServices.GeneralSettings;
                preset = ParseEnum(settings.Get(AppSettingsKeys.General.SurfacePreset), SurfacePreset.LiquidObsidian);
                density = ParseEnum(settings.Get(AppSettingsKeys.General.UiDensityMode), UiDensityMode.Balanced);
                glass = ReadBool(settings, AppSettingsKeys.General.EnableGlassEffects, true);
                adaptiveTint = ReadBool(settings, AppSettingsKeys.General.EnableAdaptiveGameTint, true);
                reducedMotion = ReadBool(settings, AppSettingsKeys.General.ReducedMotion, false);
                sidebarCollapsed = ReadBool(settings, AppSettingsKeys.General.SidebarCollapsed, false);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[VisualPreferencesService] Load failed, using defaults: {ex.Message}");
            }

            lock (_lock)
            {
                _surfacePreset = preset;
                _densityMode = density;
                _enableGlassEffects = glass;
                _enableAdaptiveGameTint = adaptiveTint;
                _reducedMotion = reducedMotion;
                _sidebarCollapsed = sidebarCollapsed;
            }
        }

        public void ApplyAndSave(
            SurfacePreset? surfacePreset = null,
            UiDensityMode? densityMode = null,
            bool? enableGlassEffects = null,
            bool? enableAdaptiveGameTint = null,
            bool? reducedMotion = null,
            bool? sidebarCollapsed = null)
        {
            SurfacePreset effectivePreset;
            UiDensityMode effectiveDensity;
            bool effectiveGlass;
            bool effectiveTint;
            bool effectiveMotion;
            bool effectiveSidebar;

            lock (_lock)
            {
                if (surfacePreset.HasValue)
                    _surfacePreset = Enum.IsDefined(surfacePreset.Value) ? surfacePreset.Value : SurfacePreset.LiquidObsidian;
                if (densityMode.HasValue)
                    _densityMode = Enum.IsDefined(densityMode.Value) ? densityMode.Value : UiDensityMode.Balanced;
                if (enableGlassEffects.HasValue)
                    _enableGlassEffects = enableGlassEffects.Value;
                if (enableAdaptiveGameTint.HasValue)
                    _enableAdaptiveGameTint = enableAdaptiveGameTint.Value;
                if (reducedMotion.HasValue)
                    _reducedMotion = reducedMotion.Value;
                if (sidebarCollapsed.HasValue)
                    _sidebarCollapsed = sidebarCollapsed.Value;

                effectivePreset = _surfacePreset;
                effectiveDensity = _densityMode;
                effectiveGlass = _enableGlassEffects;
                effectiveTint = _enableAdaptiveGameTint;
                effectiveMotion = _reducedMotion;
                effectiveSidebar = _sidebarCollapsed;
            }

            try
            {
                ISettingsService settings = AppServices.GeneralSettings;
                settings.Set(AppSettingsKeys.General.SurfacePreset, effectivePreset.ToString());
                settings.Set(AppSettingsKeys.General.UiDensityMode, effectiveDensity.ToString());
                settings.Set(AppSettingsKeys.General.EnableGlassEffects, effectiveGlass ? "true" : "false");
                settings.Set(AppSettingsKeys.General.EnableAdaptiveGameTint, effectiveTint ? "true" : "false");
                settings.Set(AppSettingsKeys.General.ReducedMotion, effectiveMotion ? "true" : "false");
                settings.Set(AppSettingsKeys.General.SidebarCollapsed, effectiveSidebar ? "true" : "false");
                AppServices.QueueDeferredSaveSettings();
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Warn($"Failed to persist visual preferences: {ex.Message}");
            }

            if (AccentThemeService.CurrentSurfacePreset != effectivePreset)
                AccentThemeService.ApplySurfacePreset(effectivePreset, save: false);

            PreferencesChanged?.Invoke();
        }

        internal void SyncSurfacePresetFromThemeService(SurfacePreset preset)
        {
            SurfacePreset normalized = Enum.IsDefined(preset) ? preset : SurfacePreset.LiquidObsidian;
            bool changed;
            lock (_lock)
            {
                changed = _surfacePreset != normalized;
                _surfacePreset = normalized;
            }
            if (changed)
                PreferencesChanged?.Invoke();
        }

        internal static TEnum ParseEnum<TEnum>(string? raw, TEnum fallback) where TEnum : struct, Enum
        {
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            return Enum.TryParse<TEnum>(raw.Trim(), ignoreCase: true, out TEnum parsed) && Enum.IsDefined(parsed)
                ? parsed
                : fallback;
        }

        internal static bool ReadBool(ISettingsService settings, string key, bool defaultValue)
        {
            string? raw = settings.Get(key);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                string trimmed = raw.Trim();
                if (bool.TryParse(trimmed, out bool parsed))
                    return parsed;
                if (trimmed == "1") return true;
                if (trimmed == "0") return false;
                return defaultValue;
            }

            return settings.Exists(key) ? settings.GetBool(key, defaultValue) : defaultValue;
        }
    }
}
