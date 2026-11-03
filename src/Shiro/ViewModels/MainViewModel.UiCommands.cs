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
                    : Shiro.Services.LocalizationService.Format("Auto_MainViewModelUiC_498", picked);
                if (picked == 0)
                {
                    ToastService.ShowWarning(
                        LocalizationService.IsEnglish ? "Quick Select" : Shiro.Services.LocalizationService.Get("Dash_QuickSelect"),
                        LocalizationService.IsEnglish
                            ? "No idle Ready accounts found in the current view."
                            : Shiro.Services.LocalizationService.Get("Auto_MainViewModelUiC_499"));
                }
            });

            InvertSelectionCommand = new RelayCommand(() =>
            {
                int count = InvertFilteredSelection();
                StatusMessage = LocalizationService.IsEnglish
                    ? $"Inverted selection ({count} account(s) selected)."
                    : Shiro.Services.LocalizationService.Format("Auto_MainViewModelUiC_500", count);
            });

            SelectByRangePromptCommand = new RelayCommand(() =>
            {
                string prompt = LocalizationService.IsEnglish
                    ? "Enter account index range (e.g. 1-15 or 1-5, 10, 21-30):"
                    : Shiro.Services.LocalizationService.Get("Auto_MainViewModelUiC_501");
                string title = LocalizationService.IsEnglish ? "Select by Range" : Shiro.Services.LocalizationService.Get("Auto_MainViewModelUiC_502");
                string input = AppServices.Dialogs.ShowInputPrompt(prompt, title);
                if (string.IsNullOrWhiteSpace(input) || input == "/UC")
                    return;

                int picked = SelectRangeExpression(input);
                if (picked > 0)
                {
                    string msg = LocalizationService.IsEnglish
                        ? $"Selected {picked} account(s) from range '{input}'."
                        : Shiro.Services.LocalizationService.Format("Auto_MainViewModelUiC_503", picked, input);
                    StatusMessage = msg;
                    ToastService.ShowSuccess(title, msg);
                }
                else
                {
                    string warn = LocalizationService.IsEnglish
                        ? $"No accounts matched range '{input}'."
                        : Shiro.Services.LocalizationService.Format("Auto_MainViewModelUiC_504", input);
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
                    : Shiro.Services.LocalizationService.Get("Auto_MainViewModelUiC_505");
                StatusMessage = stopMsg;
                ToastService.ShowWarning(
                    LocalizationService.IsEnglish ? "Queue Stopped" : Shiro.Services.LocalizationService.Get("Auto_MainViewModelUiC_506"),
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
                        : Shiro.Services.LocalizationService.Format("Auto_MainViewModelUiC_507", avm.Username);
                }
                else
                {
                    RefreshLiveProcessStatus();
                    ToastService.ShowInfo(
                        avm.Username,
                        LocalizationService.IsEnglish
                            ? "Could not find an active Roblox window for this account."
                            : Shiro.Services.LocalizationService.Get("Auto_MainViewModelUiC_508"));
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
                        : Shiro.Services.LocalizationService.Format("Auto_MainViewModelUiC_509", arranged, desc);
                    StatusMessage = okMsg;
                    ToastService.ShowSuccess(
                        LocalizationService.IsEnglish ? "Window Arranger" : Shiro.Services.LocalizationService.Get("Auto_MainViewModelUiC_510"),
                        okMsg);
                }
                else
                {
                    StatusMessage = $"{desc}";
                    ToastService.ShowWarning(
                        LocalizationService.IsEnglish ? "Window Arranger" : Shiro.Services.LocalizationService.Get("Auto_MainViewModelUiC_510"),
                        desc);
                }
            });

            ToggleSmartMuteCommand = new RelayCommand(() =>
            {
                bool next = !RobloxAudioService.IsSmartMuteEnabled;
                RobloxAudioService.ConfigureSmartMute(next);
                AppSettings.General?.Set("AutoMuteBackgroundClients", next ? "true" : "false");
                AppSettings.QueueDeferredSave();
                NotifyToolStatesChanged();

                string msg = next
                    ? (LocalizationService.IsEnglish
                        ? "Smart Auto-Mute ON: Background Roblox clients are automatically muted."
                        : Shiro.Services.LocalizationService.Get("Auto_MainViewModelUiC_511"))
                    : (LocalizationService.IsEnglish
                        ? "Smart Auto-Mute OFF: Restored audio for all Roblox clients."
                        : Shiro.Services.LocalizationService.Get("Auto_MainViewModelUiC_512"));
                StatusMessage = msg;
                ToastService.ShowInfo("Smart Auto-Mute", msg);
            });
        }
    }
}
