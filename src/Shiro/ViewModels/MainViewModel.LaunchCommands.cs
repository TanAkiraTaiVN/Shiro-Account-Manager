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
                AppSettings.CurrentPlaceId = normalized;
                AppSettings.General?.Set("SavedPlaceId", normalized);
                AppSettings.QueueDeferredSave();
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
            string notice = !string.IsNullOrWhiteSpace(avm.BanNoticeText) ? avm.BanNoticeText : Shiro.Services.LocalizationService.Get("Auto_MainViewModelLau_487");
            StatusMessage = Shiro.Services.LocalizationService.Format("Auto_MainViewModelLau_488", avm.Username, notice);
            ToastService.ShowWarning(
                Shiro.Services.LocalizationService.Format("Auto_MainViewModelLau_489", avm.Username),
                Shiro.Services.LocalizationService.Format("Auto_MainViewModelLau_490", notice),
                4.0);

            if (OnFastReLoginRequested != null)
                OnFastReLoginRequested.Invoke(avm);
            else if (OnReLoginRequested != null)
                OnReLoginRequested.Invoke(avm);
            else
                AppServices.Dialogs.OpenFastLoginDialog(avm.Model);
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
                        ToastService.ShowSuccess(Shiro.Services.LocalizationService.Format("Auto_MainViewModelLau_491", username));
                    else
                        ToastService.ShowInfo(Shiro.Services.LocalizationService.Format("Auto_MainViewModelLau_492", username));
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
                StatusMessage = Shiro.Services.LocalizationService.Get("Auto_MainViewModelLau_493");
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
                    string warnTitle = LocalizationService.IsEnglish ? "No Account Selected" : Shiro.Services.LocalizationService.Get("Auto_MainViewModelLau_494");
                    string warnMsg = LocalizationService.IsEnglish
                        ? "Please select at least one account before opening Roblox Home!"
                        : Shiro.Services.LocalizationService.Get("Auto_MainViewModelLau_495");
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
                AppSettings.SelectedAccounts = models;
                AccountLauncherService.MultiLaunchHome();
            });

            JoinServerSelectedCommand = new RelayCommand(() =>
            {
                var selected = _accounts.Where(a => a.IsSelected).ToList();
                if (selected.Count == 0)
                {
                    string warnTitle = LocalizationService.IsEnglish ? "No Account Selected" : Shiro.Services.LocalizationService.Get("Auto_MainViewModelLau_494");
                    string warnMsg = LocalizationService.IsEnglish
                        ? "Please select at least one account in the Accounts tab before playing!"
                        : Shiro.Services.LocalizationService.Get("Auto_MainViewModelLau_496");
                    StatusMessage = warnMsg;
                    ToastService.ShowWarning(warnTitle, warnMsg);
                    return;
                }

                long placeId = AccountLauncherService.ParsePlaceId(PlaceId?.Trim() ?? string.Empty);
                if (placeId <= 0)
                {
                    string pidWarn = LocalizationService.IsEnglish
                        ? "Please enter a valid Place ID or game link before joining!"
                        : Shiro.Services.LocalizationService.Get("Auto_MainViewModelLau_493");
                    StatusMessage = pidWarn;
                    ToastService.ShowWarning(LocalizationService.IsEnglish ? "Invalid Place ID" : Shiro.Services.LocalizationService.Get("Auto_MainViewModelLau_497"), pidWarn);
                    return;
                }

                if (OnJoinServerSelectedRequested != null) OnJoinServerSelectedRequested.Invoke(selected);
                else ExecuteJoinServerForAccounts(selected);
            });
        }
    }
}
