#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    public sealed class AccountInspectorDrawerTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly ILogSink? _prevSink;
        private readonly Action<Action>? _prevDispatcher;
        private readonly AppLanguage _prevLang;
        private readonly MainWindow? _prevWindow;
        private readonly List<IResourceProvider>? _origMergedDictionaries;

        public AccountInspectorDrawerTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "shiro-task9-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _prevSink = Logger.Sink;
            _prevDispatcher = AppDispatcher.CustomDispatcher;
            _prevLang = LocalizationService.CurrentLanguage;
            _prevWindow = MainWindow.Instance;
            _origMergedDictionaries = Application.Current?.Resources.MergedDictionaries.ToList();
        }

        public void Dispose()
        {
            Logger.Sink = _prevSink;
            AppDispatcher.CustomDispatcher = _prevDispatcher;
            LocalizationService.SetLanguage(_prevLang, saveSetting: false);
            if (Application.Current?.Resources.MergedDictionaries is { } merged && _origMergedDictionaries != null)
            {
                merged.Clear();
                foreach (var dict in _origMergedDictionaries)
                    merged.Add(dict);
            }
            ToastService.ResetAllForTesting();
            typeof(MainWindow).GetProperty(nameof(MainWindow.Instance))?.SetValue(null, _prevWindow);
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        [Fact]
        public void AccountInspector_OpenEditAndSave_PersistsAliasGroupDescription_ToRepository()
        {
            AppDispatcher.CustomDispatcher = a => a();
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);

            var account = new Account
            {
                UserID = 88101,
                Username = "InspectTarget",
                Alias = "OldAlias",
                Group = "Default",
                Description = "Old note",
                Valid = true,
                SecurityToken = "tok-88101"
            };
            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(new[] { account });
            repo.SaveAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);

            using var vm = new MainViewModel(Substitute.For<IAppLogger>(), CreateInMemorySettings(), repo);
            vm.LoadAccounts(new[] { account });

            AccountViewModel target = Assert.Single(vm.Accounts);
            vm.Inspector.Open(target);

            Assert.True(vm.Inspector.IsOpen);
            Assert.Same(target, vm.Inspector.InspectedAccount);
            Assert.Equal("OldAlias", vm.Inspector.EditAlias);
            Assert.Equal("Default", vm.Inspector.EditGroup);
            Assert.Equal("Old note", vm.Inspector.EditDescription);
            Assert.False(vm.Inspector.HasUnsavedChanges);

            vm.Inspector.EditAlias = "  FarmerLead  ";
            vm.Inspector.EditGroup = "  AlphaSquad  ";
            vm.Inspector.EditDescription = "Primary grinding account";
            vm.Inspector.EditSavedPlaceId = "2753915549";

            Assert.True(vm.Inspector.HasUnsavedChanges);
            vm.Inspector.SaveInlineChangesCommand.Execute(null);

            Assert.Equal("FarmerLead", account.Alias);
            Assert.Equal("AlphaSquad", account.Group);
            Assert.Equal("Primary grinding account", account.Description);
            Assert.Equal("2753915549", account.GetField("SavedPlaceId"));
            Assert.Equal("FarmerLead", target.Alias);
            Assert.Equal("AlphaSquad", target.Group);
            Assert.Equal("Primary grinding account", target.Description);
            Assert.Contains("AlphaSquad", vm.AvailableGroups);
            Assert.False(vm.Inspector.HasUnsavedChanges);
            repo.Received(1).SaveAsync(true);
        }

        [Fact]
        public void AccountInspector_SavedPlaceId_RejectsInvalidInput_AndAllowsClearing()
        {
            AppDispatcher.CustomDispatcher = a => a();
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);

            var account = new Account
            {
                UserID = 88102,
                Username = "PlaceIdGuard",
                Valid = true,
                SecurityToken = "tok-88102"
            };
            account.SetField("SavedPlaceId", "123456");

            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(new[] { account });
            repo.SaveAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);

            using var vm = new MainViewModel(Substitute.For<IAppLogger>(), CreateInMemorySettings(), repo);
            vm.LoadAccounts(new[] { account });

            vm.Inspector.Open(vm.Accounts[0]);
            Assert.Equal("123456", vm.Inspector.EditSavedPlaceId);

            vm.Inspector.EditSavedPlaceId = "abc-invalid";
            vm.Inspector.SaveInlineChangesCommand.Execute(null);

            Assert.True(vm.Inspector.HasValidationError);
            Assert.Equal("123456", account.GetField("SavedPlaceId"));
            repo.DidNotReceive().SaveAsync(Arg.Any<bool>());

            vm.Inspector.EditSavedPlaceId = string.Empty;
            vm.Inspector.SaveInlineChangesCommand.Execute(null);

            Assert.False(vm.Inspector.HasValidationError);
            Assert.Equal(string.Empty, account.GetField("SavedPlaceId"));
            repo.Received(1).SaveAsync(true);
        }

        [Fact]
        public async Task AccountInspector_DeleteWithUndo_RemovesAndRestoresAccountWithFields()
        {
            AppDispatcher.CustomDispatcher = a => a();
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);

            var account = new Account
            {
                UserID = 88103,
                Username = "UndoTarget",
                Alias = "KeepMe",
                Group = "Grind",
                Description = "Important notes",
                Pinned = true,
                RobuxBalance = 550,
                Valid = true,
                SecurityToken = "tok-restore-88103",
                Password = "pw-restore-88103",
                Proxy = "http://127.0.0.1:8888",
                HbaPrivateKey = "hba-priv-88103",
                HbaPublicKey = "hba-pub-88103",
                HbaTemplate = "hba-tmpl-88103",
                SessionUserAgent = "Mozilla/5.0 ShiroTest",
                LastBoundIpSignature = "sig-88103",
                AccountStatusIssue = "ReviewNotice"
            };
            account.SetField("SavedPlaceId", "998877");

            var repo = new AccountRepository();
            repo.ReplaceAll(new[] { account });

            using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
            using var loggerScope = AppServices.Override<IAppLogger>(Substitute.For<IAppLogger>());
            using var settingsScope = AppServices.Override<ISettingsService>(CreateInMemorySettings());
            using var repoScope = AppServices.Override<IAccountRepository>(repo);
            using var vm = new MainViewModel(Substitute.For<IAppLogger>(), AppServices.GeneralSettings, repo);
            vm.LoadAccounts(repo.GetAll());

            AccountViewModel target = Assert.Single(vm.Accounts);
            vm.Inspector.Open(target);
            Assert.True(vm.Inspector.IsOpen);

            vm.Inspector.DeleteWithUndoCommand.Execute(null);

            Assert.False(vm.Inspector.IsOpen);
            Assert.Null(vm.Inspector.InspectedAccount);
            Assert.Empty(repo.GetAll());
            Assert.Empty(vm.Accounts);

            ToastItem undoToast = Assert.Single(ToastService.ActiveToasts);
            Assert.True(undoToast.HasAction);
            Assert.NotNull(undoToast.ActionCommand);

            var undoCmd = undoToast.ActionCommand!;
            undoCmd.Execute(null);
            undoCmd.Execute(null); // Idempotent second trigger
            Assert.Null(undoToast.ActionCommand);
            Assert.False(undoToast.HasAction);

            Account restored = Assert.Single(repo.GetAll());
            Assert.Equal("UndoTarget", restored.Username);
            Assert.Equal(88103, restored.UserID);
            Assert.Equal("KeepMe", restored.Alias);
            Assert.Equal("Grind", restored.Group);
            Assert.Equal("Important notes", restored.Description);
            Assert.True(restored.Pinned);
            Assert.Equal(550, restored.RobuxBalance);
            Assert.Equal("tok-restore-88103", restored.SecurityToken);
            Assert.Equal("pw-restore-88103", restored.Password);
            Assert.Equal("http://127.0.0.1:8888", restored.Proxy);
            Assert.Equal("hba-priv-88103", restored.HbaPrivateKey);
            Assert.Equal("hba-pub-88103", restored.HbaPublicKey);
            Assert.Equal("hba-tmpl-88103", restored.HbaTemplate);
            Assert.Equal("Mozilla/5.0 ShiroTest", restored.SessionUserAgent);
            Assert.Equal("sig-88103", restored.LastBoundIpSignature);
            Assert.Equal("ReviewNotice", restored.AccountStatusIssue);
            Assert.Equal("998877", restored.GetField("SavedPlaceId"));
            AccountViewModel restoredVm = Assert.Single(vm.Accounts);
            Assert.Equal("KeepMe", restoredVm.Alias);
            await vm.Inspector.LastSaveTask;
        }

        [AvaloniaFact]
        public void MainWindow_EscapeKey_RespectsStrictTopmostOverlayPriority()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);

            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [AppSettingsKeys.General.HasAcceptedRiskDisclaimer] = "true"
            };
            var account = new Account
            {
                UserID = 88201,
                Username = "EscLayerUser",
                Valid = true,
                SecurityToken = "tok-esc"
            };
            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(new[] { account });
            repo.IsPasswordEncrypted().Returns(false);

            using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
            using var loggerScope = AppServices.Override<IAppLogger>(Substitute.For<IAppLogger>());
            using var settingsScope = AppServices.Override<ISettingsService>(CreateInMemorySettings(store));
            using var repoScope = AppServices.Override<IAccountRepository>(repo);

            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                window.Show();
                Dispatcher.UIThread.RunJobs();

                MainViewModel vm = window.ViewModel!;
                Assert.Single(vm.AccountsVm.Accounts);

                vm.AccountsVm.Accounts[0].IsSelected = true;
                vm.Inspector.Open(vm.AccountsVm.Accounts[0]);
                vm.IsNotificationCenterOpen = true;
                vm.CommandPalette.IsCommandPaletteOpen = true;
                Dispatcher.UIThread.RunJobs();

                Assert.True(vm.CommandPalette.IsCommandPaletteOpen);
                Assert.True(vm.IsNotificationCenterOpen);
                Assert.True(vm.Inspector.IsOpen);
                Assert.Equal(1, vm.AccountsVm.SelectedCount);

                // 1st Esc (also verify focused TxtCommandPalette closes ONLY CommandPalette)
                var cmdSearch = window.FindControl<CommandPaletteOverlayPanel>("ViewCommandPalette")?.SearchTextBox;
                Assert.NotNull(cmdSearch);
                cmdSearch!.Focus();
                Dispatcher.UIThread.RunJobs();

                var escFromPalette = new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Escape,
                    Source = cmdSearch
                };
                cmdSearch.RaiseEvent(escFromPalette);
                Dispatcher.UIThread.RunJobs();

                Assert.True(escFromPalette.Handled);
                Assert.False(vm.CommandPalette.IsCommandPaletteOpen);
                Assert.True(vm.IsNotificationCenterOpen);
                Assert.True(vm.Inspector.IsOpen);
                Assert.Equal(1, vm.AccountsVm.SelectedCount);

                // 2nd Esc -> closes NotificationCenter only
                var esc2 = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                window.RaiseEvent(esc2);
                Dispatcher.UIThread.RunJobs();

                Assert.True(esc2.Handled);
                Assert.False(vm.CommandPalette.IsCommandPaletteOpen);
                Assert.False(vm.IsNotificationCenterOpen);
                Assert.True(vm.Inspector.IsOpen);
                Assert.Equal(1, vm.AccountsVm.SelectedCount);

                // 3rd Esc -> closes Account Inspector only
                var esc3 = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                window.RaiseEvent(esc3);
                Dispatcher.UIThread.RunJobs();

                Assert.True(esc3.Handled);
                Assert.False(vm.IsNotificationCenterOpen);
                Assert.False(vm.Inspector.IsOpen);
                Assert.Equal(1, vm.AccountsVm.SelectedCount);

                // 4th Esc on non-Accounts page must NOT clear selection
                vm.CurrentPage = Shiro.ViewModels.NavigationPage.Settings;
                Dispatcher.UIThread.RunJobs();
                var escSettings = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                window.RaiseEvent(escSettings);
                Dispatcher.UIThread.RunJobs();
                Assert.False(escSettings.Handled);
                Assert.Equal(1, vm.AccountsVm.SelectedCount);

                // Back on Accounts page: Esc inside non-empty TxtSearch clears search without wiping selection
                vm.CurrentPage = Shiro.ViewModels.NavigationPage.Accounts;
                var txtSearch = window.DashboardView!.FindControl<TextBox>("TxtSearch");
                Assert.NotNull(txtSearch);
                vm.AccountsVm.SearchText = "EscLayer";
                txtSearch!.Focus();
                Dispatcher.UIThread.RunJobs();

                var escSearch = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = txtSearch };
                txtSearch.RaiseEvent(escSearch);
                Dispatcher.UIThread.RunJobs();
                Assert.True(escSearch.Handled);
                Assert.Equal(string.Empty, vm.AccountsVm.SearchText);
                Assert.Equal(1, vm.AccountsVm.SelectedCount);

                // Next Esc -> clears Dashboard selection only
                var esc4 = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                window.RaiseEvent(esc4);
                Dispatcher.UIThread.RunJobs();

                Assert.True(esc4.Handled);
                Assert.False(vm.AccountsVm.HasSelection);
                Assert.Equal(0, vm.AccountsVm.SelectedCount);

                Assert.Empty(sink.Errors);
            }
            finally
            {
                window?.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public void DashboardView_AccountInspectorDrawer_IsNonModal_SupportsCardClick_AndRendersCleanly()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);

            var accounts = new List<Account>
            {
                new() { UserID = 88301, Username = "DrawerUserOne", Alias = "One", Group = "Main", Valid = true, SecurityToken = "tok1", RobuxBalance = 1200 },
                new() { UserID = 88302, Username = "DrawerUserTwo", Alias = "Two", Group = "Alt", Valid = false, SecurityToken = "tok2" }
            };

            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(accounts);
            repo.SaveAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);
            var browserState = Substitute.For<IServerBrowserStateService>();
            browserState.CurrentPlaceId.Returns(string.Empty);

            using var repoScope = AppServices.Override<IAccountRepository>(repo);
            using var settingsScope = AppServices.Override<ISettingsService>(CreateInMemorySettings());
            using var browserScope = AppServices.Override<IServerBrowserStateService>(browserState);

            var view = new DashboardView();
            var window = new Window { Width = 1100, Height = 720, Content = view };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                MainViewModel vm = view.ViewModel;
                var drawer = view.FindControl<DashboardAccountDrawer>("AccountDrawer");
                Assert.NotNull(drawer);
                Assert.Equal(340.0, drawer!.Width);
                Assert.False(drawer.IsVisible);
                Assert.False(drawer.IsHitTestVisible);
                Assert.NotNull(drawer.FindControl<SkiaGlassSurface>("DrawerGlass"));

                var cards = view.GetVisualDescendants().OfType<AccountCardView>().Where(c => c.IsEffectivelyVisible).ToList();
                Assert.Equal(2, cards.Count);
                var cardOne = cards.First(c => ReferenceEquals(c.DataContext, vm.AccountsVm.FilteredAccounts[0]));
                var cardTwo = cards.First(c => ReferenceEquals(c.DataContext, vm.AccountsVm.FilteredAccounts[1]));

                // Open via card Inspect button
                var btnInspect = cardOne.FindControl<Button>("BtnInspect");
                Assert.NotNull(btnInspect);
                btnInspect!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                Assert.True(vm.Inspector.IsOpen);
                Assert.Same(vm.AccountsVm.FilteredAccounts[0], vm.Inspector.InspectedAccount);
                Assert.True(drawer.IsVisible);
                Assert.True(drawer.IsHitTestVisible);

                // Non-modal: user can still click/select cardTwo while drawer is open, and Enter switches inspector
                Point cardTwoCenter = cardTwo.TranslatePoint(new Point(20, 60), window)!.Value;
                window.MouseDown(cardTwoCenter, MouseButton.Left);
                window.MouseUp(cardTwoCenter, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                Assert.True(vm.AccountsVm.FilteredAccounts[1].IsSelected);

                view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                Dispatcher.UIThread.RunJobs();
                Assert.Same(vm.AccountsVm.FilteredAccounts[1], vm.Inspector.InspectedAccount);

                btnInspect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.Same(vm.AccountsVm.FilteredAccounts[0], vm.Inspector.InspectedAccount);

                // LiveMemoryText telemetry pill appears in Drawer header only while running
                Border? memoryPill() => drawer.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "DrawerMemoryPill");
                Assert.False(memoryPill()?.IsVisible ?? true);
                vm.AccountsVm.FilteredAccounts[0].IsInGame = true;
                vm.AccountsVm.FilteredAccounts[0].LiveMemoryText = "512 MB";
                Dispatcher.UIThread.RunJobs();
                Assert.True(memoryPill()?.IsVisible);
                Assert.Contains("512 MB", memoryPill()!.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

                // Inline edit & Save via drawer controls
                var txtAlias = drawer.FindControl<TextBox>("TxtEditAlias");
                var btnSave = drawer.FindControl<Button>("BtnSaveInspector");
                Assert.NotNull(txtAlias);
                Assert.NotNull(btnSave);
                txtAlias!.Text = "OneEdited";
                btnSave!.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("OneEdited", accounts[0].Alias);

                using var rtb = new RenderTargetBitmap(new PixelSize(1100, 720), new Vector(96, 96));
                Assert.Null(Record.Exception(() => rtb.Render(window)));
                Assert.Empty(sink.Errors);
            }
            finally
            {
                window.Close();
                view.ViewModel.Dispose();
                Dispatcher.UIThread.RunJobs();
            }
        }

        private static ISettingsService CreateInMemorySettings(Dictionary<string, string?>? initialStore = null)
        {
            var store = initialStore ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var settings = Substitute.For<ISettingsService>();
            settings.Get(Arg.Any<string>()).Returns(ci => store.TryGetValue(ci.Arg<string>(), out string? v) ? v : null);
            settings.Get<bool>(Arg.Any<string>()).Returns(ci =>
                store.TryGetValue(ci.Arg<string>(), out string? v) && bool.TryParse(v, out bool parsed) && parsed);
            settings.Exists(Arg.Any<string>()).Returns(ci => store.ContainsKey(ci.Arg<string>()));
            settings.When(s => s.Set(Arg.Any<string>(), Arg.Any<string?>()))
                .Do(ci =>
                {
                    string k = ci.ArgAt<string>(0);
                    string? v = ci.ArgAt<string?>(1);
                    if (v == null) store.Remove(k);
                    else store[k] = v;
                });
            return settings;
        }
    }
}
