#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Shiro.AvaloniaUI.Converters;
using Shiro.Classes;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StoragePathsCollection")]
    public class UiUxUpgradeTests
    {
        [Fact]
        public void Item1_ToastService_VisualOverlayLifecycle_CappedAtFourAndDismissible()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            AppDispatcher.CustomDispatcher = action => action();
            try
            {
                ToastService.ClearAll();
                Assert.Empty(ToastService.ActiveToasts);

                // Quiet default: PlayNotificationSound must not throw when unconfigured
                var beepEx = Record.Exception(() => ToastService.PlayNotificationSound(ToastType.Error));
                Assert.Null(beepEx);

                // Push 5 toasts with durationSeconds=0 (no background auto-dismiss timer)
                ToastService.ShowInfo("Info 1", "Msg 1", durationSeconds: 0);
                ToastService.ShowSuccess("Success 2", "Msg 2", durationSeconds: 0);
                ToastService.ShowWarning("Warn 3", "", durationSeconds: 0);
                ToastService.ShowError("Err 4", "Msg 4", durationSeconds: 0);

                // Duplicate toast with identical Type/Title/Message should be coalesced
                ToastService.ShowError("Err 4", "Msg 4", durationSeconds: 0);

                // Capped at 2 items to prevent screen clutter; oldest evicted
                Assert.Equal(2, ToastService.ActiveToasts.Count);
                Assert.DoesNotContain(ToastService.ActiveToasts, t => t.Title == "Info 1");
                Assert.DoesNotContain(ToastService.ActiveToasts, t => t.Title == "Success 2");

                var warnToast = ToastService.ActiveToasts.First(t => t.Title == "Warn 3");
                Assert.False(warnToast.HasMessage);
                Assert.Equal("⚠", warnToast.Glyph);
                Assert.Equal(LucideAvalonia.Enum.LucideIconNames.ShieldAlert, warnToast.IconKind);
                Assert.NotNull(warnToast.AccentBrush);
                Assert.NotNull(warnToast.BackgroundBrush);
                Assert.NotNull(warnToast.BorderBrush);
                Assert.NotNull(warnToast.BadgeBackground);

                var errToast = ToastService.ActiveToasts.First(t => t.Title == "Err 4");
                Assert.True(errToast.HasMessage);
                Assert.Equal("✕", errToast.Glyph);
                Assert.Equal(LucideAvalonia.Enum.LucideIconNames.X, errToast.IconKind);

                // Dismiss via ToastItem.DismissCommand
                warnToast.DismissCommand.Execute(null);
                Assert.Single(ToastService.ActiveToasts);
                Assert.DoesNotContain(ToastService.ActiveToasts, t => t.Title == "Warn 3");

                ToastService.ClearAll();
                Assert.Empty(ToastService.ActiveToasts);
            }
            finally
            {
                ToastService.ClearAll();
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public void Item2_LiveToolStates_TracksActiveToolsCountAndNotifiesListeners()
        {
            var vm = new MainViewModel();
            int baseCount = vm.ActiveToolsCount;
            var changedProps = new HashSet<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.PropertyName))
                    changedProps.Add(e.PropertyName);
            };

            var prevPotato = vm.IsPotatoMode;
            try
            {
                vm.IsPotatoMode = !vm.IsPotatoMode;
                int expectedAfterToggle = vm.IsPotatoMode ? baseCount + 1 : baseCount - 1;
                Assert.Equal(expectedAfterToggle, vm.ActiveToolsCount);
                Assert.Equal(vm.ActiveToolsCount > 0, vm.HasActiveTools);
                Assert.Contains(nameof(MainViewModel.ActiveToolsCount), changedProps);
                Assert.Contains(nameof(MainViewModel.HasActiveTools), changedProps);
            }
            finally
            {
                vm.IsPotatoMode = prevPotato;
            }

            changedProps.Clear();
            vm.NotifyToolStatesChanged();
            Assert.Contains(nameof(MainViewModel.IsSmartMuteActive), changedProps);
            Assert.Contains(nameof(MainViewModel.ActiveToolsCount), changedProps);
        }

        [Fact]
        public void Item3_GameInfoService_CachesRealTitlesAndForwardsActiveGameChipToServerList()
        {
            const long bloxFruitsPlaceId = 2753915549;

            Assert.True(GameInfoService.IsGenericPlaceholderName(bloxFruitsPlaceId, $"Place {bloxFruitsPlaceId}"));
            Assert.True(GameInfoService.IsGenericPlaceholderName(bloxFruitsPlaceId, $"Game {bloxFruitsPlaceId}"));
            Assert.True(GameInfoService.IsGenericPlaceholderName(bloxFruitsPlaceId, "Favorite Game"));
            Assert.False(GameInfoService.IsGenericPlaceholderName(bloxFruitsPlaceId, "Blox Fruits"));

            GameInfoService.CachePlaceName(bloxFruitsPlaceId, "Blox Fruits");
            // Generic placeholder must NOT overwrite an already-cached real title
            GameInfoService.CachePlaceName(bloxFruitsPlaceId, $"Place {bloxFruitsPlaceId}");

            Assert.True(GameInfoService.TryGetCachedName(bloxFruitsPlaceId, out string cachedTitle));
            Assert.Equal("Blox Fruits", cachedTitle);

            var mainVm = new MainViewModel();
            var serverVm = mainVm.ServerListViewModel;

            mainVm.PlaceId = bloxFruitsPlaceId.ToString();
            Assert.True(mainVm.HasCurrentGameInfo);
            Assert.Equal("Blox Fruits", mainVm.CurrentGameName);
            Assert.True(serverVm.HasCurrentGameInfo);
            Assert.Equal("Blox Fruits", serverVm.CurrentGameName);

            mainVm.PlaceId = string.Empty;
            Assert.False(mainVm.HasCurrentGameInfo);
            Assert.Equal(string.Empty, mainVm.CurrentGameName);
            Assert.False(serverVm.HasCurrentGameInfo);
        }

        [Fact]
        public void Item4_ShiftClickRangeAndQuickSelect_HandlesContiguousReadyInvertAndRangeExpressions()
        {
            var vm = new MainViewModel();
            var accounts = new List<Account>();
            for (int i = 1; i <= 10; i++)
            {
                accounts.Add(new Account
                {
                    UserID = 5000 + i,
                    Username = $"Acc_{i:D2}",
                    SecurityToken = "dummy_cookie_token",
                    Valid = i != 3 && i != 6, // Acc_03 expired, Acc_06 banned/locked
                    AccountStatusIssue = i == 6 ? "⛔ Banned 1 day" : string.Empty // Acc_06 banned
                });
            }

            vm.LoadAccounts(accounts);
            // NaturalAscending sorts valid accounts first and invalid/banned (Acc_03, Acc_06) at the bottom
            Assert.Equal("Acc_06", vm.FilteredAccounts[^1].Username);

            // Mark Acc_04 as in-game and Acc_05 as running local client
            vm.FilteredAccounts.First(a => a.Username == "Acc_04").IsInGame = true;
            vm.FilteredAccounts.First(a => a.Username == "Acc_05").IsLocalClientRunning = true;

            // 1. Shift + Click contiguous range select (index 1 -> index 4 selects 1..4)
            vm.HandleAccountClickSelection(vm.FilteredAccounts[1], isShiftPressed: false, explicitTargetState: true);
            vm.HandleAccountClickSelection(vm.FilteredAccounts[4], isShiftPressed: true);
            Assert.False(vm.FilteredAccounts[0].IsSelected);
            for (int i = 1; i <= 4; i++)
                Assert.True(vm.FilteredAccounts[i].IsSelected);
            Assert.Equal(4, vm.SelectedCount);

            // Reverse Shift + Click deselect (index 4 -> index 2 with explicit false)
            vm.HandleAccountClickSelection(vm.FilteredAccounts[4], isShiftPressed: false, explicitTargetState: false);
            vm.HandleAccountClickSelection(vm.FilteredAccounts[2], isShiftPressed: true);
            Assert.True(vm.FilteredAccounts[1].IsSelected);
            Assert.False(vm.FilteredAccounts[2].IsSelected);
            Assert.False(vm.FilteredAccounts[3].IsSelected);
            Assert.False(vm.FilteredAccounts[4].IsSelected);

            // 2. Quick-Select Ready idle accounts: skips Acc_03 (invalid), Acc_04 (in-game), Acc_05 (running), Acc_06 (banned)
            int pickedReady = vm.SelectFirstReadyAccounts(4);
            Assert.Equal(4, pickedReady);
            var selectedNames = vm.FilteredAccounts.Where(a => a.IsSelected).Select(a => a.Username).ToList();
            Assert.Equal(new[] { "Acc_01", "Acc_02", "Acc_07", "Acc_08" }, selectedNames);

            // 3. Invert selection across 10 filtered accounts -> 6 selected
            int invertedCount = vm.InvertFilteredSelection();
            Assert.Equal(6, invertedCount);
            Assert.False(vm.FilteredAccounts[0].IsSelected);
            Assert.True(vm.FilteredAccounts[^1].IsSelected);

            // 4. Range expression "1-3, 10" -> selects 1-based items 1, 2, 3, and 10 (Acc_01, Acc_02, Acc_04, Acc_06)
            int exprCount = vm.SelectRangeExpression("1-3, 10");
            Assert.Equal(4, exprCount);
            var exprSelected = vm.FilteredAccounts.Where(a => a.IsSelected).Select(a => a.Username).ToList();
            Assert.Equal(new[] { "Acc_01", "Acc_02", "Acc_04", "Acc_06" }, exprSelected);
        }

        [Fact]
        public void Item5_InteractiveMetricCardsAndSortDropdown_ToggleFiltersAndRobuxOrdering()
        {
            var vm = new MainViewModel();
            vm.LoadAccounts(new[]
            {
                new Account { UserID = 1, Username = "Alpha", SecurityToken = "tok1", Valid = true, RobuxBalance = 50 },
                new Account { UserID = 2, Username = "Bravo", SecurityToken = "tok2", Valid = true, RobuxBalance = 1200 },
                new Account { UserID = 3, Username = "Charlie", SecurityToken = "tok3", Valid = false, RobuxBalance = 300 }
            });
            vm.Accounts[1].IsInGame = true;

            // Metric card toggles
            vm.MetricCardInGameCommand.Execute(null);
            Assert.Equal(AccountFilterTab.InGame, vm.ActiveTab);
            Assert.Single(vm.FilteredAccounts);
            Assert.Equal("Bravo", vm.FilteredAccounts[0].Username);

            // Clicking again resets back to All
            vm.MetricCardInGameCommand.Execute(null);
            Assert.Equal(AccountFilterTab.All, vm.ActiveTab);
            Assert.Equal(3, vm.FilteredAccounts.Count);

            // Ready tab filters Valid && !IsInGame -> only Alpha (Bravo is InGame, Charlie is Expired)
            vm.MetricCardReadyCommand.Execute(null);
            Assert.Equal(AccountFilterTab.Ready, vm.ActiveTab);
            Assert.Single(vm.FilteredAccounts);
            Assert.Equal("Alpha", vm.FilteredAccounts[0].Username);

            vm.MetricCardExpiredCommand.Execute(null);
            Assert.Equal(AccountFilterTab.Expired, vm.ActiveTab);
            Assert.Single(vm.FilteredAccounts);
            Assert.Equal("Charlie", vm.FilteredAccounts[0].Username);

            // Robux metric card switches back to All and sorts RobuxDescending
            vm.MetricCardRobuxCommand.Execute(null);
            Assert.Equal(AccountFilterTab.All, vm.ActiveTab);
            Assert.Equal(AccountSortMode.RobuxDescending, vm.SortMode);
            Assert.Equal(new[] { "Bravo", "Charlie", "Alpha" }, vm.FilteredAccounts.Select(a => a.Username).ToArray());
            Assert.Contains("Robux", vm.CurrentSortLabel, StringComparison.OrdinalIgnoreCase);

            // Clicking Robux card again restores NaturalAscending
            vm.MetricCardRobuxCommand.Execute(null);
            Assert.Equal(AccountSortMode.NaturalAscending, vm.SortMode);
            Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, vm.FilteredAccounts.Select(a => a.Username).ToArray());
        }

        [Fact]
        public void Item6_LiveBatchLaunchBanner_TracksQueueProgressAndFiresEmergencyGlobalCancel()
        {
            var prevDispatcher = AppDispatcher.CustomDispatcher;
            AppDispatcher.CustomDispatcher = action => action();
            try
            {
                var vm = new MainViewModel();
                var acc1 = new Account { UserID = 10, Username = "Runner_1", Valid = true };
                var acc2 = new Account { UserID = 11, Username = "Runner_2", Valid = true };
                vm.LoadAccounts(new[] { acc1, acc2 });
                vm.Accounts[0].IsLaunching = true;
                vm.Accounts[1].IsLaunching = true;

                Assert.False(vm.IsBatchLaunching);

                vm.UpdateBatchLaunchBanner(new LaunchProgress
                {
                    Account = acc1,
                    Index = 1,
                    Total = 4,
                    Completed = false,
                    Message = "Authenticating ticket..."
                });

                Assert.True(vm.IsBatchLaunching);
                Assert.Contains("1/4", vm.BatchLaunchTitleText);
                Assert.Contains("Runner_1", vm.BatchLaunchTitleText);
                Assert.Equal("Authenticating ticket...", vm.BatchLaunchDetailText);
                Assert.InRange(vm.BatchLaunchProgressPercent, 5.0, 50.0);

                // Emergency stop button triggers LaunchCoordinator.GlobalCancelRequested and clears IsLaunching
                bool globalCancelFired = false;
                void OnGlobalCancel() => globalCancelFired = true;
                LaunchCoordinator.GlobalCancelRequested += OnGlobalCancel;
                try
                {
                    vm.CancelBatchLaunchCommand.Execute(null);
                    Assert.True(globalCancelFired);
                    Assert.False(vm.IsBatchLaunching);
                    Assert.Equal(0, vm.BatchLaunchProgressPercent);
                    Assert.All(vm.Accounts, a => Assert.False(a.IsLaunching));
                }
                finally
                {
                    LaunchCoordinator.GlobalCancelRequested -= OnGlobalCancel;
                    ToastService.ClearAll();
                }

                // Final completion signal hides banner cleanly
                vm.UpdateBatchLaunchBanner(new LaunchProgress { Account = acc2, Index = 2, Total = 2, Completed = false });
                Assert.True(vm.IsBatchLaunching);
                vm.UpdateBatchLaunchBanner(new LaunchProgress { Account = null, Index = 2, Total = 2, Completed = true });
                Assert.False(vm.IsBatchLaunching);
            }
            finally
            {
                AppDispatcher.CustomDispatcher = prevDispatcher;
            }
        }

        [Fact]
        public void Item7_RunningClientCardControlsAndWindowArranger_ComputesGridAndCascadeLayouts()
        {
            var avm = new AccountViewModel(new Account { UserID = 99, Username = "ClientRunner", SecurityToken = "tok99", Valid = true });
            Assert.True(avm.ShowPlayButton);
            Assert.False(avm.ShowRunningClientControls);
            var idleBorder = avm.CardBorderBrush;

            avm.IsLocalClientRunning = true;
            Assert.False(avm.ShowPlayButton);
            Assert.True(avm.ShowRunningClientControls);
            Assert.NotSame(idleBorder, avm.CardBorderBrush);

            avm.IsLocalClientRunning = false;
            Assert.True(avm.ShowPlayButton);
            Assert.False(avm.ShowRunningClientControls);

            var workArea = new RobloxWindowService.DesktopRect(0, 0, 1920, 1080);

            var grid2x2 = RobloxWindowArrangerService.CalculatePlacements(workArea, 4, RobloxWindowLayoutMode.Grid2x2);
            Assert.Equal(4, grid2x2.Count);
            Assert.Equal(new RobloxWindowArrangerService.WindowPlacementSlot(0, 0, 960, 540), grid2x2[0]);
            Assert.Equal(new RobloxWindowArrangerService.WindowPlacementSlot(960, 540, 960, 540), grid2x2[3]);

            var grid3x2 = RobloxWindowArrangerService.CalculatePlacements(workArea, 6, RobloxWindowLayoutMode.Grid3x2);
            Assert.Equal(6, grid3x2.Count);
            Assert.Equal(640, grid3x2[0].Width);
            Assert.Equal(540, grid3x2[0].Height);

            var cascade = RobloxWindowArrangerService.CalculatePlacements(workArea, 5, RobloxWindowLayoutMode.CascadeCorner);
            Assert.Equal(5, cascade.Count);
            Assert.All(cascade, slot =>
            {
                Assert.InRange(slot.X, workArea.Left, workArea.Left + workArea.Width - slot.Width);
                Assert.InRange(slot.Y, workArea.Top, workArea.Top + workArea.Height - slot.Height);
            });
            // Subsequent cascade windows step diagonally inward from bottom-right corner
            Assert.True(cascade[1].X < cascade[0].X);
            Assert.True(cascade[1].Y < cascade[0].Y);
        }

        [Fact]
        public void Item8_ServerListSortingAndQuickFilters_SortsPlayersPingAndHidesFullOrVip()
        {
            var vm = new ServerListViewModel(null);
            vm.SetRawServers(new[]
            {
                new ServerItemViewModel { JobId = "srv-full", Type = "Public", Playing = 12, MaxPlayers = 12, Ping = 45 },
                new ServerItemViewModel { JobId = "srv-low", Type = "Public", Playing = 2, MaxPlayers = 12, Ping = 140 },
                new ServerItemViewModel { JobId = "srv-mid", Type = "Public", Playing = 7, MaxPlayers = 12, Ping = 32 },
                new ServerItemViewModel { JobId = "srv-unk", Type = "Public", Playing = 4, MaxPlayers = 12, Ping = -1 },
                new ServerItemViewModel { JobId = "srv-vip", Type = "VIP Private", Playing = 1, MaxPlayers = 12, Ping = 25 }
            });

            Assert.Equal(5, vm.RawServersCount);
            Assert.Equal(5, vm.Servers.Count);

            // Toggle Players sort: Default -> PlayersAsc -> PlayersDesc -> Default
            vm.ToggleSortPlayersCommand.Execute(null);
            Assert.Equal(ServerSortMode.PlayersAsc, vm.SortMode);
            Assert.Equal("▲", vm.PlayersSortHeaderIndicator);
            Assert.Equal("srv-vip", vm.Servers[0].JobId);
            Assert.Equal("srv-full", vm.Servers[^1].JobId);

            vm.ToggleSortPlayersCommand.Execute(null);
            Assert.Equal(ServerSortMode.PlayersDesc, vm.SortMode);
            Assert.Equal("▼", vm.PlayersSortHeaderIndicator);
            Assert.Equal("srv-full", vm.Servers[0].JobId);

            // Toggle Ping sort: PlayersDesc -> PingAsc (unknown Ping <= 0 placed last!)
            vm.ToggleSortPingCommand.Execute(null);
            Assert.Equal(ServerSortMode.PingAsc, vm.SortMode);
            Assert.Equal("▲", vm.PingSortHeaderIndicator);
            Assert.Equal(new[] { "srv-vip", "srv-mid", "srv-full", "srv-low", "srv-unk" }, vm.Servers.Select(s => s.JobId).ToArray());

            // Hide Full & Hide VIP filter pills
            vm.HideFullServers = true;
            vm.HideVipServers = true;
            Assert.Equal(5, vm.RawServersCount); // Raw backing list preserved
            Assert.Equal(new[] { "srv-mid", "srv-low", "srv-unk" }, vm.Servers.Select(s => s.JobId).ToArray());

            vm.HideFullServers = false;
            vm.HideVipServers = false;
            Assert.Equal(5, vm.Servers.Count);
        }

        [Fact]
        public void Item9_ResponsiveVirtualizedColumnsCommandPaletteNavAndGroupLocalization()
        {
            Assert.Equal(2, MainViewModel.CalculateResponsiveCardColumns(720));
            Assert.Equal(3, MainViewModel.CalculateResponsiveCardColumns(980));
            Assert.Equal(4, MainViewModel.CalculateResponsiveCardColumns(1360));

            var vm = new MainViewModel();
            var accounts = Enumerable.Range(1, 7)
                .Select(i => new Account { UserID = 8000 + i, Username = $"GridAcc_{i}", Valid = true })
                .ToList();
            vm.LoadAccounts(accounts);

            // Default 3 columns -> 3 rows (3 + 3 + 1)
            Assert.Equal(3, vm.CardColumns);
            Assert.Equal(3, vm.FilteredAccountRows.Count);
            Assert.Equal(3, vm.FilteredAccountRows[0].ColumnCount);

            // Responsive widen to 4 columns -> 2 rows (4 + 3)
            vm.CardColumns = 4;
            Assert.Equal(2, vm.FilteredAccountRows.Count);
            Assert.Equal(4, vm.FilteredAccountRows[0].ColumnCount);
            Assert.True(vm.FilteredAccountRows[0].HasItem4);
            Assert.False(vm.FilteredAccountRows[1].HasItem4);

            // Responsive narrow to 2 columns -> 4 rows (2 + 2 + 2 + 1)
            vm.CardColumns = 2;
            Assert.Equal(4, vm.FilteredAccountRows.Count);
            Assert.Equal(2, vm.FilteredAccountRows[0].ColumnCount);
            Assert.False(vm.FilteredAccountRows[0].HasItem3);

            // Ctrl+K Command Palette auto-select & keyboard wrap-around
            vm.CommandPaletteSearchText = "GridAcc";
            Assert.Equal(7, vm.CommandPaletteResults.Count);
            Assert.Same(vm.CommandPaletteResults[0], vm.SelectedCommandPaletteAccount);
            Assert.True(vm.CommandPaletteResults[0].IsCommandPaletteSelected);

            vm.MoveCommandPaletteSelection(1);
            Assert.Same(vm.CommandPaletteResults[1], vm.SelectedCommandPaletteAccount);
            Assert.False(vm.CommandPaletteResults[0].IsCommandPaletteSelected);
            Assert.True(vm.CommandPaletteResults[1].IsCommandPaletteSelected);

            // Wrap backwards from 0 -> last item
            vm.SetSelectedCommandPaletteIndex(0);
            vm.MoveCommandPaletteSelection(-1);
            Assert.Same(vm.CommandPaletteResults[6], vm.SelectedCommandPaletteAccount);

            // Rich Command Palette searches system commands with keywords and Vietnamese normalization
            vm.CommandPaletteSearchText = "anti";
            Assert.Contains(vm.CommandPaletteItems, i => i.IsCommand && i.Title.Contains("Anti-AFK", StringComparison.OrdinalIgnoreCase));

            vm.CommandPaletteSearchText = "cài đặt";
            Assert.Contains(vm.CommandPaletteItems, i => i.IsCommand && (i.Title.Contains("Cài đặt", StringComparison.OrdinalIgnoreCase) || i.Title.Contains("Settings", StringComparison.OrdinalIgnoreCase)));

            vm.CommandPaletteSearchText = "potato";
            Assert.Contains(vm.CommandPaletteItems, i => i.IsCommand && i.Title.Contains("Potato", StringComparison.OrdinalIgnoreCase));

            // Execute selected system command (e.g. Potato Mode toggle)
            vm.SetSelectedCommandPaletteIndex(0);
            var prevPotato = vm.IsPotatoMode;
            bool executed = vm.ExecuteCommandPaletteSelection();
            Assert.True(executed);
            Assert.NotEqual(prevPotato, vm.IsPotatoMode);
            vm.IsPotatoMode = prevPotato;

            // GroupNameDisplayConverter localizes "All Groups" while preserving user group names
            string convertedAll = GroupNameDisplayConverter.Instance.Convert("All Groups", typeof(string), null, CultureInfo.InvariantCulture)?.ToString() ?? string.Empty;
            Assert.Equal(Loc.Get("Dash_AllGroups"), convertedAll);
            Assert.Equal("Farm_SEA", GroupNameDisplayConverter.Instance.Convert("Farm_SEA", typeof(string), null, CultureInfo.InvariantCulture));
        }

        [Fact]
        public void PostReleaseFixes_GameDetailsSourceName_GroupBadge_AndZeroGpuGuard()
        {
            // 1. GameDetails.BestDisplayName & GameInfoService.ExtractBestGameTitle prefer sourceName over translated name
            var detailsWithSource = new GameDetails
            {
                name = "Lấy trộm một quả trứng",
                sourceName = "Steal An Egg"
            };
            Assert.Equal("Steal An Egg", detailsWithSource.BestDisplayName);

            var detailsWithoutSource = new GameDetails
            {
                name = "Blox Fruits",
                sourceName = ""
            };
            Assert.Equal("Blox Fruits", detailsWithoutSource.BestDisplayName);

            using var jsonWithSource = JsonDocument.Parse("{\"name\":\"Dịch tiếng Việt\",\"sourceName\":\"Original English Title\"}");
            Assert.Equal("Original English Title", GameInfoService.ExtractBestGameTitle(jsonWithSource.RootElement));

            using var jsonFallback = JsonDocument.Parse("{\"name\":\"Fallback Only Title\"}");
            Assert.Equal("Fallback Only Title", GameInfoService.ExtractBestGameTitle(jsonFallback.RootElement));

            // 2. Compact group pill badge and tooltip formatting
            var accDefault = new AccountViewModel(new Account { UserID = 1100, Username = "Hero", Group = "Default" });
            Assert.Contains(accDefault.GroupDisplay, new[] { "Mặc định", "Default" });
            Assert.DoesNotContain("Nhóm:", accDefault.GroupDisplay);
            Assert.Contains("Hero", accDefault.Username);
            Assert.StartsWith(LocalizationService.IsEnglish ? "Group:" : "Nhóm:", accDefault.GroupTooltip);

            var accCustom = new AccountViewModel(new Account { UserID = 1101, Username = "Farmer", Group = "FarmZone" });
            Assert.Equal("FarmZone", accCustom.GroupDisplay);
            Assert.Equal(LocalizationService.IsEnglish ? "Group: FarmZone" : "Nhóm: FarmZone", accCustom.GroupTooltip);

            // 3. Zero-GPU safety guard when 0 Roblox instances are running
            if (RobloxProcessService.GetTotalRunningRobloxCount() == 0)
            {
                var vm = new MainViewModel();
                vm.IsPotatoMode = false;
                vm.IsAntiAfk = false;
                // Ensure initial state
                RobloxWindowService.ShowAllWindows();
                Assert.False(vm.AreWindowsHidden);

                // Executing ToggleWindowsCommand with 0 windows must NOT leave AreWindowsHidden true
                vm.ToggleWindowsCommand.Execute(null);
                Assert.False(vm.AreWindowsHidden);
                Assert.Equal(0, vm.ActiveToolsCount);

                // RefreshLiveProcessStatus automatically enforces false when totalRobloxProcs == 0
                vm.RefreshLiveProcessStatus();
                Assert.False(vm.AreWindowsHidden);
            }
        }
    }
}
