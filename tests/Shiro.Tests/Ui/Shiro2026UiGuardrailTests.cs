#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LucideAvalonia;
using LucideAvalonia.Enum;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.AvaloniaUI.Views.Panels;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public sealed class Shiro2026UiGuardrailTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _tempDir;
        private readonly IDisposable _dataDirScope;
        private readonly IDisposable _settingsScope;
        private readonly ILogSink? _origSink;
        private readonly Action<Action>? _origDispatcher;
        private readonly AppLanguage _origLang;
        private readonly UiDensityMode _origDensity;
        private readonly List<IResourceProvider>? _origMergedDictionaries;

        public Shiro2026UiGuardrailTests(ITestOutputHelper output)
        {
            _output = output;
            _tempDir = Path.Combine(Path.GetTempPath(), "shiro-guardrail-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
            _settingsScope = AppServices.Override<ISettingsService>(CreateInMemorySettings());
            _origSink = Logger.Sink;
            _origDispatcher = AppDispatcher.CustomDispatcher;
            _origLang = LocalizationService.CurrentLanguage;
            _origDensity = VisualPreferencesService.Instance.DensityMode;
            _origMergedDictionaries = Application.Current?.Resources.MergedDictionaries.ToList();
        }

        public void Dispose()
        {
            try
            {
                Logger.Sink = _origSink;
                AppDispatcher.CustomDispatcher = _origDispatcher;
                LocalizationService.SetLanguage(_origLang, saveSetting: false);
                VisualPreferencesService.Instance.DensityMode = _origDensity;
                ToastService.ResetAllForTesting();
                if (Application.Current?.Resources.MergedDictionaries is { } merged && _origMergedDictionaries != null)
                {
                    merged.Clear();
                    foreach (var dict in _origMergedDictionaries) merged.Add(dict);
                }
            }
            finally
            {
                _settingsScope.Dispose();
                _dataDirScope.Dispose();
                if (Directory.Exists(_tempDir))
                {
                    try { Directory.Delete(_tempDir, recursive: true); }
                    catch (IOException) { }
                }
            }
        }

        [AvaloniaFact]
        public void AllAxamlLucideIcons_ResolveToNonNullIconSource()
        {
            string uiDir = Path.Combine(FindRepoRoot(), "src", "Shiro", "AvaloniaUI");
            var axamlFiles = Directory.GetFiles(uiDir, "*.axaml", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.NotEmpty(axamlFiles);

            int checkedIcons = 0;
            var checkedNames = new HashSet<LucideIconNames>();
            foreach (string file in axamlFiles)
            {
                foreach (var elem in XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "Lucide"))
                {
                    string? iconAttr = elem.Attribute("Icon")?.Value;
                    Assert.False(string.IsNullOrWhiteSpace(iconAttr), $"<lucide:Lucide> missing Icon in {Path.GetFileName(file)}");
                    Assert.False(string.Equals(iconAttr, "Trash2", StringComparison.OrdinalIgnoreCase), $"Forbidden 'Trash2' in {Path.GetFileName(file)}");
                    Assert.True(Enum.TryParse<LucideIconNames>(iconAttr, ignoreCase: false, out var parsed), $"Invalid LucideIconNames '{iconAttr}' in {Path.GetFileName(file)}");

                    checkedIcons++;
                    if (checkedNames.Add(parsed))
                        Assert.True(new Lucide { Icon = parsed }.IconSource != null, $"LucideIconNames.{parsed} ({Path.GetFileName(file)}) resolved to null IconSource");
                }
            }
            Assert.True(checkedIcons > 0, "Expected to inspect at least one <lucide:Lucide> element.");
            _output.WriteLine($"Verified {checkedIcons} AXAML Lucide usages across {checkedNames.Count} unique icons.");
        }

        [AvaloniaFact]
        public void VirtualizedTemplates_HaveZeroTransitionsOnFastControls()
        {
            EnsureBaseThemeTokens();
            string uiDir = Path.Combine(FindRepoRoot(), "src", "Shiro", "AvaloniaUI");
            var appDoc = XDocument.Load(Path.Combine(uiDir, "App.axaml"));
            AssertStyleSetsNullTransitions(appDoc, "Button.FastIconBtn");
            AssertStyleSetsNullTransitions(appDoc, "Button.FastPillBtn");

            AssertAxamlFastControls(XDocument.Load(Path.Combine(uiDir, "Views", "AccountCardView.axaml")), "AccountCardView.axaml", false);
            var xamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
            var tableTemplate = XDocument.Load(Path.Combine(uiDir, "Views", "DashboardView.axaml")).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "ItemsControl" && e.Attribute(XName.Get("Name", xamlNs))?.Value == "TableItemsControl");
            Assert.NotNull(tableTemplate);
            AssertAxamlFastControls(tableTemplate!, "DashboardView.axaml#TableItemsControl", false);

            var srvTemplate = XDocument.Load(Path.Combine(uiDir, "Views", "Panels", "ServerListResultsPanel.axaml")).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "DataTemplate" && e.Attribute(XName.Get("DataType", xamlNs))?.Value == "vm:ServerItemViewModel");
            Assert.NotNull(srvTemplate);
            AssertAxamlFastControls(srvTemplate!, "ServerListResultsPanel.axaml#ServerRow", true);

            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);

            var account = new Account { UserID = 777, Username = "GuardUser", Valid = true, SecurityToken = "tok" };
            var avm = new AccountViewModel(account, Substitute.For<IAppLogger>(), AppServices.GeneralSettings);
            var cardView = new AccountCardView { DataContext = avm };
            var cardWin = new Window { Width = 400, Height = 200, Content = cardView };

            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(new List<Account> { account });
            var browserState = Substitute.For<IServerBrowserStateService>();
            browserState.CurrentPlaceId.Returns(string.Empty);
            using var repoScope = AppServices.Override<IAccountRepository>(repo);
            using var browserScope = AppServices.Override<IServerBrowserStateService>(browserState);

            var dashView = new DashboardView();
            var dashWin = new Window { Width = 1080, Height = 640, Content = dashView };
            using var mainVm = new MainViewModel();
            using var srvVm = new ServerListViewModel(mainVm);
            srvVm.SelectedSubTabIndex = 0;
            srvVm.BrowserTab.SetRawServers(new[] { new ServerItemViewModel { JobId = "srv-guard", Playing = 5, MaxPlayers = 10, Ping = 45 } });
            var srvPanel = new ServerListResultsPanel { DataContext = srvVm };
            var srvWin = new Window { Width = 1000, Height = 500, Content = srvPanel };

            try
            {
                cardWin.Show();
                Dispatcher.UIThread.RunJobs();
                AssertRuntimeFastControls(cardView, "AccountCardView", expectedMinButtons: 5, expectCheckBox: true);

                dashWin.Show();
                Dispatcher.UIThread.RunJobs();
                dashView.ViewModel.AccountsVm.IsCompactView = true;
                dashWin.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Assert.Single(dashView.ViewModel.AccountsVm.FilteredAccounts);
                var tableItems = dashView.FindControl<ItemsControl>("TableItemsControl");
                Assert.NotNull(tableItems);
                AssertRuntimeFastControls(tableItems!, "TableItemsControl", expectedMinButtons: 4, expectCheckBox: true);

                srvWin.Show();
                srvWin.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                AssertRuntimeFastControls(srvPanel, "ServerListResultsPanel", expectedMinButtons: 3, expectProgressBar: true);
                Assert.Empty(sink.Errors);
            }
            finally
            {
                CloseWindowSafely(srvWin);
                CloseWindowSafely(dashWin, () => dashView.ViewModel.Dispose());
                CloseWindowSafely(cardWin);
                Dispatcher.UIThread.RunJobs();
            }
        }

        [Fact]
        public void AccountCollection_500Accounts_BatchSelectAndFilter_CompletesUnderBudget()
        {
            AppDispatcher.CustomDispatcher = a => a();
            VisualPreferencesService.Instance.DensityMode = UiDensityMode.Balanced;

            using (var warmupVm = CreateCollectionVm(Array.Empty<Account>()))
            {
                warmupVm.LoadAccounts(BuildAccounts(10));
                warmupVm.ApplySelectionBatch(warmupVm.Accounts.Take(5).ToList(), additive: false);
                warmupVm.DensityMode = UiDensityMode.Compact;
                warmupVm.SearchText = "Scale_00";
                warmupVm.ApplyFilter();
            }

            var accounts = BuildAccounts(500);
            using var vm = CreateCollectionVm(accounts);
            var sw = Stopwatch.StartNew();
            vm.LoadAccounts(accounts);
            vm.ApplySelectionBatch(vm.Accounts.Take(250).ToList(), additive: false);
            vm.ApplySelectionBatch(vm.Accounts.Skip(250).Take(100).ToList(), additive: true);
            vm.DensityMode = UiDensityMode.Compact;
            vm.DensityMode = UiDensityMode.Comfortable;
            vm.SearchText = "Scale_04";
            vm.ApplyFilter();
            sw.Stop();

            _output.WriteLine($"500-account load + batch select + density toggle + filter: {sw.Elapsed.TotalMilliseconds:F2} ms");
            Assert.Equal(500, vm.Accounts.Count);
            Assert.Equal(350, vm.SelectedCount);
            Assert.Equal(UiDensityMode.Comfortable, vm.DensityMode);
            Assert.Equal(156.0, vm.CardHeight);
            Assert.Equal(100, vm.FilteredAccounts.Count);
            Assert.True(sw.Elapsed.TotalMilliseconds < 250.0, $"500-account workflow took {sw.Elapsed.TotalMilliseconds:F2} ms (budget: < 250 ms)");
        }

        private static void EnsureBaseThemeTokens()
        {
            if (Application.Current?.Resources is { } resources && !resources.TryGetResource("RadiusOuter16", null, out _))
                resources.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://Shiro/AvaloniaUI/Styles/ThemeTokens.axaml")));
        }

        private static void CloseWindowSafely(Window win, Action? afterClose = null)
        {
            try { win.Close(); }
            catch (InvalidOperationException) { }
            finally { afterClose?.Invoke(); }
        }

        private static void AssertStyleSetsNullTransitions(XDocument appDoc, string selector)
        {
            var style = appDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Style" && e.Attribute("Selector")?.Value == selector);
            Assert.NotNull(style);
            Assert.True(style!.Elements().Any(e => e.Name.LocalName == "Setter" && e.Attribute("Property")?.Value == "Transitions" && e.Attribute("Value")?.Value == "{x:Null}"),
                $"App.axaml style '{selector}' must set Transitions=\"{{x:Null}}\".");
        }

        private static void AssertAxamlFastControls(XContainer root, string label, bool requireExplicitOnButtons)
        {
            foreach (var elem in root.Descendants())
            {
                string? classes = elem.Attribute("Classes")?.Value;
                bool isFastBtn = elem.Name.LocalName == "Button" && classes != null
                    && (classes.Contains("FastIconBtn", StringComparison.Ordinal) || classes.Contains("FastPillBtn", StringComparison.Ordinal));
                bool isProgress = elem.Name.LocalName == "ProgressBar", isCheck = elem.Name.LocalName == "CheckBox";
                if (!isFastBtn && !isProgress && !isCheck) continue;

                string? transitions = elem.Attribute("Transitions")?.Value;
                if (isProgress || isCheck || requireExplicitOnButtons)
                    Assert.True(transitions == "{x:Null}", $"{label}: <{elem.Name.LocalName} Classes=\"{classes}\"> must declare Transitions=\"{{x:Null}}\".");
                else if (transitions != null)
                    Assert.True(transitions == "{x:Null}", $"{label}: <{elem.Name.LocalName} Classes=\"{classes}\"> overrides Transitions with '{transitions}'.");
            }
        }

        private static void AssertRuntimeFastControls(Visual root, string label, int expectedMinButtons, bool expectCheckBox = false, bool expectProgressBar = false)
        {
            var fastButtons = root.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("FastIconBtn") || b.Classes.Contains("FastPillBtn")).ToList();
            Assert.True(fastButtons.Count >= expectedMinButtons, $"{label}: expected >= {expectedMinButtons} fast buttons, found {fastButtons.Count}");
            Assert.All(fastButtons, b => Assert.Null(b.Transitions));

            var checkBoxes = root.GetVisualDescendants().OfType<CheckBox>().ToList();
            if (expectCheckBox) Assert.NotEmpty(checkBoxes);
            Assert.All(checkBoxes, cb => Assert.Null(cb.Transitions));

            var progressBars = root.GetVisualDescendants().OfType<ProgressBar>().ToList();
            if (expectProgressBar) Assert.NotEmpty(progressBars);
            Assert.All(progressBars, pb => Assert.Null(pb.Transitions));
        }

        private static AccountCollectionViewModel CreateCollectionVm(IReadOnlyList<Account> accounts)
        {
            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(accounts);
            return new AccountCollectionViewModel(Substitute.For<IAppLogger>(), AppServices.GeneralSettings, repo);
        }

        private static List<Account> BuildAccounts(int count) => Enumerable.Range(0, count).Select(i => new Account
        {
            UserID = 200000 + i,
            Username = $"Scale_{i:D4}",
            Group = (i % 4) switch { 0 => "Alpha", 1 => "Beta", 2 => "Gamma", _ => "Default" },
            Valid = true,
            SecurityToken = "tok"
        }).ToList();

        private static ISettingsService CreateInMemorySettings()
        {
            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var settings = Substitute.For<ISettingsService>();
            settings.Get(Arg.Any<string>()).Returns(ci => store.TryGetValue(ci.Arg<string>(), out string? v) ? v : null);
            settings.Exists(Arg.Any<string>()).Returns(ci => store.ContainsKey(ci.Arg<string>()));
            settings.When(s => s.Set(Arg.Any<string>(), Arg.Any<string?>())).Do(ci =>
            {
                string k = ci.ArgAt<string>(0); string? v = ci.ArgAt<string?>(1);
                if (v == null) store.Remove(k); else store[k] = v;
            });
            return settings;
        }

        private static string FindRepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln"))) return dir.FullName;
            throw new DirectoryNotFoundException("Could not locate Shiro.sln from AppContext.BaseDirectory.");
        }
    }
}
