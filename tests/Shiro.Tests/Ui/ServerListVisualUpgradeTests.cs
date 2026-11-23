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

        public ServerListVisualUpgradeTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "shiro-srv-v2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _prevSink = Logger.Sink;
            _prevLang = LocalizationService.CurrentLanguage;
        }

        public void Dispose()
        {
            ToastService.ClearAll();
            LocalizationService.SetLanguage(_prevLang, saveSetting: false);
            Application.Current?.Resources.MergedDictionaries.Clear();
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

            // 3/12 = 25% -> Green (#4ADE80)
            var lowCapBrush = Assert.IsAssignableFrom<ISolidColorBrush>(item.CapacityFillBrush);
            Assert.Equal(Color.Parse("#4ADE80"), lowCapBrush.Color);

            // 10/12 = 83.3% (75..94%) -> Amber (#FBBF24)
            item.Playing = 10;
            var midCapBrush = Assert.IsAssignableFrom<ISolidColorBrush>(item.CapacityFillBrush);
            Assert.Equal(Color.Parse("#FBBF24"), midCapBrush.Color);
            Assert.Contains(nameof(ServerItemViewModel.CapacityFillBrush), changed);

            // 12/12 = 100% (>= 95%) -> Crimson (#F87171)
            item.Playing = 12;
            var fullCapBrush = Assert.IsAssignableFrom<ISolidColorBrush>(item.CapacityFillBrush);
            Assert.Equal(Color.Parse("#F87171"), fullCapBrush.Color);

            // Boundary check on MaxPlayers change: 12/20 = 60% -> Green
            item.MaxPlayers = 20;
            Assert.Equal(Color.Parse("#4ADE80"), Assert.IsAssignableFrom<ISolidColorBrush>(item.CapacityFillBrush).Color);

            // Ping 40ms -> level 3 (all 3 bars green)
            Assert.Equal(3, item.PingBarsLevel);
            Assert.Equal(Color.Parse("#4ADE80"), Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar1Brush).Color);
            Assert.Equal(Color.Parse("#4ADE80"), Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar2Brush).Color);
            Assert.Equal(Color.Parse("#4ADE80"), Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar3Brush).Color);

            // Ping 110ms -> level 2 (2 bars amber, 3rd muted)
            changed.Clear();
            item.Ping = 110;
            Assert.Equal(2, item.PingBarsLevel);
            Assert.Contains(nameof(ServerItemViewModel.PingBarsLevel), changed);
            var bar2 = Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar2Brush);
            var bar3 = Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar3Brush);
            Assert.Equal(Color.Parse("#FBBF24"), bar2.Color);
            Assert.NotEqual(Color.Parse("#FBBF24"), bar3.Color);

            // Ping 210ms -> level 1 (1 bar crimson, 2nd & 3rd muted)
            item.Ping = 210;
            Assert.Equal(1, item.PingBarsLevel);
            Assert.Equal(Color.Parse("#F87171"), Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar1Brush).Color);
            Assert.NotEqual(Color.Parse("#F87171"), Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar2Brush).Color);
            Assert.NotEqual(Color.Parse("#F87171"), Assert.IsAssignableFrom<ISolidColorBrush>(item.PingBar3Brush).Color);

            // Unknown ping (-1) -> 0 active bars
            item.Ping = -1;
            Assert.Equal(0, item.PingBarsLevel);
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
            serverVm.PlaceIdInput = "1818";
            serverVm.BrowserTab.SetRawServers(new[]
            {
                new ServerItemViewModel { JobId = "job-green", Playing = 3, MaxPlayers = 12, Ping = 40, Region = "Singapore" },
                new ServerItemViewModel { JobId = "job-amber", Playing = 10, MaxPlayers = 12, Ping = 110, Region = "Tokyo" },
                new ServerItemViewModel { JobId = "job-red", Playing = 12, MaxPlayers = 12, Ping = 210, Region = "Frankfurt" }
            });
            serverVm.ExplorerTab.SearchGames.Add(new GameSearchItemViewModel
            {
                PlaceId = 2753915549,
                Name = "Blox Fruits",
                PlayerCount = 420000,
                ThumbnailUrl = string.Empty
            });
            serverVm.LibraryTab.Favorites.Add(new FavoriteGameItemViewModel
            {
                PlaceId = 920587237,
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

                // Tab 0: Server Table (virtualized rows keep Transitions=null + semantic capacity ProgressBar)
                serverVm.SelectedSubTabIndex = 0;
                Dispatcher.UIThread.RunJobs();

                var progressBars = resultsPanel!.GetVisualDescendants().OfType<ProgressBar>().ToList();
                Assert.Equal(3, progressBars.Count);
                Assert.All(progressBars, pb => Assert.Null(pb.Transitions));

                var fastBtns = resultsPanel.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.Classes.Contains("FastIconBtn") || b.Classes.Contains("FastPillBtn"))
                    .ToList();
                Assert.NotEmpty(fastBtns);
                Assert.All(fastBtns, btn => Assert.Null(btn.Transitions));

                // Tab 1: Game Explorer (Playnite-style 60x60 SpotlightBorder cover cards)
                serverVm.SelectedSubTabIndex = 1;
                Dispatcher.UIThread.RunJobs();

                var explorerCards = resultsPanel.GetVisualDescendants().OfType<SpotlightBorder>()
                    .Where(c => c.Classes.Contains("ExplorerCard"))
                    .ToList();
                Assert.Single(explorerCards);
                var explorerThumb = explorerCards[0].GetVisualDescendants().OfType<Image>().FirstOrDefault();
                Assert.NotNull(explorerThumb);
                Assert.Equal(60.0, explorerThumb!.Width);
                Assert.Equal(60.0, explorerThumb.Height);

                // Tab 2: Game Library (Recent + Favorites 60x60 cards + Trash icon)
                serverVm.SelectedSubTabIndex = 2;
                mainVm.RecentGamesList.Add(new RecentGameItemViewModel
                {
                    PlaceId = 1818,
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
                    var thumb = card.GetVisualDescendants().OfType<Image>().FirstOrDefault();
                    Assert.NotNull(thumb);
                    Assert.Equal(60.0, thumb!.Width);
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
