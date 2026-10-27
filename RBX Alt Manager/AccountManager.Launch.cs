using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager
{
    public partial class AccountManager
    {
        private async Task LaunchAccounts(List<Account> Accounts, long PlaceID, string JobID, bool FollowUser = false, bool VIPServer = false)
        {
            if (Accounts == null || Accounts.Count == 0)
                return;

            // Snapshot the UI selection before awaiting Roblox.  Cards may be
            // rebuilt by presence updates while a launch queue is active.
            List<Account> launchList = Accounts.Where(account => account != null).ToList();
            if (launchList.Count == 0)
                return;

            List<LaunchRequest> requests = new List<LaunchRequest>(launchList.Count);
            foreach (Account account in launchList)
            {
                long placeId = PlaceID;
                string jobId = JobID ?? string.Empty;

                if (!FollowUser)
                {
                    if (placeId <= 0 && long.TryParse(account.GetField("SavedPlaceId"), out long savedPlaceId))
                        placeId = savedPlaceId;
                    if (string.IsNullOrEmpty(jobId) && !string.IsNullOrEmpty(account.GetField("SavedJobId")))
                        jobId = account.GetField("SavedJobId");
                }

                requests.Add(new LaunchRequest(account, placeId, jobId, FollowUser, VIPServer));
            }

            int requestedCount = requests.Count;
            try
            {
                LaunchBatchResult batch = await launchCoordinator.EnqueueAsync(
                    requests,
                    GetConfiguredAccountJoinDelaySeconds(),
                    General.Get<bool>("AsyncJoin"),
                    progress =>
                    {
                        if (!string.IsNullOrWhiteSpace(progress?.Message))
                            SetDashboardStatus(progress.Message);
                    },
                    lifetimeCancellation.Token).ConfigureAwait(true);

                if (batch.Cancelled)
                    SetDashboardStatus("ℹ️ Đã hủy hàng đợi khởi chạy.");
                else
                    SetDashboardStatus($"🚀 Đã hoàn tất hàng đợi khởi chạy cho {requestedCount} tài khoản.");
            }
            catch (InvalidOperationException exception)
            {
                SetDashboardStatus(exception.Message);
            }
        }

        private async Task LaunchHomeAccountsAsync(
            IReadOnlyList<Account> accounts,
            bool preferChromeHome)
        {
            List<Account> launchList = (accounts ?? Array.Empty<Account>())
                .Where(account => account != null)
                .Distinct()
                .ToList();
            if (launchList.Count == 0)
                return;

            if (General.Get<bool>("EnableMultiRbx") && !RobloxSingletonService.MutexHeld)
            {
                TryEnableMultiRoblox(promptIfRobloxRunning: false);
            }

            List<LaunchRequest> requests = launchList
                .Select(account => new LaunchRequest(
                    account,
                    placeId: 0,
                    jobId: string.Empty,
                    followUser: false,
                    vipServer: false,
                    openHome: true,
                    preferChromeHome: preferChromeHome))
                .ToList();

            try
            {
                LaunchBatchResult batch = await launchCoordinator.EnqueueAsync(
                    requests,
                    // Home uses the short stagger used by the old dedicated
                    // loop; the normal account join delay should not make a
                    // browser Home batch wait 30 seconds between cards.
                    Math.Max(1, HomeLaunchStaggerMilliseconds / 1000),
                    asyncJoin: false,
                    progress: progress =>
                    {
                        if (!string.IsNullOrWhiteSpace(progress?.Message))
                            SetDashboardStatus(progress.Message);
                    },
                    lifetimeCancellation.Token).ConfigureAwait(true);

                var failed = batch.Results.FirstOrDefault(r => r.Status != LaunchOutcome.Success);
                if (failed != null)
                {
                    SetDashboardStatus(failed.Message ?? failed.Result);
                }
                else if (batch.Cancelled)
                {
                    SetDashboardStatus("ℹ️ Đã hủy hàng đợi khởi chạy.");
                }
                else
                {
                    SetDashboardStatus($"🏠 Đã mở Roblox Home cho {launchList.Count} tài khoản.");
                }
            }
            catch (InvalidOperationException exception)
            {
                SetDashboardStatus(exception.Message);
            }
            catch (OperationCanceledException)
            {
                SetDashboardStatus("ℹ️ Đã hủy hàng đợi khởi chạy.");
            }
        }

        private async Task<string> ExecuteLaunchRequestAsync(LaunchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.OpenHome)
                return await browserService.OpenHomeAsync(
                    request.Account,
                    request.PreferChromeHome,
                    cancellationToken).ConfigureAwait(false);

            return await request.Account.JoinServer(
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
