#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    internal static class AccountLaunchBatchService
    {
        public static async Task LaunchAccountsAsync(
            ILaunchCoordinator launchCoordinator,
            List<Account>? accounts,
            long placeId,
            string? jobId,
            bool followUser,
            bool vipServer,
            int accountJoinDelaySeconds,
            bool asyncJoin,
            Action<string>? statusCallback,
            CancellationToken cancellationToken)
        {
            if (accounts == null || accounts.Count == 0)
                return;

            List<Account> launchList = accounts.Where(account => account != null).ToList();
            if (launchList.Count == 0)
                return;

            List<LaunchRequest> requests = new List<LaunchRequest>(launchList.Count);
            foreach (Account account in launchList)
            {
                long targetPlaceId = placeId;
                string targetJobId = jobId ?? string.Empty;

                if (!followUser)
                {
                    if (targetPlaceId <= 0 && long.TryParse(account.GetField("SavedPlaceId"), out long savedPlaceId))
                        targetPlaceId = savedPlaceId;
                    if (string.IsNullOrEmpty(targetJobId) && !string.IsNullOrEmpty(account.GetField("SavedJobId")))
                        targetJobId = account.GetField("SavedJobId");
                }

                requests.Add(new LaunchRequest(account, targetPlaceId, targetJobId, followUser, vipServer));
            }

            int requestedCount = requests.Count;
            void SetStatus(string message)
            {
                AppServices.HostUiBridge.NotifyWindowStatus(message);
                statusCallback?.Invoke(message);
            }

            try
            {
                LaunchBatchResult batch = await launchCoordinator.EnqueueAsync(
                    requests,
                    accountJoinDelaySeconds,
                    asyncJoin,
                    progress =>
                    {
                        if (!string.IsNullOrWhiteSpace(progress?.Message))
                            SetStatus(progress.Message);
                    },
                    cancellationToken).ConfigureAwait(true);

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
                    SetStatus(LocalizationService.Get("Launch_QueueCancelled"));
                }
                else if (expiredAccounts.Count > 0 || otherFailedAccounts.Count > 0)
                {
                    string errDetail = "";
                    if (expiredAccounts.Count > 0)
                        errDetail += LocalizationService.Format("Launch_Summary_ExpiredDetail", expiredAccounts.Count, string.Join(", ", expiredAccounts));
                    if (otherFailedAccounts.Count > 0)
                        errDetail += LocalizationService.Format("Launch_Summary_OtherDetail", otherFailedAccounts.Count, string.Join(", ", otherFailedAccounts));

                    AppServices.Logger.Warn($"[LaunchQueue] Launch queue completed: {successCount}/{requestedCount} succeeded. {errDetail}");
                    SetStatus(LocalizationService.Format("Launch_Summary_CompletedWithErrors", successCount, requestedCount, errDetail));

                    AppDispatcher.Post(() =>
                    {
                        string toastMsg = LocalizationService.Format("Launch_Toast_SuccessCount", successCount, requestedCount) + "\n" +
                                          (expiredAccounts.Count > 0 ? LocalizationService.Format("Launch_Toast_ExpiredList", string.Join(", ", expiredAccounts)) + "\n" : "") +
                                          (otherFailedAccounts.Count > 0 ? LocalizationService.Format("Launch_Toast_ErrorList", string.Join(", ", otherFailedAccounts)) + "\n" : "") +
                                          LocalizationService.Get("Launch_Toast_CheckExpiredFooter");
                        ToastService.ShowWarning(LocalizationService.Get("Launch_Toast_SessionError_Title"), toastMsg, 7.0);
                    });
                }
                else
                {
                    AppServices.Logger.Info($"[LaunchQueue] Completed: All {successCount}/{requestedCount} accounts launched successfully.");
                    SetStatus(LocalizationService.Format("Launch_Summary_Success", successCount, requestedCount));
                    AppDispatcher.Post(() =>
                    {
                        ToastService.ShowSuccess(LocalizationService.Get("Launch_Toast_Success_Title"), LocalizationService.Format("Launch_Toast_Success_Message", successCount, requestedCount));
                    });
                }
            }
            catch (InvalidOperationException exception)
            {
                SetStatus(exception.Message);
            }
        }

        public static async Task LaunchHomeAccountsAsync(
            ILaunchCoordinator launchCoordinator,
            IReadOnlyList<Account>? accounts,
            bool preferChromeHome,
            int homeLaunchStaggerMilliseconds,
            Action? ensureMultiRoblox,
            Action<string>? statusCallback,
            CancellationToken cancellationToken)
        {
            List<Account> launchList = (accounts ?? Array.Empty<Account>())
                .Where(account => account != null)
                .Distinct()
                .ToList();
            if (launchList.Count == 0)
                return;

            ensureMultiRoblox?.Invoke();

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

            void SetStatus(string message)
            {
                AppServices.HostUiBridge.NotifyWindowStatus(message);
                statusCallback?.Invoke(message);
            }

            try
            {
                LaunchBatchResult batch = await launchCoordinator.EnqueueAsync(
                    requests,
                    Math.Max(1, homeLaunchStaggerMilliseconds / 1000),
                    asyncJoin: false,
                    progress: progress =>
                    {
                        if (!string.IsNullOrWhiteSpace(progress?.Message))
                            SetStatus(progress.Message);
                    },
                    cancellationToken).ConfigureAwait(true);

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
                    SetStatus(LocalizationService.Get("Launch_QueueCancelled"));
                }
                else if (expiredAccounts.Count > 0 || otherFailedAccounts.Count > 0)
                {
                    string errDetail = "";
                    if (expiredAccounts.Count > 0)
                        errDetail += LocalizationService.Format("Launch_Summary_ExpiredDetail", expiredAccounts.Count, string.Join(", ", expiredAccounts));
                    if (otherFailedAccounts.Count > 0)
                        errDetail += LocalizationService.Format("Launch_Summary_OtherDetail", otherFailedAccounts.Count, string.Join(", ", otherFailedAccounts));

                    AppServices.Logger.Warn($"[LaunchHomeQueue] Completed Home queue: {successCount}/{launchList.Count} succeeded. {errDetail}");
                    SetStatus(LocalizationService.Format("Launch_Summary_CompletedWithErrors", successCount, launchList.Count, errDetail));

                    AppDispatcher.Post(() =>
                    {
                        string toastMsg = LocalizationService.Format("Launch_Home_Toast_SuccessCount", successCount, launchList.Count) + "\n" +
                                          (expiredAccounts.Count > 0 ? LocalizationService.Format("Launch_Toast_ExpiredList", string.Join(", ", expiredAccounts)) + "\n" : "") +
                                          (otherFailedAccounts.Count > 0 ? LocalizationService.Format("Launch_Toast_ErrorList", string.Join(", ", otherFailedAccounts)) + "\n" : "") +
                                          LocalizationService.Get("Launch_Toast_CheckExpiredFooter");
                        ToastService.ShowWarning(LocalizationService.Get("Launch_Toast_SessionError_Title"), toastMsg, 7.0);
                    });
                }
                else
                {
                    AppServices.Logger.Info($"[LaunchHomeQueue] Completed: All {successCount}/{launchList.Count} accounts launched Home successfully.");
                    SetStatus(LocalizationService.Format("Launch_Home_Summary_Success", launchList.Count));
                    AppDispatcher.Post(() =>
                    {
                        ToastService.ShowSuccess(LocalizationService.Get("Launch_Home_Toast_Success_Title"), LocalizationService.Format("Launch_Home_Toast_Success_Message", successCount, launchList.Count));
                    });
                }
            }
            catch (InvalidOperationException exception)
            {
                SetStatus(exception.Message);
            }
            catch (OperationCanceledException)
            {
                SetStatus(LocalizationService.Get("Launch_QueueCancelled"));
            }
        }
    }
}
