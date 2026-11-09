#nullable enable
using System;
using System.Windows.Input;
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
                StatusMessage = Shiro.Services.LocalizationService.Format("Main.QuickSelectedReady", picked);
                if (picked == 0)
                {
                    ToastService.ShowWarning(
                        Shiro.Services.LocalizationService.Get("Dash_QuickSelect"),
                        Shiro.Services.LocalizationService.Get("Main.NoReadyAccountsInList"));
                }
            });

            InvertSelectionCommand = new RelayCommand(() =>
            {
                int count = InvertFilteredSelection();
                StatusMessage = Shiro.Services.LocalizationService.Format("Main.InvertedSelectionResult", count);
            });

            SelectByRangePromptCommand = new RelayCommand(() =>
            {
                string prompt = Shiro.Services.LocalizationService.Get("Main.EnterSelectionRangePrompt");
                string title = Shiro.Services.LocalizationService.Get("Main.SelectByRangeTitle");
                string input = AppServices.Dialogs.ShowInputPrompt(prompt, title);
                if (string.IsNullOrWhiteSpace(input) || input == "/UC")
                    return;

                int picked = SelectRangeExpression(input);
                if (picked > 0)
                {
                    string msg = Shiro.Services.LocalizationService.Format("Main.SelectedByRangeResult", picked, input);
                    StatusMessage = msg;
                    ToastService.ShowSuccess(title, msg);
                }
                else
                {
                    string warn = Shiro.Services.LocalizationService.Format("Main.NoAccountsMatchedRange", input);
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
                string stopMsg = Shiro.Services.LocalizationService.Get("Main.LaunchQueueStopped");
                StatusMessage = stopMsg;
                ToastService.ShowWarning(
                    Shiro.Services.LocalizationService.Get("Main.LaunchQueueStoppedTitle"),
                    stopMsg);
            });

            FocusAccountWindowCommand = new RelayCommand(param =>
            {
                if (param is not AccountViewModel avm || avm.Model == null)
                    return;

                bool ok = RobloxWindowArrangerService.FocusAccountWindow(avm.Model);
                if (ok)
                {
                    StatusMessage = Shiro.Services.LocalizationService.Format("Main.BroughtWindowToFront", avm.Username);
                }
                else
                {
                    RefreshLiveProcessStatus();
                    ToastService.ShowInfo(
                        avm.Username,
                        Shiro.Services.LocalizationService.Get("Main.WindowNotFoundForAccount"));
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
                    string okMsg = Shiro.Services.LocalizationService.Format("Main.WindowsArrangedWithLayout", arranged, desc);
                    StatusMessage = okMsg;
                    ToastService.ShowSuccess(
                        Shiro.Services.LocalizationService.Get("Main.ArrangeWindowsTitle"),
                        okMsg);
                }
                else
                {
                    StatusMessage = $"{desc}";
                    ToastService.ShowWarning(
                        Shiro.Services.LocalizationService.Get("Main.ArrangeWindowsTitle"),
                        desc);
                }
            });

            ToggleSmartMuteCommand = new RelayCommand(() =>
            {
                bool next = !RobloxAudioService.IsSmartMuteEnabled;
                RobloxAudioService.ConfigureSmartMute(next);
                _settings.Set("AutoMuteBackgroundClients", next ? "true" : "false");
                AppServices.HostUiBridge.QueueDeferredSaveSettings();
                NotifyToolStatesChanged();

                string msg = next
                    ? LocalizationService.Get("Main.SmartAutoMuteActivated")
                    : LocalizationService.Get("Main.SmartAutoMuteDeactivated");
                StatusMessage = msg;
                ToastService.ShowInfo("Smart Auto-Mute", msg);
            });
        }
    }
}
