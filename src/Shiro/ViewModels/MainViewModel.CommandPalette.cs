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
                    Description = Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_370", acc.StatusText, acc.UserId),
                    Category = Shiro.Services.LocalizationService.Get("Nav_Accounts"),
                    ActionBadge = Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_371"),
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
                            StatusMessage = Shiro.Services.LocalizationService.Format("Auto_MainViewModelCom_372", acc.ShownUsername);
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
            public bool Matches(string normQuery, bool isEnglish = false)
            {
                if (string.IsNullOrWhiteSpace(normQuery))
                    return IsDefaultQuickAction;

                string title = NormalizeSearchText(Shiro.Services.LocalizationService.Get(TitleKey));
                string desc = NormalizeSearchText(Shiro.Services.LocalizationService.Get(DescKey));
                string cat = NormalizeSearchText(Shiro.Services.LocalizationService.Get(CategoryKey));

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

            public CommandPaletteItemViewModel ToViewModel(bool isEnglish = false) => new()
            {
                ItemType = CategoryKey == "Auto_MainViewModelCom_426" ? CommandPaletteItemType.Navigation : CommandPaletteItemType.Command,
                Title = Shiro.Services.LocalizationService.Get(TitleKey),
                Description = Shiro.Services.LocalizationService.Get(DescKey),
                Category = Shiro.Services.LocalizationService.Get(CategoryKey),
                ActionBadge = Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_373"),
                IconName = IconName,
                IconColorHex = IconColorHex,
                CommandAction = Action
            };
        }

        private static List<BuiltinCommandDef> GetBuiltinCommands() => new()
        {
            new("tool.antiafk", "Auto_MainViewModelCom_375", "Auto_MainViewModelCom_374", "Nav_Tools", "Shield", "#38BDF8", true, new[] { "anti", "afk", "kick", "bao ve" }, vm => vm.ToggleAntiAfkCommand.Execute(null)),
            new("tool.zerogpu", "Auto_MainViewModelCom_377", "Auto_MainViewModelCom_376", "Nav_Tools", "EyeOff", "#4ADE80", true, new[] { "zero", "gpu", "an cua so", "hide", "power" }, vm => vm.ToggleWindowsCommand.Execute(null)),
            new("tool.potato", "Auto_MainViewModelCom_379", "Auto_MainViewModelCom_378", "Nav_Tools", "Zap", "#F59E0B", true, new[] { "potato", "khoai tay", "fps", "lag", "fastflag" }, vm => vm.TogglePotatoModeCommand.Execute(null)),
            new("tool.smartmute", "Auto_MainViewModelCom_382", "Auto_MainViewModelCom_381", "Auto_MainViewModelCom_380", "Volume2", "#38BDF8", false, new[] { "mute", "smart", "tat tieng", "sound" }, vm => vm.ToggleSmartMuteCommand.Execute(null)),
            new("tool.muteall", "Auto_MainViewModelCom_386", "Auto_MainViewModelCom_385", "Auto_MainViewModelCom_380", "VolumeX", "#F87171", false, new[] { "mute all", "tat am thanh", "tat tieng", "silence" }, _ => { bool wasUnmuted = RobloxAudioService.ToggleMuteAll(); ToastService.ShowSuccess(wasUnmuted ? Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_384") : Shiro.Services.LocalizationService.Get("Auto_MainViewModelCom_383")); }),
            new("tool.streamer", "Auto_MainViewModelCom_389", "Auto_MainViewModelCom_388", "Auto_MainViewModelCom_387", "ShieldCheck", "#A855F7", false, new[] { "stream", "streamer", "che ten", "an danh" }, vm => vm.ToggleStreamerModeCommand.Execute(null)),
            new("tool.ram", "Auto_MainViewModelCom_392", "Auto_MainViewModelCom_391", "Auto_MainViewModelCom_390", "Cpu", "#22C55E", true, new[] { "ram", "lag", "toi uu", "clean", "boost" }, vm => vm.AntiLagBoosterCommand.Execute(null)),
            new("tool.tray", "Auto_MainViewModelCom_395", "Auto_MainViewModelCom_394", "Auto_MainViewModelCom_393", "Brush", "#EAB308", false, new[] { "tray", "khay", "icon", "ma", "ghost" }, vm => vm.CleanSystemTrayCommand.Execute(null)),
            new("tile.auto", "Auto_MainViewModelCom_398", "Auto_MainViewModelCom_397", "Auto_MainViewModelCom_396", "LayoutGrid", "#60A5FA", true, new[] { "tile", "sap xep", "auto", "grid", "luoi" }, vm => vm.ArrangeRobloxWindowsCommand.Execute("Auto")),
            new("tile.2x2", "Auto_MainViewModelCom_400", "Auto_MainViewModelCom_399", "Auto_MainViewModelCom_396", "LayoutGrid", "#60A5FA", false, new[] { "2x2", "chia 4", "grid" }, vm => vm.ArrangeRobloxWindowsCommand.Execute("2x2")),
            new("tile.3x2", "Auto_MainViewModelCom_402", "Auto_MainViewModelCom_401", "Auto_MainViewModelCom_396", "LayoutGrid", "#60A5FA", false, new[] { "3x2", "chia 6" }, vm => vm.ArrangeRobloxWindowsCommand.Execute("3x2")),
            new("tile.cascade", "Auto_MainViewModelCom_404", "Auto_MainViewModelCom_403", "Auto_MainViewModelCom_396", "Layers", "#60A5FA", false, new[] { "cascade", "xep tang", "so le" }, vm => vm.ArrangeRobloxWindowsCommand.Execute("Cascade")),
            new("system.killall", "Auto_MainViewModelCom_406", "Auto_MainViewModelCom_405", "Auto_MainViewModelCom_393", "Power", "#EF4444", true, new[] { "close", "kill", "dong", "tat", "exit", "thoat" }, vm => vm.KillAllClientsCommand.Execute(null)),
            new("accounts.checksession", "Auto_MainViewModelCom_408", "Auto_MainViewModelCom_407", "Nav_Accounts", "RefreshCw", "#38BDF8", false, new[] { "session", "phien", "kiem tra", "check", "het han" }, vm => vm.CheckSessionsCommand.Execute(null)),
            new("accounts.relogin", "Auto_MainViewModelCom_410", "Auto_MainViewModelCom_409", "Nav_Accounts", "Key", "#F59E0B", false, new[] { "relogin", "dang nhap lai", "het han", "cookie" }, vm => vm.ReLoginAllExpiredCommand.Execute(null)),
            new("accounts.robux", "Auto_MainViewModelCom_412", "Auto_MainViewModelCom_411", "Nav_Accounts", "Coins", "#EAB308", false, new[] { "robux", "so du", "tien", "balance" }, vm => vm.RefreshAllRobuxCommand.Execute(null)),
            new("select.all", "Bar_SelectAllTooltip", "Auto_MainViewModelCom_413", "Palette.Category.Selection", "CheckCheck", "#4ADE80", false, new[] { "select all", "chon tat ca", "chon het" }, vm => vm.SelectAllCommand.Execute(null)),
            new("select.clear", "Auto_MainViewModelCom_415", "Auto_MainViewModelCom_414", "Palette.Category.Selection", "Square", "#9CA3AF", false, new[] { "clear", "bo chon", "deselect" }, vm => vm.ClearSelectionCommand.Execute(null)),
            new("select.invert", "Auto_MainViewModelCom_417", "Auto_MainViewModelCom_416", "Palette.Category.Selection", "ArrowUpDown", "#A855F7", false, new[] { "invert", "dao nguoc" }, vm => vm.InvertSelectionCommand.Execute(null)),
            new("select.quick5", "Auto_MainViewModelCom_419", "Auto_MainViewModelCom_418", "Palette.Category.Selection", "ListChecks", "#4ADE80", false, new[] { "chon 5", "select 5", "5 acc" }, vm => vm.SelectReadyBatchCommand.Execute(5)),
            new("select.quick10", "Auto_MainViewModelCom_421", "Auto_MainViewModelCom_420", "Palette.Category.Selection", "ListChecks", "#4ADE80", false, new[] { "chon 10", "select 10", "10 acc" }, vm => vm.SelectReadyBatchCommand.Execute(10)),
            new("select.quick20", "Auto_MainViewModelCom_423", "Auto_MainViewModelCom_422", "Palette.Category.Selection", "ListChecks", "#4ADE80", false, new[] { "chon 20", "select 20", "20 acc" }, vm => vm.SelectReadyBatchCommand.Execute(20)),
            new("select.range", "Auto_MainViewModelCom_425", "Auto_MainViewModelCom_424", "Palette.Category.Selection", "Hash", "#38BDF8", false, new[] { "dai", "range", "1-10", "1-20" }, vm => vm.SelectByRangePromptCommand.Execute(null)),
            new("nav.serverlist", "Auto_MainViewModelCom_428", "Auto_MainViewModelCom_427", "Auto_MainViewModelCom_426", "Globe", "#60A5FA", false, new[] { "server", "may chu", "tim server", "ping" }, vm => vm.NavigateToServerListCommand.Execute(null)),
            new("nav.recentgames", "Auto_MainViewModelCom_430", "Auto_MainViewModelCom_429", "Auto_MainViewModelCom_426", "Gamepad2", "#F472B6", false, new[] { "game", "gan day", "recent", "yeu thich" }, vm => vm.NavigateToRecentGamesCommand.Execute(null)),
            new("nav.settings", "Auto_MainViewModelCom_432", "Auto_MainViewModelCom_431", "Auto_MainViewModelCom_426", "Settings", "#9CA3AF", false, new[] { "setting", "cai dat", "config", "ngon ngu" }, vm => vm.NavigateToSettingsCommand.Execute(null)),
            new("nav.accounts", "Auto_MainViewModelCom_434", "Auto_MainViewModelCom_433", "Auto_MainViewModelCom_426", "User", "#38BDF8", false, new[] { "tai khoan", "account", "home", "dashboard" }, vm => vm.NavigateToAccountsCommand.Execute(null)),
            new("dialog.proxy", "Auto_MainViewModelCom_437", "Auto_MainViewModelCom_436", "Auto_MainViewModelCom_435", "Network", "#34D399", false, new[] { "proxy", "ip", "socks" }, vm => vm.OpenProxyManagerCommand.Execute(null)),
            new("dialog.nexus", "Auto_MainViewModelCom_439", "Auto_MainViewModelCom_438", "Auto_MainViewModelCom_435", "Radio", "#A855F7", false, new[] { "nexus", "socket", "engine" }, vm => vm.OpenNexusCommand.Execute(null)),
            new("dialog.altgen", "Auto_MainViewModelCom_441", "Auto_MainViewModelCom_440", "Auto_MainViewModelCom_435", "UserPlus", "#38BDF8", false, new[] { "alt", "tao acc", "generator" }, _ => AppServices.Dialogs.OpenAltGenDialog())
        };
    }
}