#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class P3ViewModelDecompositionTests : IDisposable
    {
        private readonly string? _origSavedPlaceId = AppServices.GeneralSettings.Get("SavedPlaceId");
        private readonly string _origCurrentPlaceId = AppServices.ServerBrowserState.CurrentPlaceId ?? string.Empty;

        public void Dispose()
        {
            if (_origSavedPlaceId is null) AppServices.GeneralSettings.RemoveProperty("SavedPlaceId");
            else AppServices.GeneralSettings.Set("SavedPlaceId", _origSavedPlaceId);
            AppServices.ServerBrowserState.CurrentPlaceId = _origCurrentPlaceId;
        }

        [Fact]
        public void MainViewModel_ExposesComposedChildViewModels_Under1150Budget()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            AppDispatcher.CustomDispatcher = a => a();
            try
            {
                using var vm = new MainViewModel();
                Assert.IsType<AccountCollectionViewModel>(vm.AccountsVm);
                Assert.IsType<LaunchBarViewModel>(vm.LaunchBar);
                Assert.IsType<ToolsStatusViewModel>(vm.ToolsStatus);
                Assert.IsType<CommandPaletteViewModel>(vm.CommandPalette);

                var acc1 = new Account { Username = "ChildAcc1", UserID = 1001, SecurityToken = "tok-child-1", Valid = true, RobuxBalance = 250 };
                var acc2 = new Account { Username = "ChildAcc2", UserID = 1002, Valid = false };
                vm.AccountsVm.LoadAccounts(new[] { acc1, acc2 });
                Assert.Equal(2, vm.AccountsVm.TotalCount);
                Assert.Equal(1, vm.AccountsVm.ReadyCount);
                Assert.Equal(2, vm.TotalCount);
                Assert.Equal(2, vm.FilteredAccounts.Count);

                vm.LaunchBar.PlaceId = "189707";
                Assert.Equal("189707", vm.PlaceId);
                Assert.Equal("189707", vm.ServerListViewModel.PlaceIdInput);

                bool prevPotato = vm.ToolsStatus.IsPotatoMode;
                int beforeTools = vm.ToolsStatus.ActiveToolsCount;
                vm.ToolsStatus.IsPotatoMode = !prevPotato;
                int expectedTools = vm.ToolsStatus.IsPotatoMode ? beforeTools + 1 : beforeTools - 1;
                Assert.Equal(expectedTools, vm.ToolsStatus.ActiveToolsCount);
                Assert.Equal(expectedTools, vm.ActiveToolsCount);
                vm.ToolsStatus.IsPotatoMode = prevPotato;

                vm.CommandPalette.CommandPaletteSearchText = "anti";
                Assert.Contains(
                    vm.CommandPalette.CommandPaletteItems,
                    i => i.IsCommand && i.Title.Contains("Anti-AFK", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public void ServerListViewModel_ExposesComposedTabChildViewModels_WithWorkingStateAndCommands()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-DecompSL-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var storageScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                using var slvm = new ServerListViewModel();

                Assert.IsType<ServerBrowserTabViewModel>(slvm.BrowserTab);
                Assert.IsType<GameExplorerTabViewModel>(slvm.ExplorerTab);
                Assert.IsType<GameLibraryTabViewModel>(slvm.LibraryTab);

                var s1 = new ServerItemViewModel { JobId = "srv-high", Type = "Public", Playing = 10, MaxPlayers = 12, Ping = 80 };
                var s2 = new ServerItemViewModel { JobId = "srv-low", Type = "Public", Playing = 2, MaxPlayers = 12, Ping = 20 };
                slvm.BrowserTab.SetRawServers(new[] { s1, s2 });
                Assert.Equal(2, slvm.BrowserTab.TotalServersCount);
                Assert.Equal(2, slvm.TotalServersCount);

                slvm.BrowserTab.ToggleSortPlayersCommand.Execute(null);
                Assert.Equal(ServerSortMode.PlayersAsc, slvm.BrowserTab.SortMode);
                Assert.Equal("srv-low", slvm.BrowserTab.Servers[0].JobId);

                slvm.ExplorerTab.GameSearchTerm = "Obby";
                Assert.Equal("Obby", slvm.ExplorerTab.GameSearchTerm);
                Assert.True(slvm.ExplorerTab.IsSearchGamesEmpty);
                slvm.ExplorerTab.IsSearchingGames = true;
                Assert.False(slvm.ExplorerTab.IsSearchGamesEmpty);
                slvm.ExplorerTab.IsSearchingGames = false;

                slvm.PlaceIdInput = "189707";
                slvm.LibraryTab.AddFavoriteCommand.Execute(null);
                Assert.Single(slvm.LibraryTab.Favorites);
                Assert.Equal(189707L, slvm.LibraryTab.Favorites[0].PlaceId);
                Assert.False(slvm.LibraryTab.IsFavoritesEmpty);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public void ServerWatcherConfigService_SingleSourceOfTruth_SynchronizesSettingsAndServerList()
        {
            var prevIni = AppSettings.ActiveIniFile;
            bool prevCloseTitle = RobloxWatcher.CloseIfWindowTitle;
            string? prevExpectedTitle = RobloxWatcher.ExpectedWindowTitle;
            try
            {
                var ini = new IniFile();
                AppSettings.ActiveIniFile = ini;

                var configSvc = new ServerWatcherConfigService();
                var settingsVm = new SettingsViewModel(
                    new AppSettings(),
                    ini["General"],
                    Substitute.For<ISettingsSideEffects>(),
                    Substitute.For<IDialogService>(),
                    TimeSpan.FromMilliseconds(10),
                    Substitute.For<IAppLogger>());

                settingsVm.Load();
                settingsVm.CloseIfWindowTitle = true;
                settingsVm.ExpectedWindowTitle = "ShiroTestWindow";
                settingsVm.NoConnectionTimeout = 35m;
                settingsVm.Save();

                Assert.Equal("true", ini["Watcher"].Get<string>(AppSettingsKeys.Watcher.CloseIfWindowTitle));
                Assert.Equal("ShiroTestWindow", ini["Watcher"].Get<string>(AppSettingsKeys.Watcher.ExpectedWindowTitle));
                Assert.Equal("35", ini["Watcher"].Get<string>(AppSettingsKeys.Watcher.NoConnectionTimeout));
                Assert.True(RobloxWatcher.CloseIfWindowTitle);
                Assert.Equal("ShiroTestWindow", RobloxWatcher.ExpectedWindowTitle);

                var loaded = configSvc.Load(ini["Watcher"], syncRuntime: false);
                Assert.True(loaded.CloseIfWindowTitle);
                Assert.Equal("ShiroTestWindow", loaded.ExpectedWindowTitle);
                Assert.Equal(35m, loaded.ConnectionTimeoutSec);

                using var slvm = new ServerListViewModel();
                Assert.Equal("ShiroTestWindow", slvm.ExpectedWindowTitle);
                Assert.Equal(35m, slvm.ConnectionTimeoutSec);

                slvm.ConnectionTimeoutSec = 50m;
                Assert.Equal("50", ini["Watcher"].Get<string>(AppSettingsKeys.Watcher.NoConnectionTimeout));
            }
            finally
            {
                RobloxWatcher.CloseIfWindowTitle = prevCloseTitle;
                RobloxWatcher.ExpectedWindowTitle = prevExpectedTitle;
                AppSettings.ActiveIniFile = prevIni;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference<AccountCollectionViewModel> CreateUndisposedAccountsVmWeak(IAccountRepository repo)
        {
            var vm = new AccountCollectionViewModel(Substitute.For<IAppLogger>(), new IniFile()["General"], repo);
            return new WeakReference<AccountCollectionViewModel>(vm);
        }

        [Fact]
        public void AccountCollectionViewModel_StandaloneSync_OrdersVersionsAndReclaimsOnGc()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            var queued = new Queue<Action>();
            AppDispatcher.CustomDispatcher = a => queued.Enqueue(a);
            try
            {
                var mockRepo = Substitute.For<IAccountRepository>();
                using var accountsVm = new AccountCollectionViewModel(Substitute.For<IAppLogger>(), new IniFile()["General"], mockRepo);

                var newer = new Account { Username = "NewerChild", UserID = 701, Valid = true };
                var stale = new Account { Username = "StaleChild", UserID = 702, Valid = true };

                mockRepo.AccountsChanged += Raise.Event<EventHandler<AccountsChangedEventArgs>>(
                    mockRepo,
                    new AccountsChangedEventArgs(new[] { newer }, version: 2));
                mockRepo.AccountsChanged += Raise.Event<EventHandler<AccountsChangedEventArgs>>(
                    mockRepo,
                    new AccountsChangedEventArgs(new[] { stale }, version: 1));

                while (queued.Count > 0)
                    queued.Dequeue().Invoke();

                Assert.Single(accountsVm.Accounts);
                Assert.Equal("NewerChild", accountsVm.Accounts[0].Username);
            }
            finally
            {
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }

            var realRepo = new AccountRepository();
            WeakReference<AccountCollectionViewModel> weakRef = CreateUndisposedAccountsVmWeak(realRepo);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            realRepo.AddThreadSafe(new Account { Username = "TriggerGcChild", UserID = 703, Valid = true });
            Assert.False(weakRef.TryGetTarget(out _));
        }

        [Fact]
        public void ChildViewModels_ExecuteLaunchToolsAndPaletteWorkflowsIndependently()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            AppDispatcher.CustomDispatcher = a => a();
            try
            {
                using var mainVm = new MainViewModel();
                var acc = new Account { Username = "WorkflowChildUser", UserID = 801, Valid = true };
                mainVm.AccountsVm.LoadAccounts(new[] { acc });
                mainVm.AccountsVm.Accounts[0].IsSelected = true;

                Assert.Contains("1", mainVm.LaunchBar.JoinServerButtonText);
                mainVm.LaunchBar.NormalizePlaceIdInput("https://www.roblox.com/games/2753915549/Blox-Fruits");
                Assert.Equal("2753915549", mainVm.LaunchBar.PlaceId);

                mainVm.LaunchBar.UpdateBatchLaunchBanner(new LaunchProgress
                {
                    Account = acc,
                    Index = 1,
                    Total = 2,
                    Completed = false,
                    Message = "Launching"
                });
                Assert.True(mainVm.LaunchBar.IsBatchLaunching);
                Assert.Equal("Launching", mainVm.LaunchBar.BatchLaunchDetailText);

                mainVm.LaunchBar.CancelBatchLaunchCommand.Execute(null);
                Assert.False(mainVm.LaunchBar.IsBatchLaunching);
                Assert.Equal(0, mainVm.LaunchBar.BatchLaunchProgressPercent);

                bool prevPotato = mainVm.ToolsStatus.IsPotatoMode;
                mainVm.ToolsStatus.TogglePotatoModeCommand.Execute(null);
                Assert.NotEqual(prevPotato, mainVm.ToolsStatus.IsPotatoMode);
                mainVm.ToolsStatus.TogglePotatoModeCommand.Execute(null);

                mainVm.CommandPalette.IsCommandPaletteOpen = true;
                mainVm.CommandPalette.CommandPaletteSearchText = "potato";
                Assert.True(mainVm.CommandPalette.CommandPaletteItems.Count >= 1);
                mainVm.CommandPalette.SetSelectedCommandPaletteIndex(0);
                bool executed = mainVm.CommandPalette.ExecuteCommandPaletteSelection();
                Assert.True(executed);
                Assert.False(mainVm.CommandPalette.IsCommandPaletteOpen);
                mainVm.ToolsStatus.IsPotatoMode = prevPotato;
            }
            finally
            {
                ToastService.ClearAll();
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public void ViewsAndPanels_CompiledBindings_BindDirectlyToDecomposedChildViewModels()
        {
            string viewsDir = Path.Combine(ArchitectureTests.RepoRoot, "src", "Shiro", "AvaloniaUI", "Views");

            string dashboard = File.ReadAllText(Path.Combine(viewsDir, "DashboardView.axaml"));
            string footer = File.ReadAllText(Path.Combine(viewsDir, "Panels", "DashboardFooterBar.axaml"));
            string inspector = File.ReadAllText(Path.Combine(viewsDir, "Panels", "DashboardInspectorPanel.axaml"));
            string mainWindow = File.ReadAllText(Path.Combine(viewsDir, "MainWindow.axaml"));
            string overlays = File.ReadAllText(Path.Combine(viewsDir, "Panels", "MainWindowOverlaysPanel.axaml"));
            string serverList = File.ReadAllText(Path.Combine(viewsDir, "ServerListView.axaml"));
            string serverResults = File.ReadAllText(Path.Combine(viewsDir, "Panels", "ServerListResultsPanel.axaml"));

            Assert.Contains("AccountsVm.FilteredAccountRows", dashboard, StringComparison.Ordinal);
            Assert.Contains("LaunchBar.PlaceId", dashboard, StringComparison.Ordinal);
            Assert.Contains("DataContext=\"{Binding AccountsVm}\"", dashboard, StringComparison.Ordinal);
            Assert.Contains("AccountsVm.IsFilterAll", footer, StringComparison.Ordinal);
            Assert.Contains("LaunchBar.IsBatchLaunching", footer, StringComparison.Ordinal);
            Assert.Contains("x:DataType=\"vm:AccountCollectionViewModel\"", inspector, StringComparison.Ordinal);
            Assert.Contains("ToolsStatus.HasActiveTools", mainWindow, StringComparison.Ordinal);
            Assert.Contains("CommandPalette.IsCommandPaletteOpen", mainWindow, StringComparison.Ordinal);
            Assert.Contains("ToolsStatus.StatusMessage", overlays, StringComparison.Ordinal);
            Assert.Contains("AccountsVm.FooterSelectedText", overlays, StringComparison.Ordinal);
            Assert.Contains("BrowserTab.LoadAllRegionsCommand", serverList, StringComparison.Ordinal);
            Assert.Contains("LibraryTab.Favorites", serverList, StringComparison.Ordinal);
            Assert.Contains("BrowserTab.Servers", serverResults, StringComparison.Ordinal);
            Assert.Contains("ExplorerTab.SearchGames", serverResults, StringComparison.Ordinal);
        }

        [Fact]
        public void GameLibraryTabViewModel_AddFavoriteCachedName_CorruptOrLockedFileAndNullParams_HandledCleanly()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-LibTabCov-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var storageScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                string favPath = StoragePaths.GetUserFile("FavoriteGames.json");
                Directory.CreateDirectory(Path.GetDirectoryName(favPath)!);
                var logger = Substitute.For<IAppLogger>();

                using (new FileStream(favPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    using var lockedVm = new ServerListViewModel(null, logger, null);
                    Assert.True(lockedVm.LibraryTab.IsFavoritesEmpty);
                }

                using var slvm = new ServerListViewModel(null, logger, null);
                GameInfoService.CachePlaceName(77112233L, "Cached Library Game");
                slvm.PlaceIdInput = "77112233";
                slvm.LibraryTab.AddFavoriteCommand.Execute(null);
                Assert.Single(slvm.LibraryTab.Favorites);
                Assert.Equal("Cached Library Game", slvm.LibraryTab.Favorites[0].Name);

                var fromSearch = new GameSearchItemViewModel
                {
                    PlaceId = 889900L,
                    Name = "SearchThumbGame",
                    ThumbnailUrl = "https://tr.rbxcdn.com/img.png"
                };
                slvm.LibraryTab.AddFavoriteCommand.Execute(fromSearch);
                Assert.Equal(2, slvm.LibraryTab.Favorites.Count);
                Assert.Equal("https://tr.rbxcdn.com/img.png", slvm.LibraryTab.Favorites[1].ThumbnailUrl);

                slvm.LibraryTab.RemoveFavoriteCommand.Execute(null);
                slvm.LibraryTab.SelectFavoriteCommand.Execute(null);
                slvm.LibraryTab.LaunchFavoriteCommand.Execute(null);
                Assert.Equal(2, slvm.LibraryTab.Favorites.Count);

                slvm.LibraryTab.NotifyRecentGamesChanged();
                Assert.False(slvm.LibraryTab.HasRecentGames);
                Assert.True(slvm.LibraryTab.IsRecentGamesEmpty);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); }
                    catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                }
            }
        }
    }
}
