#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using Shiro.AvaloniaUI.Controls;
using Shiro.AvaloniaUI.Views;
using Shiro.AvaloniaUI.Views.Panels;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public sealed class SettingsAppearanceAndDialogsTests : IDisposable
    {
        private readonly ILogSink? _prevSink = Logger.Sink;
        private readonly AppLanguage _origLanguage = LocalizationService.CurrentLanguage;
        private readonly AccentTheme _origTheme = AccentThemeService.CurrentTheme;
        private readonly SurfacePreset _origPreset = VisualPreferencesService.Instance.SurfacePreset;
        private readonly UiDensityMode _origDensity = VisualPreferencesService.Instance.DensityMode;
        private readonly bool _origGlass = VisualPreferencesService.Instance.EnableGlassEffects;
        private readonly bool _origAdaptive = VisualPreferencesService.Instance.EnableAdaptiveGameTint;
        private readonly bool _origReducedMotion = VisualPreferencesService.Instance.ReducedMotion;
        private readonly Color? _origAdaptiveColor = AccentThemeService.AdaptiveGameColor;
        private readonly bool _origStreamerEnabled = StreamerModeService.IsEnabled;
        private readonly bool _origStreamerAutoDetect = StreamerModeService.AutoDetectEnabled;
        private readonly List<IResourceProvider>? _origMerged = Application.Current?.Resources.MergedDictionaries.ToList();

        public void Dispose()
        {
            Logger.Sink = _prevSink;
            StreamerModeService.AutoDetectEnabled = _origStreamerAutoDetect;
            StreamerModeService.IsEnabled = _origStreamerEnabled;
            AccentThemeService.SetAdaptiveGameColor(_origAdaptiveColor);
            VisualPreferencesService.Instance.SurfacePreset = _origPreset;
            VisualPreferencesService.Instance.DensityMode = _origDensity;
            VisualPreferencesService.Instance.EnableGlassEffects = _origGlass;
            VisualPreferencesService.Instance.EnableAdaptiveGameTint = _origAdaptive;
            VisualPreferencesService.Instance.ReducedMotion = _origReducedMotion;
            AccentThemeService.ApplySurfacePreset(_origPreset, save: false);
            AccentThemeService.ApplyTheme(_origTheme, save: false);
            LocalizationService.SetLanguage(_origLanguage, saveSetting: false);

            if (Application.Current?.Resources.MergedDictionaries is { } merged && _origMerged != null)
            {
                merged.Clear();
                foreach (var dict in _origMerged)
                    merged.Add(dict);
            }
        }

        private static ISettingsService CreateInMemorySettings(Dictionary<string, string?> store)
        {
            var mock = Substitute.For<ISettingsService>();
            mock.Get(Arg.Any<string>()).Returns(ci => store.TryGetValue(ci.Arg<string>(), out var v) ? v : null);
            mock.Get(Arg.Any<string>(), Arg.Any<string>()).Returns(ci =>
                store.TryGetValue(ci.ArgAt<string>(0), out var v) && v != null ? v : ci.ArgAt<string>(1));
            mock.Get(Arg.Any<string>(), Arg.Any<bool>()).Returns(ci =>
                store.TryGetValue(ci.ArgAt<string>(0), out var v) && bool.TryParse(v, out var b) ? b : ci.ArgAt<bool>(1));
            mock.Get(Arg.Any<string>(), Arg.Any<int>()).Returns(ci =>
                store.TryGetValue(ci.ArgAt<string>(0), out var v) && int.TryParse(v, out var n) ? n : ci.ArgAt<int>(1));
            mock.Exists(Arg.Any<string>()).Returns(ci => store.ContainsKey(ci.Arg<string>()));
            mock.When(s => s.Set(Arg.Any<string>(), Arg.Any<string?>())).Do(ci =>
            {
                string key = ci.ArgAt<string>(0);
                string? val = ci.ArgAt<string?>(1);
                if (val is null) store.Remove(key);
                else store[key] = val;
            });
            return mock;
        }

        private static void EnsureBaseThemeTokens()
        {
            var app = Application.Current;
            if (app?.Resources == null) return;
            if (!app.Resources.TryGetResource("RadiusOuter16", null, out _))
            {
                var tokens = (ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
                    new Uri("avares://Shiro/AvaloniaUI/Styles/ThemeTokens.axaml"));
                app.Resources.MergedDictionaries.Add(tokens);
            }
        }

        [Fact]
        public void SettingsViewModel_AppearanceControls_UpdateVisualPreferencesAndAccentTheme()
        {
            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var general = CreateInMemorySettings(store);
            using var settingsScope = AppServices.Override<ISettingsService>(general);
            var effects = Substitute.For<ISettingsSideEffects>();
            effects.When(e => e.ApplyTheme(Arg.Any<AccentTheme>(), Arg.Any<bool>()))
                .Do(ci => AccentThemeService.ApplyTheme(ci.ArgAt<AccentTheme>(0), save: false));

            var vm = new SettingsViewModel(new AppSettings(), general, effects);
            try
            {
                vm.LoadCommand.Execute(null);

                vm.SelectSurfacePresetCommand.Execute(SurfacePreset.AmoledVoid);
                Assert.Equal(SurfacePreset.AmoledVoid, vm.SelectedSurfacePreset);
                Assert.True(vm.IsPresetAmoledVoid);
                Assert.False(vm.IsPresetLiquidObsidian);
                Assert.Equal(SurfacePreset.AmoledVoid, VisualPreferencesService.Instance.SurfacePreset);
                Assert.Equal(SurfacePreset.AmoledVoid, AccentThemeService.CurrentSurfacePreset);
                Color amoledCanvas = Assert.IsAssignableFrom<ISolidColorBrush>(vm.PreviewCanvasBrush).Color;

                vm.SelectedSurfacePreset = SurfacePreset.WarmGraphite;
                Assert.Equal(SurfacePreset.WarmGraphite, VisualPreferencesService.Instance.SurfacePreset);
                Assert.True(vm.IsPresetWarmGraphite);
                Color warmCanvas = Assert.IsAssignableFrom<ISolidColorBrush>(vm.PreviewCanvasBrush).Color;
                Assert.NotEqual(amoledCanvas, warmCanvas);

                vm.SelectDensityCommand.Execute(UiDensityMode.Compact);
                Assert.Equal(UiDensityMode.Compact, vm.SelectedDensityMode);
                Assert.True(vm.IsDensityCompact);
                Assert.Equal(new Thickness(8), vm.PreviewCardPadding);

                vm.EnableGlassEffects = false;
                Assert.Equal(0.0, vm.PreviewBlurSigma);

                vm.EnableAdaptiveGameTint = false;
                Assert.False(vm.PreviewAdaptiveBadgeVisible);

                vm.ReducedMotion = true;
                vm.SelectThemeCommand.Execute(AccentTheme.Emerald);

                vm.SaveCommand.Execute(null);
                Assert.Equal(LocalizationService.Get("Settings.SaveSuccess"), vm.SaveFeedback);

                Assert.Equal("WarmGraphite", store[AppSettingsKeys.General.SurfacePreset]);
                Assert.Equal("Compact", store[AppSettingsKeys.General.UiDensityMode]);
                Assert.Equal("false", store[AppSettingsKeys.General.EnableGlassEffects]);
                Assert.Equal("false", store[AppSettingsKeys.General.EnableAdaptiveGameTint]);
                Assert.Equal("true", store[AppSettingsKeys.General.ReducedMotion]);

                VisualPreferencesService.Instance.SurfacePreset = SurfacePreset.LiquidObsidian;
                VisualPreferencesService.Instance.DensityMode = UiDensityMode.Comfortable;
                VisualPreferencesService.Instance.EnableGlassEffects = true;
                VisualPreferencesService.Instance.EnableAdaptiveGameTint = true;
                VisualPreferencesService.Instance.ReducedMotion = false;

                VisualPreferencesService.Instance.Load();
                var reloaded = new SettingsViewModel(new AppSettings(), general, effects);
                try
                {
                    reloaded.LoadCommand.Execute(null);
                    Assert.Equal(SurfacePreset.WarmGraphite, reloaded.SelectedSurfacePreset);
                    Assert.Equal(UiDensityMode.Compact, reloaded.SelectedDensityMode);
                    Assert.False(reloaded.EnableGlassEffects);
                    Assert.False(reloaded.EnableAdaptiveGameTint);
                    Assert.True(reloaded.ReducedMotion);
                }
                finally
                {
                    reloaded.OnClosing();
                }
            }
            finally
            {
                vm.OnClosing();
            }
        }

        [AvaloniaFact]
        public void SettingsView_And_AppearancePanel_RenderLivePreviewAndPreserveContracts_WithZeroBindingErrors()
        {
            EnsureBaseThemeTokens();
            var sink = new BindingErrorSink();
            Logger.Sink = sink;

            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var general = CreateInMemorySettings(store);
            using var settingsScope = AppServices.Override<ISettingsService>(general);

            Window? window = null;
            SettingsView? view = null;
            try
            {
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                var vm = new SettingsViewModel(new AppSettings(), general, SettingsSideEffectsService.Default);
                view = new SettingsView(vm);
                window = new Window { Width = 1040, Height = 760, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var cmbLanguage = view.FindControl<ComboBox>("CmbLanguage");
                Assert.NotNull(cmbLanguage);
                Assert.Equal(205.0, cmbLanguage!.Width);

                var panel = view.GetVisualDescendants().OfType<SettingsAppearancePanel>().FirstOrDefault();
                Assert.NotNull(panel);

                var previewSurface = panel!.FindControl<SkiaGlassSurface>("AppearanceLivePreviewCard");
                var btnAmoled = panel.FindControl<Button>("BtnPresetAmoledVoid");
                var btnMidnight = panel.FindControl<Button>("BtnPresetMidnightGlass");
                var btnWarm = panel.FindControl<Button>("BtnPresetWarmGraphite");
                var btnLiquid = panel.FindControl<Button>("BtnPresetLiquidObsidian");
                var btnCompact = panel.FindControl<Button>("BtnDensityCompact");
                var btnBalanced = panel.FindControl<Button>("BtnDensityBalanced");
                var btnComfortable = panel.FindControl<Button>("BtnDensityComfortable");
                var btnThemeViolet = panel.FindControl<Button>("BtnThemeViolet");
                var swGlass = panel.FindControl<ToggleSwitch>("SwEnableGlassEffects");
                var swAdaptive = panel.FindControl<ToggleSwitch>("SwEnableAdaptiveGameTint");
                var swMotion = panel.FindControl<ToggleSwitch>("SwReducedMotion");

                Assert.NotNull(previewSurface);
                Assert.NotNull(btnAmoled);
                Assert.NotNull(btnMidnight);
                Assert.NotNull(btnWarm);
                Assert.NotNull(btnLiquid);
                Assert.NotNull(btnCompact);
                Assert.NotNull(btnBalanced);
                Assert.NotNull(btnComfortable);
                Assert.NotNull(btnThemeViolet);
                Assert.NotNull(swGlass);
                Assert.NotNull(swAdaptive);
                Assert.NotNull(swMotion);

                btnAmoled!.Command?.Execute(btnAmoled.CommandParameter);
                btnCompact!.Command?.Execute(btnCompact.CommandParameter);
                btnThemeViolet!.Command?.Execute(btnThemeViolet.CommandParameter);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(SurfacePreset.AmoledVoid, vm.SelectedSurfacePreset);
                Assert.Equal(UiDensityMode.Compact, vm.SelectedDensityMode);
                Assert.Equal(AccentTheme.Violet, vm.SelectedTheme);
                Assert.Equal(new CornerRadius(8), previewSurface!.CornerRadius);

                btnMidnight!.Command?.Execute(btnMidnight.CommandParameter);
                btnComfortable!.Command?.Execute(btnComfortable.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(SurfacePreset.MidnightGlass, vm.SelectedSurfacePreset);
                Assert.Equal(UiDensityMode.Comfortable, vm.SelectedDensityMode);
                Assert.Equal(new Thickness(14), vm.PreviewCardPadding);

                swGlass!.IsChecked = false;
                swMotion!.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0.0, previewSurface.BlurSigma);

                vm.OnClosing();
                VisualPreferencesService.Instance.DensityMode = UiDensityMode.Balanced;
                Assert.Equal(UiDensityMode.Comfortable, vm.SelectedDensityMode);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(sink.Errors);
            }
            finally
            {
                window?.Close();
            }
        }

        [AvaloniaFact]
        public void AllEightDialogs_ConstructAndBind_WithZeroBindingErrors_AndPreservedNames()
        {
            EnsureBaseThemeTokens();
            var sink = new BindingErrorSink();
            Logger.Sink = sink;

            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(new List<Account>());
            using var repoScope = AppServices.Override<IAccountRepository>(repo);

            var account = new Account { Username = "ConcentricSmokeUser", UserID = 123456, Valid = true };

            var msgDlg = new MessageBoxDialog();
            var inputDlg = new InputDialog();
            var bulkDlg = new BulkImportDialog();
            var proxyDlg = new ProxyManagerDialog();
            var fieldsDlg = new AccountFieldsDialog(account);
            var utilsDlg = new AccountUtilsDialog(account);
            var missingDlg = new MissingAssetsDialog(account, new long[] { 101 });
            var fastDlg = new FastLoginDialog(account, startInCookieMode: true);

            var dialogs = new Window[]
            {
                msgDlg, inputDlg, bulkDlg, proxyDlg, fieldsDlg, utilsDlg, missingDlg, fastDlg
            };

            try
            {
                foreach (var dlg in dialogs)
                {
                    dlg.Show();
                }
                Dispatcher.UIThread.RunJobs();

                Assert.NotNull(msgDlg.FindControl<Border>("IconBadge"));
                Assert.NotNull(msgDlg.FindControl<TextBlock>("TxtTitle"));
                Assert.NotNull(msgDlg.FindControl<SelectableTextBlock>("TxtMessage"));
                Assert.NotNull(msgDlg.FindControl<Button>("BtnYes"));
                Assert.NotNull(msgDlg.FindControl<Button>("BtnNo"));
                Assert.NotNull(msgDlg.FindControl<Button>("BtnOK"));
                Assert.NotNull(msgDlg.FindControl<Button>("BtnCancel"));

                Assert.NotNull(inputDlg.FindControl<TextBlock>("TxtTitle"));
                Assert.NotNull(inputDlg.FindControl<TextBox>("TxtInput"));
                Assert.NotNull(inputDlg.FindControl<Button>("BtnOK"));
                Assert.NotNull(inputDlg.FindControl<Button>("BtnCancel"));

                Assert.NotNull(bulkDlg.FindControl<TextBox>("TxtImportData"));
                Assert.NotNull(bulkDlg.FindControl<Button>("BtnStartImport"));
                Assert.NotNull(bulkDlg.FindControl<TextBlock>("TxtStatus"));
                Assert.NotNull(bulkDlg.FindControl<Button>("BtnClear"));

                Assert.NotNull(proxyDlg.FindControl<TextBox>("TxtNewProxy"));
                Assert.NotNull(proxyDlg.FindControl<Button>("BtnAddProxy"));
                Assert.NotNull(proxyDlg.FindControl<ListBox>("LstProxies"));
                Assert.NotNull(proxyDlg.FindControl<Button>("BtnTestAll"));
                Assert.NotNull(proxyDlg.FindControl<Button>("BtnRemoveDead"));
                Assert.NotNull(proxyDlg.FindControl<TextBlock>("TxtStatus"));
                Assert.NotNull(proxyDlg.FindControl<Button>("BtnSaveClose"));

                Assert.NotNull(fieldsDlg.FindControl<ListBox>("FieldsListBox"));
                Assert.NotNull(fieldsDlg.FindControl<TextBlock>("TxtHeaderTitle"));
                Assert.NotNull(fieldsDlg.FindControl<TextBox>("TxtNewKey"));

                Assert.NotNull(utilsDlg.FindControl<TextBlock>("TxtTitle"));
                Assert.NotNull(utilsDlg.FindControl<TextBlock>("TxtDisplayName"));
                Assert.NotNull(utilsDlg.FindControl<TextBox>("TxtPassword"));
                Assert.NotNull(utilsDlg.FindControl<TextBlock>("TxtStatus"));

                Assert.NotNull(missingDlg.FindControl<TextBlock>("TxtTitle"));
                Assert.NotNull(missingDlg.FindControl<ListBox>("AssetsListBox"));
                Assert.NotNull(missingDlg.FindControl<TextBlock>("TxtStatus"));
                Assert.NotNull(missingDlg.FindControl<TextBlock>("TxtTotalPrice"));

                Assert.NotNull(fastDlg.FindControl<Grid>("PnlInApp"));
                Assert.NotNull(fastDlg.FindControl<ScrollViewer>("PnlCookie"));
                Assert.NotNull(fastDlg.FindControl<Button>("BtnVerify"));
                Assert.NotNull(fastDlg.FindControl<Button>("BtnAddCookie"));
                Assert.NotNull(fastDlg.FindControl<TextBlock>("TxtTip"));
                Assert.NotNull(fastDlg.FindControl<TextBlock>("TxtPreviewUsername"));
                Assert.NotNull(fastDlg.FindControl<TextBox>("TxtCookieAlias"));
                Assert.NotNull(fastDlg.FindControl<TextBox>("TxtCookieGroup"));
                Assert.NotNull(fastDlg.FindControl<TextBlock>("TxtDialogTitle"));

                foreach (var dlg in dialogs)
                {
                    bool hasOuter16 = dlg.GetVisualDescendants()
                        .OfType<Border>()
                        .Any(b => b.CornerRadius == new CornerRadius(16));
                    Assert.True(hasOuter16, $"{dlg.GetType().Name} must expose a 16px outer concentric container.");
                }

                Assert.Empty(sink.Errors);
            }
            finally
            {
                foreach (var dlg in dialogs)
                {
                    try { dlg.Close(); }
                    catch (InvalidOperationException) { }
                }
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public void FastLoginDialog_RemainsResilient_WhenMergedDictionariesCleared()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;

            Application.Current?.Resources.MergedDictionaries.Clear();

            FastLoginDialog? dlg = null;
            try
            {
                dlg = new FastLoginDialog(null, startInCookieMode: true);
                dlg.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.True(dlg.IsCookieModeActive);
                Assert.Equal(FastLoginDialog.CookieModeHeight, dlg.Height);

                dlg.SelectLoginMode(isCookieMode: false);
                Dispatcher.UIThread.RunJobs();
                Assert.False(dlg.IsCookieModeActive);
                Assert.Equal(FastLoginDialog.InAppModeHeight, dlg.Height);

                dlg.SelectLoginMode(isCookieMode: true);
                Dispatcher.UIThread.RunJobs();
                Assert.True(dlg.IsCookieModeActive);
                Assert.Empty(sink.Errors);
            }
            finally
            {
                dlg?.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [Fact]
        public void SettingsViewModel_IsolatedGeneralService_LoadsAndSavesWithoutMutatingGlobalPreferences()
        {
            var globalStore = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var globalGeneral = CreateInMemorySettings(globalStore);
            using var settingsScope = AppServices.Override<ISettingsService>(globalGeneral);

            VisualPreferencesService.Instance.SurfacePreset = SurfacePreset.LiquidObsidian;
            VisualPreferencesService.Instance.DensityMode = UiDensityMode.Balanced;
            VisualPreferencesService.Instance.EnableGlassEffects = true;
            VisualPreferencesService.Instance.EnableAdaptiveGameTint = true;
            VisualPreferencesService.Instance.ReducedMotion = false;
            AccentThemeService.ApplySurfacePreset(SurfacePreset.LiquidObsidian, save: false);

            var isolatedStore = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [AppSettingsKeys.General.SurfacePreset] = "MidnightGlass",
                [AppSettingsKeys.General.UiDensityMode] = "Comfortable",
                [AppSettingsKeys.General.EnableGlassEffects] = "false",
                [AppSettingsKeys.General.EnableAdaptiveGameTint] = "false",
                [AppSettingsKeys.General.ReducedMotion] = "true"
            };
            var isolatedGeneral = CreateInMemorySettings(isolatedStore);
            var fakeEffects = Substitute.For<ISettingsSideEffects>();

            using var vm = new SettingsViewModel(new AppSettings(), isolatedGeneral, fakeEffects);
            vm.Load();

            Assert.Equal(SurfacePreset.MidnightGlass, vm.SelectedSurfacePreset);
            Assert.Equal(UiDensityMode.Comfortable, vm.SelectedDensityMode);
            Assert.False(vm.EnableGlassEffects);
            Assert.False(vm.EnableAdaptiveGameTint);
            Assert.True(vm.ReducedMotion);
            Assert.Equal(SurfacePreset.LiquidObsidian, VisualPreferencesService.Instance.SurfacePreset);
            Assert.Equal(UiDensityMode.Balanced, VisualPreferencesService.Instance.DensityMode);

            vm.SelectedSurfacePreset = SurfacePreset.AmoledVoid;
            vm.SelectedDensityMode = UiDensityMode.Compact;
            vm.EnableGlassEffects = true;
            vm.EnableAdaptiveGameTint = false;
            vm.ReducedMotion = false;

            Assert.Equal(SurfacePreset.LiquidObsidian, VisualPreferencesService.Instance.SurfacePreset);
            Assert.Equal(SurfacePreset.LiquidObsidian, AccentThemeService.CurrentSurfacePreset);
            Assert.Equal(UiDensityMode.Balanced, VisualPreferencesService.Instance.DensityMode);

            vm.Save();

            Assert.Equal("AmoledVoid", isolatedStore[AppSettingsKeys.General.SurfacePreset]);
            Assert.Equal("Compact", isolatedStore[AppSettingsKeys.General.UiDensityMode]);
            Assert.Equal("true", isolatedStore[AppSettingsKeys.General.EnableGlassEffects]);
            Assert.Equal("false", isolatedStore[AppSettingsKeys.General.EnableAdaptiveGameTint]);
            Assert.Equal("false", isolatedStore[AppSettingsKeys.General.ReducedMotion]);
            Assert.False(globalStore.ContainsKey(AppSettingsKeys.General.SurfacePreset));
            Assert.False(globalStore.ContainsKey(AppSettingsKeys.General.UiDensityMode));
            Assert.Equal(SurfacePreset.LiquidObsidian, VisualPreferencesService.Instance.SurfacePreset);
            Assert.True(VisualPreferencesService.Instance.EnableAdaptiveGameTint);
        }

        [AvaloniaFact]
        public void SettingsViewModel_DisposeAndSettingsViewDetach_UnsubscribeStaticEventsAndAllowGc()
        {
            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var general = CreateInMemorySettings(store);
            using var settingsScope = AppServices.Override<ISettingsService>(general);
            var effects = Substitute.For<ISettingsSideEffects>();

            StreamerModeService.AutoDetectEnabled = false;
            StreamerModeService.IsEnabled = false;
            VisualPreferencesService.Instance.SurfacePreset = SurfacePreset.LiquidObsidian;
            VisualPreferencesService.Instance.DensityMode = UiDensityMode.Balanced;

            var vm = new SettingsViewModel(new AppSettings(), general, effects);
            vm.Load();
            int postDisposeNotifications = 0;
            vm.PropertyChanged += (_, _) => postDisposeNotifications++;
            vm.Dispose();

            VisualPreferencesService.Instance.SurfacePreset = SurfacePreset.AmoledVoid;
            VisualPreferencesService.Instance.DensityMode = UiDensityMode.Compact;
            AccentThemeService.ApplyTheme(AccentTheme.Cyan, save: false);
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
            StreamerModeService.IsEnabled = true;
            Assert.Equal(0, postDisposeNotifications);

            Window? window = null;
            SettingsView? view = null;
            try
            {
                view = new SettingsView(new SettingsViewModel(new AppSettings(), general, effects));
                window = new Window { Width = 960, Height = 720, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                int postDetachNotifications = 0;
                view.ViewModel.PropertyChanged += (_, _) => postDetachNotifications++;
                view.IsVisible = false;
                Dispatcher.UIThread.RunJobs();

                VisualPreferencesService.Instance.DensityMode = UiDensityMode.Comfortable;
                AccentThemeService.ApplyTheme(AccentTheme.Pink, save: false);
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                StreamerModeService.IsEnabled = false;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, postDetachNotifications);
            }
            finally
            {
                window?.Close();
                Dispatcher.UIThread.RunJobs();
            }

            WeakReference<SettingsViewModel> weakVm = CreateUndisposedLoadedSettingsVmWeak(general);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            VisualPreferencesService.Instance.DensityMode = UiDensityMode.Balanced;
            AccentThemeService.ApplyTheme(AccentTheme.Violet, save: false);
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
            StreamerModeService.IsEnabled = true;

            Assert.False(weakVm.TryGetTarget(out _));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference<SettingsViewModel> CreateUndisposedLoadedSettingsVmWeak(ISettingsService general)
        {
            var vm = new SettingsViewModel(new AppSettings(), general, Substitute.For<ISettingsSideEffects>());
            vm.Load();
            return new WeakReference<SettingsViewModel>(vm);
        }
    }
}
