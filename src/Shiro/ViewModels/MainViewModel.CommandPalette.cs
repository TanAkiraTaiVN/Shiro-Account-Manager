#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class MainViewModel
    {
        private bool _isCommandPaletteOpen;
        private string _commandPaletteSearchText = string.Empty;
        private int _selectedCommandPaletteIndex = -1;
        private CommandPaletteItemViewModel? _selectedCommandPaletteItem;

        private readonly ObservableCollection<CommandPaletteItemViewModel> _commandPaletteItems = new();
        public ObservableCollection<CommandPaletteItemViewModel> CommandPaletteItems => _commandPaletteItems;

        private readonly ObservableCollection<AccountViewModel> _commandPaletteResults = new();
        public ObservableCollection<AccountViewModel> CommandPaletteResults => _commandPaletteResults;

        public CommandPaletteItemViewModel? SelectedCommandPaletteItem => _selectedCommandPaletteItem;
        public AccountViewModel? SelectedCommandPaletteAccount => _selectedCommandPaletteItem?.Account;

        public bool IsCommandPaletteOpen
        {
            get => _isCommandPaletteOpen;
            set
            {
                if (SetProperty(ref _isCommandPaletteOpen, value))
                {
                    if (value)
                    {
                        CommandPaletteSearchText = string.Empty;
                        UpdateCommandPaletteResults();
                    }
                }
            }
        }

        public string CommandPaletteSearchText
        {
            get => _commandPaletteSearchText;
            set
            {
                if (SetProperty(ref _commandPaletteSearchText, value))
                {
                    UpdateCommandPaletteResults();
                }
            }
        }

        public void UpdateCommandPaletteResults()
        {
            _commandPaletteItems.Clear();
            _commandPaletteResults.Clear();

            string rawQuery = _commandPaletteSearchText.Trim();
            string normQuery = NormalizeSearchText(rawQuery);
            bool isEnglish = LocalizationService.IsEnglish;

            // 1. Filter Commands & Navigation
            var matchedCommands = new List<CommandPaletteItemViewModel>();
            var allCommands = GetBuiltinCommands();

            foreach (var cmd in allCommands)
            {
                if (string.IsNullOrEmpty(normQuery))
                {
                    // Empty search: present popular default actions
                    if (cmd.IsDefaultQuickAction)
                    {
                        matchedCommands.Add(cmd.ToViewModel(isEnglish));
                    }
                }
                else if (cmd.Matches(normQuery, isEnglish))
                {
                    matchedCommands.Add(cmd.ToViewModel(isEnglish));
                }
            }

            foreach (var c in matchedCommands.Take(7))
            {
                _commandPaletteItems.Add(c);
            }

            // 2. Filter Accounts
            var accQuery = AccountSearchQuery.Create(rawQuery);
            var matchedAccounts = accQuery.IsEmpty
                ? _accounts.Take(7).ToList()
                : _accounts.Where(accQuery.IsMatch).Take(12).ToList();

            foreach (var acc in matchedAccounts)
            {
                _commandPaletteResults.Add(acc);
                _commandPaletteItems.Add(new CommandPaletteItemViewModel
                {
                    ItemType = CommandPaletteItemType.Account,
                    Title = acc.ShownUsername,
                    Description = isEnglish
                        ? $"Account • {acc.StatusText} (ID: {acc.UserId})"
                        : Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_370", acc.StatusText, acc.UserId),
                    Category = isEnglish ? "Account" : Shiro.Services.LocalizationService.Get("Nav_Accounts"),
                    ActionBadge = isEnglish ? "Select [Enter]" : Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_371"),
                    IconName = "User",
                    IconColorHex = "#38BDF8",
                    Account = acc,
                    AccountAction = joinServer =>
                    {
                        IsCommandPaletteOpen = false;
                        if (joinServer && AccountLauncherService.ParsePlaceId(PlaceId) > 0)
                        {
                            JoinServerAccountCommand.Execute(acc);
                        }
                        else
                        {
                            acc.IsSelected = true;
                            CurrentPage = NavigationPage.Accounts;
                            StatusMessage = isEnglish
                                ? $"Selected account: {acc.ShownUsername}"
                                : Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_372", acc.ShownUsername);
                        }
                    }
                });
            }

            SetSelectedCommandPaletteIndex(0);
        }

        public void SetSelectedCommandPaletteIndex(int index)
        {
            if (_commandPaletteItems.Count == 0)
            {
                if (_selectedCommandPaletteItem != null)
                    _selectedCommandPaletteItem.IsSelected = false;
                _selectedCommandPaletteItem = null;
                _selectedCommandPaletteIndex = -1;
                OnPropertyChanged(nameof(SelectedCommandPaletteItem));
                OnPropertyChanged(nameof(SelectedCommandPaletteAccount));
                return;
            }

            int count = _commandPaletteItems.Count;
            int normalized = ((index % count) + count) % count;

            if (_selectedCommandPaletteItem != null && !ReferenceEquals(_selectedCommandPaletteItem, _commandPaletteItems[normalized]))
            {
                _selectedCommandPaletteItem.IsSelected = false;
            }

            _selectedCommandPaletteIndex = normalized;
            _selectedCommandPaletteItem = _commandPaletteItems[normalized];
            _selectedCommandPaletteItem.IsSelected = true;

            OnPropertyChanged(nameof(SelectedCommandPaletteItem));
            OnPropertyChanged(nameof(SelectedCommandPaletteAccount));
        }

        public void MoveCommandPaletteSelection(int delta)
        {
            if (_commandPaletteItems.Count == 0)
                return;
            int baseIdx = _selectedCommandPaletteIndex < 0 ? 0 : _selectedCommandPaletteIndex;
            SetSelectedCommandPaletteIndex(baseIdx + delta);
        }

        public bool ExecuteCommandPaletteSelection(bool joinCurrentServer = false)
        {
            var target = _selectedCommandPaletteItem ?? _commandPaletteItems.FirstOrDefault();
            if (target == null)
                return false;

            IsCommandPaletteOpen = false;

            if (target.IsAccount)
            {
                target.AccountAction?.Invoke(joinCurrentServer);
                return true;
            }

            if (target.CommandAction != null)
            {
                target.CommandAction.Invoke(this);
                return true;
            }

            return false;
        }

        public static string NormalizeSearchText(string? input) => CommandPaletteCoordinator.NormalizeSearchText(input);

        private sealed record BuiltinCommandDef(
            string Id,
            string TitleVi,
            string TitleEn,
            string DescVi,
            string DescEn,
            string CategoryVi,
            string CategoryEn,
            string IconName,
            string IconColorHex,
            bool IsDefaultQuickAction,
            string[] Keywords,
            Action<MainViewModel> Action
        )
        {
            public bool Matches(string normQuery, bool isEnglish)
            {
                if (string.IsNullOrWhiteSpace(normQuery))
                    return IsDefaultQuickAction;

                string title = NormalizeSearchText(isEnglish ? TitleEn : TitleVi);
                string desc = NormalizeSearchText(isEnglish ? DescEn : DescVi);
                string cat = NormalizeSearchText(isEnglish ? CategoryEn : CategoryVi);

                if (title.Contains(normQuery, StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains(normQuery, StringComparison.OrdinalIgnoreCase) ||
                    cat.Contains(normQuery, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                foreach (var kw in Keywords)
                {
                    if (NormalizeSearchText(kw).Contains(normQuery, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            public CommandPaletteItemViewModel ToViewModel(bool isEnglish) => new()
            {
                ItemType = CategoryEn == "Navigation" ? CommandPaletteItemType.Navigation : CommandPaletteItemType.Command,
                Title = isEnglish ? TitleEn : TitleVi,
                Description = isEnglish ? DescEn : DescVi,
                Category = isEnglish ? CategoryEn : CategoryVi,
                ActionBadge = isEnglish ? "Run [Enter]" : Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_373"),
                IconName = IconName,
                IconColorHex = IconColorHex,
                CommandAction = Action
            };
        }

        private static List<BuiltinCommandDef> GetBuiltinCommands() => new()
        {
            new("tool.antiafk", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_375"), "Toggle Anti-AFK", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_374"), "Prevent 20-minute AFK kick across all accounts", Shiro.Services.LocalizationService.Get("Nav_Tools"), "Tools", "Shield", "#38BDF8", true, new[] { "anti", "afk", "kick", "bao ve" }, vm => vm.ToggleAntiAfkCommand.Execute(null)),
            new("tool.zerogpu", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_377"), "Toggle Zero-GPU Mode", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_376"), "Hide all Roblox windows to save 99% GPU power", Shiro.Services.LocalizationService.Get("Nav_Tools"), "Tools", "EyeOff", "#4ADE80", true, new[] { "zero", "gpu", "an cua so", "hide", "power" }, vm => vm.ToggleWindowsCommand.Execute(null)),
            new("tool.potato", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_379"), "Toggle Potato Mode", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_378"), "Lower graphic FastFlags to extreme potato levels", Shiro.Services.LocalizationService.Get("Nav_Tools"), "Tools", "Zap", "#F59E0B", true, new[] { "potato", "khoai tay", "fps", "lag", "fastflag" }, vm => vm.TogglePotatoModeCommand.Execute(null)),
            new("tool.smartmute", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_382"), "Toggle Smart Auto-Mute", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_381"), "Automatically mute Roblox audio when running in background", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_380"), "Audio", "Volume2", "#38BDF8", false, new[] { "mute", "smart", "tat tieng", "sound" }, vm => vm.ToggleSmartMuteCommand.Execute(null)),
            new("tool.muteall", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_386"), "Toggle Mute All Roblox", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_385"), "Mute or unmute all running Roblox clients", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_380"), "Audio", "VolumeX", "#F87171", false, new[] { "mute all", "tat am thanh", "tat tieng", "silence" }, _ => { bool wasUnmuted = RobloxAudioService.ToggleMuteAll(); ToastService.ShowSuccess(wasUnmuted ? Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_384") : Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_383")); }),
            new("tool.streamer", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_389"), "Toggle Streamer Mode", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_388"), "Obfuscate usernames and IDs during streams", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_387"), "Security", "ShieldCheck", "#A855F7", false, new[] { "stream", "streamer", "che ten", "an danh" }, vm => vm.ToggleStreamerModeCommand.Execute(null)),
            new("tool.ram", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_392"), "Optimize RAM & Process Priority", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_391"), "Clean working set and elevate client priority", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_390"), "Performance", "Cpu", "#22C55E", true, new[] { "ram", "lag", "toi uu", "clean", "boost" }, vm => vm.AntiLagBoosterCommand.Execute(null)),
            new("tool.tray", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_395"), "Clean Windows Notification Tray", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_394"), "Remove ghost Roblox icons from notification tray", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_393"), "System", "Brush", "#EAB308", false, new[] { "tray", "khay", "icon", "ma", "ghost" }, vm => vm.CleanSystemTrayCommand.Execute(null)),
            new("tile.auto", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_398"), "Arrange Windows: Auto Tile", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_397"), "Auto-calculate balanced grid across monitors", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_396"), "Window", "LayoutGrid", "#60A5FA", true, new[] { "tile", "sap xep", "auto", "grid", "luoi" }, vm => vm.ArrangeRobloxWindowsCommand.Execute("Auto")),
            new("tile.2x2", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_400"), "Arrange Windows: 2×2 Grid", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_399"), "Tile 4 Roblox windows evenly", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_396"), "Window", "LayoutGrid", "#60A5FA", false, new[] { "2x2", "chia 4", "grid" }, vm => vm.ArrangeRobloxWindowsCommand.Execute("2x2")),
            new("tile.3x2", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_402"), "Arrange Windows: 3×2 Grid", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_401"), "Tile 6 Roblox windows evenly", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_396"), "Window", "LayoutGrid", "#60A5FA", false, new[] { "3x2", "chia 6" }, vm => vm.ArrangeRobloxWindowsCommand.Execute("3x2")),
            new("tile.cascade", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_404"), "Arrange Windows: Cascade", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_403"), "Cascade all Roblox windows", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_396"), "Window", "Layers", "#60A5FA", false, new[] { "cascade", "xep tang", "so le" }, vm => vm.ArrangeRobloxWindowsCommand.Execute("Cascade")),
            new("system.killall", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_406"), "Close All Roblox Processes", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_405"), "Kill all running Roblox processes instantly", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_393"), "System", "Power", "#EF4444", true, new[] { "close", "kill", "dong", "tat", "exit", "thoat" }, vm => vm.KillAllClientsCommand.Execute(null)),
            new("accounts.checksession", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_408"), "Check All Account Sessions", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_407"), "Validate session cookies across all accounts", Shiro.Services.LocalizationService.Get("Nav_Accounts"), "Accounts", "RefreshCw", "#38BDF8", false, new[] { "session", "phien", "kiem tra", "check", "het han" }, vm => vm.CheckSessionsCommand.Execute(null)),
            new("accounts.relogin", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_410"), "Re-login All Expired Accounts", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_409"), "Batch re-authenticate all expired accounts", Shiro.Services.LocalizationService.Get("Nav_Accounts"), "Accounts", "Key", "#F59E0B", false, new[] { "relogin", "dang nhap lai", "het han", "cookie" }, vm => vm.ReLoginAllExpiredCommand.Execute(null)),
            new("accounts.robux", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_412"), "Refresh All Robux Balances", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_411"), "Query current Robux balance for all accounts", Shiro.Services.LocalizationService.Get("Nav_Accounts"), "Accounts", "Coins", "#EAB308", false, new[] { "robux", "so du", "tien", "balance" }, vm => vm.RefreshAllRobuxCommand.Execute(null)),
            new("select.all", Shiro.Services.LocalizationService.Get("Bar_SelectAllTooltip"), "Select All Accounts", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_413"), "Select every account in current filter", Shiro.Services.LocalizationService.Get("Col_Actions"), "Selection", "CheckCheck", "#4ADE80", false, new[] { "select all", "chon tat ca", "chon het" }, vm => vm.SelectAllCommand.Execute(null)),
            new("select.clear", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_415"), "Clear Selection", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_414"), "Clear account selection", Shiro.Services.LocalizationService.Get("Col_Actions"), "Selection", "Square", "#9CA3AF", false, new[] { "clear", "bo chon", "deselect" }, vm => vm.ClearSelectionCommand.Execute(null)),
            new("select.invert", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_417"), "Invert Account Selection", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_416"), "Invert account selection checks", Shiro.Services.LocalizationService.Get("Col_Actions"), "Selection", "ArrowUpDown", "#A855F7", false, new[] { "invert", "dao nguoc" }, vm => vm.InvertSelectionCommand.Execute(null)),
            new("select.quick5", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_419"), "Quick Select: 5 Ready Accounts", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_418"), "Select top 5 ready idle accounts", Shiro.Services.LocalizationService.Get("Col_Actions"), "Selection", "ListChecks", "#4ADE80", false, new[] { "chon 5", "select 5", "5 acc" }, vm => vm.SelectReadyBatchCommand.Execute(5)),
            new("select.quick10", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_421"), "Quick Select: 10 Ready Accounts", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_420"), "Select top 10 ready idle accounts", Shiro.Services.LocalizationService.Get("Col_Actions"), "Selection", "ListChecks", "#4ADE80", false, new[] { "chon 10", "select 10", "10 acc" }, vm => vm.SelectReadyBatchCommand.Execute(10)),
            new("select.quick20", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_423"), "Quick Select: 20 Ready Accounts", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_422"), "Select top 20 ready idle accounts", Shiro.Services.LocalizationService.Get("Col_Actions"), "Selection", "ListChecks", "#4ADE80", false, new[] { "chon 20", "select 20", "20 acc" }, vm => vm.SelectReadyBatchCommand.Execute(20)),
            new("select.range", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_425"), "Select by Range (e.g. 1-20)", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_424"), "Prompt to select accounts by 1-based index range", Shiro.Services.LocalizationService.Get("Col_Actions"), "Selection", "Hash", "#38BDF8", false, new[] { "dai", "range", "1-10", "1-20" }, vm => vm.SelectByRangePromptCommand.Execute(null)),
            new("nav.serverlist", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_428"), "Go to: Server List", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_427"), "Navigate to Roblox Server List", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_426"), "Navigation", "Globe", "#60A5FA", false, new[] { "server", "may chu", "tim server", "ping" }, vm => vm.NavigateToServerListCommand.Execute(null)),
            new("nav.recentgames", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_430"), "Go to: Recent & Favorite Games", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_429"), "Navigate to Recent & Favorite Games", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_426"), "Navigation", "Gamepad2", "#F472B6", false, new[] { "game", "gan day", "recent", "yeu thich" }, vm => vm.NavigateToRecentGamesCommand.Execute(null)),
            new("nav.settings", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_432"), "Go to: Settings", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_431"), "Navigate to Settings", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_426"), "Navigation", "Settings", "#9CA3AF", false, new[] { "setting", "cai dat", "config", "ngon ngu" }, vm => vm.NavigateToSettingsCommand.Execute(null)),
            new("nav.accounts", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_434"), "Go to: Accounts", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_433"), "Navigate to Accounts Dashboard", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_426"), "Navigation", "User", "#38BDF8", false, new[] { "tai khoan", "account", "home", "dashboard" }, vm => vm.NavigateToAccountsCommand.Execute(null)),
            new("dialog.proxy", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_437"), "Open: Proxy Manager", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_436"), "Open Proxy Manager Dialog", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_435"), "Utilities", "Network", "#34D399", false, new[] { "proxy", "ip", "socks" }, vm => vm.OpenProxyManagerCommand.Execute(null)),
            new("dialog.nexus", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_439"), "Open: Nexus Studio Engine", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_438"), "Open Nexus Automation Window", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_435"), "Utilities", "Radio", "#A855F7", false, new[] { "nexus", "socket", "engine" }, vm => vm.OpenNexusCommand.Execute(null)),
            new("dialog.altgen", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_441"), "Open: Alt Account Generator", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_440"), "Open Alt Account Generator Dialog", Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_435"), "Utilities", "UserPlus", "#38BDF8", false, new[] { "alt", "tao acc", "generator" }, _ => AppServices.Dialogs.OpenAltGenDialog())
        };
    }
}