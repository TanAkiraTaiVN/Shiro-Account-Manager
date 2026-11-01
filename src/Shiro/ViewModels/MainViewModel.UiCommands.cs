#nullable enable
using System;
using System.Windows.Input;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels
{
    /// <summary>
    /// Commands powering the UI/UX upgrades on <see cref="MainViewModel"/>:
    /// Quick-Select menu, interactive summary metric cards, batch launch cancel,
    /// per-account Roblox window focus, 1-click Roblox window arranger, and Smart Mute toggle.
    /// </summary>
    public partial class MainViewModel
    {
        public ICommand SelectReadyBatchCommand { get; private set; } = null!;
        public ICommand InvertSelectionCommand { get; private set; } = null!;
        public ICommand SelectByRangePromptCommand { get; private set; } = null!;
        public ICommand MetricCardInGameCommand { get; private set; } = null!;
        public ICommand MetricCardReadyCommand { get; private set; } = null!;
        public ICommand MetricCardExpiredCommand { get; private set; } = null!;
        public ICommand MetricCardRobuxCommand { get; private set; } = null!;
        public ICommand CancelBatchLaunchCommand { get; private set; } = null!;
        public ICommand FocusAccountWindowCommand { get; private set; } = null!;
        public ICommand ArrangeRobloxWindowsCommand { get; private set; } = null!;
        public ICommand ToggleSmartMuteCommand { get; private set; } = null!;

        private void InitializeUiUpgradeCommands()
        {
            SelectReadyBatchCommand = new RelayCommand(param =>
            {
                int targetCount = 10;
                if (param is int n && n > 0)
                    targetCount = n;
                else if (param is string s && int.TryParse(s, out int parsed) && parsed > 0)
                    targetCount = parsed;

                int picked = SelectFirstReadyAccounts(targetCount);
                StatusMessage = LocalizationService.IsEnglish
                    ? $"Selected {picked} ready idle account(s)."
                    : $"Đã chọn nhanh {picked} tài khoản Sẵn sàng (chưa mở game).";
                if (picked == 0)
                {
                    ToastService.ShowWarning(
                        LocalizationService.IsEnglish ? "Quick Select" : "Chọn nhanh",
                        LocalizationService.IsEnglish
                            ? "No idle Ready accounts found in the current view."
                            : "Không có tài khoản Sẵn sàng (chưa vào game) nào trong danh sách hiện tại.");
                }
            });

            InvertSelectionCommand = new RelayCommand(() =>
            {
                int count = InvertFilteredSelection();
                StatusMessage = LocalizationService.IsEnglish
                    ? $"Inverted selection ({count} account(s) selected)."
                    : $"Đã đảo ngược lựa chọn ({count} tài khoản đang được chọn).";
            });

            SelectByRangePromptCommand = new RelayCommand(() =>
            {
                string prompt = LocalizationService.IsEnglish
                    ? "Enter account index range (e.g. 1-15 or 1-5, 10, 21-30):"
                    : "Nhập dải số thứ tự tài khoản cần chọn (ví dụ: 1-15 hoặc 1-5, 10, 21-30):";
                string title = LocalizationService.IsEnglish ? "Select by Range" : "Chọn theo dải số thứ tự";
                string input = AppServices.Dialogs.ShowInputPrompt(prompt, title);
                if (string.IsNullOrWhiteSpace(input) || input == "/UC")
                    return;

                int picked = SelectRangeExpression(input);
                if (picked > 0)
                {
                    string msg = LocalizationService.IsEnglish
                        ? $"Selected {picked} account(s) from range '{input}'."
                        : $"Đã chọn {picked} tài khoản theo dải '{input}'.";
                    StatusMessage = msg;
                    ToastService.ShowSuccess(title, msg);
                }
                else
                {
                    string warn = LocalizationService.IsEnglish
                        ? $"No accounts matched range '{input}'."
                        : $"Không tìm thấy tài khoản nào khớp với dải '{input}'.";
                    StatusMessage = "" + warn;
                    ToastService.ShowWarning(title, warn);
                }
            });

            MetricCardInGameCommand = new RelayCommand(() =>
            {
                ActiveTab = ActiveTab == AccountFilterTab.InGame
                    ? AccountFilterTab.All
                    : AccountFilterTab.InGame;
            });

            MetricCardReadyCommand = new RelayCommand(() =>
            {
                ActiveTab = ActiveTab == AccountFilterTab.Ready
                    ? AccountFilterTab.All
                    : AccountFilterTab.Ready;
            });

            MetricCardExpiredCommand = new RelayCommand(() =>
            {
                ActiveTab = ActiveTab == AccountFilterTab.Expired
                    ? AccountFilterTab.All
                    : AccountFilterTab.Expired;
            });

            MetricCardRobuxCommand = new RelayCommand(() =>
            {
                ActiveTab = AccountFilterTab.All;
                SortMode = SortMode == AccountSortMode.RobuxDescending
                    ? AccountSortMode.NaturalAscending
                    : AccountSortMode.RobuxDescending;
            });

            CancelBatchLaunchCommand = new RelayCommand(() =>
            {
                LaunchCoordinator.RequestGlobalCancel();
                IsBatchLaunching = false;
                BatchLaunchProgressPercent = 0;
                foreach (var acc in _accounts)
                {
                    acc.IsLaunching = false;
                }
                string stopMsg = LocalizationService.IsEnglish
                    ? "Stopped batch launch queue."
                    : "Đã dừng hàng đợi khởi chạy.";
                StatusMessage = stopMsg;
                ToastService.ShowWarning(
                    LocalizationService.IsEnglish ? "Queue Stopped" : "Đã dừng hàng đợi",
                    stopMsg);
            });

            FocusAccountWindowCommand = new RelayCommand(param =>
            {
                if (param is not AccountViewModel avm || avm.Model == null)
                    return;

                bool ok = RobloxWindowArrangerService.FocusAccountWindow(avm.Model);
                if (ok)
                {
                    StatusMessage = LocalizationService.IsEnglish
                        ? $"Brought Roblox window for {avm.Username} to foreground."
                        : $"Đã đưa cửa sổ Roblox của {avm.Username} lên phía trước.";
                }
                else
                {
                    RefreshLiveProcessStatus();
                    ToastService.ShowInfo(
                        avm.Username,
                        LocalizationService.IsEnglish
                            ? "Could not find an active Roblox window for this account."
                            : "Không tìm thấy cửa sổ Roblox đang mở của tài khoản này.");
                }
            });

            ArrangeRobloxWindowsCommand = new RelayCommand(param =>
            {
                RobloxWindowLayoutMode mode = RobloxWindowLayoutMode.AutoGrid;
                if (param is RobloxWindowLayoutMode explicitMode)
                {
                    mode = explicitMode;
                }
                else if (param is string raw)
                {
                    mode = raw.Trim().ToLowerInvariant() switch
                    {
                        "2x2" or "grid2x2" => RobloxWindowLayoutMode.Grid2x2,
                        "3x2" or "grid3x2" => RobloxWindowLayoutMode.Grid3x2,
                        "4x3" or "grid4x3" => RobloxWindowLayoutMode.Grid4x3,
                        "cascade" or "cascadecorner" => RobloxWindowLayoutMode.CascadeCorner,
                        _ => RobloxWindowLayoutMode.AutoGrid
                    };
                }

                var (arranged, desc) = RobloxWindowArrangerService.ArrangeWindows(mode);
                if (arranged > 0)
                {
                    string okMsg = LocalizationService.IsEnglish
                        ? $"Arranged {arranged} Roblox window(s) ({desc})!"
                        : $"Đã sắp xếp {arranged} cửa sổ Roblox ({desc})!";
                    StatusMessage = okMsg;
                    ToastService.ShowSuccess(
                        LocalizationService.IsEnglish ? "Window Arranger" : "Sắp xếp cửa sổ",
                        okMsg);
                }
                else
                {
                    StatusMessage = $"{desc}";
                    ToastService.ShowWarning(
                        LocalizationService.IsEnglish ? "Window Arranger" : "Sắp xếp cửa sổ",
                        desc);
                }
            });

            ToggleSmartMuteCommand = new RelayCommand(() =>
            {
                bool next = !RobloxAudioService.IsSmartMuteEnabled;
                RobloxAudioService.ConfigureSmartMute(next);
                AccountManager.General?.Set("AutoMuteBackgroundClients", next ? "true" : "false");
                AccountManager.QueueDeferredSaveSettings();
                NotifyToolStatesChanged();

                string msg = next
                    ? (LocalizationService.IsEnglish
                        ? "Smart Auto-Mute ON: Background Roblox clients are automatically muted."
                        : "Đã BẬT Auto-Mute thông minh: Tự tắt tiếng các cửa sổ Roblox chạy nền.")
                    : (LocalizationService.IsEnglish
                        ? "Smart Auto-Mute OFF: Restored audio for all Roblox clients."
                        : "Đã TẮT Auto-Mute thông minh: Đã mở lại tiếng cho toàn bộ Roblox.");
                StatusMessage = msg;
                ToastService.ShowInfo("Smart Auto-Mute", msg);
            });
        }
    }
}
