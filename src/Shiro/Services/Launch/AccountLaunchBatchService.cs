#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    internal static partial class AccountLaunchBatchService
    {
        public static List<LaunchRequest> BuildLaunchRequests(
            IReadOnlyList<Account> accounts,
            long placeId,
            string? jobId,
            bool followUser,
            bool vipServer)
        {
            List<LaunchRequest> requests = new List<LaunchRequest>(accounts.Count);
            foreach (Account account in accounts)
            {
                if (account == null)
                    continue;

                long targetPlaceId = placeId;
                string targetJobId = jobId ?? string.Empty;

                if (!followUser)
                {
                    bool hasSavedPlace = long.TryParse(account.GetField("SavedPlaceId"), out long savedPlaceId) && savedPlaceId > 0;
                    if (targetPlaceId <= 0 && hasSavedPlace)
                        targetPlaceId = savedPlaceId;

                    if (string.IsNullOrEmpty(targetJobId) &&
                        !string.IsNullOrEmpty(account.GetField("SavedJobId")) &&
                        (placeId <= 0 || (hasSavedPlace && targetPlaceId == savedPlaceId)))
                    {
                        targetJobId = account.GetField("SavedJobId");
                    }
                }

                requests.Add(new LaunchRequest(account, targetPlaceId, targetJobId, followUser, vipServer));
            }

            return requests;
        }

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

            List<LaunchRequest> requests = BuildLaunchRequests(launchList, placeId, jobId, followUser, vipServer);

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

                ReportGameBatchSummary(batch, requests.Count, SetStatus);
            }
            catch (OperationCanceledException)
            {
                SetStatus(LocalizationService.Get("Launch_QueueCancelled"));
            }
            catch (InvalidOperationException exception)
            {
                SetStatus(exception.Message);
            }
        }

        private static void ReportGameBatchSummary(
            LaunchBatchResult batch,
            int initialRequestedCount,
            Action<string> setStatus)
        {
            if (batch.Cancelled)
            {
                setStatus(LocalizationService.Get("Launch_QueueCancelled"));
                return;
            }

            int totalCount = batch.Results.Count > 0 ? batch.Results.Count : initialRequestedCount;
            int successCount = batch.Results.Count(r => r.Status == LaunchOutcome.Success);
            int retriedSucceededCount = batch.Results.Count(r => r.Status == LaunchOutcome.Success && r.CaptchaRetried);

            var captchaAccounts = batch.Results
                .Where(r => r.Status != LaunchOutcome.Success && (r.CaptchaFailedAfterRetry || r.CaptchaDetected))
                .Select(r => r.Account?.Username ?? "Unknown")
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var captchaSet = new HashSet<string>(captchaAccounts, StringComparer.Ordinal);

            var expiredResults = batch.Results
                .Where(r => !captchaSet.Contains(r.Account?.Username ?? string.Empty) &&
                            (r.Status == LaunchOutcome.TicketError || (r.Account != null && !r.Account.Valid)))
                .ToList();

            var expiredAccounts = expiredResults
                .Select(r => r.Account?.Username ?? "Unknown")
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var reloginModels = expiredResults
                .Where(r => r.Account != null && !r.Account.IsBannedOrLocked)
                .Select(r => r.Account!)
                .Distinct()
                .ToList();

            var expiredSet = new HashSet<string>(expiredAccounts, StringComparer.Ordinal);

            var otherFailedAccounts = batch.Results
                .Where(r => r.Status is LaunchOutcome.Failed or LaunchOutcome.Timeout or LaunchOutcome.RateLimited or LaunchOutcome.Cancelled &&
                            !captchaSet.Contains(r.Account?.Username ?? string.Empty) &&
                            !expiredSet.Contains(r.Account?.Username ?? string.Empty))
                .Select(r => r.Account?.Username ?? "Unknown")
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (captchaAccounts.Count == 0 && expiredAccounts.Count == 0 && otherFailedAccounts.Count == 0)
            {
                string retriedSuffix = retriedSucceededCount > 0
                    ? LocalizationService.Format("Launch_Summary_RetriedOkSuffix", retriedSucceededCount)
                    : string.Empty;
                AppServices.Logger.Info($"[LaunchQueue] Completed: {successCount}/{totalCount} accounts launched successfully{retriedSuffix}.");
                setStatus(retriedSucceededCount > 0
                    ? LocalizationService.Format("Launch_Summary_Done", successCount, totalCount, retriedSuffix, string.Empty)
                    : LocalizationService.Format("Launch_Summary_Success", successCount, totalCount));

                AppDispatcher.Post(() =>
                {
                    ToastService.ShowSuccess(
                        LocalizationService.Get("Launch_Toast_Success_Title"),
                        LocalizationService.Format("Launch_Toast_Success_Message", successCount, totalCount) + retriedSuffix);
                });
                return;
            }

            string detailSuffix = BuildErrorDetailSuffix(captchaAccounts, expiredAccounts, otherFailedAccounts);
            string summaryRetriedSuffix = retriedSucceededCount > 0
                ? LocalizationService.Format("Launch_Summary_RetriedOkSuffix", retriedSucceededCount)
                : string.Empty;

            AppServices.Logger.Warn($"[LaunchQueue] Launch queue completed: {successCount}/{totalCount} succeeded{summaryRetriedSuffix}.{detailSuffix}");
            setStatus(LocalizationService.Format("Launch_Summary_Done", successCount, totalCount, summaryRetriedSuffix, detailSuffix));

            string toastMsg = BuildSummaryToastMessage(
                successCount,
                totalCount,
                summaryRetriedSuffix,
                captchaAccounts,
                expiredAccounts,
                otherFailedAccounts);

            string toastTitle = expiredAccounts.Count > 0
                ? LocalizationService.Get("Launch_Toast_SessionError_Title")
                : LocalizationService.Get("Launch_Toast_Summary_Title");

            AppDispatcher.Post(() =>
            {
                if (reloginModels.Count > 0)
                {
                    ToastService.ShowWarningWithAction(
                        toastTitle,
                        toastMsg,
                        LocalizationService.Get("Launch_Toast_ReloginExpiredBtn"),
                        () => OpenReloginDialogs(reloginModels),
                        durationSeconds: 12.0);
                }
                else
                {
                    ToastService.ShowWarning(toastTitle, toastMsg, 9.0);
                }
            });
        }

        private static string BuildErrorDetailSuffix(
            List<string> captchaAccounts,
            List<string> expiredAccounts,
            List<string> otherFailedAccounts)
        {
            var sb = new StringBuilder();
            if (captchaAccounts.Count > 0)
                sb.Append(LocalizationService.Format("Launch_Summary_CaptchaDetail", captchaAccounts.Count, string.Join(", ", captchaAccounts)));
            if (expiredAccounts.Count > 0)
                sb.Append(LocalizationService.Format("Launch_Summary_ExpiredDetail", expiredAccounts.Count, string.Join(", ", expiredAccounts)));
            if (otherFailedAccounts.Count > 0)
                sb.Append(LocalizationService.Format("Launch_Summary_OtherDetail", otherFailedAccounts.Count, string.Join(", ", otherFailedAccounts)));
            return sb.ToString();
        }

        private static string BuildSummaryToastMessage(
            int successCount,
            int totalCount,
            string retriedSuffix,
            List<string> captchaAccounts,
            List<string> expiredAccounts,
            List<string> otherFailedAccounts)
        {
            var lines = new List<string>
            {
                LocalizationService.Format("Launch_Toast_SuccessCount", successCount, totalCount) + retriedSuffix
            };
            if (captchaAccounts.Count > 0)
                lines.Add(LocalizationService.Format("Launch_Toast_CaptchaList", string.Join(", ", captchaAccounts)));
            if (expiredAccounts.Count > 0)
                lines.Add(LocalizationService.Format("Launch_Toast_ExpiredList", string.Join(", ", expiredAccounts)));
            if (otherFailedAccounts.Count > 0)
                lines.Add(LocalizationService.Format("Launch_Toast_ErrorList", string.Join(", ", otherFailedAccounts)));
            if (expiredAccounts.Count > 0)
                lines.Add(LocalizationService.Get("Launch_Toast_CheckExpiredFooter"));

            return string.Join("\n", lines);
        }

        private static void OpenReloginDialogs(List<Account> expiredModels)
        {
            if (expiredModels.Count == 1)
                AppServices.Dialogs.OpenFastLoginDialog(expiredModels[0]);
            else if (expiredModels.Count > 1)
                AppServices.Dialogs.OpenFastLoginDialogBatch(expiredModels);
        }

        public static async Task LaunchHomeAccountsAsync(
            ILaunchCoordinator launchCoordinator,
            IReadOnlyList<Account>? accounts,
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
                    openHome: true))
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

                var expiredResults = batch.Results
                    .Where(r => r.Status == LaunchOutcome.TicketError || (r.Account != null && !r.Account.Valid))
                    .ToList();

                var expiredAccounts = expiredResults
                    .Select(r => r.Account?.Username ?? "Unknown")
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var reloginModels = expiredResults
                    .Where(r => r.Account != null && !r.Account.IsBannedOrLocked)
                    .Select(r => r.Account!)
                    .Distinct()
                    .ToList();

                var otherFailedAccounts = batch.Results
                    .Where(r => r.Status is LaunchOutcome.Failed or LaunchOutcome.Timeout or LaunchOutcome.RateLimited or LaunchOutcome.Cancelled &&
                                !expiredAccounts.Contains(r.Account?.Username ?? ""))
                    .Select(r => r.Account?.Username ?? "Unknown")
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                int successCount = batch.Results.Count(r => r.Status == LaunchOutcome.Success);

                if (batch.Cancelled)
                {
                    SetStatus(LocalizationService.Get("Launch_QueueCancelled"));
                }
                else if (expiredAccounts.Count > 0 || otherFailedAccounts.Count > 0)
                {
                    string errDetail = BuildErrorDetailSuffix(new List<string>(), expiredAccounts, otherFailedAccounts);
                    AppServices.Logger.Warn($"[LaunchHomeQueue] Completed Home queue: {successCount}/{launchList.Count} succeeded.{errDetail}");
                    SetStatus(LocalizationService.Format("Launch_Summary_Done", successCount, launchList.Count, string.Empty, errDetail));

                    string toastMsg = LocalizationService.Format("Launch_Home_Toast_SuccessCount", successCount, launchList.Count) + "\n" +
                                      (expiredAccounts.Count > 0 ? LocalizationService.Format("Launch_Toast_ExpiredList", string.Join(", ", expiredAccounts)) + "\n" : "") +
                                      (otherFailedAccounts.Count > 0 ? LocalizationService.Format("Launch_Toast_ErrorList", string.Join(", ", otherFailedAccounts)) + "\n" : "") +
                                      (expiredAccounts.Count > 0 ? LocalizationService.Get("Launch_Toast_CheckExpiredFooter") : string.Empty);

                    AppDispatcher.Post(() =>
                    {
                        string title = expiredAccounts.Count > 0
                            ? LocalizationService.Get("Launch_Toast_SessionError_Title")
                            : LocalizationService.Get("Launch_Toast_Summary_Title");
                        if (reloginModels.Count > 0)
                        {
                            ToastService.ShowWarningWithAction(
                                title,
                                toastMsg.TrimEnd(),
                                LocalizationService.Get("Launch_Toast_ReloginExpiredBtn"),
                                () => OpenReloginDialogs(reloginModels),
                                durationSeconds: 12.0);
                        }
                        else
                        {
                            ToastService.ShowWarning(title, toastMsg.TrimEnd(), 9.0);
                        }
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

        public static async Task<LaunchResult> ExecuteSingleLaunchAsync(
            ILaunchCoordinator? launchCoordinator,
            Account account,
            long placeId,
            string jobId,
            bool followUser,
            bool joinVip,
            int accountJoinDelaySeconds,
            Action<string>? statusCallback,
            CancellationToken cancellationToken = default)
        {
            if (launchCoordinator != null && !launchCoordinator.IsRunning)
            {
                try
                {
                    var batch = await launchCoordinator.EnqueueAsync(
                        new[] { new LaunchRequest(account, placeId, jobId ?? string.Empty, followUser, joinVip) },
                        accountJoinDelaySeconds,
                        asyncJoin: false,
                        progress =>
                        {
                            if (!string.IsNullOrWhiteSpace(progress?.Message))
                                statusCallback?.Invoke(progress.Message);
                        },
                        cancellationToken).ConfigureAwait(false);

                    if (batch.Results.Count > 0)
                    {
                        var first = batch.Results[0];
                        return first.Status == LaunchOutcome.Success
                            ? LaunchResult.Success()
                            : LaunchResult.Fail(first.Status, first.Result ?? "Failed to launch Roblox");
                    }
                }
                catch (InvalidOperationException ex)
                {
                    AppServices.Logger.Debug($"[AccountLaunchBatchService] Coordinator busy, falling back to CookieGuard scope: {ex.Message}");
                }
            }

            using var cookieGuard = RobloxDirectLauncher.BeginCookieGuardScope(restoreDelayOnDisposeMs: 12_000);
            return await account.JoinServerResult(placeId, jobId ?? string.Empty, followUser, joinVip, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
