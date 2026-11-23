#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Logging;
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
    public sealed class CommandPalette2Tests : IDisposable
    {
        private readonly string _tempDir;
        private readonly ILogSink? _prevSink;
        private readonly AppLanguage _prevLang;
        private readonly bool _prevStreamer;
        private readonly bool _prevStreamerAuto;
        private readonly bool _prevPotato;
        private readonly MainWindow? _prevWindow;
        private readonly Action<Action>? _prevDispatcher;
        private readonly SurfacePreset _origPreset;
        private readonly AccentTheme _origTheme;
        private readonly List<IResourceProvider>? _origMergedDictionaries;

        public CommandPalette2Tests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "shiro-cp2-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _prevSink = Logger.Sink;
            _prevLang = LocalizationService.CurrentLanguage;
            _prevStreamer = StreamerModeService.IsEnabled;
            _prevStreamerAuto = StreamerModeService.AutoDetectEnabled;
            _prevPotato = AppServices.GeneralSettings.Get<bool>(AppSettingsKeys.General.EnablePotatoMode);
            _prevWindow = MainWindow.Instance;
            _prevDispatcher = AppDispatcher.CustomDispatcher;
            _origPreset = AccentThemeService.CurrentSurfacePreset;
            _origTheme = AccentThemeService.CurrentTheme;
            _origMergedDictionaries = Application.Current?.Resources.MergedDictionaries.ToList();
        }

        public void Dispose()
        {
            AppDispatcher.CustomDispatcher = _prevDispatcher;
            StreamerModeService.IsEnabled = _prevStreamer;
            StreamerModeService.AutoDetectEnabled = _prevStreamerAuto;
            AccentThemeService.ApplySurfacePreset(_origPreset, save: false);
            AccentThemeService.ApplyTheme(_origTheme, save: false);
            LocalizationService.SetLanguage(_prevLang, saveSetting: false);
            if (Application.Current?.Resources.MergedDictionaries is { } merged && _origMergedDictionaries != null)
            {
                merged.Clear();
                foreach (var dict in _origMergedDictionaries)
                    merged.Add(dict);
            }
            Logger.Sink = _prevSink;
            ToastService.ClearAll();
            typeof(MainWindow).GetProperty(nameof(MainWindow.Instance))?.SetValue(null, _prevWindow);
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        [Fact]
        public void CommandPalette2_ScopeFiltering_RestrictsItemsToAccountsOrCommands()
        {
            AppDispatcher.CustomDispatcher = a => a();
            using var vm = new MainViewModel();
            vm.LoadAccounts(new[]
            {
                new Account { UserID = 101, Username = "ScopeAlpha", Valid = true, SecurityToken = "t1" },
                new Account { UserID = 102, Username = "ScopeBravo", Valid = true, SecurityToken = "t2" },
                new Account { UserID = 103, Username = "ScopeCharlie", Valid = false, SecurityToken = "t3" }
            });

            var palette = vm.CommandPalette;
            Assert.Equal(CommandPaletteScope.All, palette.ActiveScope);

            palette.IsCommandPaletteOpen = true;
            Assert.Contains(palette.CommandPaletteItems, i => i.IsCommand);
            Assert.Contains(palette.CommandPaletteItems, i => i.IsAccount);

            palette.ActiveScope = CommandPaletteScope.Accounts;
            Assert.True(palette.CommandPaletteItems.Count >= 3);
            Assert.All(palette.CommandPaletteItems, i =>
            {
                Assert.True(i.IsAccount);
                Assert.False(i.IsCommand);
            });

            palette.SetScopeCommand.Execute(CommandPaletteScope.Commands);
            Assert.Equal(CommandPaletteScope.Commands, palette.ActiveScope);
            Assert.NotEmpty(palette.CommandPaletteItems);
            Assert.All(palette.CommandPaletteItems, i =>
            {
                Assert.True(i.IsCommand);
                Assert.False(i.IsAccount);
            });

            palette.SetScopeCommand.Execute(CommandPaletteScope.All);
            Assert.Equal(CommandPaletteScope.All, palette.ActiveScope);
            Assert.Contains(palette.CommandPaletteItems, i => i.IsCommand);
            Assert.Contains(palette.CommandPaletteItems, i => i.IsAccount);
        }

        [Fact]
        public void CommandPalette2_RecentHistory_PromotesExecutedCommands_WithoutBreaking29BuiltinCount()
        {
            AppDispatcher.CustomDispatcher = a => a();
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
            using var vm = new MainViewModel();
            var palette = vm.CommandPalette;
            bool initialPotato = vm.ToolsStatus.IsPotatoMode;

            try
            {
                palette.IsCommandPaletteOpen = true;
                palette.CommandPaletteSearchText = "settings";
                Assert.True(palette.CommandPaletteItems.Count >= 1);
                palette.SetSelectedCommandPaletteIndex(0);
                Assert.True(palette.ExecuteCommandPaletteSelection());

                palette.IsCommandPaletteOpen = true;
                palette.CommandPaletteSearchText = "potato";
                Assert.True(palette.CommandPaletteItems.Count >= 1);
                palette.SetSelectedCommandPaletteIndex(0);
                Assert.True(palette.ExecuteCommandPaletteSelection());

                Assert.Equal(2, palette.RecentCommandIds.Count);
                Assert.Equal("tool.potato", palette.RecentCommandIds[0]);
                Assert.Equal("nav.settings", palette.RecentCommandIds[1]);

                // Reopen with empty query: recent commands must appear first
                palette.IsCommandPaletteOpen = true;
                Assert.Equal(string.Empty, palette.CommandPaletteSearchText);
                Assert.True(palette.CommandPaletteItems.Count >= 2);
                Assert.Equal("tool.potato", palette.CommandPaletteItems[0].CommandId);
                Assert.Equal("nav.settings", palette.CommandPaletteItems[1].CommandId);
                Assert.True(palette.CommandPaletteItems[0].IsRecent);
                Assert.True(palette.CommandPaletteItems[1].IsRecent);

                // Executing more than 5 distinct commands caps MRU history at 5
                string[] extraQueries = { "select all", "clear", "invert", "accounts", "server list", "recent games" };
                foreach (string q in extraQueries)
                {
                    palette.IsCommandPaletteOpen = true;
                    palette.ActiveScope = CommandPaletteScope.Commands;
                    palette.CommandPaletteSearchText = q;
                    if (palette.CommandPaletteItems.Count > 0)
                    {
                        palette.SetSelectedCommandPaletteIndex(0);
                        palette.ExecuteCommandPaletteSelection();
                    }
                }

                Assert.Equal(5, palette.RecentCommandIds.Count);

                // ArchitectureTests built-in count invariant must remain exactly 29
                var method = typeof(MainViewModel).GetMethod(
                    "GetBuiltinCommands",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(method);
                var commands = (System.Collections.IEnumerable)method!.Invoke(null, null)!;
                Assert.Equal(29, commands.Cast<object>().Count());
                new ArchitectureTests().AllCommandPaletteBuiltinCommands_ShouldMapToValidLucideIcons();
            }
            finally
            {
                vm.ToolsStatus.IsPotatoMode = initialPotato;
                AppServices.GeneralSettings.Set(AppSettingsKeys.General.EnablePotatoMode, _prevPotato ? "true" : "false");
            }
        }

        [Fact]
        public void CommandPalette2_LivePreview_UpdatesSynchronouslyWithArrowNavigation()
        {
            AppDispatcher.CustomDispatcher = a => a();
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
            StreamerModeService.AutoDetectEnabled = false;
            StreamerModeService.IsEnabled = false;

            using var vm = new MainViewModel();
            var acc1 = new Account
            {
                UserID = 9001,
                Username = "PreviewHero",
                Alias = "MainFarmer",
                Group = "FarmZone",
                RobuxBalance = 2450,
                Valid = true,
                SecurityToken = "tok-9001"
            };
            var acc2 = new Account
            {
                UserID = 9002,
                Username = "PreviewAlt",
                Group = "Default",
                RobuxBalance = -1,
                Valid = true,
                SecurityToken = "tok-9002"
            };
            vm.LoadAccounts(new[] { acc1, acc2 });
            vm.Accounts[0].IsInGame = true;
            vm.Accounts[0].LiveMemoryText = "412 MB";

            var palette = vm.CommandPalette;
            palette.IsCommandPaletteOpen = true;
            Assert.True(palette.HasPreviewItem);
            Assert.Same(palette.CommandPaletteItems[0], palette.SelectedCommandPaletteItem);
            Assert.False(string.IsNullOrWhiteSpace(palette.SelectedCommandPaletteItem!.PrimaryHintText));
            Assert.False(string.IsNullOrWhiteSpace(palette.SelectedCommandPaletteItem.SecondaryHintText));

            // Move to next item synchronously
            palette.MoveCommandPaletteSelection(1);
            Assert.Same(palette.CommandPaletteItems[1], palette.SelectedCommandPaletteItem);
            Assert.True(palette.HasPreviewItem);

            // Switch to Accounts scope to verify account preview metadata
            palette.ActiveScope = CommandPaletteScope.Accounts;
            Assert.True(palette.HasPreviewItem);
            var firstAccItem = palette.SelectedCommandPaletteItem;
            Assert.NotNull(firstAccItem);
            Assert.True(firstAccItem!.IsAccount);
            Assert.Contains("412 MB", firstAccItem.PreviewStatusText, StringComparison.Ordinal);
            Assert.Equal("R$ 2,450", firstAccItem.PreviewRobuxText);
            Assert.Equal("FarmZone", firstAccItem.PreviewGroupText);
            Assert.Contains("Enter", firstAccItem.PrimaryHintText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Shift+Enter", firstAccItem.SecondaryHintText, StringComparison.OrdinalIgnoreCase);

            palette.MoveCommandPaletteSelection(1);
            var secondAccItem = palette.SelectedCommandPaletteItem;
            Assert.NotNull(secondAccItem);
            Assert.NotSame(firstAccItem, secondAccItem);
            Assert.Equal("Default", secondAccItem!.PreviewGroupText);

            // Empty search match clears preview item cleanly
            palette.CommandPaletteSearchText = "__no_palette_match_999__";
            Assert.Empty(palette.CommandPaletteItems);
            Assert.Null(palette.SelectedCommandPaletteItem);
            Assert.False(palette.HasPreviewItem);
        }

        [AvaloniaFact]
        public void CommandPalette2_OverlayPanel_RendersTwoColumns_WithoutBindingErrors()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            var app = Application.Current!;
            if (!app.Resources.TryGetResource("RadiusOuter16", null, out _))
            {
                app.Resources.MergedDictionaries.Add((ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
                    new Uri("avares://Shiro/AvaloniaUI/Styles/ThemeTokens.axaml")));
            }
            AccentThemeService.ApplySurfacePreset(SurfacePreset.LiquidObsidian, save: false);
            AccentThemeService.ApplyTheme(AccentTheme.Monochrome, save: false);
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);

            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [AppSettingsKeys.General.HasAcceptedRiskDisclaimer] = "true"
            };
            using var dataDirScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
            using var settingsScope = AppServices.Override<ISettingsService>(CreateMockSettings(store));
            using var repoScope = AppServices.Override<IAccountRepository>(CreateMockRepo(
                new Account { UserID = 701, Username = "GlassPlayer", Group = "VIP", RobuxBalance = 500, Valid = true, SecurityToken = "t701" },
                new Account { UserID = 702, Username = "GlassAlt", Group = "Default", RobuxBalance = 120, Valid = true, SecurityToken = "t702" }));

            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var overlayPanel = window.FindControl<CommandPaletteOverlayPanel>("ViewCommandPalette");
                Assert.NotNull(overlayPanel);
                Assert.NotNull(overlayPanel!.SearchTextBox);

                var vm = window.ViewModel!;
                vm.CommandPalette.IsCommandPaletteOpen = true;
                Dispatcher.UIThread.RunJobs();

                var glassSurface = overlayPanel.GetVisualDescendants().OfType<SkiaGlassSurface>().FirstOrDefault();
                Assert.NotNull(glassSurface);
                Assert.Equal(720.0, glassSurface!.Width);
                Assert.Equal(new CornerRadius(16), glassSurface.CornerRadius);

                var btnAccounts = overlayPanel.FindControl<Button>("BtnScopeAccounts");
                var btnCommands = overlayPanel.FindControl<Button>("BtnScopeCommands");
                var btnAll = overlayPanel.FindControl<Button>("BtnScopeAll");
                Assert.NotNull(btnAccounts);
                Assert.NotNull(btnCommands);
                Assert.NotNull(btnAll);

                Assert.NotNull(btnAccounts!.Command);
                Assert.Equal(CommandPaletteScope.Accounts, btnAccounts.CommandParameter);
                btnAccounts.Command!.Execute(btnAccounts.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(CommandPaletteScope.Accounts, vm.CommandPalette.ActiveScope);
                Assert.Contains("active", btnAccounts.Classes);
                Assert.All(vm.CommandPalette.CommandPaletteItems, item => Assert.True(item.IsAccount));

                var prevSelected = vm.CommandPalette.SelectedCommandPaletteItem;
                overlayPanel.SearchTextBox!.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Down
                });
                Dispatcher.UIThread.RunJobs();
                Assert.NotSame(prevSelected, vm.CommandPalette.SelectedCommandPaletteItem);

                // StreamerMode masks Robux & Username in Live Preview Pane
                StreamerModeService.AutoDetectEnabled = false;
                StreamerModeService.IsEnabled = true;
                vm.CommandPalette.UpdateCommandPaletteResults();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("R$ ***", vm.CommandPalette.SelectedCommandPaletteItem?.PreviewRobuxText);

                Assert.NotNull(btnCommands!.Command);
                Assert.Equal(CommandPaletteScope.Commands, btnCommands.CommandParameter);
                btnCommands.Command!.Execute(btnCommands.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(CommandPaletteScope.Commands, vm.CommandPalette.ActiveScope);
                Assert.Contains("active", btnCommands.Classes);
                Assert.All(vm.CommandPalette.CommandPaletteItems, item => Assert.True(item.IsCommand));

                Assert.Empty(sink.Errors);
            }
            finally
            {
                window?.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        private static ISettingsService CreateMockSettings(Dictionary<string, string?> store)
        {
            var settings = Substitute.For<ISettingsService>();
            settings.Get(Arg.Any<string>()).Returns(ci =>
                store.TryGetValue(ci.Arg<string>(), out string? val) ? val : null);
            settings.Get<bool>(Arg.Any<string>()).Returns(ci =>
                store.TryGetValue(ci.Arg<string>(), out string? val) && bool.TryParse(val, out bool parsed) && parsed);
            settings.Get(Arg.Any<string>(), Arg.Any<bool>()).Returns(ci =>
                store.TryGetValue(ci.ArgAt<string>(0), out string? val) && bool.TryParse(val, out bool parsed)
                    ? parsed
                    : ci.ArgAt<bool>(1));
            settings.Exists(Arg.Any<string>()).Returns(ci =>
                store.ContainsKey(ci.Arg<string>()));
            settings.When(s => s.Set(Arg.Any<string>(), Arg.Any<string?>()))
                .Do(ci =>
                {
                    string key = ci.ArgAt<string>(0);
                    string? val = ci.ArgAt<string?>(1);
                    if (val is null) store.Remove(key);
                    else store[key] = val;
                });
            return settings;
        }

        private static IAccountRepository CreateMockRepo(params Account[] accounts)
        {
            var list = accounts.ToList();
            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(list);
            repo.Count.Returns(list.Count);
            repo.IsPasswordEncrypted().Returns(false);
            return repo;
        }
    }
}
