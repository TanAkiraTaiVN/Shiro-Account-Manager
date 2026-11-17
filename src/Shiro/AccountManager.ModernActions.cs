using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager
    {
        internal void SetDashboardStatus(string message) => AppServices.HostUiBridge.NotifyWindowStatus(message);

        internal async Task ModernLaunchAccountAppHome(Account account)
        {
            if (account != null)
                await LaunchHomeAccountsAsync(new[] { account }, preferChromeHome: false).ConfigureAwait(true);
        }

        internal List<Account> GetCurrentSelectedAccounts() =>
            AppServices.HostUiBridge.TryGetDashboardSelectedAccounts()
            ?? AppServices.SelectionState.SelectedAccounts?.ToList() ?? new List<Account>();

        internal void ModernMultiLaunch()
        {
            if (AppServices.AccountRepository.Count == 0)
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.AddAccountBeforeLaunch"));
                return;
            }

            List<Account> multiSel = GetCurrentSelectedAccounts();
            if (multiSel == null || multiSel.Count == 0)
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.SelectAccountBeforeHome"));
                return;
            }

            if (multiSel.Count > 1 && AppSettings.General.Get<bool>("EnableMultiRbx") && !RobloxSingletonService.MutexHeld && !TryEnableMultiRoblox(promptIfRobloxRunning: true))
                return;

            _ = LaunchHomeAccountsAsync(multiSel, preferChromeHome: false);
            SetDashboardStatus(Shiro.Services.LocalizationService.Format("AccountManager.OpeningHome", multiSel.Count));
        }

        internal async Task ModernJoinServerDirect(List<Account> accounts, long placeId, string jobId)
        {
            var snapshot = AppServices.AccountRepository.GetAll();
            if (snapshot.Count == 0)
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.AddAccountBeforeJoin"));
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
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.SelectAccountBeforeJoin"));
                return;
            }

            if (placeId <= 0)
            {
                // Prefer the dashboard input, then the global CurrentPlaceId (unless the user explicitly
                // cleared it), and only then fall back to the target account's own SavedPlaceId.
                string? pidSource = ResolveEffectivePlaceIdSource(
                    AppServices.HostUiBridge.TryGetDashboardPlaceId(),
                    targetAccounts,
                    (AppServices.ServerBrowserState as ServerBrowserStateService)?.IsPlaceIdExplicitlyCleared == true ? string.Empty : AppServices.ServerBrowserState.CurrentPlaceId);

                if (!string.IsNullOrWhiteSpace(pidSource))
                    placeId = AccountLauncherService.ParsePlaceId(pidSource);
            }

            if (placeId <= 0)
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.EnterPlaceIdBeforeJoin"));
                return;
            }

            string? dashboardJobId = AppServices.HostUiBridge.TryGetDashboardJobId();
            string cleanJobId = ResolveJoinJobId(jobId, dashboardJobId, placeId, targetAccounts, AppServices.ServerBrowserState.CurrentJobId);

            cleanJobId = cleanJobId.Trim();
            bool isVip = cleanJobId.StartsWith("VIP:", StringComparison.OrdinalIgnoreCase);
            if (isVip) cleanJobId = cleanJobId.Substring(4).Trim();

            AppServices.ServerBrowserState.CurrentPlaceId = placeId.ToString();
            AppServices.ServerBrowserState.CurrentJobId = isVip ? ("VIP:" + cleanJobId) : cleanJobId;

            AppServices.SelectionState.SelectedAccounts = targetAccounts.ToList();
            AppServices.SelectionState.SelectedAccount = AppServices.SelectionState.SelectedAccounts.Count == 1 ? AppServices.SelectionState.SelectedAccounts[0] : null;

            if (targetAccounts.Count > 1 && AppSettings.General.Get<bool>("EnableMultiRbx") && !RobloxSingletonService.MutexHeld && !TryEnableMultiRoblox(promptIfRobloxRunning: false))
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.MultiRobloxFailedCloseClient"));
                return;
            }

            if (placeId > 0)
                _ = ObserveBackgroundTaskAsync(AddRecentGame(new Game(placeId)), "recent game update");

            if (launchCoordinator != null && launchCoordinator.IsRunning)
            {
                var appendRequests = AccountLaunchBatchService.BuildLaunchRequests(
                    AppServices.SelectionState.SelectedAccounts, placeId, cleanJobId, followUser: false, vipServer: isVip);
                if (launchCoordinator.TryAppendRequests(appendRequests, out int appendedCount))
                {
                    SetDashboardStatus(appendedCount > 0
                        ? Shiro.Services.LocalizationService.Format("AccountManager.AppendedToQueue", appendedCount)
                        : Shiro.Services.LocalizationService.Get("AccountManager.AlreadyInQueue"));
                    return;
                }
            }

            CancelLaunching();
            if (!await WaitForLaunchQueueToDrainAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(true))
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.PreviousQueueStopping"));
                return;
            }

            string serverDesc = string.IsNullOrEmpty(cleanJobId) ? "Public Server" : (isVip ? "VIP Server" : $"Server {cleanJobId}");
            SetDashboardStatus(Shiro.Services.LocalizationService.Format("AccountManager.JoiningServer", serverDesc, placeId, AppServices.SelectionState.SelectedAccounts.Count));

            await LaunchAccounts(AppServices.SelectionState.SelectedAccounts, placeId, cleanJobId, false, isVip).ConfigureAwait(true);
        }

        internal void ModernCloseRoblox(bool notify = true)
        {
            int totalKilled = RobloxProcessService.KillAllRobloxProcesses();
            if (notify)
            {
                SetDashboardStatus(totalKilled > 0
                    ? Shiro.Services.LocalizationService.Format("AccountManager.KilledRobloxProcesses", totalKilled)
                    : Shiro.Services.LocalizationService.Get("AccountManager.NoRobloxProcessesRunning"));
            }

            AppServices.HostUiBridge.NotifyRobloxProcessesClosed(totalKilled, notify);
            if (AppSettings.General.Get<bool>("EnableMultiRbx") && !RobloxSingletonService.MutexHeld && UpdateMultiRoblox() && notify)
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.KilledAndActivatedMulti"));

            ModernRefreshPresence();
        }

        internal static string ResolveJoinJobId(
            string? explicitJobId,
            string? dashboardJobId,
            long placeId,
            IReadOnlyList<Account>? targetAccounts,
            string? sessionCurrentJobId)
        {
            if (!string.IsNullOrWhiteSpace(explicitJobId))
                return explicitJobId.Trim();

            // When Dashboard UI is attached, honor the visible Job Id box directly so leaving it blank joins public servers.
            if (dashboardJobId != null)
                return string.IsNullOrWhiteSpace(dashboardJobId) ? string.Empty : dashboardJobId.Trim();

            if (!string.IsNullOrWhiteSpace(sessionCurrentJobId))
                return sessionCurrentJobId.Trim();

            if (targetAccounts == null)
                return string.Empty;

            foreach (var account in targetAccounts)
            {
                if (account == null) continue;
                string savedJob = account.GetField("SavedJobId");
                if (string.IsNullOrEmpty(savedJob)) continue;

                if (placeId <= 0 ||
                    (long.TryParse(account.GetField("SavedPlaceId"), out long savedPid) && savedPid == placeId))
                {
                    return savedJob.Trim();
                }
            }

            return string.Empty;
        }

        internal static string? ResolveEffectivePlaceIdSource(string? viewModelPlaceId, IEnumerable<Account>? targetAccounts, string? fallbackCurrentPlaceId)
        {
            if (!string.IsNullOrWhiteSpace(viewModelPlaceId))
                return viewModelPlaceId;
            if (!string.IsNullOrWhiteSpace(fallbackCurrentPlaceId))
                return fallbackCurrentPlaceId;
            return targetAccounts?.FirstOrDefault(a => a != null && !string.IsNullOrWhiteSpace(a.GetField("SavedPlaceId")))?.GetField("SavedPlaceId");
        }
    }
}
