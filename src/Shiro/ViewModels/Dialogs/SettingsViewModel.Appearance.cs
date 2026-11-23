#nullable enable
using System;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs
{
    public sealed partial class SettingsViewModel
    {
        private bool _isUpdatingAppearance;
        private bool _appearanceSubscribed;
        private Action? _preferencesChangedHandler;
        private Action<AccentTheme>? _themeChangedHandler;
        private Action<SurfacePreset>? _surfacePresetChangedHandler;
        private Action<AppLanguage>? _languageChangedHandler;

        private SurfacePreset _selectedSurfacePreset = SurfacePreset.LiquidObsidian;
        private UiDensityMode _selectedDensityMode = UiDensityMode.Balanced;
        private bool _enableGlassEffects = true;
        private bool _enableAdaptiveGameTint = true;
        private bool _reducedMotion;

        private IBrush _previewCanvasBrush = Brushes.Black;
        private IBrush _previewPanelBrush = Brushes.Black;
        private IBrush _previewCardBrush = Brushes.Black;
        private IBrush _previewElevatedBrush = Brushes.Black;
        private IBrush _previewBorderBrush = Brushes.Gray;
        private IBrush _previewBorderStrongBrush = Brushes.Gray;
        private IBrush _previewTextPrimaryBrush = Brushes.White;
        private IBrush _previewTextSecondaryBrush = Brushes.Silver;
        private IBrush _previewTextMutedBrush = Brushes.Gray;
        private IBrush _previewAccentBrush = Brushes.White;
        private IBrush _previewAccentSoftBrush = Brushes.Transparent;
        private IBrush _previewAccentBorderBrush = Brushes.White;
        private IBrush _previewAccentForegroundBrush = Brushes.Black;
        private IBrush _previewGlassTintBrush = Brushes.Black;
        private IBrush _previewAmbientBrush = Brushes.Transparent;
        private double _previewBlurSigma = 18.0;
        private Thickness _previewCardPadding = new(11);
        private string _previewMotionBadgeText = string.Empty;

        public ICommand SelectSurfacePresetCommand { get; private set; } = null!;
        public ICommand SelectDensityCommand { get; private set; } = null!;

        public SurfacePreset SelectedSurfacePreset
        {
            get => _selectedSurfacePreset;
            set
            {
                SurfacePreset normalized = Enum.IsDefined(value) ? value : SurfacePreset.LiquidObsidian;
                if (!SetProperty(ref _selectedSurfacePreset, normalized)) return;
                NotifySurfacePresetFlags();
                ApplyLiveAppearanceUpdate(() => VisualPreferencesService.Instance.SurfacePreset = normalized);
                RefreshAppearancePreview();
            }
        }

        public bool IsPresetLiquidObsidian => SelectedSurfacePreset == SurfacePreset.LiquidObsidian;
        public bool IsPresetMidnightGlass => SelectedSurfacePreset == SurfacePreset.MidnightGlass;
        public bool IsPresetAmoledVoid => SelectedSurfacePreset == SurfacePreset.AmoledVoid;
        public bool IsPresetWarmGraphite => SelectedSurfacePreset == SurfacePreset.WarmGraphite;

        public UiDensityMode SelectedDensityMode
        {
            get => _selectedDensityMode;
            set
            {
                UiDensityMode normalized = Enum.IsDefined(value) ? value : UiDensityMode.Balanced;
                if (!SetProperty(ref _selectedDensityMode, normalized)) return;
                NotifyDensityFlags();
                ApplyLiveAppearanceUpdate(() => VisualPreferencesService.Instance.DensityMode = normalized);
                RefreshAppearancePreview();
            }
        }

        public bool IsDensityCompact => SelectedDensityMode == UiDensityMode.Compact;
        public bool IsDensityBalanced => SelectedDensityMode == UiDensityMode.Balanced;
        public bool IsDensityComfortable => SelectedDensityMode == UiDensityMode.Comfortable;

        public bool EnableGlassEffects
        {
            get => _enableGlassEffects;
            set
            {
                if (!SetProperty(ref _enableGlassEffects, value)) return;
                ApplyLiveAppearanceUpdate(() => VisualPreferencesService.Instance.EnableGlassEffects = value);
                RefreshAppearancePreview();
            }
        }

        public bool EnableAdaptiveGameTint
        {
            get => _enableAdaptiveGameTint;
            set
            {
                if (!SetProperty(ref _enableAdaptiveGameTint, value)) return;
                ApplyLiveAppearanceUpdate(() => VisualPreferencesService.Instance.EnableAdaptiveGameTint = value);
                OnPropertyChanged(nameof(PreviewAdaptiveBadgeVisible));
                RefreshAppearancePreview();
            }
        }

        public bool ReducedMotion
        {
            get => _reducedMotion;
            set
            {
                if (!SetProperty(ref _reducedMotion, value)) return;
                ApplyLiveAppearanceUpdate(() => VisualPreferencesService.Instance.ReducedMotion = value);
                RefreshAppearancePreview();
            }
        }

        public IBrush PreviewCanvasBrush { get => _previewCanvasBrush; private set => SetProperty(ref _previewCanvasBrush, value); }
        public IBrush PreviewPanelBrush { get => _previewPanelBrush; private set => SetProperty(ref _previewPanelBrush, value); }
        public IBrush PreviewCardBrush { get => _previewCardBrush; private set => SetProperty(ref _previewCardBrush, value); }
        public IBrush PreviewElevatedBrush { get => _previewElevatedBrush; private set => SetProperty(ref _previewElevatedBrush, value); }
        public IBrush PreviewBorderBrush { get => _previewBorderBrush; private set => SetProperty(ref _previewBorderBrush, value); }
        public IBrush PreviewBorderStrongBrush { get => _previewBorderStrongBrush; private set => SetProperty(ref _previewBorderStrongBrush, value); }
        public IBrush PreviewTextPrimaryBrush { get => _previewTextPrimaryBrush; private set => SetProperty(ref _previewTextPrimaryBrush, value); }
        public IBrush PreviewTextSecondaryBrush { get => _previewTextSecondaryBrush; private set => SetProperty(ref _previewTextSecondaryBrush, value); }
        public IBrush PreviewTextMutedBrush { get => _previewTextMutedBrush; private set => SetProperty(ref _previewTextMutedBrush, value); }
        public IBrush PreviewAccentBrush { get => _previewAccentBrush; private set => SetProperty(ref _previewAccentBrush, value); }
        public IBrush PreviewAccentSoftBrush { get => _previewAccentSoftBrush; private set => SetProperty(ref _previewAccentSoftBrush, value); }
        public IBrush PreviewAccentBorderBrush { get => _previewAccentBorderBrush; private set => SetProperty(ref _previewAccentBorderBrush, value); }
        public IBrush PreviewAccentForegroundBrush { get => _previewAccentForegroundBrush; private set => SetProperty(ref _previewAccentForegroundBrush, value); }
        public IBrush PreviewGlassTintBrush { get => _previewGlassTintBrush; private set => SetProperty(ref _previewGlassTintBrush, value); }
        public IBrush PreviewAmbientBrush { get => _previewAmbientBrush; private set => SetProperty(ref _previewAmbientBrush, value); }
        public double PreviewBlurSigma { get => _previewBlurSigma; private set => SetProperty(ref _previewBlurSigma, value); }
        public Thickness PreviewCardPadding { get => _previewCardPadding; private set => SetProperty(ref _previewCardPadding, value); }
        public bool PreviewAdaptiveBadgeVisible => EnableAdaptiveGameTint;
        public string PreviewMotionBadgeText { get => _previewMotionBadgeText; private set => SetProperty(ref _previewMotionBadgeText, value); }

        private bool IsLiveAppearanceMode => ReferenceEquals(_general, AppServices.GeneralSettings);

        private void InitializeAppearance()
        {
            SelectSurfacePresetCommand = new RelayCommand<SurfacePreset>(p => SelectedSurfacePreset = p);
            SelectDensityCommand = new RelayCommand<UiDensityMode>(d => SelectedDensityMode = d);
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SelectedTheme))
                    RefreshAppearancePreview();
            };
            SyncAppearanceFromSource();
        }

        private void LoadAppearance()
        {
            _appearanceSubscribed = true;
            if (_languageChangedHandler == null)
            {
                _languageChangedHandler = WeakEventSubscription.CreateAction<SettingsViewModel, AppLanguage>(
                    this,
                    static (vm, _) => vm.OnExternalLanguageChanged(),
                    static h => LocalizationService.LanguageChanged -= h);
                LocalizationService.LanguageChanged += _languageChangedHandler;
            }

            if (IsLiveAppearanceMode)
            {
                if (_preferencesChangedHandler == null)
                {
                    _preferencesChangedHandler = WeakEventSubscription.CreateAction<SettingsViewModel>(
                        this,
                        static vm => vm.OnVisualPreferencesChanged(),
                        static h => VisualPreferencesService.Instance.PreferencesChanged -= h);
                    VisualPreferencesService.Instance.PreferencesChanged += _preferencesChangedHandler;
                }
                if (_themeChangedHandler == null)
                {
                    _themeChangedHandler = WeakEventSubscription.CreateAction<SettingsViewModel, AccentTheme>(
                        this,
                        static (vm, theme) => vm.OnExternalThemeChanged(theme),
                        static h => AccentThemeService.ThemeChanged -= h);
                    AccentThemeService.ThemeChanged += _themeChangedHandler;
                }
                if (_surfacePresetChangedHandler == null)
                {
                    _surfacePresetChangedHandler = WeakEventSubscription.CreateAction<SettingsViewModel, SurfacePreset>(
                        this,
                        static (vm, _) => vm.OnExternalSurfacePresetChanged(),
                        static h => AccentThemeService.SurfacePresetChanged -= h);
                    AccentThemeService.SurfacePresetChanged += _surfacePresetChangedHandler;
                }
            }
            else
            {
                DetachLiveAppearanceHandlers();
            }

            SyncAppearanceFromSource();
        }

        private void SaveAppearance()
        {
            if (IsLiveAppearanceMode)
            {
                VisualPreferencesService.Instance.ApplyAndSave(
                    surfacePreset: SelectedSurfacePreset,
                    densityMode: SelectedDensityMode,
                    enableGlassEffects: EnableGlassEffects,
                    enableAdaptiveGameTint: EnableAdaptiveGameTint,
                    reducedMotion: ReducedMotion);
                return;
            }

            _general.Set(AppSettingsKeys.General.SurfacePreset, SelectedSurfacePreset.ToString());
            _general.Set(AppSettingsKeys.General.UiDensityMode, SelectedDensityMode.ToString());
            _general.Set(AppSettingsKeys.General.EnableGlassEffects, EnableGlassEffects ? "true" : "false");
            _general.Set(AppSettingsKeys.General.EnableAdaptiveGameTint, EnableAdaptiveGameTint ? "true" : "false");
            _general.Set(AppSettingsKeys.General.ReducedMotion, ReducedMotion ? "true" : "false");
        }

        private void CleanupAppearance()
        {
            _appearanceSubscribed = false;
            DetachLiveAppearanceHandlers();
            if (_languageChangedHandler != null)
            {
                LocalizationService.LanguageChanged -= _languageChangedHandler;
                _languageChangedHandler = null;
            }
        }

        private void DetachLiveAppearanceHandlers()
        {
            if (_preferencesChangedHandler != null)
            {
                VisualPreferencesService.Instance.PreferencesChanged -= _preferencesChangedHandler;
                _preferencesChangedHandler = null;
            }
            if (_themeChangedHandler != null)
            {
                AccentThemeService.ThemeChanged -= _themeChangedHandler;
                _themeChangedHandler = null;
            }
            if (_surfacePresetChangedHandler != null)
            {
                AccentThemeService.SurfacePresetChanged -= _surfacePresetChangedHandler;
                _surfacePresetChangedHandler = null;
            }
        }

        private void ApplyLiveAppearanceUpdate(Action updateAction)
        {
            if (_isUpdatingAppearance || _isLoading || !IsLiveAppearanceMode) return;
            _isUpdatingAppearance = true;
            try { updateAction(); }
            finally { _isUpdatingAppearance = false; }
        }

        private void SyncAppearanceFromSource()
        {
            if (_isUpdatingAppearance) return;
            _isUpdatingAppearance = true;
            try
            {
                SurfacePreset preset;
                UiDensityMode density;
                bool glass;
                bool adaptiveTint;
                bool reducedMotion;

                if (IsLiveAppearanceMode)
                {
                    var prefs = VisualPreferencesService.Instance;
                    preset = prefs.SurfacePreset;
                    density = prefs.DensityMode;
                    glass = prefs.EnableGlassEffects;
                    adaptiveTint = prefs.EnableAdaptiveGameTint;
                    reducedMotion = prefs.ReducedMotion;
                }
                else
                {
                    preset = VisualPreferencesService.ParseEnum(
                        _general.Get(AppSettingsKeys.General.SurfacePreset),
                        SurfacePreset.LiquidObsidian);
                    density = VisualPreferencesService.ParseEnum(
                        _general.Get(AppSettingsKeys.General.UiDensityMode),
                        UiDensityMode.Balanced);
                    glass = VisualPreferencesService.ReadBool(_general, AppSettingsKeys.General.EnableGlassEffects, true);
                    adaptiveTint = VisualPreferencesService.ReadBool(_general, AppSettingsKeys.General.EnableAdaptiveGameTint, true);
                    reducedMotion = VisualPreferencesService.ReadBool(_general, AppSettingsKeys.General.ReducedMotion, false);
                }

                if (SetProperty(ref _selectedSurfacePreset, preset, nameof(SelectedSurfacePreset)))
                    NotifySurfacePresetFlags();
                if (SetProperty(ref _selectedDensityMode, density, nameof(SelectedDensityMode)))
                    NotifyDensityFlags();
                SetProperty(ref _enableGlassEffects, glass, nameof(EnableGlassEffects));
                if (SetProperty(ref _enableAdaptiveGameTint, adaptiveTint, nameof(EnableAdaptiveGameTint)))
                    OnPropertyChanged(nameof(PreviewAdaptiveBadgeVisible));
                SetProperty(ref _reducedMotion, reducedMotion, nameof(ReducedMotion));
            }
            finally { _isUpdatingAppearance = false; }
            RefreshAppearancePreview();
        }

        private void OnVisualPreferencesChanged() => DispatchAppearanceSync(SyncAppearanceFromSource);
        private void OnExternalSurfacePresetChanged() => DispatchAppearanceSync(SyncAppearanceFromSource);
        private void OnExternalThemeChanged(AccentTheme theme) => DispatchAppearanceSync(() =>
        {
            if (SetProperty(ref _selectedTheme, theme, nameof(SelectedTheme)))
                NotifyThemeFlags();
            RefreshAppearancePreview();
        });
        private void OnExternalLanguageChanged() => DispatchAppearanceSync(RefreshAppearancePreview);

        private void DispatchAppearanceSync(Action action)
        {
            if (!_appearanceSubscribed) return;
            if (AppDispatcher.HasUiThreadAccess()) { action(); return; }
            AppDispatcher.Post(() =>
            {
                if (_appearanceSubscribed) action();
            });
        }

        private void NotifySurfacePresetFlags()
        {
            OnPropertyChanged(nameof(IsPresetLiquidObsidian));
            OnPropertyChanged(nameof(IsPresetMidnightGlass));
            OnPropertyChanged(nameof(IsPresetAmoledVoid));
            OnPropertyChanged(nameof(IsPresetWarmGraphite));
        }

        private void NotifyDensityFlags()
        {
            OnPropertyChanged(nameof(IsDensityCompact));
            OnPropertyChanged(nameof(IsDensityBalanced));
            OnPropertyChanged(nameof(IsDensityComfortable));
        }

        private void NotifyThemeFlags()
        {
            OnPropertyChanged(nameof(IsThemeMonochrome));
            OnPropertyChanged(nameof(IsThemeViolet));
            OnPropertyChanged(nameof(IsThemeCyan));
            OnPropertyChanged(nameof(IsThemeEmerald));
            OnPropertyChanged(nameof(IsThemePink));
        }

        private void RefreshAppearancePreview()
        {
            SurfacePalette s = OklchColorMath.DeriveSurfacePalette(SelectedSurfacePreset);
            Color? adaptive = IsLiveAppearanceMode && EnableAdaptiveGameTint ? AccentThemeService.AdaptiveGameColor : null;
            var a = adaptive.HasValue
                ? OklchColorMath.DeriveAdaptiveAccentPalette(adaptive.Value)
                : AccentThemeService.GetExtendedColors(SelectedTheme);

            PreviewCanvasBrush = new ImmutableSolidColorBrush(s.SurfaceCanvas);
            PreviewPanelBrush = new ImmutableSolidColorBrush(s.SurfacePanel);
            PreviewCardBrush = new ImmutableSolidColorBrush(s.SurfaceCard);
            PreviewElevatedBrush = new ImmutableSolidColorBrush(s.SurfaceElevated);
            PreviewBorderBrush = new ImmutableSolidColorBrush(s.BorderSubtle);
            PreviewBorderStrongBrush = new ImmutableSolidColorBrush(s.BorderStrong);
            PreviewTextPrimaryBrush = new ImmutableSolidColorBrush(s.TextPrimary);
            PreviewTextSecondaryBrush = new ImmutableSolidColorBrush(s.TextSecondary);
            PreviewTextMutedBrush = new ImmutableSolidColorBrush(s.TextMuted);
            PreviewAccentBrush = new ImmutableSolidColorBrush(a.Primary);
            PreviewAccentSoftBrush = new ImmutableSolidColorBrush(a.SoftFill);
            PreviewAccentBorderBrush = new ImmutableSolidColorBrush(a.Border);
            PreviewAccentForegroundBrush = new ImmutableSolidColorBrush(a.TextOnAccent);
            PreviewGlassTintBrush = new ImmutableSolidColorBrush(
                Color.FromArgb(EnableGlassEffects ? (byte)214 : (byte)255, s.SurfacePanel.R, s.SurfacePanel.G, s.SurfacePanel.B));
            var center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            var radius = new RelativeScalar(0.55, RelativeUnit.Relative);
            PreviewAmbientBrush = new ImmutableRadialGradientBrush(
                new[]
                {
                    new ImmutableGradientStop(0.0, Color.FromArgb(88, a.Primary.R, a.Primary.G, a.Primary.B)),
                    new ImmutableGradientStop(1.0, Color.FromArgb(0, a.Primary.R, a.Primary.G, a.Primary.B))
                },
                center: center,
                gradientOrigin: center,
                radiusX: radius,
                radiusY: radius);
            PreviewBlurSigma = EnableGlassEffects ? 18.0 : 0.0;
            PreviewCardPadding = SelectedDensityMode switch
            {
                UiDensityMode.Compact => new Thickness(8),
                UiDensityMode.Comfortable => new Thickness(14),
                _ => new Thickness(11)
            };
            PreviewMotionBadgeText = LocalizationService.Get(
                ReducedMotion ? "Set_LivePreviewBadgeMotionInstant" : "Set_LivePreviewBadgeMotionSmooth");
        }
    }
}
