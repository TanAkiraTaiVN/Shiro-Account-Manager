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
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.AddAccountBeforeLaunch"));
                return;
            }

            List<Account> multiSel = GetCurrentSelectedAccounts();
            if (multiSel == null || multiSel.Count == 0)
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.SelectAccountBeforeHome"));
                return;
            }

            if (multiSel.Count > 1 && General.Get<bool>("EnableMultiRbx") && !RobloxSingletonService.MutexHeld && !TryEnableMultiRoblox(promptIfRobloxRunning: true))
                return;

            _ = LaunchHomeAccountsAsync(multiSel, preferChromeHome: false);
            SetDashboardStatus(Shiro.Services.LocalizationService.Format("AccountManager.OpeningHome", multiSel.Count));
        }

        internal async Task ModernJoinServerDirect(List<Account> accounts, long placeId, string jobId)
        {
            var snapshot = GetAccountsSnapshot();
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
                    Shiro.AvaloniaUI.Views.MainWindow.Instance?.DashboardView?.ViewModel?.PlaceId,
                    targetAccounts,
                    ServerBrowserStateService.Default.IsPlaceIdExplicitlyCleared ? string.Empty : CurrentPlaceId);

                if (!string.IsNullOrWhiteSpace(pidSource))
                    placeId = AccountLauncherService.ParsePlaceId(pidSource);
            }

            if (placeId <= 0)
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.EnterPlaceIdBeforeJoin"));
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
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.MultiRobloxFailedCloseClient"));
                return;
            }

            if (placeId > 0)
                _ = ObserveBackgroundTaskAsync(AddRecentGame(new Game(placeId)), "recent game update");

            CancelLaunching();
            if (!await WaitForLaunchQueueToDrainAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(true))
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.PreviousQueueStopping"));
                return;
            }

            SelectedAccounts = targetAccounts.ToList();
            SelectedAccount = SelectedAccounts.Count == 1 ? SelectedAccounts[0] : null;

            string serverDesc = string.IsNullOrEmpty(cleanJobId) ? "Public Server" : (isVip ? "VIP Server" : $"Server {cleanJobId}");
            SetDashboardStatus(Shiro.Services.LocalizationService.Format("AccountManager.JoiningServer", serverDesc, placeId, SelectedAccounts.Count));

            await LaunchAccounts(SelectedAccounts, placeId, cleanJobId, false, isVip).ConfigureAwait(true);
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

            if (Shiro.AvaloniaUI.Views.MainWindow.Instance?.DashboardView?.ViewModel is var mainVm && mainVm != null)
            {
                if (notify)
                {
                    mainVm.StatusMessage = totalKilled > 0
                        ? Shiro.Services.LocalizationService.Format("AccountManager.KilledRobloxProcessesExclamation", totalKilled)
                        : Shiro.Services.LocalizationService.Get("AccountManager.NoRobloxProcessesRunning");
                }
                foreach (var acc in mainVm.Accounts) acc.IsInGame = false;
                mainVm.NotifyCounts();
            }
            if (General.Get<bool>("EnableMultiRbx") && !RobloxSingletonService.MutexHeld && UpdateMultiRoblox() && notify)
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.KilledAndActivatedMulti"));

            ModernRefreshPresence();
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
