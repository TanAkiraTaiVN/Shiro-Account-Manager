#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Threading;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Shiro.Tests.Ui;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P0UiAndViewModelRegressionTests : IDisposable
    {
        private readonly Action<Action>? _originalDispatcher = AppDispatcher.CustomDispatcher;
        private readonly Func<string, Task<(bool Confirmed, string? Path)>>? _originalExportPathOverride =
            SettingsSideEffectsService.TestPickExportPathOverride;

        public void Dispose()
        {
            AppDispatcher.CustomDispatcher = _originalDispatcher;
            SettingsSideEffectsService.TestPickExportPathOverride = _originalExportPathOverride;
            ToastService.ClearAll();
        }

        [Fact]
        public async Task AppDispatcher_Headless_InvokeAsyncAction_ThrowsOnceAndExecutesOnce()
        {
            AppDispatcher.CustomDispatcher = null;
            int invocationCount = 0;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AppDispatcher.InvokeAsync(() =>
                {
                    invocationCount++;
                    throw new InvalidOperationException("boom");
                }));

            Assert.Equal("boom", ex.Message);
            Assert.Equal(1, invocationCount);
        }

        [Fact]
        public async Task AppDispatcher_Headless_InvokeAsyncFunc_ThrowsOnceAndExecutesOnce()
        {
            AppDispatcher.CustomDispatcher = null;
            int invocationCount = 0;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AppDispatcher.InvokeAsync<int>(() =>
                {
                    invocationCount++;
                    throw new InvalidOperationException("boom-func");
                }));

            Assert.Equal("boom-func", ex.Message);
            Assert.Equal(1, invocationCount);
        }

        [Fact]
        public void AppDispatcher_Headless_Post_ThrowsOnceAndExecutesOnce()
        {
            AppDispatcher.CustomDispatcher = null;
            int invocationCount = 0;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                AppDispatcher.Post(() =>
                {
                    invocationCount++;
                    throw new InvalidOperationException("boom-post");
                }));

            Assert.Equal("boom-post", ex.Message);
            Assert.Equal(1, invocationCount);
        }

        [Fact]
        public void AppDispatcher_DispatchOrFallback_WhenDispatcherActiveAndDispatchThrows_DoesNotInvokeFallback()
        {
            int dispatchCount = 0;
            int fallbackCount = 0;

            AppDispatcher.DispatchOrFallback(
                hasActiveDispatcher: true,
                dispatch: () =>
                {
                    dispatchCount++;
                    throw new InvalidOperationException("dispatcher-boom");
                },
                fallback: () => fallbackCount++);

            Assert.Equal(1, dispatchCount);
            Assert.Equal(0, fallbackCount);
        }

        [Fact]
        public void AppDispatcher_DispatchOrFallback_WhenNoActiveDispatcher_InvokesFallbackOnce()
        {
            int dispatchCount = 0;
            int fallbackCount = 0;

            AppDispatcher.DispatchOrFallback(
                hasActiveDispatcher: false,
                dispatch: () => dispatchCount++,
                fallback: () => fallbackCount++);

            Assert.Equal(0, dispatchCount);
            Assert.Equal(1, fallbackCount);
        }

        [Fact]
        public async Task AppDispatcher_DispatchOrFallbackAsync_WhenDispatcherActiveAndDispatchThrows_PropagatesAndDoesNotInvokeFallback()
        {
            int dispatchCount = 0;
            int fallbackCount = 0;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AppDispatcher.DispatchOrFallbackAsync(
                    hasActiveDispatcher: true,
                    dispatch: () =>
                    {
                        dispatchCount++;
                        throw new InvalidOperationException("async-boom");
                    },
                    fallback: () =>
                    {
                        fallbackCount++;
                        return Task.CompletedTask;
                    }));

            Assert.Equal("async-boom", ex.Message);
            Assert.Equal(1, dispatchCount);
            Assert.Equal(0, fallbackCount);
        }

        [Fact]
        public async Task AppDispatcher_DispatchOrFallbackAsyncGeneric_WhenDispatcherActiveAndDispatchThrows_PropagatesAndDoesNotInvokeFallback()
        {
            int dispatchCount = 0;
            int fallbackCount = 0;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AppDispatcher.DispatchOrFallbackAsync<int>(
                    hasActiveDispatcher: true,
                    dispatch: () =>
                    {
                        dispatchCount++;
                        throw new InvalidOperationException("async-generic-boom");
                    },
                    fallback: () =>
                    {
                        fallbackCount++;
                        return Task.FromResult(42);
                    }));

            Assert.Equal("async-generic-boom", ex.Message);
            Assert.Equal(1, dispatchCount);
            Assert.Equal(0, fallbackCount);
        }

        [Fact]
        public async Task SettingsSideEffectsService_ExportVaultAsync_WhenPickerCancelled_ReturnsFalseCreatesNoFileAndShowsNoNotification()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "shiro-vault-cancel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            ToastService.ClearAll();
            try
            {
                using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                var repo = Substitute.For<IAccountRepository>();
                repo.GetAll().Returns(new List<Account>
                {
                    new() { Username = "CancelExportUser", UserID = 1001, SecurityToken = "tok", Valid = true }
                });

                var dialogs = Substitute.For<IDialogService>();
                dialogs.ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                    .Returns(Task.FromResult("Passphrase#123"));

                using var repoScope = AppServices.Override<IAccountRepository>(repo);
                using var dialogScope = AppServices.Override<IDialogService>(dialogs);
                SettingsSideEffectsService.TestPickExportPathOverride =
                    _ => Task.FromResult<(bool Confirmed, string? Path)>((false, null));

                bool exported = await SettingsSideEffectsService.Default.ExportVaultAsync();

                Assert.False(exported);
                Assert.Empty(Directory.GetFiles(tempDir, "*.shirovault", SearchOption.AllDirectories));
                dialogs.DidNotReceiveWithAnyArgs().ShowInfo(default!, default!);
                Assert.Empty(ToastService.ActiveToasts);
            }
            finally
            {
                SettingsSideEffectsService.TestPickExportPathOverride = _originalExportPathOverride;
                ToastService.ClearAll();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task SettingsSideEffectsService_ExportAndImportVault_InvokeShowSecretPromptAsyncInsteadOfUnmaskedPrompt()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "shiro-vault-secret-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string exportPath = Path.Combine(tempDir, "picked.shirovault");
            ToastService.ClearAll();
            try
            {
                using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                var stored = new List<Account>
                {
                    new() { Username = "ExportSecretUser", UserID = 2002, SecurityToken = "tok", Valid = true }
                };
                var repo = Substitute.For<IAccountRepository>();
                repo.GetAll().Returns(_ => stored.ToList());
                repo.When(r => r.AddThreadSafe(Arg.Any<Account>()))
                    .Do(ci => stored.Add(ci.Arg<Account>()));
                repo.SaveAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);

                var exportDialogs = Substitute.For<IDialogService>();
                exportDialogs.ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                    .Returns(Task.FromResult("SecretPass#456"));

                using var repoScope = AppServices.Override<IAccountRepository>(repo);
                using (AppServices.Override<IDialogService>(exportDialogs))
                {
                    SettingsSideEffectsService.TestPickExportPathOverride =
                        _ => Task.FromResult<(bool Confirmed, string? Path)>((true, exportPath));

                    bool exported = await SettingsSideEffectsService.Default.ExportVaultAsync();

                    Assert.True(exported);
                    Assert.True(File.Exists(exportPath));
                    await exportDialogs.Received(1).ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
                    await exportDialogs.DidNotReceiveWithAnyArgs().ShowInputPromptAsync(default!, default!, default!);
                }

                stored.Clear();
                var importDialogs = Substitute.For<IDialogService>();
                importDialogs.ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                    .Returns(Task.FromResult("SecretPass#456"));
                var host = Substitute.For<IHostUiBridgeService>();

                using (AppServices.Override<IDialogService>(importDialogs))
                using (AppServices.Override<IHostUiBridgeService>(host))
                {
                    bool imported = await SettingsSideEffectsService.Default.ImportVaultFromPathAsync(exportPath);

                    Assert.True(imported);
                    Assert.Single(stored);
                    await importDialogs.Received(1).ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
                    await importDialogs.DidNotReceiveWithAnyArgs().ShowInputPromptAsync(default!, default!, default!);
                }
            }
            finally
            {
                SettingsSideEffectsService.TestPickExportPathOverride = _originalExportPathOverride;
                ToastService.ClearAll();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task SettingsSideEffectsService_ExportVaultAsync_WhenHeadlessWithoutPicker_FallsBackToBackupsDirectory()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "shiro-vault-headless-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            ToastService.ClearAll();
            try
            {
                using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                var repo = Substitute.For<IAccountRepository>();
                repo.GetAll().Returns(new List<Account>
                {
                    new() { Username = "HeadlessExportUser", UserID = 3003, SecurityToken = "tok", Valid = true }
                });

                var dialogs = Substitute.For<IDialogService>();
                dialogs.ShowSecretPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                    .Returns(Task.FromResult("Passphrase#789"));

                using var repoScope = AppServices.Override<IAccountRepository>(repo);
                using var dialogScope = AppServices.Override<IDialogService>(dialogs);
                SettingsSideEffectsService.TestPickExportPathOverride = null;

                bool exported = await SettingsSideEffectsService.Default.ExportVaultAsync();

                Assert.True(exported);
                var backupFiles = Directory.GetFiles(StoragePaths.BackupsDirectory, "*.shirovault", SearchOption.TopDirectoryOnly);
                Assert.Single(backupFiles);
            }
            finally
            {
                SettingsSideEffectsService.TestPickExportPathOverride = _originalExportPathOverride;
                ToastService.ClearAll();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void InputDialog_ShouldMaskInput_ReturnsTrueForAnyLocalizedCaptionWhenIsSecretTrue()
        {
            var prevLang = LocalizationService.CurrentLanguage;
            try
            {
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                Assert.False(InputDialog.ShouldMaskInput(LocalizationService.Get("MainWindow.UnlockShiroTitle"), isSecret: false));
                Assert.True(InputDialog.ShouldMaskInput(LocalizationService.Get("MainWindow.UnlockShiroTitle"), isSecret: true));

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                Assert.False(InputDialog.ShouldMaskInput("M\u1edf kh\u00f3a Shiro", isSecret: false));
                Assert.True(InputDialog.ShouldMaskInput("M\u1edf kh\u00f3a Shiro", isSecret: true));
                Assert.False(InputDialog.ShouldMaskInput(LocalizationService.Get("MainWindow.UnlockShiroTitle"), isSecret: false));
                Assert.True(InputDialog.ShouldMaskInput(LocalizationService.Get("MainWindow.UnlockShiroTitle"), isSecret: true));

                Assert.True(InputDialog.ShouldMaskInput("Nh\u1eadp m\u1eadt kh\u1ea9u kho...", isSecret: true));
                Assert.True(InputDialog.ShouldMaskInput("Export Vault", isSecret: true));
                Assert.True(InputDialog.ShouldMaskInput(null, isSecret: true));
                Assert.True(InputDialog.ShouldMaskInput("Enter Password", isSecret: false));
                Assert.False(InputDialog.ShouldMaskInput("Group Name", isSecret: false));

                var rootDir = new DirectoryInfo(AppContext.BaseDirectory);
                while (rootDir != null && !File.Exists(Path.Combine(rootDir.FullName, "Shiro.sln")))
                    rootDir = rootDir.Parent;
                Assert.NotNull(rootDir);
                string mainWindowSource = File.ReadAllText(Path.Combine(rootDir!.FullName, "src", "Shiro", "AvaloniaUI", "Views", "MainWindow.axaml.cs"));
                int unlockIdx = mainWindowSource.IndexOf("MainWindow.UnlockShiroTitle", StringComparison.Ordinal);
                Assert.True(unlockIdx > 0);
                int callEnd = mainWindowSource.IndexOf(");", unlockIdx, StringComparison.Ordinal);
                Assert.True(callEnd > unlockIdx);
                string unlockCallSlice = mainWindowSource.Substring(unlockIdx, callEnd - unlockIdx);
                Assert.Contains("isSecret: true", unlockCallSlice, StringComparison.Ordinal);
            }
            finally
            {
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
            }
        }

        [Theory]
        [InlineData(NavigationPage.Accounts, "ViewDashboard", "BtnNavDashboard", null, false, false)]
        [InlineData(NavigationPage.ServerList, "ViewServerList", "BtnNavServerList", 0, false, true)]
        [InlineData(NavigationPage.RecentGames, "ViewServerList", "BtnNavRecentGames", 2, true, false)]
        [InlineData(NavigationPage.Settings, "ViewSettings", "BtnNavSettings", null, false, false)]
        [InlineData(NavigationPage.About, "ViewAbout", "BtnNavAbout", null, false, false)]
        public void MainWindow_ResolveNavigationTarget_MapsEveryPageToExpectedViewAndNavButton(
            NavigationPage page,
            string expectedViewName,
            string expectedButtonName,
            int? expectedSubTabIndex,
            bool expectedReloadRecentGames,
            bool expectedEnsureServersLoaded)
        {
            var spec = MainWindow.ResolveNavigationTarget(page);
            Assert.Equal(expectedViewName, spec.ViewName);
            Assert.Equal(expectedButtonName, spec.NavButtonName);
            Assert.Equal(expectedSubTabIndex, spec.ServerListSubTabIndex);
            Assert.Equal(expectedReloadRecentGames, spec.ReloadRecentGames);
            Assert.Equal(expectedEnsureServersLoaded, spec.EnsureServersLoaded);
        }

        [AvaloniaFact]
        public async Task MainWindow_BidirectionalNavigation_SyncsViewsNavPillsAndCurrentPage_AndDetachesOnClose()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "shiro-nav-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var sink = new BindingErrorSink();
            var prevSink = Logger.Sink;
            var prevLang = LocalizationService.CurrentLanguage;
            var prevPlaceId = AppServices.ServerBrowserState.CurrentPlaceId;
            MainWindow? window = null;
            MainViewModel? vm = null;
            Logger.Sink = sink;
            try
            {
                using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                AppServices.ServerBrowserState.CurrentPlaceId = string.Empty;

                var settings = Substitute.For<ISettingsService>();
                settings.Get(AppSettingsKeys.General.HasAcceptedRiskDisclaimer).Returns("true");
                settings.Get<bool>(AppSettingsKeys.General.HasAcceptedRiskDisclaimer).Returns(true);
                settings.Get("SavedPlaceId").Returns((string?)null);

                var repo = Substitute.For<IAccountRepository>();
                repo.GetAll().Returns(new List<Account>());
                repo.IsPasswordEncrypted().Returns(false);

                using var settingsScope = AppServices.Override<ISettingsService>(settings);
                using var repoScope = AppServices.Override<IAccountRepository>(repo);

                window = new MainWindow();
                window.Show();
                Dispatcher.UIThread.RunJobs();

                vm = window.ViewModel;
                Assert.NotNull(vm);
                Assert.NotNull(vm!.OnPageNavigated);

                var viewDashboard = window.FindControl<Control>("ViewDashboard");
                var viewServerList = window.FindControl<Control>("ViewServerList");
                var viewSettings = window.FindControl<Control>("ViewSettings");
                var viewAbout = window.FindControl<Control>("ViewAbout");
                var btnDashboard = window.FindControl<Button>("BtnNavDashboard");
                var btnServerList = window.FindControl<Button>("BtnNavServerList");
                var btnRecentGames = window.FindControl<Button>("BtnNavRecentGames");
                var btnSettings = window.FindControl<Button>("BtnNavSettings");
                var btnAbout = window.FindControl<Button>("BtnNavAbout");

                Assert.NotNull(viewDashboard);
                Assert.NotNull(viewServerList);
                Assert.NotNull(viewSettings);
                Assert.NotNull(viewAbout);
                Assert.NotNull(btnDashboard);
                Assert.NotNull(btnServerList);
                Assert.NotNull(btnRecentGames);
                Assert.NotNull(btnSettings);
                Assert.NotNull(btnAbout);

                void AssertActive(Control? expectedView, Button? expectedButton)
                {
                    Assert.Equal(ReferenceEquals(viewDashboard, expectedView), viewDashboard!.IsVisible);
                    Assert.Equal(ReferenceEquals(viewServerList, expectedView), viewServerList!.IsVisible);
                    Assert.Equal(ReferenceEquals(viewSettings, expectedView), viewSettings!.IsVisible);
                    Assert.Equal(ReferenceEquals(viewAbout, expectedView), viewAbout!.IsVisible);

                    Assert.Equal(ReferenceEquals(btnDashboard, expectedButton), btnDashboard!.Classes.Contains("active"));
                    Assert.Equal(ReferenceEquals(btnServerList, expectedButton), btnServerList!.Classes.Contains("active"));
                    Assert.Equal(ReferenceEquals(btnRecentGames, expectedButton), btnRecentGames!.Classes.Contains("active"));
                    Assert.Equal(ReferenceEquals(btnSettings, expectedButton), btnSettings!.Classes.Contains("active"));
                    Assert.Equal(ReferenceEquals(btnAbout, expectedButton), btnAbout!.Classes.Contains("active"));
                }

                // Initial page is Accounts
                Assert.Equal(NavigationPage.Accounts, vm.CurrentPage);
                AssertActive(viewDashboard, btnDashboard);

                // Programmatic ViewModel -> MainWindow transitions
                vm.CurrentPage = NavigationPage.Settings;
                AssertActive(viewSettings, btnSettings);

                vm.NavigateToRecentGamesCommand.Execute(null);
                Assert.Equal(NavigationPage.RecentGames, vm.CurrentPage);
                Assert.Equal(2, vm.ServerListViewModel.SelectedSubTabIndex);
                AssertActive(viewServerList, btnRecentGames);

                vm.CurrentPage = NavigationPage.About;
                Assert.True(vm.IsAboutActive);
                AssertActive(viewAbout, btnAbout);

                vm.CurrentPage = NavigationPage.ServerList;
                Assert.Equal(NavigationPage.ServerList, vm.CurrentPage);
                Assert.Equal(0, vm.ServerListViewModel.SelectedSubTabIndex);
                AssertActive(viewServerList, btnServerList);

                vm.CurrentPage = NavigationPage.Accounts;
                AssertActive(viewDashboard, btnDashboard);

                // Sidebar button click -> ViewModel.CurrentPage + no recursion loop
                int currentPageChangeCount = 0;
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.CurrentPage))
                        currentPageChangeCount++;
                };

                btnSettings!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(NavigationPage.Settings, vm.CurrentPage);
                Assert.Equal(1, currentPageChangeCount);
                AssertActive(viewSettings, btnSettings);

                // Re-clicking active sidebar pill does not re-fire CurrentPage change
                btnSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, currentPageChangeCount);
                AssertActive(viewSettings, btnSettings);

                btnAbout!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(NavigationPage.About, vm.CurrentPage);
                AssertActive(viewAbout, btnAbout);

                btnRecentGames!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(NavigationPage.RecentGames, vm.CurrentPage);
                Assert.Equal(2, vm.ServerListViewModel.SelectedSubTabIndex);
                AssertActive(viewServerList, btnRecentGames);

                btnServerList!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(NavigationPage.ServerList, vm.CurrentPage);
                Assert.Equal(0, vm.ServerListViewModel.SelectedSubTabIndex);
                AssertActive(viewServerList, btnServerList);

                btnDashboard!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(NavigationPage.Accounts, vm.CurrentPage);
                AssertActive(viewDashboard, btnDashboard);

                window.Close();
                Dispatcher.UIThread.RunJobs();

                Assert.Null(vm.OnPageNavigated);
                Assert.Null(MainWindow.Instance);

                window.ApplyNavigationPage(NavigationPage.Settings);
                await Task.Run(() => window.ApplyNavigationPage(NavigationPage.About));
                Dispatcher.UIThread.RunJobs();
                Assert.True(viewDashboard!.IsVisible);
                Assert.False(viewSettings!.IsVisible);
                Assert.False(viewAbout!.IsVisible);

                Assert.Empty(sink.Errors);
            }
            finally
            {
                window?.Close();
                Dispatcher.UIThread.RunJobs();
                AppServices.ServerBrowserState.CurrentPlaceId = prevPlaceId;
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
                Avalonia.Application.Current?.Resources.MergedDictionaries.Clear();
                Logger.Sink = prevSink;
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
