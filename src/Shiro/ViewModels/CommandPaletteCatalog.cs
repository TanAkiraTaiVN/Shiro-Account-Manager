#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Shiro.Services;

namespace Shiro.ViewModels
{
    /// <summary>
    /// Built-in Command Palette catalog definitions and query-matching helpers for <see cref="MainViewModel"/>.
    /// </summary>
    internal static class CommandPaletteCatalog
    {
        internal sealed record BuiltinCommandDef(
            string Id,
            string TitleKey,
            string DescKey,
            string CategoryKey,
            string IconName,
            string IconColorHex,
            bool IsDefaultQuickAction,
            string[] Keywords,
            Action<MainViewModel> Action
        )
        {
            public bool Matches(string normQuery, bool _ = false)
            {
                if (string.IsNullOrWhiteSpace(normQuery))
                    return IsDefaultQuickAction;

                string title = MainViewModel.NormalizeSearchText(LocalizationService.Get(TitleKey));
                string desc = MainViewModel.NormalizeSearchText(LocalizationService.Get(DescKey));
                string cat = MainViewModel.NormalizeSearchText(LocalizationService.Get(CategoryKey));

                if (title.Contains(normQuery, StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains(normQuery, StringComparison.OrdinalIgnoreCase) ||
                    cat.Contains(normQuery, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                foreach (var kw in Keywords)
                {
                    if (MainViewModel.NormalizeSearchText(kw).Contains(normQuery, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            public CommandPaletteItemViewModel ToViewModel(bool isRecent = false) => new()
            {
                CommandId = Id,
                IsRecent = isRecent,
                ItemType = CategoryKey == "Main.NavigationCategory" ? CommandPaletteItemType.Navigation : CommandPaletteItemType.Command,
                Title = LocalizationService.Get(TitleKey),
                Description = LocalizationService.Get(DescKey),
                Category = LocalizationService.Get(CategoryKey),
                ActionBadge = LocalizationService.Get("Main.ExecuteEnterKey"),
                IconName = IconName,
                IconColorHex = IconColorHex,
                CommandAction = Action
            };
        }

        internal static void PopulateResults(
            MainViewModel vm,
            string rawQuery,
            IReadOnlyList<AccountViewModel> accounts,
            ObservableCollection<CommandPaletteItemViewModel> items,
            ObservableCollection<AccountViewModel> accountResults,
            CommandPaletteScope scope = CommandPaletteScope.All,
            IReadOnlyList<string>? recentCommandIds = null)
        {
            items.Clear();
            accountResults.Clear();

            string normQuery = MainViewModel.NormalizeSearchText(rawQuery);
            var recentSet = recentCommandIds is { Count: > 0 }
                ? new HashSet<string>(recentCommandIds, StringComparer.Ordinal)
                : null;

            if (scope != CommandPaletteScope.Accounts)
            {
                AddCommandMatches(items, normQuery, scope, recentCommandIds, recentSet);
            }

            if (scope != CommandPaletteScope.Commands)
            {
                AddAccountMatches(vm, rawQuery, accounts, items, accountResults, scope);
            }
        }

        private static void AddCommandMatches(
            ObservableCollection<CommandPaletteItemViewModel> items,
            string normQuery,
            CommandPaletteScope scope,
            IReadOnlyList<string>? recentCommandIds,
            HashSet<string>? recentSet)
        {
            var matchedCommands = new List<CommandPaletteItemViewModel>();
            var commands = GetBuiltinCommands();

            if (string.IsNullOrEmpty(normQuery))
            {
                var addedIds = new HashSet<string>(StringComparer.Ordinal);
                if (recentCommandIds != null && recentCommandIds.Count > 0)
                {
                    var byId = commands.ToDictionary(c => c.Id, StringComparer.Ordinal);
                    foreach (string recentId in recentCommandIds)
                    {
                        if (byId.TryGetValue(recentId, out var recentCmd) && addedIds.Add(recentCmd.Id))
                            matchedCommands.Add(recentCmd.ToViewModel(isRecent: true));
                    }
                }

                int recentCount = matchedCommands.Count;
                foreach (var cmd in commands)
                {
                    if ((scope == CommandPaletteScope.Commands || cmd.IsDefaultQuickAction) && addedIds.Add(cmd.Id))
                        matchedCommands.Add(cmd.ToViewModel(isRecent: false));
                }

                int limit = scope == CommandPaletteScope.Commands ? commands.Count : Math.Max(7, recentCount + 5);
                foreach (var c in matchedCommands.Take(limit))
                    items.Add(c);
                return;
            }

            foreach (var cmd in commands)
            {
                if (cmd.Matches(normQuery))
                {
                    bool isRecent = recentSet != null && recentSet.Contains(cmd.Id);
                    matchedCommands.Add(cmd.ToViewModel(isRecent));
                }
            }

            int queryLimit = scope == CommandPaletteScope.Commands ? commands.Count : 7;
            foreach (var c in matchedCommands.Take(queryLimit))
                items.Add(c);
        }

        private static void AddAccountMatches(
            MainViewModel vm,
            string rawQuery,
            IReadOnlyList<AccountViewModel> accounts,
            ObservableCollection<CommandPaletteItemViewModel> items,
            ObservableCollection<AccountViewModel> accountResults,
            CommandPaletteScope scope)
        {
            var accQuery = AccountSearchQuery.Create(rawQuery);
            int emptyLimit = scope == CommandPaletteScope.Accounts ? 24 : 7;
            int searchLimit = scope == CommandPaletteScope.Accounts ? 24 : 12;
            var matchedAccounts = accQuery.IsEmpty
                ? accounts.Take(emptyLimit).ToList()
                : accounts.Where(accQuery.IsMatch).Take(searchLimit).ToList();

            foreach (var acc in matchedAccounts)
            {
                accountResults.Add(acc);
                items.Add(new CommandPaletteItemViewModel
                {
                    ItemType = CommandPaletteItemType.Account,
                    CommandId = string.Empty,
                    Title = acc.ShownUsername,
                    Description = LocalizationService.Format("Main.AccountBadgeFormat", acc.StatusText, acc.ShownUserId),
                    Category = LocalizationService.Get("Nav_Accounts"),
                    ActionBadge = LocalizationService.Get("Main.SelectEnterKey"),
                    IconName = "User",
                    IconColorHex = "#38BDF8",
                    Account = acc,
                    AccountAction = joinServer =>
                    {
                        vm.CommandPalette.IsCommandPaletteOpen = false;
                        if (joinServer && AccountLauncherService.ParsePlaceId(vm.LaunchBar.PlaceId) > 0)
                        {
                            vm.LaunchBar.JoinServerAccountCommand.Execute(acc);
                        }
                        else
                        {
                            acc.IsSelected = true;
                            vm.CurrentPage = NavigationPage.Accounts;
                            vm.ToolsStatus.StatusMessage = LocalizationService.Format("Main.AccountSelected", acc.ShownUsername);
                        }
                    }
                });
            }
        }

        internal static IReadOnlyList<BuiltinCommandDef> GetBuiltinCommands() => CachedBuiltinCommands;

        private static readonly IReadOnlyList<BuiltinCommandDef> CachedBuiltinCommands = new BuiltinCommandDef[]
        {
            new("tool.antiafk", "Main.ToggleAntiAfkTitle", "Main.AntiAfkDescription", "Nav_Tools", "Shield", "#38BDF8", true, new[] { "anti", "afk", "kick", "bao ve" }, vm => vm.ToolsStatus.ToggleAntiAfkCommand.Execute(null)),
            new("tool.zerogpu", "Main.ToggleZeroGpuTitle", "Main.ZeroGpuDescription", "Nav_Tools", "EyeOff", "#4ADE80", true, new[] { "zero", "gpu", "an cua so", "hide", "power" }, vm => vm.ToolsStatus.ToggleWindowsCommand.Execute(null)),
            new("tool.potato", "Main.TogglePotatoModeTitle", "Main.PotatoModeDescription", "Nav_Tools", "Zap", "#F59E0B", true, new[] { "potato", "khoai tay", "fps", "lag", "fastflag" }, vm => vm.ToolsStatus.TogglePotatoModeCommand.Execute(null)),
            new("tool.smartmute", "Main.ToggleSmartAutoMuteTitle", "Main.SmartAutoMuteDescription", "Main.AudioCategory", "Volume2", "#38BDF8", false, new[] { "mute", "smart", "tat tieng", "sound" }, vm => vm.ToolsStatus.ToggleSmartMuteCommand.Execute(null)),
            new("tool.muteall", "Main.ToggleMuteAllTitle", "Main.ToggleMuteAllDescription", "Main.AudioCategory", "VolumeX", "#F87171", false, new[] { "mute all", "tat am thanh", "tat tieng", "silence" }, _ => { bool wasUnmuted = RobloxAudioService.ToggleMuteAll(); ToastService.ShowSuccess(wasUnmuted ? LocalizationService.Get("Main.RobloxAudioMuted") : LocalizationService.Get("Main.RobloxAudioUnmuted")); }),
            new("tool.streamer", "Main.ToggleStreamerModeTitle", "Main.StreamerModeDescription", "Main.SecurityCategory", "ShieldCheck", "#A855F7", false, new[] { "stream", "streamer", "che ten", "an danh" }, vm => vm.ToolsStatus.ToggleStreamerModeCommand.Execute(null)),
            new("tool.ram", "Main.OptimizeRamTitle", "Main.OptimizeRamDescription", "Main.PerformanceCategory", "Cpu", "#22C55E", true, new[] { "ram", "lag", "toi uu", "clean", "boost" }, vm => vm.ToolsStatus.AntiLagBoosterCommand.Execute(null)),
            new("tool.tray", "Main.CleanTrayTitle", "Main.CleanTrayDescription", "Main.SystemCategory", "Brush", "#EAB308", false, new[] { "tray", "khay", "icon", "ma", "ghost" }, vm => vm.ToolsStatus.CleanSystemTrayCommand.Execute(null)),
            new("tile.auto", "Main.ArrangeAutoTitle", "Main.ArrangeAutoDescription", "Main.WindowsCategory", "LayoutGrid", "#60A5FA", true, new[] { "tile", "sap xep", "auto", "grid", "luoi" }, vm => vm.ToolsStatus.ArrangeRobloxWindowsCommand.Execute("Auto")),
            new("tile.2x2", "Main.ArrangeGrid2x2Title", "Main.ArrangeGrid2x2Description", "Main.WindowsCategory", "LayoutGrid", "#60A5FA", false, new[] { "2x2", "chia 4", "grid" }, vm => vm.ToolsStatus.ArrangeRobloxWindowsCommand.Execute("2x2")),
            new("tile.3x2", "Main.ArrangeGrid3x2Title", "Main.ArrangeGrid3x2Description", "Main.WindowsCategory", "LayoutGrid", "#60A5FA", false, new[] { "3x2", "chia 6" }, vm => vm.ToolsStatus.ArrangeRobloxWindowsCommand.Execute("3x2")),
            new("tile.cascade", "Main.ArrangeCascadeTitle", "Main.ArrangeCascadeDescription", "Main.WindowsCategory", "Layers", "#60A5FA", false, new[] { "cascade", "xep tang", "so le" }, vm => vm.ToolsStatus.ArrangeRobloxWindowsCommand.Execute("Cascade")),
            new("system.killall", "Main.KillAllRobloxTitle", "Main.KillAllRobloxDescription", "Main.SystemCategory", "Power", "#EF4444", true, new[] { "close", "kill", "dong", "tat", "exit", "thoat" }, vm => vm.ToolsStatus.KillAllClientsCommand.Execute(null)),
            new("accounts.checksession", "Main.CheckAllSessionsTitle", "Main.CheckAllSessionsDescription", "Nav_Accounts", "RefreshCw", "#38BDF8", false, new[] { "session", "phien", "kiem tra", "check", "het han" }, vm => vm.AccountsVm.CheckSessionsCommand.Execute(null)),
            new("accounts.audit", "Main.AuditAllSessionsTitle", "Main.AuditAllSessionsDescription", "Nav_Accounts", "ShieldCheck", "#34D399", false, new[] { "audit", "session", "quet phien", "kiem tra phien" }, vm => vm.AccountsVm.AuditAllAccountsCommand.Execute(null)),
            new("accounts.relogin", "Main.ReloginExpiredTitle", "Main.ReloginExpiredDescription", "Nav_Accounts", "Key", "#F59E0B", false, new[] { "relogin", "dang nhap lai", "het han", "cookie" }, vm => vm.AccountsVm.ReLoginAllExpiredCommand.Execute(null)),
            new("accounts.robux", "Main.RefreshRobuxTitle", "Main.RefreshRobuxDescription", "Nav_Accounts", "Coins", "#EAB308", false, new[] { "robux", "so du", "tien", "balance" }, vm => vm.AccountsVm.RefreshAllRobuxCommand.Execute(null)),
            new("select.all", "Bar_SelectAllTooltip", "Main.SelectAllAccountsDescription", "Palette.Category.Selection", "CheckCheck", "#4ADE80", false, new[] { "select all", "chon tat ca", "chon het" }, vm => vm.AccountsVm.SelectAllCommand.Execute(null)),
            new("select.clear", "Main.DeselectAllAccountsTitle", "Main.DeselectAllAccountsDescription", "Palette.Category.Selection", "Square", "#9CA3AF", false, new[] { "clear", "bo chon", "deselect" }, vm => vm.AccountsVm.ClearSelectionCommand.Execute(null)),
            new("select.invert", "Main.InvertSelectionTitle", "Main.InvertSelectionDescription", "Palette.Category.Selection", "ArrowUpDown", "#A855F7", false, new[] { "invert", "dao nguoc" }, vm => vm.AccountsVm.InvertSelectionCommand.Execute(null)),
            new("select.quick5", "Main.QuickSelect5Title", "Main.QuickSelect5Description", "Palette.Category.Selection", "ListChecks", "#4ADE80", false, new[] { "chon 5", "select 5", "5 acc" }, vm => vm.AccountsVm.SelectReadyBatchCommand.Execute(5)),
            new("select.quick10", "Main.QuickSelect10Title", "Main.QuickSelect10Description", "Palette.Category.Selection", "ListChecks", "#4ADE80", false, new[] { "chon 10", "select 10", "10 acc" }, vm => vm.AccountsVm.SelectReadyBatchCommand.Execute(10)),
            new("select.quick20", "Main.QuickSelect20Title", "Main.QuickSelect20Description", "Palette.Category.Selection", "ListChecks", "#4ADE80", false, new[] { "chon 20", "select 20", "20 acc" }, vm => vm.AccountsVm.SelectReadyBatchCommand.Execute(20)),
            new("select.range", "Main.SelectRangeTitle", "Main.SelectRangeDescription", "Palette.Category.Selection", "Hash", "#38BDF8", false, new[] { "dai", "range", "1-10", "1-20" }, vm => vm.AccountsVm.SelectByRangePromptCommand.Execute(null)),
            new("nav.serverlist", "Main.OpenServerListTitle", "Main.OpenServerListDescription", "Main.NavigationCategory", "Globe", "#60A5FA", false, new[] { "server", "may chu", "tim server", "ping" }, vm => vm.NavigateToServerListCommand.Execute(null)),
            new("nav.recentgames", "Main.OpenRecentGamesTitle", "Main.OpenRecentGamesDescription", "Main.NavigationCategory", "Gamepad2", "#F472B6", false, new[] { "game", "gan day", "recent", "yeu thich" }, vm => vm.NavigateToRecentGamesCommand.Execute(null)),
            new("nav.scheduler", "Main.OpenSchedulerTitle", "Main.OpenSchedulerDescription", "Main.NavigationCategory", "Clock", "#38BDF8", false, new[] { "scheduler", "lich hen", "hen gio", "rotation", "ca" }, vm => vm.NavigateToSchedulerCommand.Execute(null)),
            new("tool.scheduler", "Main.ToggleSchedulerTitle", "Main.ToggleSchedulerDescription", "Nav_Tools", "Play", "#4ADE80", false, new[] { "scheduler", "bat tat lich", "auto launch" }, vm => vm.Scheduler.IsEngineEnabled = !vm.Scheduler.IsEngineEnabled),
            new("nav.settings", "Main.OpenSettingsTitle", "Main.OpenSettingsDescription", "Main.NavigationCategory", "Settings", "#9CA3AF", false, new[] { "setting", "cai dat", "config", "ngon ngu" }, vm => vm.NavigateToSettingsCommand.Execute(null)),
            new("nav.accounts", "Main.OpenAccountsTitle", "Main.OpenAccountsDescription", "Main.NavigationCategory", "User", "#38BDF8", false, new[] { "tai khoan", "account", "home", "dashboard" }, vm => vm.NavigateToAccountsCommand.Execute(null)),
            new("dialog.proxy", "Main.OpenProxyManagerTitle", "Main.OpenProxyManagerDescription", "Main.ToolsCategory", "Network", "#34D399", false, new[] { "proxy", "ip", "socks" }, vm => vm.ToolsStatus.OpenProxyManagerCommand.Execute(null)),
        };
    }
}
