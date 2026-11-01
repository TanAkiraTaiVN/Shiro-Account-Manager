#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class MainViewModel
    {
        public Action<AccountViewModel>? OnLaunchRequested { get; set; }
        public Action<AccountViewModel>? OnOpenHomeRequested { get; set; }
        public Action<IList<AccountViewModel>>? OnMultiLaunchRequested { get; set; }
        public Action<IList<AccountViewModel>>? OnJoinServerSelectedRequested { get; set; }
        public Action<AccountViewModel>? OnJoinServerSingleRequested { get; set; }

        public ICommand LaunchAccountCommand { get; private set; } = null!;
        public ICommand OpenHomeCommand { get; private set; } = null!;
        public ICommand JoinServerAccountCommand { get; private set; } = null!;
        public ICommand LaunchSelectedCommand { get; private set; } = null!;
        public ICommand JoinServerSelectedCommand { get; private set; } = null!;
        public ICommand PrimaryCardActionCommand { get; private set; } = null!;
        public ICommand ViewBanAccountCommand { get; private set; } = null!;
        public ICommand CloseAccountRobloxCommand { get; private set; } = null!;

        public void NormalizePlaceIdInput(string? rawInput = null)
        {
            string candidate = rawInput ?? PlaceId;
            if (string.IsNullOrWhiteSpace(candidate))
                return;

            long parsed = AccountLauncherService.ParsePlaceId(candidate);
            if (parsed > 0)
            {
                string normalized = parsed.ToString();
                PlaceId = normalized;
                AccountManager.CurrentPlaceId = normalized;
                AccountManager.General?.Set("SavedPlaceId", normalized);
                AccountManager.QueueDeferredSaveSettings();
            }
        }

        private void ExecuteLaunchSingleHome(AccountViewModel acc)
        {
            if (acc?.Model != null)
            {
                _ = Task.Run(async () => await AccountLauncherService.LaunchAppHomeAsync(acc.Model).ConfigureAwait(false));
            }
        }

        private void ExecuteViewBanForAccount(AccountViewModel? avm)
        {
            if (avm?.Model == null) return;
            var model = avm.Model;
            string notice = !string.IsNullOrWhiteSpace(avm.BanNoticeText) ? avm.BanNoticeText : "Tài khoản bị kiểm duyệt hoặc khóa";
            ToastService.ShowWarning(
                $"Mở Roblox: {avm.Username}",
                $"{notice}\nĐang mở trực tiếp ứng dụng Roblox để bạn xem thông báo án phạt và kích hoạt lại tài khoản...",
                4.0);

            _ = Task.Run(async () =>
            {
                try
                {
                    await AccountLauncherService.LaunchAppHomeAsync(model).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Error($"[ViewBanAccountCommand] Failed to launch Roblox Home: {ex.Message}");
                    Utilities.OpenUrl("https://www.roblox.com/home");
                }
            });
        }

        private void ExecuteCloseRobloxForAccount(AccountViewModel? avm)
        {
            if (avm?.Model == null) return;
            var model = avm.Model;
            string username = avm.Username;
            _ = Task.Run(() =>
            {
                bool closed = RobloxProcessService.CloseAccountRobloxProcess(model);
                AppDispatcher.Post(() =>
                {
                    if (closed)
                        ToastService.ShowSuccess($"Đã đóng Roblox cho {username}.");
                    else
                        ToastService.ShowInfo($"Tài khoản {username} không có tiến trình Roblox nào đang chạy.");
                    RefreshLiveProcessStatus();
                });
            });
        }

        private void ExecuteJoinServerForAccounts(IList<AccountViewModel> list)
        {
            if (list == null || list.Count == 0)
                return;

            string placeIdStr = PlaceId?.Trim() ?? string.Empty;
            long placeId = AccountLauncherService.ParsePlaceId(placeIdStr);
            if (placeId > 0)
            {
                NormalizePlaceIdInput(placeIdStr);
                var models = list.Where(a => a?.Model != null).Select(a => a.Model).ToList();
                if (models.Count > 0)
                {
                    string jobId = JobId?.Trim() ?? string.Empty;
                    _ = Task.Run(async () => await AccountLauncherService.JoinServerAsync(models, placeId, jobId).ConfigureAwait(false));
                }
            }
            else
            {
                StatusMessage = "Vui lòng nhập Place ID hoặc link game hợp lệ trước khi vào Server!";
            }
        }

        private void InitializeLaunchCommands()
        {
            LaunchAccountCommand = new RelayCommand(param =>
            {
                if (param is AccountViewModel acc)
                {
                    if (OnLaunchRequested != null) OnLaunchRequested.Invoke(acc);
                    else ExecuteLaunchSingleHome(acc);
                }
            });

            OpenHomeCommand = new RelayCommand(param =>
            {
                if (param is AccountViewModel acc)
                {
                    if (OnOpenHomeRequested != null) OnOpenHomeRequested.Invoke(acc);
                    else if (OnLaunchRequested != null) OnLaunchRequested.Invoke(acc);
                    else ExecuteLaunchSingleHome(acc);
                }
            });

            PrimaryCardActionCommand = new RelayCommand(param =>
            {
                if (param is AccountViewModel avm && avm.Model != null)
                {
                    if (avm.IsBannedOrLocked)
                    {
                        ExecuteViewBanForAccount(avm);
                        return;
                    }
                    if (avm.IsExpired)
                    {
                        AppServices.Dialogs.OpenFastLoginDialog(avm.Model);
                        return;
                    }
                    if (OnLaunchRequested != null) OnLaunchRequested.Invoke(avm);
                    else ExecuteLaunchSingleHome(avm);
                }
            });

            ViewBanAccountCommand = new RelayCommand(param =>
            {
                if (param is AccountViewModel avm)
                    ExecuteViewBanForAccount(avm);
            });

            CloseAccountRobloxCommand = new RelayCommand(param =>
            {
                if (param is AccountViewModel avm)
                    ExecuteCloseRobloxForAccount(avm);
            });

            JoinServerAccountCommand = new RelayCommand(param =>
            {
                if (param is AccountViewModel acc)
                {
                    if (OnJoinServerSingleRequested != null) OnJoinServerSingleRequested.Invoke(acc);
                    else ExecuteJoinServerForAccounts(new[] { acc });
                }
            });

            LaunchSelectedCommand = new RelayCommand(() =>
            {
                var selected = _accounts.Where(a => a.IsSelected).ToList();
                if (selected.Count == 0)
                {
                    string warnTitle = LocalizationService.IsEnglish ? "No Account Selected" : "Chưa chọn tài khoản";
                    string warnMsg = LocalizationService.IsEnglish
                        ? "Please select at least one account before opening Roblox Home!"
                        : "Vui lòng tick chọn ít nhất một tài khoản trước khi mở Roblox Home!";
                    StatusMessage = warnMsg;
                    ToastService.ShowWarning(warnTitle, warnMsg);
                    return;
                }

                if (OnMultiLaunchRequested != null)
                {
                    OnMultiLaunchRequested.Invoke(selected);
                    return;
                }

                var models = selected.Where(a => a.Model != null).Select(a => a.Model).ToList();
                AccountManager.SelectedAccounts = models;
                AccountLauncherService.MultiLaunchHome();
            });

            JoinServerSelectedCommand = new RelayCommand(() =>
            {
                var selected = _accounts.Where(a => a.IsSelected).ToList();
                if (selected.Count == 0)
                {
                    string warnTitle = LocalizationService.IsEnglish ? "No Account Selected" : "Chưa chọn tài khoản";
                    string warnMsg = LocalizationService.IsEnglish
                        ? "Please select at least one account in the Accounts tab before playing!"
                        : "Chưa chọn tài khoản nào! Vui lòng tick chọn tài khoản ở tab 'Tài khoản' trước khi bấm Chơi / Vào Server.";
                    StatusMessage = warnMsg;
                    ToastService.ShowWarning(warnTitle, warnMsg);
                    return;
                }

                long placeId = AccountLauncherService.ParsePlaceId(PlaceId?.Trim() ?? string.Empty);
                if (placeId <= 0)
                {
                    string pidWarn = LocalizationService.IsEnglish
                        ? "Please enter a valid Place ID or game link before joining!"
                        : "Vui lòng nhập Place ID hoặc link game hợp lệ trước khi vào Server!";
                    StatusMessage = pidWarn;
                    ToastService.ShowWarning(LocalizationService.IsEnglish ? "Invalid Place ID" : "Thiếu Place ID", pidWarn);
                    return;
                }

                if (OnJoinServerSelectedRequested != null) OnJoinServerSelectedRequested.Invoke(selected);
                else ExecuteJoinServerForAccounts(selected);
            });
        }
    }
}
