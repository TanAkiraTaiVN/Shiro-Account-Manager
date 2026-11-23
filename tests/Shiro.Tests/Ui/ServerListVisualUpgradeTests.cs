#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LucideAvalonia;
using Shiro.AvaloniaUI.Controls;
using Shiro.AvaloniaUI.Views;
using Shiro.AvaloniaUI.Views.Panels;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public sealed class ServerListVisualUpgradeTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly ILogSink? _prevSink;
        private readonly AppLanguage _prevLang;
        private readonly string _prevPlaceId;
        private readonly List<IResourceProvider>? _origMergedDictionaries;

        public ServerListVisualUpgradeTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "shiro-srv-v2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _prevSink = Logger.Sink;
            _prevLang = LocalizationService.CurrentLanguage;
            _prevPlaceId = AppServices.ServerBrowserState.CurrentPlaceId;
            _origMergedDictionaries = Application.Current?.Resources.MergedDictionaries.ToList();
            GameThumbnailService.ClearCache();
        }

        public void Dispose()
        {
            ToastService.ClearAll();
            GameThumbnailService.ClearCache();
            AppServices.ServerBrowserState.CurrentPlaceId = _prevPlaceId;
            LocalizationService.SetLanguage(_prevLang, saveSetting: false);
            if (Application.Current?.Resources.MergedDictionaries is { } merged && _origMergedDictionaries != null)
            {
                merged.Clear();
                foreach (var dict in _origMergedDictionaries)
                    merged.Add(dict);
            }
            Logger.Sink = _prevSink;
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, recursive: true); }
                catch (IOException) { }
            }
        }

        [Fact]
        public void ServerItemViewModel_ComputesSemanticCapacityBrush_AndPingSignalBars()
        {
            var green = Color.Parse("#4ADE80");
            var amber = Color.Parse("#FBBF24");
            var crimson = Color.Parse("#F87171");
            var mutedText = Color.Parse("#71717A");

            var item = new ServerItemViewModel
            {
                JobId = "srv-1",
                Playing = 3,
                MaxPlayers = 12,
                Ping = 40
            };

            var changed = new List<string>();
            item.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.PropertyName))
                    changed.Add(e.PropertyName);
            };

            // 3/12 = 25% -> Green; 10/12 = 83.3% -> Amber; 12/12 = 100% -> Crimson
            Assert.Equal(green, Assert.IsAssignableFrom<ISolidColorBrush>(item.CapacityFillBrush).Color);
            item.Playing = 10;
            Assert.Equal(amber, Assert.IsAssignableFrom<ISolidColorBrush>(item.CapacityFillBrush).Color);
            Assert.Contains(nameof(ServerItemViewModel.CapacityFillBrush), changed);
            item.Playing = 12;
            Assert.Equal(crimson, Assert.IsAssignableFrom<ISolidColorBrush>(item.CapacityFillBrush).Color);

            // Exact capacity boundaries: MaxPlayers=0, 74%, 75%, 94%, 95%
            item.MaxPlayers = 0;
            item.Playing = 0;
            Assert.Equal(green, Assert.IsAssignableFrom<ISolidColorBrush>(item.CapacityFillBrush).Color);

            item.MaxPlayers = 100;
            item.Playing = 74;
            Assert.Equal(green, Assert.IsAssignableFrom<ISolidColorBrush>(item.CapacityFillBrush).Color);
            item.Playing = 75;
            Assert.Equal(amber, Assert.IsAssignableFrom<ISolidColorBrush>(item.CapacityFillBrush).Color);
            item.Playing = 94;
            Assert.Equal(amber, Assert.IsAssignableFrom<ISolidColorBrush>(item.CapacityFillBrush).Color);
            item.Playing = 95;
            Assert.Equal(crimson, Assert.IsAssignableFrom<ISolidColorBrush>(item.CapacityFillBrush).Color);

            // Ping 40ms & 70ms -> level 3 (all 3 bars + PingTextBrush green)
            Assert.Equal(3, item.PingBarsLevel);
            Assert.Equal(green, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar1Brush).Color);
            Assert.Equal(green, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar2Brush).Color);
            Assert.Equal(green, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar3Brush).Color);
            Assert.Equal(green, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingTextBrush).Color);
            item.Ping = 70;
            Assert.Equal(3, item.PingBarsLevel);
            Assert.Equal(green, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingTextBrush).Color);

            // Ping 71ms, 110ms, 140ms -> level 2 (2 bars + PingTextBrush amber, 3rd muted)
            changed.Clear();
            item.Ping = 71;
            Assert.Equal(2, item.PingBarsLevel);
            Assert.Equal(amber, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingTextBrush).Color);
            Assert.Contains(nameof(ServerItemViewModel.PingBarsLevel), changed);
            Assert.Contains(nameof(ServerItemViewModel.PingTextBrush), changed);

            item.Ping = 110;
            Assert.Equal(2, item.PingBarsLevel);
            Assert.Equal(amber, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar1Brush).Color);
            Assert.Equal(amber, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar2Brush).Color);
            Assert.NotEqual(amber, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar3Brush).Color);
            Assert.Equal(amber, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingTextBrush).Color);

            item.Ping = 140;
            Assert.Equal(2, item.PingBarsLevel);
            Assert.Equal(amber, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingTextBrush).Color);

            // Ping 141ms & 210ms -> level 1 (1 bar + PingTextBrush crimson, 2nd & 3rd muted)
            item.Ping = 141;
            Assert.Equal(1, item.PingBarsLevel);
            Assert.Equal(crimson, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingTextBrush).Color);

            item.Ping = 210;
            Assert.Equal(1, item.PingBarsLevel);
            Assert.Equal(crimson, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar1Brush).Color);
            Assert.NotEqual(crimson, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar2Brush).Color);
            Assert.NotEqual(crimson, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar3Brush).Color);
            Assert.Equal(crimson, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingTextBrush).Color);

            // Unknown ping (-1) -> 0 active bars & muted PingTextBrush
            item.Ping = -1;
            Assert.Equal(0, item.PingBarsLevel);
            Assert.Equal(mutedText, Assert.IsAssignableFrom<ISolidColorBrush>(item.PingTextBrush).Color);
        }

        [Fact]
        public void ServerListViewModel_ArrangeRobloxWindowsCommand_ParsesModesAndExecutesSafely()
        {
            Assert.Equal(RobloxWindowLayoutMode.AutoGrid, RobloxWindowArrangerService.ParseLayoutMode(null));
            Assert.Equal(RobloxWindowLayoutMode.AutoGrid, RobloxWindowArrangerService.ParseLayoutMode("Auto"));
            Assert.Equal(RobloxWindowLayoutMode.Grid2x2, RobloxWindowArrangerService.ParseLayoutMode("2x2"));
            Assert.Equal(RobloxWindowLayoutMode.Grid3x2, RobloxWindowArrangerService.ParseLayoutMode("3x2"));
            Assert.Equal(RobloxWindowLayoutMode.Grid4x3, RobloxWindowArrangerService.ParseLayoutMode("4x3"));
            Assert.Equal(RobloxWindowLayoutMode.CascadeCorner, RobloxWindowArrangerService.ParseLayoutMode("Cascade"));
            Assert.Equal(RobloxWindowLayoutMode.Grid3x2, RobloxWindowArrangerService.ParseLayoutMode(RobloxWindowLayoutMode.Grid3x2));

            using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
            using var mainVm = new MainViewModel();
            var forwardedVm = mainVm.ServerListViewModel;
            Assert.Same(mainVm.ToolsStatus.ArrangeRobloxWindowsCommand, forwardedVm.ArrangeRobloxWindowsCommand);

            var forwardedEx = Record.Exception(() => forwardedVm.ArrangeRobloxWindowsCommand.Execute("2x2"));
            Assert.Null(forwardedEx);
            Assert.False(string.IsNullOrWhiteSpace(mainVm.StatusMessage));

            using var standaloneVm = new ServerListViewModel(null);
            foreach (string mode in new[] { "Auto", "2x2", "3x2", "4x3", "Cascade" })
            {
                standaloneVm.LoadingStatus = string.Empty;
                var ex = Record.Exception(() => standaloneVm.ArrangeRobloxWindowsCommand.Execute(mode));
                Assert.Null(ex);
                Assert.False(string.IsNullOrWhiteSpace(standaloneVm.LoadingStatus));
            }
        }

        [AvaloniaFact]
        public void ServerListView_AllFourSubTabs_RenderWithZeroBindingErrors_AndNullIcons()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);

            using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
            using var mainVm = new MainViewModel();

            var serverVm = mainVm.ServerListViewModel;
            serverVm.PlaceIdInput = string.Empty;
            var greenServer = new ServerItemViewModel { JobId = "job-green", Playing = 3, MaxPlayers = 12, Ping = 40, Region = "Singapore" };
            var amberServer = new ServerItemViewModel { JobId = "job-amber", Playing = 10, MaxPlayers = 12, Ping = 80, Region = "Tokyo" };
            var redServer = new ServerItemViewModel { JobId = "job-red", Playing = 12, MaxPlayers = 12, Ping = 210, Region = "Frankfurt" };
            serverVm.BrowserTab.SetRawServers(new[] { greenServer, amberServer, redServer });

            // PlaceId=0 avoids live HTTP thumbnail fetches while verifying null-icon fallback rendering
            serverVm.ExplorerTab.SearchGames.Add(new GameSearchItemViewModel
            {
                PlaceId = 0,
                Name = "Blox Fruits",
                PlayerCount = 420000,
                ThumbnailUrl = string.Empty
            });
            serverVm.LibraryTab.Favorites.Add(new FavoriteGameItemViewModel
            {
                PlaceId = 0,
                Name = "Adopt Me!",
                ThumbnailUrl = string.Empty
            });

            var view = new ServerListView { DataContext = serverVm };
            var window = new Window { Width = 1160, Height = 760, Content = view };

            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var txtPlaceId = view.FindControl<TextBox>("TxtPlaceId");
                Assert.NotNull(txtPlaceId);

                var resultsPanel = view.GetVisualDescendants().OfType<ServerListResultsPanel>().FirstOrDefault();
                Assert.NotNull(resultsPanel);

                // Tab 0: Server Table (virtualized rows keep Transitions=null + synchronized PingTextBrush)
                serverVm.SelectedSubTabIndex = 0;
                Dispatcher.UIThread.RunJobs();

                var progressBars = resultsPanel!.GetVisualDescendants().OfType<ProgressBar>().ToList();
                Assert.Equal(3, progressBars.Count);
                Assert.All(progressBars, pb => Assert.Null(pb.Transitions));

                var amberPingBlock = resultsPanel.GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(tb => tb.Text == "80 ms");
                Assert.NotNull(amberPingBlock);
                Assert.Equal(Color.Parse("#FBBF24"), Assert.IsAssignableFrom<ISolidColorBrush>(amberPingBlock!.Foreground).Color);

                var fastBtns = resultsPanel.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.Classes.Contains("FastIconBtn") || b.Classes.Contains("FastPillBtn"))
                    .ToList();
                Assert.NotEmpty(fastBtns);
                Assert.All(fastBtns, btn => Assert.Null(btn.Transitions));

                // Tab 1: Game Explorer (Playnite-style 60x60 SpotlightBorder cover card outside Lucide template)
                serverVm.SelectedSubTabIndex = 1;
                Dispatcher.UIThread.RunJobs();

                var explorerCards = resultsPanel.GetVisualDescendants().OfType<SpotlightBorder>()
                    .Where(c => c.Classes.Contains("ExplorerCard"))
                    .ToList();
                Assert.Single(explorerCards);
                var explorerThumb = explorerCards[0].GetVisualDescendants()
                    .OfType<Image>()
                    .Single(i => i.FindAncestorOfType<Lucide>() == null);
                Assert.Equal(60.0, explorerThumb.Width);
                Assert.Equal(60.0, explorerThumb.Height);

                // Tab 2: Game Library (Recent + Favorites 60x60 cards + Trash icon)
                serverVm.SelectedSubTabIndex = 2;
                mainVm.RecentGamesList.Add(new RecentGameItemViewModel
                {
                    PlaceId = 0,
                    Name = "Classic Crossroads",
                    ImageUrl = string.Empty
                });
                serverVm.LibraryTab.NotifyRecentGamesChanged();
                Dispatcher.UIThread.RunJobs();

                var libraryCards = view.GetVisualDescendants().OfType<SpotlightBorder>()
                    .Where(c => c.Classes.Contains("GameCard"))
                    .ToList();
                Assert.Equal(2, libraryCards.Count);
                foreach (var card in libraryCards)
                {
                    var thumb = card.GetVisualDescendants()
                        .OfType<Image>()
                        .Single(i => i.FindAncestorOfType<Lucide>() == null);
                    Assert.Equal(60.0, thumb.Width);
                    Assert.Equal(60.0, thumb.Height);
                }

                // Tab 3: Watcher & Window Tiling Preview Strip
                serverVm.SelectedSubTabIndex = 3;
                Dispatcher.UIThread.RunJobs();

                var watcherPanel = view.GetVisualDescendants().OfType<ServerListWatcherPanel>().FirstOrDefault();
                Assert.NotNull(watcherPanel);

                var tilingStrip = watcherPanel!.FindControl<Panel>("TilingPreviewStrip");
                Assert.NotNull(tilingStrip);
                var tileButtons = tilingStrip!.GetVisualDescendants().OfType<Button>().ToList();
                Assert.Equal(5, tileButtons.Count);
                Assert.Equal(
                    new[] { "Auto", "2x2", "3x2", "4x3", "Cascade" },
                    tileButtons.Select(b => b.CommandParameter as string).ToArray());
                Assert.All(tileButtons, b => Assert.NotNull(b.Command));

                // Every Lucide icon across all 4 tabs resolves cleanly and never uses Trash2
                var lucides = view.GetVisualDescendants().OfType<Lucide>().ToList();
                Assert.NotEmpty(lucides);
                Assert.Contains(lucides, icon => string.Equals(icon.Icon?.ToString(), "Trash", StringComparison.OrdinalIgnoreCase));
                foreach (var icon in lucides)
                {
                    Assert.False(string.Equals(icon.Icon?.ToString(), "Trash2", StringComparison.OrdinalIgnoreCase));
                    Assert.NotNull(icon.IconSource);
                }

                Assert.Empty(sink.Errors);
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
