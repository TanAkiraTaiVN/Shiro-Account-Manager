using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager
    {
        internal void SetDashboardStatus(string message) => Shiro.AvaloniaUI.Views.MainWindow.Instance?.SetStatus(message);

        internal async Task ModernLaunchAccountAppHome(Account account)
        {
            if (account != null)
                await LaunchHomeAccountsAsync(new[] { account }, preferChromeHome: false).ConfigureAwait(true);
        }

        internal async Task ModernOpenAccountHomeChrome(Account account)
        {
            if (account != null)
                await LaunchHomeAccountsAsync(new[] { account }, preferChromeHome: true).ConfigureAwait(true);
        }

        internal List<Account> GetCurrentSelectedAccounts() =>
            Shiro.AvaloniaUI.Views.MainWindow.Instance?.DashboardView?.ViewModel?.GetSelectedAccounts()
            ?? SelectedAccounts?.ToList() ?? new List<Account>();

        internal void ModernOpenAccountControl() => AppServices.Dialogs.OpenNexusWindow();

        internal void ModernMultiLaunch()
        {
            if (GetAccountsSnapshot().Count == 0)
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("Auto_AccountManagerMo_011"));
                return;
            }

            List<Account> multiSel = GetCurrentSelectedAccounts();
            if (multiSel == null || multiSel.Count == 0)
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("Auto_AccountManagerMo_012"));
                return;
            }

            if (multiSel.Count > 1 && General.Get<bool>("EnableMultiRbx") && !RobloxSingletonService.MutexHeld && !TryEnableMultiRoblox(promptIfRobloxRunning: true))
                return;

            _ = LaunchHomeAccountsAsync(multiSel, preferChromeHome: false);
            SetDashboardStatus(Shiro.Services.LocalizationService.Format("Auto_AccountManagerMo_013", multiSel.Count));
        }

        internal async Task ModernJoinServerDirect(List<Account> accounts, long placeId, string jobId)
        {
            var snapshot = GetAccountsSnapshot();
            if (snapshot.Count == 0)
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("Auto_AccountManagerMo_014"));
                return;
            }

            List<Account>? targetAccounts = accounts?.Where(a => a != null).ToList();
            if (targetAccounts == null || targetAccounts.Count == 0)
            {
                var multiSel = GetCurrentSelectedAccounts();
                if (multiSel != null && multiSel.Count > 0)
                    targetAccounts = multiSel.ToList();
            }

            if (targetAccounts == null || targetAccounts.Count == 0)
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("Auto_AccountManagerMo_015"));
                return;
            }

            if (placeId <= 0)
            {
                string? pidSource = Shiro.AvaloniaUI.Views.MainWindow.Instance?.DashboardView?.ViewModel?.PlaceId is string vmPid && !string.IsNullOrWhiteSpace(vmPid)
                    ? vmPid
                    : !string.IsNullOrWhiteSpace(CurrentPlaceId)
                        ? CurrentPlaceId
                        : targetAccounts.FirstOrDefault(a => !string.IsNullOrEmpty(a.GetField("SavedPlaceId")))?.GetField("SavedPlaceId");

                if (!string.IsNullOrWhiteSpace(pidSource))
                    placeId = AccountLauncherService.ParsePlaceId(pidSource);
            }

            if (placeId <= 0)
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("Auto_AccountManagerMo_016"));
                return;
            }

            string cleanJobId = !string.IsNullOrWhiteSpace(jobId)
                ? jobId
                : Shiro.AvaloniaUI.Views.MainWindow.Instance?.DashboardView?.ViewModel?.JobId is string vmJid && !string.IsNullOrWhiteSpace(vmJid)
                    ? vmJid
                    : !string.IsNullOrWhiteSpace(CurrentJobId)
                        ? CurrentJobId
                        : targetAccounts.FirstOrDefault(a => !string.IsNullOrEmpty(a.GetField("SavedJobId")))?.GetField("SavedJobId") ?? string.Empty;

            cleanJobId = cleanJobId?.Trim() ?? string.Empty;
            bool isVip = cleanJobId.StartsWith("VIP:", StringComparison.OrdinalIgnoreCase);
            if (isVip) cleanJobId = cleanJobId.Substring(4).Trim();

            CurrentPlaceId = placeId.ToString();
            CurrentJobId = isVip ? ("VIP:" + cleanJobId) : cleanJobId;

            if (targetAccounts.Count > 1 && General.Get<bool>("EnableMultiRbx") && !RobloxSingletonService.MutexHeld && !TryEnableMultiRoblox(promptIfRobloxRunning: false))
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("Auto_AccountManagerMo_017"));
                return;
            }

            if (placeId > 0)
                _ = ObserveBackgroundTaskAsync(AddRecentGame(new Game(placeId)), "recent game update");

            CancelLaunching();
            if (!await WaitForLaunchQueueToDrainAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(true))
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("Auto_AccountManagerMo_018"));
                return;
            }

            SelectedAccounts = targetAccounts.ToList();
            SelectedAccount = SelectedAccounts.Count == 1 ? SelectedAccounts[0] : null;

            string serverDesc = string.IsNullOrEmpty(cleanJobId) ? "Public Server" : (isVip ? "VIP Server" : $"Server {cleanJobId}");
            SetDashboardStatus(Shiro.Services.LocalizationService.Format("Auto_AccountManagerMo_019", serverDesc, placeId, SelectedAccounts.Count));

            await LaunchAccounts(SelectedAccounts, placeId, cleanJobId, false, isVip).ConfigureAwait(true);
        }

        internal void ModernCloseRoblox(bool notify = true)
        {
            int totalKilled = RobloxProcessService.KillAllRobloxProcesses();
            if (notify)
            {
                SetDashboardStatus(totalKilled > 0
                    ? Shiro.Services.LocalizationService.Format("Auto_AccountManagerMo_020", totalKilled)
                    : Shiro.Services.LocalizationService.Get("Auto_AccountManagerMo_021"));
            }

            if (Shiro.AvaloniaUI.Views.MainWindow.Instance?.DashboardView?.ViewModel is var mainVm && mainVm != null)
            {
                if (notify)
                {
                    mainVm.StatusMessage = totalKilled > 0
                        ? Shiro.Services.LocalizationService.Format("Auto_AccountManagerMo_022", totalKilled)
                        : Shiro.Services.LocalizationService.Get("Auto_AccountManagerMo_021");
                }
                foreach (var acc in mainVm.Accounts) acc.IsInGame = false;
                mainVm.NotifyCounts();
            }
            if (General.Get<bool>("EnableMultiRbx") && !RobloxSingletonService.MutexHeld && UpdateMultiRoblox() && notify)
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("Auto_AccountManagerMo_023"));

            ModernRefreshPresence();
        }
    }
}
