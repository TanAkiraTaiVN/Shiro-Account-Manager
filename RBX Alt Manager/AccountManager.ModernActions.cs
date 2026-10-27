using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager
{
    public partial class AccountManager
    {
        private static void HideModernNavigationWindows()
        {
        }

        internal void SetDashboardStatus(string message)
        {
            RBX_Alt_Manager.AvaloniaUI.Views.MainWindow.Instance?.SetStatus(message);
        }

        internal async Task ModernLaunchAccountAppHome(Account account)
        {
            if (account == null) return;
            await LaunchHomeAccountsAsync(new[] { account }, preferChromeHome: false).ConfigureAwait(true);
        }

        internal async Task ModernOpenAccountHomeChrome(Account account)
        {
            if (account == null) return;
            await LaunchHomeAccountsAsync(new[] { account }, preferChromeHome: true).ConfigureAwait(true);
        }

        internal List<Account> GetCurrentSelectedAccounts()
        {
            if (RBX_Alt_Manager.AvaloniaUI.Views.MainWindow.Instance?.DashboardView?.ViewModel != null)
            {
                var list = RBX_Alt_Manager.AvaloniaUI.Views.MainWindow.Instance.DashboardView.ViewModel.GetSelectedAccounts();
                if (list != null && list.Count > 0)
                    return list;
            }
            return SelectedAccounts?.ToList() ?? new List<Account>();
        }

        internal void ModernOpenAccountControl()
        {
            AppServices.Dialogs.OpenNexusWindow();
        }

        internal void ModernMultiLaunch()
        {
            HideModernNavigationWindows();
            if (GetAccountsSnapshot().Count == 0)
            {
                SetDashboardStatus("Vui lòng thêm tài khoản trước khi khởi chạy.");
                return;
            }

            List<Account> multiSel = GetCurrentSelectedAccounts();

            if (multiSel == null || multiSel.Count == 0)
            {
                SetDashboardStatus("⚠️ Vui lòng tick chọn ít nhất một tài khoản (hoặc bấm 'Chọn hết') trước khi mở Home!");
                return;
            }

            if (multiSel.Count > 1 && General.Get<bool>("EnableMultiRbx") && !RobloxSingletonService.MutexHeld)
            {
                if (!TryEnableMultiRoblox(promptIfRobloxRunning: true))
                    return;
            }

            // Multi-Launch is explicitly configured to only open Roblox Home page
            _ = LaunchHomeAccountsAsync(multiSel, preferChromeHome: false);
            SetDashboardStatus($"🏠 Đang mở trang chủ Roblox Home cho {multiSel.Count} tài khoản đã chọn…");
        }

        internal async Task ModernJoinServerDirect(List<Account> accounts, long placeId, string jobId)
        {
            HideModernNavigationWindows();
            var snapshot = GetAccountsSnapshot();
            if (snapshot.Count == 0)
            {
                SetDashboardStatus("⚠️ Vui lòng thêm tài khoản trước khi vào server.");
                return;
            }

            List<Account> targetAccounts = accounts?.Where(a => a != null).ToList();
            if (targetAccounts == null || targetAccounts.Count == 0)
            {
                var multiSel = GetCurrentSelectedAccounts();
                if (multiSel != null && multiSel.Count > 0)
                    targetAccounts = multiSel.ToList();
                else if (SelectedAccount != null)
                    targetAccounts = new List<Account> { SelectedAccount };
                else if (LastValidAccount != null)
                    targetAccounts = new List<Account> { LastValidAccount };
                else
                    targetAccounts = snapshot.Take(1).ToList();
            }

            if (targetAccounts == null || targetAccounts.Count == 0)
            {
                SetDashboardStatus("⚠️ Vui lòng chọn ít nhất một tài khoản trước khi vào server!");
                return;
            }

            // Resolve Place ID if not passed
            if (placeId <= 0)
            {
                string pidSource = null;
                if (RBX_Alt_Manager.AvaloniaUI.Views.MainWindow.Instance?.DashboardView?.ViewModel != null &&
                    !string.IsNullOrWhiteSpace(RBX_Alt_Manager.AvaloniaUI.Views.MainWindow.Instance.DashboardView.ViewModel.PlaceId))
                {
                    pidSource = RBX_Alt_Manager.AvaloniaUI.Views.MainWindow.Instance.DashboardView.ViewModel.PlaceId;
                }
                else if (!string.IsNullOrWhiteSpace(CurrentPlaceId))
                {
                    pidSource = CurrentPlaceId;
                }
                else
                {
                    pidSource = targetAccounts.FirstOrDefault(a => !string.IsNullOrEmpty(a.GetField("SavedPlaceId")))?.GetField("SavedPlaceId");
                }

                if (!string.IsNullOrWhiteSpace(pidSource))
                {
                    placeId = AccountLauncherService.ParsePlaceId(pidSource);
                }
            }

            if (placeId <= 0)
            {
                SetDashboardStatus("⚠️ Vui lòng nhập Place ID ở thanh trên trước khi vào server!");
                return;
            }

            // Resolve Job ID
            string cleanJobId = jobId;
            if (string.IsNullOrWhiteSpace(cleanJobId))
            {
                if (RBX_Alt_Manager.AvaloniaUI.Views.MainWindow.Instance?.DashboardView?.ViewModel != null &&
                    !string.IsNullOrWhiteSpace(RBX_Alt_Manager.AvaloniaUI.Views.MainWindow.Instance.DashboardView.ViewModel.JobId))
                {
                    cleanJobId = RBX_Alt_Manager.AvaloniaUI.Views.MainWindow.Instance.DashboardView.ViewModel.JobId;
                }
                else if (!string.IsNullOrWhiteSpace(CurrentJobId))
                {
                    cleanJobId = CurrentJobId;
                }
                else
                {
                    cleanJobId = targetAccounts.FirstOrDefault(a => !string.IsNullOrEmpty(a.GetField("SavedJobId")))?.GetField("SavedJobId") ?? string.Empty;
                }
            }

            cleanJobId = cleanJobId?.Trim() ?? string.Empty;
            bool isVip = cleanJobId.StartsWith("VIP:", StringComparison.OrdinalIgnoreCase);
            if (isVip)
            {
                cleanJobId = cleanJobId.Substring(4).Trim();
            }

            CurrentPlaceId = placeId.ToString();
            CurrentJobId = isVip ? ("VIP:" + cleanJobId) : cleanJobId;

            if (targetAccounts.Count > 1 && General.Get<bool>("EnableMultiRbx") && !RobloxSingletonService.MutexHeld)
            {
                if (!TryEnableMultiRoblox(promptIfRobloxRunning: false))
                {
                    SetDashboardStatus("⚠️ Không thể kích hoạt Multi Roblox. Vui lòng đóng Roblox và thử lại.");
                    return;
                }
            }

            if (placeId > 0)
            {
                _ = ObserveBackgroundTaskAsync(AddRecentGame(new Game(placeId)), "recent game update");
            }

            CancelLaunching();
            if (!await WaitForLaunchQueueToDrainAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(true))
            {
                SetDashboardStatus("⚠️ Hàng đợi khởi chạy trước đó đang dừng. Vui lòng thử lại sau giây lát.");
                return;
            }

            SelectedAccounts = targetAccounts.ToList();
            SelectedAccount = SelectedAccounts.Count == 1 ? SelectedAccounts[0] : null;

            string serverDesc = string.IsNullOrEmpty(cleanJobId) ? "Public Server" : (isVip ? "VIP Server" : $"Server {cleanJobId}");
            SetDashboardStatus($"🎯 Đang vào {serverDesc} (Place ID {placeId}) cho {SelectedAccounts.Count} tài khoản…");

            await LaunchAccounts(SelectedAccounts, placeId, cleanJobId, false, isVip).ConfigureAwait(true);
        }

        internal void ModernCloseRoblox(bool notify = true)
        {
            int totalKilled = RobloxProcessService.KillAllRobloxProcesses();

            if (notify)
            {
                if (totalKilled > 0)
                {
                    SetDashboardStatus($"🧹 Đã tắt {totalKilled} tiến trình Roblox và làm sạch khay hệ thống.");
                }
                else
                {
                    SetDashboardStatus("ℹ️ Không tìm thấy tiến trình Roblox nào đang chạy.");
                }
            }

            if (RBX_Alt_Manager.AvaloniaUI.Views.MainWindow.Instance?.DashboardView?.ViewModel is var mainVm && mainVm != null)
            {
                if (notify)
                {
                    mainVm.StatusMessage = totalKilled > 0
                        ? $"🧹 Đã tắt {totalKilled} tiến trình Roblox và làm sạch khay hệ thống!"
                        : "ℹ️ Không tìm thấy tiến trình Roblox nào đang chạy.";
                }
                foreach (var acc in mainVm.Accounts)
                {
                    acc.IsInGame = false;
                }
                mainVm.NotifyCounts();
            }
            if (General.Get<bool>("EnableMultiRbx") && !RobloxSingletonService.MutexHeld)
            {
                if (UpdateMultiRoblox() && notify)
                    SetDashboardStatus("✅ Đã tắt Roblox và kích hoạt Multi Roblox.");
            }

            ModernRefreshPresence();
        }
    }
}
