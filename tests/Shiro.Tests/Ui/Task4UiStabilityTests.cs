#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using Shiro.AvaloniaUI.Controls;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public sealed class Task4UiStabilityTests : IDisposable
    {
        private readonly bool _origStreamerEnabled = StreamerModeService.IsEnabled;
        private readonly bool _origStreamerAutoDetect = StreamerModeService.AutoDetectEnabled;
        private readonly string? _origStreamerSetting = AppServices.GeneralSettings.Get<string>(AppSettingsKeys.General.StreamerModeEnabled);
        private readonly string? _origHideUsernamesSetting = AppServices.GeneralSettings.Get<string>(AppSettingsKeys.General.HideUsernames);
        private readonly Action<Action>? _origDispatcher = AppDispatcher.CustomDispatcher;
        private readonly AccentTheme _origTheme = AccentThemeService.CurrentTheme;
        private readonly AppLanguage _origLanguage = LocalizationService.CurrentLanguage;

        public void Dispose()
        {
            AppDispatcher.CustomDispatcher = _origDispatcher;
            StreamerModeService.IsEnabled = _origStreamerEnabled;
            StreamerModeService.AutoDetectEnabled = _origStreamerAutoDetect;
            RestoreSetting(AppSettingsKeys.General.StreamerModeEnabled, _origStreamerSetting);
            RestoreSetting(AppSettingsKeys.General.HideUsernames, _origHideUsernamesSetting);
            AccentThemeService.ApplyTheme(_origTheme, save: false);
            LocalizationService.SetLanguage(_origLanguage, saveSetting: false);
            ToastService.ClearAll();
            Application.Current?.Resources.MergedDictionaries.Clear();
        }

        private static void RestoreSetting(string key, string? previous)
        {
            if (previous is null)
                AppServices.GeneralSettings.RemoveProperty(key);
            else
                AppServices.GeneralSettings.Set(key, previous);
        }

        private static List<Window> GetTrackedStreamerWindows()
        {
            var field = typeof(StreamerModeService).GetField(
                "_registeredWindows",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            var list = (List<WeakReference<Window>>)field!.GetValue(null)!;
            var result = new List<Window>();
            foreach (var wr in list)
            {
                if (wr.TryGetTarget(out var win) && win != null)
                    result.Add(win);
            }
            return result;
        }

        [AvaloniaFact]
        public void AllNineDialogs_RegisterWithStreamerModeService_OnOpen_AndUnregisterOnClose()
        {
            var repo = Substitute.For<IAccountRepository>();
            repo.Count.Returns(1);
            using var repoScope = AppServices.Override<IAccountRepository>(repo);

            var account = new Account { Username = "StreamerDlgUser", UserID = 12345 };
            var dialogs = new Window[]
            {
                new AboutDialog(),
                new AccountFieldsDialog(account),
                new AccountUtilsDialog(account),
                new BulkImportDialog(),
                new FastLoginDialog(),
                new InputDialog(),
                new MessageBoxDialog(),
                new MissingAssetsDialog(account, new long[] { 101 }),
                new ProxyManagerDialog()
            };

            try
            {
                var tracked = GetTrackedStreamerWindows();
                foreach (var dlg in dialogs)
                {
                    Assert.Contains(dlg, tracked);
                    dlg.Show();
                }
                Dispatcher.UIThread.RunJobs();

                foreach (var dlg in dialogs)
                {
                    dlg.Close();
                }
                Dispatcher.UIThread.RunJobs();

                var trackedAfterClose = GetTrackedStreamerWindows();
                foreach (var dlg in dialogs)
                {
                    Assert.DoesNotContain(dlg, trackedAfterClose);
                }
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

        [Fact]
        public void StreamerMode_MasksUsernameAliasUserIdRobux_AcrossViewModels()
        {
            var ini = new IniFile();
            var gen = ini["General"];
            gen.Set(AppSettingsKeys.General.StreamerModeHideUsernames, "true");
            gen.Set(AppSettingsKeys.General.StreamerModeHideRobux, "true");
            using var settingsScope = AppServices.Override<ISettingsService>(gen);

            StreamerModeService.AutoDetectEnabled = false;
            StreamerModeService.IsEnabled = true;

            var acc = new Account
            {
                Username = "SecretPlayerOne",
                Alias = "MainTrader",
                UserID = 99887766,
                RobuxBalance = 12500,
                Valid = true,
                SecurityToken = "tok"
            };

            var accVm = new AccountViewModel(acc, settings: gen);
            Assert.DoesNotContain("SecretPlayerOne", accVm.PrimaryTitle, StringComparison.Ordinal);
            Assert.DoesNotContain("MainTrader", accVm.PrimaryTitle, StringComparison.Ordinal);
            Assert.DoesNotContain("99887766", accVm.SecondaryTitle, StringComparison.Ordinal);
            Assert.DoesNotContain("SecretPlayerOne", accVm.SecondaryTitle, StringComparison.Ordinal);
            Assert.DoesNotContain("MainTrader", accVm.DisplayTitle, StringComparison.Ordinal);
            Assert.DoesNotContain("99887766", accVm.DisplayTitle, StringComparison.Ordinal);
            Assert.DoesNotContain("12,500", accVm.RobuxTooltip, StringComparison.Ordinal);
            Assert.DoesNotContain("12500", accVm.RobuxTooltip, StringComparison.Ordinal);

            var queue = new FastLoginQueue(new[] { acc }, _ => { });
            Assert.DoesNotContain("SecretPlayerOne", queue.Title, StringComparison.Ordinal);

            var prompt = Substitute.For<IUserPrompt>();
            var utilsVm = new AccountUtilsViewModel(acc, prompt: prompt);
            Assert.DoesNotContain("SecretPlayerOne", utilsVm.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("SecretPlayerOne", utilsVm.DisplayName, StringComparison.Ordinal);

            var missingVm = new MissingAssetsViewModel(acc, new long[] { 55 }, prompt: prompt);
            Assert.DoesNotContain("SecretPlayerOne", missingVm.Title, StringComparison.Ordinal);

            using var mainVm = new MainViewModel();
            mainVm.LoadAccounts(new[] { acc });
            Assert.DoesNotContain("12,500", mainVm.TotalRobuxText, StringComparison.Ordinal);
            Assert.DoesNotContain("12500", mainVm.TotalRobuxText, StringComparison.Ordinal);
            StreamerModeService.IsEnabled = _origStreamerEnabled;
        }

        [AvaloniaFact]
        public async Task StreamerMode_MasksAccountFieldsHeader_AndCookieImportPreview_AndUtilsConfirmPrompt()
        {
            using var settingsScope = AppServices.Override<ISettingsService>(new IniFile()["General"]);
            try
            {
                StreamerModeService.AutoDetectEnabled = false;
                StreamerModeService.IsEnabled = true;

                var acc = new Account { Username = "SensitiveAccount", UserID = 55443322, Valid = true };
                var fieldsDlg = new AccountFieldsDialog(acc);
                try
                {
                    fieldsDlg.Show();
                    Dispatcher.UIThread.RunJobs();
                    var header = fieldsDlg.FindControl<TextBlock>("TxtHeaderTitle");
                    Assert.NotNull(header);
                    Assert.DoesNotContain("SensitiveAccount", header!.Text ?? string.Empty, StringComparison.Ordinal);
                }
                finally
                {
                    fieldsDlg.Close();
                }

                var repo = Substitute.For<IAccountRepository>();
                var cookieVm = new CookieImportViewModel(repo, _ => Task.FromResult(acc));
                cookieVm.Cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--tok";
                await cookieVm.VerifyCommand.ExecuteAsync();
                Assert.DoesNotContain("SensitiveAccount", cookieVm.PreviewUsername, StringComparison.Ordinal);
                Assert.DoesNotContain("55443322", cookieVm.PreviewUsername, StringComparison.Ordinal);

                string? capturedConfirm = null;
                var prompt = Substitute.For<IUserPrompt>();
                prompt.ConfirmAsync(Arg.Do<string>(msg => capturedConfirm = msg), Arg.Any<string>())
                    .Returns(false);
                var utilsVm = new AccountUtilsViewModel(acc, repo, prompt);
                await utilsVm.LogOutOtherSessionsAsync();
                Assert.NotNull(capturedConfirm);
                Assert.DoesNotContain("SensitiveAccount", capturedConfirm!, StringComparison.Ordinal);
            }
            finally
            {
                StreamerModeService.IsEnabled = _origStreamerEnabled;
            }
        }

        [Fact]
        public void SettingsViewModel_HideUsernames_LiveAppliesStreamerMode()
        {
            var general = new IniSection("General");
            using var settingsScope = AppServices.Override<ISettingsService>(general);
            var vm = new SettingsViewModel(new AppSettings(), general, SettingsSideEffectsService.Default);
            try
            {
                StreamerModeService.AutoDetectEnabled = false;
                StreamerModeService.IsEnabled = false;
                vm.LoadCommand.Execute(null);

                vm.HideUsernames = true;

                Assert.True(StreamerModeService.IsEnabled);

                vm.HideUsernames = false;

                Assert.False(StreamerModeService.IsEnabled);
            }
            finally
            {
                vm.OnClosing();
                StreamerModeService.IsEnabled = _origStreamerEnabled;
            }
        }

        [AvaloniaFact]
        public async Task AccentThemeService_ApplyTheme_OffUiThread_SwapsDictionariesBeforeThemeChangedFires()
        {
            Application.Current?.Resources.MergedDictionaries.Clear();
            AccentThemeService.ApplyTheme(AccentTheme.Monochrome, save: false);

            var app = Application.Current!;
            Color? observedColorAtEvent = null;
            var eventFiredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnThemeChanged(AccentTheme theme)
            {
                if (theme == AccentTheme.Cyan)
                {
                    if (app.Resources.TryGetResource("AccentBrush", null, out object? val) && val is ISolidColorBrush b)
                        observedColorAtEvent = b.Color;
                    eventFiredTcs.TrySetResult(true);
                }
            }

            AccentThemeService.ThemeChanged += OnThemeChanged;
            try
            {
                await Task.Run(() => AccentThemeService.ApplyTheme(AccentTheme.Cyan, save: false));
                Dispatcher.UIThread.RunJobs();
                await eventFiredTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.NotNull(observedColorAtEvent);
                Assert.Equal(AccentThemeService.GetThemeColors(AccentTheme.Cyan).primary, observedColorAtEvent!.Value);
            }
            finally
            {
                AccentThemeService.ThemeChanged -= OnThemeChanged;
            }
        }

        [Fact]
        public void WebView2Control_CleanupOrphanedProfiles_RemovesOnlyStaleShiroProfiles()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "ShiroWvCleanup_" + Guid.NewGuid().ToString("N"));
            string profilesRoot = Path.Combine(tempRoot, "EBWebView");
            string staleOwnedGuidDir = Path.Combine(profilesRoot, "Shiro_" + Guid.NewGuid().ToString("N"));
            string freshOwnedGuidDir = Path.Combine(profilesRoot, "Shiro_" + Guid.NewGuid().ToString("N"));
            string defaultDir = Path.Combine(profilesRoot, "Default");

            try
            {
                Directory.CreateDirectory(staleOwnedGuidDir);
                Directory.CreateDirectory(freshOwnedGuidDir);
                Directory.CreateDirectory(defaultDir);

                DateTime staleUtc = DateTime.UtcNow.AddHours(-3);
                Directory.SetLastWriteTimeUtc(staleOwnedGuidDir, staleUtc);
                Directory.SetLastWriteTimeUtc(defaultDir, staleUtc);

                WebView2Control.CleanupOrphanedProfiles(tempRoot);

                Assert.False(Directory.Exists(staleOwnedGuidDir));
                Assert.True(Directory.Exists(freshOwnedGuidDir));
                Assert.True(Directory.Exists(defaultDir));
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    try { Directory.Delete(tempRoot, true); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public void ViewStyles_And_AccentThemes_EnforceContrastAndSelectorOrder()
        {
            string viewStylesPath = Path.Combine(ArchitectureTests.RepoRoot, "src", "Shiro", "AvaloniaUI", "Styles", "ViewStyles.axaml");
            string viewStyles = File.ReadAllText(viewStylesPath);

            int subTabHover = viewStyles.IndexOf("RadioButton.SubTabPill:pointerover", StringComparison.Ordinal);
            int subTabChecked = viewStyles.IndexOf("RadioButton.SubTabPill:checked", StringComparison.Ordinal);
            Assert.True(subTabHover >= 0 && subTabChecked >= 0 && subTabHover < subTabChecked,
                "SubTabPill:pointerover must appear before :checked so active tab does not lose highlight on hover.");

            int settingsHover = viewStyles.IndexOf("RadioButton.SettingsTabPill:pointerover", StringComparison.Ordinal);
            int settingsChecked = viewStyles.IndexOf("RadioButton.SettingsTabPill:checked", StringComparison.Ordinal);
            Assert.True(settingsHover >= 0 && settingsChecked >= 0 && settingsHover < settingsChecked,
                "SettingsTabPill:pointerover must appear before :checked.");

            int filterHover = viewStyles.IndexOf("RadioButton.FilterTabPill:pointerover", StringComparison.Ordinal);
            int filterChecked = viewStyles.IndexOf("RadioButton.FilterTabPill:checked", StringComparison.Ordinal);
            Assert.True(filterHover >= 0 && filterChecked >= 0 && filterHover < filterChecked,
                "FilterTabPill:pointerover must appear before :checked.");

            string settingsViewPath = Path.Combine(ArchitectureTests.RepoRoot, "src", "Shiro", "AvaloniaUI", "Views", "SettingsView.axaml");
            string settingsView = File.ReadAllText(settingsViewPath);
            Assert.DoesNotContain("StrokeBrush=\"#09090B\"", settingsView, StringComparison.Ordinal);

            foreach (AccentTheme theme in Enum.GetValues<AccentTheme>())
            {
                var (primary, _, textOnAccent, _, _, _) = AccentThemeService.GetThemeColors(theme);
                double contrast = ComputeContrastRatio(primary, textOnAccent);
                Assert.True(contrast >= 4.5, string.Concat("AccentTheme ", theme.ToString(), " contrast ratio ", contrast.ToString("F2"), " < 4.5 WCAG AA"));
            }
        }

        [AvaloniaFact]
        public void App_HandleUnhandledAsyncCommandError_ShowsToastSafely_AndFastLoginCookieBoxMasksUnderStreamerMode()
        {
            using var settingsScope = AppServices.Override<ISettingsService>(new IniFile()["General"]);
            try
            {
                ToastService.ClearAll();
                StreamerModeService.AutoDetectEnabled = false;
                StreamerModeService.IsEnabled = true;

                Shiro.AvaloniaUI.App.HandleUnhandledAsyncCommandError(new InvalidOperationException("command failure"));
                Dispatcher.UIThread.RunJobs();
                Assert.Single(ToastService.ActiveToasts);
                Assert.Contains("command failure", ToastService.ActiveToasts[0].Message, StringComparison.Ordinal);

                var dlg = new FastLoginDialog(null, startInCookieMode: true);
                try
                {
                    dlg.Show();
                    Dispatcher.UIThread.RunJobs();
                    var txtCookie = dlg.FindControl<TextBox>("TxtCookie");
                    Assert.NotNull(txtCookie);
                    Assert.Equal('*', txtCookie!.PasswordChar);

                    StreamerModeService.IsEnabled = false;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal('\0', txtCookie.PasswordChar);
                }
                finally
                {
                    dlg.Close();
                }
            }
            finally
            {
                StreamerModeService.IsEnabled = _origStreamerEnabled;
            }
        }

        [AvaloniaFact]
        public async Task LocalizationService_SetLanguage_OffUiThread_SwapsDictionariesBeforeLanguageChangedFires()
        {
            Application.Current?.Resources.MergedDictionaries.Clear();
            LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);

            var app = Application.Current!;
            string? observedTitleAtEvent = null;
            var firedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnLanguageChanged(AppLanguage lang)
            {
                if (lang == AppLanguage.English)
                {
                    if (app.Resources.TryGetResource("Btn_OK", null, out object? val) && val is string s)
                        observedTitleAtEvent = s;
                    firedTcs.TrySetResult(true);
                }
            }

            LocalizationService.LanguageChanged += OnLanguageChanged;
            try
            {
                await Task.Run(() => LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false));
                Dispatcher.UIThread.RunJobs();
                await firedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.Equal("OK", observedTitleAtEvent);
            }
            finally
            {
                LocalizationService.LanguageChanged -= OnLanguageChanged;
            }
        }

        [AvaloniaFact]
        public void ServerItem_And_DashboardCompactTable_MaskPiiUnderStreamerMode()
        {
            using var settingsScope = AppServices.Override<ISettingsService>(new IniFile()["General"]);
            try
            {
                StreamerModeService.AutoDetectEnabled = false;
                StreamerModeService.IsEnabled = true;

                var srvItem = new ServerItemViewModel { JobId = "secret-job-id-999" };
                Assert.Equal("***-***", srvItem.ShownJobId);

                var repo = Substitute.For<IAccountRepository>();
                var acc = new Account
                {
                    Username = "TableSecretUser",
                    Alias = "TableAlias",
                    UserID = 77665544,
                    Valid = true,
                    SecurityToken = "tok"
                };
                repo.GetAll().Returns(new List<Account> { acc });
                repo.Count.Returns(1);
                using var repoScope = AppServices.Override<IAccountRepository>(repo);

                var view = new DashboardView();
                var win = new Window { Width = 1200, Height = 800, Content = view };
                try
                {
                    view.ViewModel.IsCompactView = true;
                    win.Show();
                    Dispatcher.UIThread.RunJobs();

                    var tableTexts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
                    Assert.DoesNotContain(tableTexts, t => t.Contains("TableSecretUser", StringComparison.Ordinal));
                    Assert.DoesNotContain(tableTexts, t => t.Contains("TableAlias", StringComparison.Ordinal));
                    Assert.DoesNotContain(tableTexts, t => t.Contains("77665544", StringComparison.Ordinal));
                }
                finally
                {
                    win.Close();
                    view.ViewModel.Dispose();
                }
            }
            finally
            {
                StreamerModeService.IsEnabled = _origStreamerEnabled;
            }
        }

        [Fact]
        public void WebView2Control_TryDeleteOwnedProfileDirectory_DeletesOnlyOwnedGuidProfiles_AndIgnoresUnrelated()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "ShiroWvOwned_" + Guid.NewGuid().ToString("N"));
            string profilesRoot = Path.Combine(tempRoot, "EBWebView");
            string ownedName = "Shiro_" + Guid.NewGuid().ToString("N");
            string ownedPath = Path.Combine(profilesRoot, ownedName);
            string customShiroPath = Path.Combine(profilesRoot, "Shiro_custom_keep");
            string unrelatedPath = Path.Combine(profilesRoot, "UserUnrelated");

            try
            {
                Directory.CreateDirectory(ownedPath);
                File.WriteAllText(Path.Combine(ownedPath, "Preferences"), "stub");
                Directory.CreateDirectory(customShiroPath);
                Directory.CreateDirectory(unrelatedPath);
                File.WriteAllText(Path.Combine(unrelatedPath, "Preferences"), "keep");

                bool deletedOwned = WebView2Control.TryDeleteOwnedProfileDirectory(tempRoot, ownedName);
                bool deletedCustomShiro = WebView2Control.TryDeleteOwnedProfileDirectory(tempRoot, "Shiro_custom_keep");
                bool deletedUnrelated = WebView2Control.TryDeleteOwnedProfileDirectory(tempRoot, "UserUnrelated");
                bool deletedTraversal = WebView2Control.TryDeleteOwnedProfileDirectory(tempRoot, "..\\UserUnrelated");

                Assert.True(deletedOwned);
                Assert.False(Directory.Exists(ownedPath));
                Assert.False(deletedCustomShiro);
                Assert.False(deletedUnrelated);
                Assert.False(deletedTraversal);
                Assert.True(Directory.Exists(customShiroPath));
                Assert.True(Directory.Exists(unrelatedPath));
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    try { Directory.Delete(tempRoot, true); }
                    catch (IOException) { }
                }
            }
        }

        private static double ComputeContrastRatio(Color a, Color b)
        {
            double l1 = RelativeLuminance(a);
            double l2 = RelativeLuminance(b);
            double lighter = Math.Max(l1, l2);
            double darker = Math.Min(l1, l2);
            return (lighter + 0.05) / (darker + 0.05);
        }

        private static double RelativeLuminance(Color c)
        {
            static double Channel(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
    }
}
