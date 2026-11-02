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
                        : $"Tài khoản • {acc.StatusText} (ID: {acc.UserId})",
                    Category = isEnglish ? "Account" : "Tài khoản",
                    ActionBadge = isEnglish ? "Select [Enter]" : "Chọn [Enter]",
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
                                : $"Đã chọn tài khoản: {acc.ShownUsername}";
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
                ActionBadge = isEnglish ? "Run [Enter]" : "Thực thi [Enter]",
                IconName = IconName,
                IconColorHex = IconColorHex,
                CommandAction = Action
            };
        }

        private static List<BuiltinCommandDef> GetBuiltinCommands() => new()
        {
            new("tool.antiafk", "Bật/Tắt Anti-AFK", "Toggle Anti-AFK", "Tự động chống kick AFK 20 phút cho toàn bộ tài khoản", "Prevent 20-minute AFK kick across all accounts", "Công cụ", "Tools", "Shield", "#38BDF8", true, new[] { "anti", "afk", "kick", "bao ve" }, vm => vm.ToggleAntiAfkCommand.Execute(null)),
            new("tool.zerogpu", "Bật/Tắt Zero-GPU (Ẩn cửa sổ Roblox)", "Toggle Zero-GPU Mode", "Ẩn toàn bộ cửa sổ Roblox để giảm 99% tải GPU và điện năng", "Hide all Roblox windows to save 99% GPU power", "Công cụ", "Tools", "EyeOff", "#4ADE80", true, new[] { "zero", "gpu", "an cua so", "hide", "power" }, vm => vm.ToggleWindowsCommand.Execute(null)),
            new("tool.potato", "Bật/Tắt Potato Mode", "Toggle Potato Mode", "Giảm tải đồ họa FastFlags xuống mức thấp nhất", "Lower graphic FastFlags to extreme potato levels", "Công cụ", "Tools", "Zap", "#F59E0B", true, new[] { "potato", "khoai tay", "fps", "lag", "fastflag" }, vm => vm.TogglePotatoModeCommand.Execute(null)),
            new("tool.smartmute", "Bật/Tắt Smart Auto-Mute", "Toggle Smart Auto-Mute", "Tự động tắt tiếng khi cửa sổ Roblox chạy ngầm", "Automatically mute Roblox audio when running in background", "Âm thanh", "Audio", "Volume2", "#38BDF8", false, new[] { "mute", "smart", "tat tieng", "sound" }, vm => vm.ToggleSmartMuteCommand.Execute(null)),
            new("tool.muteall", "Bật/Tắt Toàn bộ Âm thanh Roblox", "Toggle Mute All Roblox", "Tắt hoặc bật lại âm thanh cho toàn bộ client", "Mute or unmute all running Roblox clients", "Âm thanh", "Audio", "VolumeX", "#F87171", false, new[] { "mute all", "tat am thanh", "tat tieng", "silence" }, _ => { bool wasUnmuted = RobloxAudioService.ToggleMuteAll(); ToastService.ShowSuccess(wasUnmuted ? "Đã tắt toàn bộ âm thanh Roblox." : "Đã bật lại âm thanh Roblox."); }),
            new("tool.streamer", "Bật/Tắt Streamer Mode", "Toggle Streamer Mode", "Ẩn danh, che mờ tên và ID tài khoản khi quay màn hình", "Obfuscate usernames and IDs during streams", "Bảo mật", "Security", "ShieldCheck", "#A855F7", false, new[] { "stream", "streamer", "che ten", "an danh" }, vm => vm.ToggleStreamerModeCommand.Execute(null)),
            new("tool.ram", "Tối ưu RAM & Độ ưu tiên hệ thống", "Optimize RAM & Process Priority", "Dọn dẹp bộ nhớ đệm và tăng tốc phản hồi cho game", "Clean working set and elevate client priority", "Hiệu năng", "Performance", "Cpu", "#22C55E", true, new[] { "ram", "lag", "toi uu", "clean", "boost" }, vm => vm.AntiLagBoosterCommand.Execute(null)),
            new("tool.tray", "Dọn dẹp khay hệ thống Windows", "Clean Windows Notification Tray", "Loại bỏ các icon Roblox ma còn sót lại trên khay", "Remove ghost Roblox icons from notification tray", "Hệ thống", "System", "Brush", "#EAB308", false, new[] { "tray", "khay", "icon", "ma", "ghost" }, vm => vm.CleanSystemTrayCommand.Execute(null)),
            new("tile.auto", "Sắp xếp cửa sổ Roblox: Tự động", "Arrange Windows: Auto Tile", "Tự động chia lưới cửa sổ cân đối trên màn hình", "Auto-calculate balanced grid across monitors", "Cửa sổ", "Window", "LayoutGrid", "#60A5FA", true, new[] { "tile", "sap xep", "auto", "grid", "luoi" }, vm => vm.ArrangeRobloxWindowsCommand.Execute("Auto")),
            new("tile.2x2", "Sắp xếp cửa sổ Roblox: Lưới 2×2", "Arrange Windows: 2×2 Grid", "Chia đều 4 cửa sổ Roblox trên màn hình", "Tile 4 Roblox windows evenly", "Cửa sổ", "Window", "LayoutGrid", "#60A5FA", false, new[] { "2x2", "chia 4", "grid" }, vm => vm.ArrangeRobloxWindowsCommand.Execute("2x2")),
            new("tile.3x2", "Sắp xếp cửa sổ Roblox: Lưới 3×2", "Arrange Windows: 3×2 Grid", "Chia đều 6 cửa sổ Roblox trên màn hình", "Tile 6 Roblox windows evenly", "Cửa sổ", "Window", "LayoutGrid", "#60A5FA", false, new[] { "3x2", "chia 6" }, vm => vm.ArrangeRobloxWindowsCommand.Execute("3x2")),
            new("tile.cascade", "Sắp xếp cửa sổ Roblox: Xếp tầng", "Arrange Windows: Cascade", "Xếp so le các cửa sổ Roblox lệch nhau theo từng bậc", "Cascade all Roblox windows", "Cửa sổ", "Window", "Layers", "#60A5FA", false, new[] { "cascade", "xep tang", "so le" }, vm => vm.ArrangeRobloxWindowsCommand.Execute("Cascade")),
            new("system.killall", "Đóng tất cả tiến trình Roblox (Kill All)", "Close All Roblox Processes", "Tắt nhanh toàn bộ các game và client Roblox đang chạy", "Kill all running Roblox processes instantly", "Hệ thống", "System", "Power", "#EF4444", true, new[] { "close", "kill", "dong", "tat", "exit", "thoat" }, vm => vm.KillAllClientsCommand.Execute(null)),
            new("accounts.checksession", "Quét kiểm tra phiên toàn bộ tài khoản", "Check All Account Sessions", "Kiểm tra tình trạng cookie, phát hiện tài khoản hết hạn", "Validate session cookies across all accounts", "Tài khoản", "Accounts", "RefreshCw", "#38BDF8", false, new[] { "session", "phien", "kiem tra", "check", "het han" }, vm => vm.CheckSessionsCommand.Execute(null)),
            new("accounts.relogin", "Re-login nhanh tất cả tài khoản hết hạn", "Re-login All Expired Accounts", "Mở trình duyệt đăng nhập lại các tài khoản cookie hết hạn", "Batch re-authenticate all expired accounts", "Tài khoản", "Accounts", "Key", "#F59E0B", false, new[] { "relogin", "dang nhap lai", "het han", "cookie" }, vm => vm.ReLoginAllExpiredCommand.Execute(null)),
            new("accounts.robux", "Cập nhật số dư Robux tất cả tài khoản", "Refresh All Robux Balances", "Gửi yêu cầu tra cứu số dư Robux mới nhất từ Roblox", "Query current Robux balance for all accounts", "Tài khoản", "Accounts", "Coins", "#EAB308", false, new[] { "robux", "so du", "tien", "balance" }, vm => vm.RefreshAllRobuxCommand.Execute(null)),
            new("select.all", "Chọn tất cả tài khoản", "Select All Accounts", "Tick chọn toàn bộ tài khoản trong danh sách", "Select every account in current filter", "Thao tác", "Selection", "CheckCheck", "#4ADE80", false, new[] { "select all", "chon tat ca", "chon het" }, vm => vm.SelectAllCommand.Execute(null)),
            new("select.clear", "Bỏ chọn tất cả tài khoản", "Clear Selection", "Bỏ chọn toàn bộ tài khoản đang được tick", "Clear account selection", "Thao tác", "Selection", "Square", "#9CA3AF", false, new[] { "clear", "bo chon", "deselect" }, vm => vm.ClearSelectionCommand.Execute(null)),
            new("select.invert", "Đảo ngược lựa chọn tài khoản", "Invert Account Selection", "Đổi trạng thái tick chọn của các tài khoản", "Invert account selection checks", "Thao tác", "Selection", "ArrowUpDown", "#A855F7", false, new[] { "invert", "dao nguoc" }, vm => vm.InvertSelectionCommand.Execute(null)),
            new("select.quick5", "Chọn nhanh: 5 tài khoản sẵn sàng", "Quick Select: 5 Ready Accounts", "Chọn 5 tài khoản hợp lệ đầu tiên chưa chạy game", "Select top 5 ready idle accounts", "Thao tác", "Selection", "ListChecks", "#4ADE80", false, new[] { "chon 5", "select 5", "5 acc" }, vm => vm.SelectReadyBatchCommand.Execute(5)),
            new("select.quick10", "Chọn nhanh: 10 tài khoản sẵn sàng", "Quick Select: 10 Ready Accounts", "Chọn 10 tài khoản hợp lệ đầu tiên chưa chạy game", "Select top 10 ready idle accounts", "Thao tác", "Selection", "ListChecks", "#4ADE80", false, new[] { "chon 10", "select 10", "10 acc" }, vm => vm.SelectReadyBatchCommand.Execute(10)),
            new("select.quick20", "Chọn nhanh: 20 tài khoản sẵn sàng", "Quick Select: 20 Ready Accounts", "Chọn 20 tài khoản hợp lệ đầu tiên chưa chạy game", "Select top 20 ready idle accounts", "Thao tác", "Selection", "ListChecks", "#4ADE80", false, new[] { "chon 20", "select 20", "20 acc" }, vm => vm.SelectReadyBatchCommand.Execute(20)),
            new("select.range", "Chọn theo dải số thứ tự (ví dụ: 1-20)", "Select by Range (e.g. 1-20)", "Nhập dải số thứ tự tài khoản để chọn nhanh", "Prompt to select accounts by 1-based index range", "Thao tác", "Selection", "Hash", "#38BDF8", false, new[] { "dai", "range", "1-10", "1-20" }, vm => vm.SelectByRangePromptCommand.Execute(null)),
            new("nav.serverlist", "Mở: Danh sách máy chủ Roblox", "Go to: Server List", "Chuyển tới giao diện tìm kiếm server, ping và người chơi", "Navigate to Roblox Server List", "Điều hướng", "Navigation", "Globe", "#60A5FA", false, new[] { "server", "may chu", "tim server", "ping" }, vm => vm.NavigateToServerListCommand.Execute(null)),
            new("nav.recentgames", "Mở: Game gần đây & Yêu thích", "Go to: Recent & Favorite Games", "Xem các game Roblox vừa chơi và yêu thích đã lưu", "Navigate to Recent & Favorite Games", "Điều hướng", "Navigation", "Gamepad2", "#F472B6", false, new[] { "game", "gan day", "recent", "yeu thich" }, vm => vm.NavigateToRecentGamesCommand.Execute(null)),
            new("nav.settings", "Mở: Cài đặt hệ thống", "Go to: Settings", "Tùy chỉnh giao diện, ngôn ngữ, bảo mật và kết nối", "Navigate to Settings", "Điều hướng", "Navigation", "Settings", "#9CA3AF", false, new[] { "setting", "cai dat", "config", "ngon ngu" }, vm => vm.NavigateToSettingsCommand.Execute(null)),
            new("nav.accounts", "Mở: Không gian Tài khoản", "Go to: Accounts", "Quay về màn hình chính quản lý danh sách tài khoản", "Navigate to Accounts Dashboard", "Điều hướng", "Navigation", "User", "#38BDF8", false, new[] { "tai khoan", "account", "home", "dashboard" }, vm => vm.NavigateToAccountsCommand.Execute(null)),
            new("dialog.proxy", "Mở: Trình quản lý Proxy", "Open: Proxy Manager", "Cấu hình HTTP/SOCKS5 proxy gán cho từng tài khoản", "Open Proxy Manager Dialog", "Tiện ích", "Utilities", "Network", "#34D399", false, new[] { "proxy", "ip", "socks" }, vm => vm.OpenProxyManagerCommand.Execute(null)),
            new("dialog.nexus", "Mở: Nexus Studio Engine", "Open: Nexus Studio Engine", "Cửa sổ kết nối WebSocket điều khiển automation Roblox", "Open Nexus Automation Window", "Tiện ích", "Utilities", "Radio", "#A855F7", false, new[] { "nexus", "socket", "engine" }, vm => vm.OpenNexusCommand.Execute(null)),
            new("dialog.altgen", "Mở: Tạo tài khoản Roblox phụ (Alt Generator)", "Open: Alt Account Generator", "Sinh nhanh tài khoản Roblox mới tự động", "Open Alt Account Generator Dialog", "Tiện ích", "Utilities", "UserPlus", "#38BDF8", false, new[] { "alt", "tao acc", "generator" }, _ => AppServices.Dialogs.OpenAltGenDialog())
        };
    }
}