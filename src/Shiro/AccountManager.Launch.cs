using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager
    {
        private Task LaunchAccounts(List<Account> Accounts, long PlaceID, string JobID, bool FollowUser = false, bool VIPServer = false) =>
            AccountLaunchBatchService.LaunchAccountsAsync(
                launchCoordinator,
                Accounts,
                PlaceID,
                JobID,
                FollowUser,
                VIPServer,
                GetConfiguredAccountJoinDelaySeconds(),
                AppServices.GeneralSettings.GetBool("AsyncJoin"),
                SetDashboardStatus,
                lifetimeCancellation.Token);

        private Task LaunchHomeAccountsAsync(IReadOnlyList<Account> accounts, bool preferChromeHome) =>
            AccountLaunchBatchService.LaunchHomeAccountsAsync(
                launchCoordinator,
                accounts,
                preferChromeHome,
                HomeLaunchStaggerMilliseconds,
                () => { if (AppServices.GeneralSettings.GetBool("EnableMultiRbx") && !RobloxSingletonService.MutexHeld) TryEnableMultiRoblox(promptIfRobloxRunning: false); },
                SetDashboardStatus,
                lifetimeCancellation.Token);

        private async Task<LaunchResult> ExecuteLaunchRequestAsync(LaunchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.OpenHome)
                return await browserService.OpenHomeResultAsync(
                    request.Account,
                    request.PreferChromeHome,
                    cancellationToken).ConfigureAwait(false);

            return await request.Account.JoinServerResult(
                request.PlaceId,
                request.JobId,
                request.FollowUser,
                request.VipServer,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> WaitForLaunchQueueToDrainAsync(TimeSpan timeout)
        {
            if (launchCoordinator == null)
                return true;

            // The lifetime token is deliberately not used here: shutdown
            // cancels it before waiting, but the queue still needs a short
            // grace period to unwind its finally block and release the gate.
            return await launchCoordinator.WaitForIdleAsync(timeout, CancellationToken.None).ConfigureAwait(true);
        }

        public void NextAccount() => launchCoordinator.SignalNext();
        public void CancelLaunching() => launchCoordinator.Cancel();
    }
}
