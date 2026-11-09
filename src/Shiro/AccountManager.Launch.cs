using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
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
                    AppSettings.General.Get<bool>("AsyncJoin"),
                    progress =>
                    {
                        if (!string.IsNullOrWhiteSpace(progress?.Message))
                            SetDashboardStatus(progress.Message);
                    },
                    lifetimeCancellation.Token).ConfigureAwait(true);

                var expiredAccounts = batch.Results
                    .Where(r => r.Status == LaunchOutcome.TicketError || (r.Account != null && !r.Account.Valid))
                    .Select(r => r.Account?.Username ?? "Unknown")
                    .Distinct()
                    .ToList();

                var otherFailedAccounts = batch.Results
                    .Where(r => (r.Status == LaunchOutcome.Failed || r.Status == LaunchOutcome.Timeout) &&
                                !expiredAccounts.Contains(r.Account?.Username ?? ""))
                    .Select(r => r.Account?.Username ?? "Unknown")
                    .Distinct()
                    .ToList();

                int successCount = batch.Results.Count(r => r.Status == LaunchOutcome.Success);

                if (batch.Cancelled)
                {
                    SetDashboardStatus(Loc.Get("Launch_QueueCancelled"));
                }
                else if (expiredAccounts.Count > 0 || otherFailedAccounts.Count > 0)
                {
                    string errDetail = "";
                    if (expiredAccounts.Count > 0)
                        errDetail += Loc.Format("Launch_Summary_ExpiredDetail", expiredAccounts.Count, string.Join(", ", expiredAccounts));
                    if (otherFailedAccounts.Count > 0)
                        errDetail += Loc.Format("Launch_Summary_OtherDetail", otherFailedAccounts.Count, string.Join(", ", otherFailedAccounts));

                    Program.Logger.Warn($"[LaunchQueue] Launch queue completed: {successCount}/{requestedCount} succeeded. {errDetail}");
                    SetDashboardStatus(Loc.Format("Launch_Summary_CompletedWithErrors", successCount, requestedCount, errDetail));

                    AppDispatcher.Post(() =>
                    {
                        string toastMsg = Loc.Format("Launch_Toast_SuccessCount", successCount, requestedCount) + "\n" +
                                          (expiredAccounts.Count > 0 ? Loc.Format("Launch_Toast_ExpiredList", string.Join(", ", expiredAccounts)) + "\n" : "") +
                                          (otherFailedAccounts.Count > 0 ? Loc.Format("Launch_Toast_ErrorList", string.Join(", ", otherFailedAccounts)) + "\n" : "") +
                                          Loc.Get("Launch_Toast_CheckExpiredFooter");
                        ToastService.ShowWarning(Loc.Get("Launch_Toast_SessionError_Title"), toastMsg, 7.0);
                    });
                }
                else
                {
                    Program.Logger.Info($"[LaunchQueue] Completed: All {successCount}/{requestedCount} accounts launched successfully.");
                    SetDashboardStatus(Loc.Format("Launch_Summary_Success", successCount, requestedCount));
                    AppDispatcher.Post(() =>
                    {
                        ToastService.ShowSuccess(Loc.Get("Launch_Toast_Success_Title"), Loc.Format("Launch_Toast_Success_Message", successCount, requestedCount));
                    });
                }
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

            if (AppSettings.General.Get<bool>("EnableMultiRbx") && !RobloxSingletonService.MutexHeld)
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

                var expiredAccounts = batch.Results
                    .Where(r => r.Status == LaunchOutcome.TicketError || (r.Account != null && !r.Account.Valid))
                    .Select(r => r.Account?.Username ?? "Unknown")
                    .Distinct()
                    .ToList();

                var otherFailedAccounts = batch.Results
                    .Where(r => (r.Status == LaunchOutcome.Failed || r.Status == LaunchOutcome.Timeout) &&
                                !expiredAccounts.Contains(r.Account?.Username ?? ""))
                    .Select(r => r.Account?.Username ?? "Unknown")
                    .Distinct()
                    .ToList();

                int successCount = batch.Results.Count(r => r.Status == LaunchOutcome.Success);

                if (batch.Cancelled)
                {
                    SetDashboardStatus(Loc.Get("Launch_QueueCancelled"));
                }
                else if (expiredAccounts.Count > 0 || otherFailedAccounts.Count > 0)
                {
                    string errDetail = "";
                    if (expiredAccounts.Count > 0)
                        errDetail += Loc.Format("Launch_Summary_ExpiredDetail", expiredAccounts.Count, string.Join(", ", expiredAccounts));
                    if (otherFailedAccounts.Count > 0)
                        errDetail += Loc.Format("Launch_Summary_OtherDetail", otherFailedAccounts.Count, string.Join(", ", otherFailedAccounts));

                    Program.Logger.Warn($"[LaunchHomeQueue] Completed Home queue: {successCount}/{launchList.Count} succeeded. {errDetail}");
                    SetDashboardStatus(Loc.Format("Launch_Summary_CompletedWithErrors", successCount, launchList.Count, errDetail));

                    AppDispatcher.Post(() =>
                    {
                        string toastMsg = Loc.Format("Launch_Home_Toast_SuccessCount", successCount, launchList.Count) + "\n" +
                                          (expiredAccounts.Count > 0 ? Loc.Format("Launch_Toast_ExpiredList", string.Join(", ", expiredAccounts)) + "\n" : "") +
                                          (otherFailedAccounts.Count > 0 ? Loc.Format("Launch_Toast_ErrorList", string.Join(", ", otherFailedAccounts)) + "\n" : "") +
                                          Loc.Get("Launch_Toast_CheckExpiredFooter");
                        ToastService.ShowWarning(Loc.Get("Launch_Toast_SessionError_Title"), toastMsg, 7.0);
                    });
                }
                else
                {
                    Program.Logger.Info($"[LaunchHomeQueue] Completed: All {successCount}/{launchList.Count} accounts launched Home successfully.");
                    SetDashboardStatus(Loc.Format("Launch_Home_Summary_Success", launchList.Count));
                    AppDispatcher.Post(() =>
                    {
                        ToastService.ShowSuccess(Loc.Get("Launch_Home_Toast_Success_Title"), Loc.Format("Launch_Home_Toast_Success_Message", successCount, launchList.Count));
                    });
                }
            }
            catch (InvalidOperationException exception)
            {
                SetDashboardStatus(exception.Message);
            }
            catch (OperationCanceledException)
            {
                SetDashboardStatus(Shiro.Services.LocalizationService.Get("Launch_QueueCancelled"));
            }
        }

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
