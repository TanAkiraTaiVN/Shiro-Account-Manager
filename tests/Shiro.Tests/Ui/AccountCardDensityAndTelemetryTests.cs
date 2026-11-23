#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using Shiro.AvaloniaUI.Controls;
using Shiro.AvaloniaUI.Views;
using Shiro.AvaloniaUI.Views.Panels;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public sealed class AccountCardDensityAndTelemetryTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly ILogSink? _origSink;
        private readonly Action<Action>? _origDispatcher;
        private readonly AppLanguage _origLang;
        private readonly UiDensityMode _origDensity;

        public AccountCardDensityAndTelemetryTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "shiro-task8-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _origSink = Logger.Sink;
            _origDispatcher = AppDispatcher.CustomDispatcher;
            _origLang = LocalizationService.CurrentLanguage;
            _origDensity = VisualPreferencesService.Instance.DensityMode;
        }

        public void Dispose()
        {
            Logger.Sink = _origSink;
            AppDispatcher.CustomDispatcher = _origDispatcher;
            LocalizationService.SetLanguage(_origLang, saveSetting: false);
            VisualPreferencesService.Instance.DensityMode = _origDensity;
            ToastService.ClearAll();
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        [Fact]
        public void AccountCollectionViewModel_DensityMode_UpdatesCardAndTableHeights_Cleanly()
        {
            AppDispatcher.CustomDispatcher = a => a();
            var settings = CreateInMemorySettings();
            using var settingsScope = AppServices.Override<ISettingsService>(settings);
            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(Array.Empty<Account>());

            var prefs = VisualPreferencesService.Instance;
            prefs.DensityMode = UiDensityMode.Balanced;

            using var vm = new AccountCollectionViewModel(
                logger: Substitute.For<IAppLogger>(),
                settings: settings,
                accountRepository: repo);

            Assert.Equal(UiDensityMode.Balanced, vm.DensityMode);
            Assert.Equal(138.0, vm.CardHeight);
            Assert.Equal(150.0, vm.CardRowHeight);
            Assert.Equal(40.0, vm.TableRowHeight);

            vm.DensityMode = UiDensityMode.Compact;
            Assert.Equal(UiDensityMode.Compact, vm.DensityMode);
            Assert.Equal(122.0, vm.CardHeight);
            Assert.Equal(130.0, vm.CardRowHeight);
            Assert.Equal(34.0, vm.TableRowHeight);
            Assert.Equal(UiDensityMode.Compact, prefs.DensityMode);
            Assert.Equal("Compact", settings.Get(AppSettingsKeys.General.UiDensityMode));

            vm.SetDensityModeCommand.Execute(UiDensityMode.Comfortable);
            Assert.Equal(UiDensityMode.Comfortable, vm.DensityMode);
            Assert.Equal(156.0, vm.CardHeight);
            Assert.Equal(170.0, vm.CardRowHeight);
            Assert.Equal(46.0, vm.TableRowHeight);
            Assert.Equal(UiDensityMode.Comfortable, prefs.DensityMode);

            vm.SetDensityModeCommand.Execute("Balanced");
            Assert.Equal(UiDensityMode.Balanced, vm.DensityMode);
            Assert.Equal(138.0, vm.CardHeight);
            Assert.Equal(150.0, vm.CardRowHeight);
            Assert.Equal(40.0, vm.TableRowHeight);

            prefs.ApplyAndSave(densityMode: UiDensityMode.Compact);
            Assert.Equal(UiDensityMode.Compact, vm.DensityMode);
            Assert.Equal(122.0, vm.CardHeight);

            int beforeDispose = GetPreferencesSubscriberCount(prefs);
            int postDisposeNotifications = 0;
            vm.PropertyChanged += (_, _) => postDisposeNotifications++;

            vm.Dispose();
            Assert.Equal(beforeDispose - 1, GetPreferencesSubscriberCount(prefs));
            prefs.DensityMode = UiDensityMode.Comfortable;
            Assert.Equal(UiDensityMode.Compact, vm.DensityMode);
            Assert.Equal(0, postDisposeNotifications);
        }

        [Fact]
        public void AccountViewModel_HasLiveMemory_VisibleOnlyWhenClientRunningAndMemoryPresent()
        {
            var account = new Account { UserID = 1001, Username = "TelemetryUser", Valid = true, SecurityToken = "tok" };
            var avm = new AccountViewModel(account, Substitute.For<IAppLogger>(), CreateInMemorySettings());
            var changed = new List<string?>();
            avm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            avm.LiveMemoryText = "412 MB";
            Assert.False(avm.HasLiveMemory);

            changed.Clear();
            avm.IsInGame = true;
            Assert.True(avm.HasLiveMemory);
            Assert.Contains(nameof(AccountViewModel.HasLiveMemory), changed);

            avm.IsInGame = false;
            Assert.False(avm.HasLiveMemory);

            avm.IsLocalClientRunning = true;
            Assert.True(avm.HasLiveMemory);

            avm.LiveMemoryText = string.Empty;
            Assert.False(avm.HasLiveMemory);
        }

        [Fact]
        public void AccountCollectionViewModel_EmptyStates_DistinguishNoAccountsFromFilteredOut()
        {
            AppDispatcher.CustomDispatcher = a => a();
            var accounts = new List<Account>
            {
                new() { UserID = 1, Username = "AlphaOne", Group = "Farm", Valid = true, SecurityToken = "t1" },
                new() { UserID = 2, Username = "BetaTwo", Group = "Main", Valid = true, SecurityToken = "t2" },
                new() { UserID = 3, Username = "GammaThree", Group = "Farm", Valid = true, SecurityToken = "t3" }
            };

            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(accounts);
            using var vm = new AccountCollectionViewModel(
                logger: Substitute.For<IAppLogger>(),
                settings: CreateInMemorySettings(),
                accountRepository: repo);

            Assert.True(vm.IsEmptyNoAccounts);
            Assert.False(vm.IsEmptyFiltered);

            vm.LoadAccounts(accounts);
            Assert.False(vm.IsEmptyNoAccounts);
            Assert.False(vm.IsEmptyFiltered);
            Assert.Equal(3, vm.FilteredAccounts.Count);

            vm.SearchText = "NoSuchAccountMatch";
            vm.ApplyFilter();
            Assert.False(vm.IsEmptyNoAccounts);
            Assert.True(vm.IsEmptyFiltered);
            Assert.Empty(vm.FilteredAccounts);

            vm.ActiveTab = AccountFilterTab.Expired;
            vm.SelectedGroup = "Main";
            Assert.True(vm.IsEmptyFiltered);

            vm.ClearFiltersCommand.Execute(null);
            Assert.Equal(string.Empty, vm.SearchText);
            Assert.Equal("All Groups", vm.SelectedGroup);
            Assert.Equal(AccountFilterTab.All, vm.ActiveTab);
            Assert.False(vm.IsEmptyNoAccounts);
            Assert.False(vm.IsEmptyFiltered);
            Assert.Equal(3, vm.FilteredAccounts.Count);

            vm.LoadAccounts(Array.Empty<Account>());
            Assert.True(vm.IsEmptyNoAccounts);
            Assert.False(vm.IsEmptyFiltered);
        }

        [AvaloniaFact]
        public void DashboardView_DensityTelemetryGlassBulkBarAndEmptyStates_RenderCleanly()
        {
            var accounts = new List<Account>
            {
                new() { UserID = 5001, Username = "RunnerAcc", Valid = true, SecurityToken = "tok1", RobuxBalance = 120 },
                new() { UserID = 5002, Username = "IdleAcc", Valid = true, SecurityToken = "tok2", RobuxBalance = 50 }
            };

            using var host = CreateMountedDashboard(accounts, width: 1080, height: 760);
            var view = host.View;
            var vm = view.ViewModel;

            vm.AccountsVm.Accounts[0].IsInGame = true;
            vm.AccountsVm.Accounts[0].LiveMemoryText = "412 MB";
            vm.AccountsVm.Accounts[1].LiveMemoryText = "300 MB";
            Dispatcher.UIThread.RunJobs();

            var cards = view.GetVisualDescendants().OfType<AccountCardView>().Where(c => c.IsEffectivelyVisible).ToList();
            Assert.Equal(2, cards.Count);
            var runnerCard = cards.First(c => ReferenceEquals(c.DataContext, vm.AccountsVm.Accounts[0]));
            var idleCard = cards.First(c => ReferenceEquals(c.DataContext, vm.AccountsVm.Accounts[1]));

            var runnerRamPill = runnerCard.FindControl<Border>("MemoryPill");
            var idleRamPill = idleCard.FindControl<Border>("MemoryPill");
            Assert.NotNull(runnerRamPill);
            Assert.NotNull(idleRamPill);
            Assert.True(runnerRamPill!.IsVisible);
            Assert.False(idleRamPill!.IsVisible);

            // Verify CardHeight, CardRowHeight, Margin, and Footer density toggle across all 3 density modes
            var footer = view.GetVisualDescendants().OfType<DashboardFooterBar>().Single();
            var btnCompact = footer.FindControl<RadioButton>("BtnDensityCompact")!;
            var btnBalanced = footer.FindControl<RadioButton>("BtnDensityBalanced")!;
            var btnComfortable = footer.FindControl<RadioButton>("BtnDensityComfortable")!;
            Assert.True(btnBalanced.IsChecked);

            var runnerRoot = runnerCard.FindControl<Border>("CardRoot")!;
            Assert.Equal(138.0, runnerRoot.Height);
            Assert.Equal(new Thickness(5, 0, 5, 12), runnerRoot.Margin);
            Assert.Equal(150.0, view.GetVisualDescendants().OfType<UniformRowPanel>().First(p => p.IsVisible).Height);

            btnCompact.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(UiDensityMode.Compact, vm.AccountsVm.DensityMode);
            Assert.Equal(122.0, runnerRoot.Height);
            Assert.Equal(new Thickness(5, 0, 5, 8), runnerRoot.Margin);
            Assert.Equal(130.0, view.GetVisualDescendants().OfType<UniformRowPanel>().First(p => p.IsVisible).Height);

            btnComfortable.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(UiDensityMode.Comfortable, vm.AccountsVm.DensityMode);
            Assert.Equal(156.0, runnerRoot.Height);
            Assert.Equal(new Thickness(5, 0, 5, 14), runnerRoot.Margin);
            Assert.Equal(170.0, view.GetVisualDescendants().OfType<UniformRowPanel>().First(p => p.IsVisible).Height);

            // Switch to compact table and verify TableRowHeight updates + RAM pill visibility
            vm.AccountsVm.IsCompactView = true;
            vm.AccountsVm.DensityMode = UiDensityMode.Compact;
            Dispatcher.UIThread.RunJobs();
            var compactRows = view.FindControl<ItemsControl>("TableItemsControl")!
                .GetVisualDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("CompactTableRow"))
                .ToList();
            Assert.Equal(2, compactRows.Count);
            Assert.All(compactRows, r => Assert.Equal(34.0, r.Height));
            var runnerTableRow = compactRows.First(r => ReferenceEquals(r.DataContext, vm.AccountsVm.Accounts[0]));
            var idleTableRow = compactRows.First(r => ReferenceEquals(r.DataContext, vm.AccountsVm.Accounts[1]));
            var runnerRowTexts = runnerTableRow.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            var idleRowVisibleRam = idleTableRow.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "300 MB"));
            Assert.Contains("412 MB", runnerRowTexts);
            Assert.True(idleRowVisibleRam == null || !idleRowVisibleRam.IsVisible);

            vm.AccountsVm.DensityMode = UiDensityMode.Comfortable;
            Dispatcher.UIThread.RunJobs();
            Assert.All(compactRows, r => Assert.Equal(46.0, r.Height));

            // Bulk bar upgrades to SkiaGlassSurface and appears + renders cleanly on selection
            var bulkPanel = view.GetVisualDescendants().OfType<DashboardBulkBarPanel>().Single();
            Assert.False(bulkPanel.IsVisible);
            Assert.NotNull(bulkPanel.FindControl<SkiaGlassSurface>("BulkBarGlass"));

            vm.AccountsVm.Accounts[0].IsSelected = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(bulkPanel.IsVisible);
            using var rtb = new RenderTargetBitmap(new PixelSize(1080, 760), new Vector(96, 96));
            Assert.Null(Record.Exception(() => rtb.Render(host.Window)));

            // Smart empty states: filtered out vs zero accounts
            vm.AccountsVm.SearchText = "NoMatchingUser999";
            vm.AccountsVm.ApplyFilter();
            Dispatcher.UIThread.RunJobs();

            var emptyPanel = view.GetVisualDescendants().OfType<DashboardEmptyStatePanel>().Single();
            Assert.True(emptyPanel.IsVisible);
            Assert.True(emptyPanel.FindControl<Border>("FilteredEmptyCard")!.IsVisible);
            Assert.False(emptyPanel.FindControl<Border>("NoAccountsEmptyCard")!.IsVisible);

            emptyPanel.FindControl<Button>("BtnClearFilters")!.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(emptyPanel.IsVisible);
            Assert.Equal(2, vm.AccountsVm.FilteredAccounts.Count);

            vm.AccountsVm.LoadAccounts(Array.Empty<Account>());
            Dispatcher.UIThread.RunJobs();
            Assert.True(emptyPanel.IsVisible);
            Assert.True(emptyPanel.FindControl<Border>("NoAccountsEmptyCard")!.IsVisible);
            Assert.False(emptyPanel.FindControl<Border>("FilteredEmptyCard")!.IsVisible);

            Assert.Empty(host.Sink.Errors);
        }

        private MountedDashboard CreateMountedDashboard(List<Account> accounts, double width, double height)
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(accounts);
            var browserState = Substitute.For<IServerBrowserStateService>();
            browserState.CurrentPlaceId.Returns(string.Empty);
            var repoScope = AppServices.Override<IAccountRepository>(repo);
            var settingsScope = AppServices.Override<ISettingsService>(CreateInMemorySettings());
            var browserScope = AppServices.Override<IServerBrowserStateService>(browserState);
            var view = new DashboardView();
            var window = new Window { Width = width, Height = height, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return new MountedDashboard(view, window, sink, repoScope, settingsScope, browserScope);
        }

        private static ISettingsService CreateInMemorySettings()
        {
            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var settings = Substitute.For<ISettingsService>();
            settings.Get(Arg.Any<string>()).Returns(ci => store.TryGetValue(ci.Arg<string>(), out string? v) ? v : null);
            settings.Exists(Arg.Any<string>()).Returns(ci => store.ContainsKey(ci.Arg<string>()));
            settings.When(s => s.Set(Arg.Any<string>(), Arg.Any<string?>()))
                .Do(ci => { string k = ci.ArgAt<string>(0); string? v = ci.ArgAt<string?>(1); if (v == null) store.Remove(k); else store[k] = v; });
            return settings;
        }

        private static int GetPreferencesSubscriberCount(VisualPreferencesService prefs)
        {
            FieldInfo? field = typeof(VisualPreferencesService).GetField(
                nameof(VisualPreferencesService.PreferencesChanged),
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var del = field!.GetValue(prefs) as Delegate;
            return del?.GetInvocationList().Length ?? 0;
        }

        private sealed class MountedDashboard(DashboardView view, Window window, BindingErrorSink sink,
            IDisposable repoScope, IDisposable settingsScope, IDisposable browserScope) : IDisposable
        {
            public DashboardView View => view;
            public Window Window => window;
            public BindingErrorSink Sink => sink;
            public void Dispose()
            {
                window.Close(); view.ViewModel.Dispose(); Dispatcher.UIThread.RunJobs();
                browserScope.Dispose(); settingsScope.Dispose(); repoScope.Dispose();
            }
        }
    }
}
