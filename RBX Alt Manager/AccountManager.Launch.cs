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
                    SetDashboardStatus("ℹ️ Đã hủy hàng đợi khởi chạy.");
                }
                else if (expiredAccounts.Count > 0 || otherFailedAccounts.Count > 0)
                {
                    string errDetail = "";
                    if (expiredAccounts.Count > 0)
                        errDetail += $"⚠️ {expiredAccounts.Count} acc hết phiên: [{string.Join(", ", expiredAccounts)}]. ";
                    if (otherFailedAccounts.Count > 0)
                        errDetail += $"❌ {otherFailedAccounts.Count} acc lỗi: [{string.Join(", ", otherFailedAccounts)}].";

                    Program.Logger.Warn($"[LaunchQueue] Hoàn tất hàng đợi: {successCount}/{requestedCount} thành công. {errDetail}");
                    SetDashboardStatus($"⚠️ Khởi chạy hoàn tất: {successCount}/{requestedCount} thành công. {errDetail}Kiểm tra tab '🔴 Hết hạn'!");

                    AppDispatcher.Post(() =>
                    {
                        string toastMsg = $"{successCount}/{requestedCount} tài khoản vào server thành công.\n" +
                                          (expiredAccounts.Count > 0 ? $"• Hết hạn phiên: {string.Join(", ", expiredAccounts)}\n" : "") +
                                          (otherFailedAccounts.Count > 0 ? $"• Lỗi khởi chạy: {string.Join(", ", otherFailedAccounts)}\n" : "") +
                                          "Vui lòng kiểm tra tab '🔴 Hết hạn' để Re-login!";
                        ToastService.ShowWarning("Phát hiện tài khoản lỗi phiên", toastMsg, 7.0);
                    });
                }
                else
                {
                    Program.Logger.Info($"[LaunchQueue] Hoàn tất: Toàn bộ {successCount}/{requestedCount} tài khoản khởi chạy thành công.");
                    SetDashboardStatus($"🚀 Đã hoàn tất khởi chạy thành công toàn bộ {successCount}/{requestedCount} tài khoản.");
                    AppDispatcher.Post(() =>
                    {
                        ToastService.ShowSuccess("Khởi chạy thành công", $"Đã mở toàn bộ {successCount}/{requestedCount} tài khoản vào game!");
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
                    SetDashboardStatus("ℹ️ Đã hủy hàng đợi khởi chạy.");
                }
                else if (expiredAccounts.Count > 0 || otherFailedAccounts.Count > 0)
                {
                    string errDetail = "";
                    if (expiredAccounts.Count > 0)
                        errDetail += $"⚠️ {expiredAccounts.Count} acc hết phiên: [{string.Join(", ", expiredAccounts)}]. ";
                    if (otherFailedAccounts.Count > 0)
                        errDetail += $"❌ {otherFailedAccounts.Count} acc lỗi: [{string.Join(", ", otherFailedAccounts)}].";

                    Program.Logger.Warn($"[LaunchHomeQueue] Hoàn tất hàng đợi Home: {successCount}/{launchList.Count} thành công. {errDetail}");
                    SetDashboardStatus($"⚠️ Hoàn tất: {successCount}/{launchList.Count} thành công. {errDetail}Kiểm tra tab '🔴 Hết hạn'!");

                    AppDispatcher.Post(() =>
                    {
                        string toastMsg = $"{successCount}/{launchList.Count} tài khoản mở Home thành công.\n" +
                                          (expiredAccounts.Count > 0 ? $"• Hết hạn phiên: {string.Join(", ", expiredAccounts)}\n" : "") +
                                          "Vui lòng kiểm tra tab '🔴 Hết hạn' để Re-login!";
                        ToastService.ShowWarning("Phát hiện tài khoản lỗi phiên", toastMsg, 7.0);
                    });
                }
                else
                {
                    Program.Logger.Info($"[LaunchHomeQueue] Hoàn tất: Toàn bộ {successCount}/{launchList.Count} tài khoản mở Home thành công.");
                    SetDashboardStatus($"🏠 Đã mở Roblox Home cho toàn bộ {launchList.Count} tài khoản.");
                    AppDispatcher.Post(() =>
                    {
                        ToastService.ShowSuccess("Mở Roblox Home thành công", $"Đã mở Roblox Home cho toàn bộ {successCount}/{launchList.Count} tài khoản!");
                    });
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
