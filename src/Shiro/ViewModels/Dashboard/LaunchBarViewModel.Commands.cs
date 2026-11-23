#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public sealed partial class LaunchBarViewModel
    {
        public ICommand LaunchAccountCommand { get; private set; } = null!;
        public ICommand OpenHomeCommand { get; private set; } = null!;
        public ICommand JoinServerAccountCommand { get; private set; } = null!;
        public ICommand LaunchSelectedCommand { get; private set; } = null!;
        public ICommand JoinServerSelectedCommand { get; private set; } = null!;
        public ICommand PrimaryCardActionCommand { get; private set; } = null!;
        public ICommand ViewBanAccountCommand { get; private set; } = null!;
        public ICommand CloseAccountRobloxCommand { get; private set; } = null!;
        public ICommand SelectRecentGameCommand { get; private set; } = null!;
        public ICommand CancelBatchLaunchCommand { get; private set; } = null!;
        public ICommand OpenCurrentGameServersCommand { get; private set; } = null!;

        internal void UpdateBatchLaunchBanner(LaunchProgress progress)
        {
            if (progress == null) return;

            if (progress.Account == null && progress.Completed)
            {
                IsBatchLaunching = false;
                BatchLaunchProgressPercent = 0;
                return;
            }

            if (progress.Total > 1 || (!progress.Completed && progress.Status == LaunchOutcome.RateLimited))
            {
                int total = Math.Max(1, progress.Total);
                int index = Math.Clamp(progress.Index, 1, total);
                double units = Math.Max(0.15, index - (progress.Completed ? 0.0 : 0.45));

                IsBatchLaunching = true;
                BatchLaunchProgressPercent = Math.Clamp((units / total) * 100.0, 5.0, 100.0);
                string accName = StreamerModeService.GetDisplayUsername(progress.Account?.Username);
                BatchLaunchTitleText = LocalizationService.Format("Main.LaunchQueueProgress", index, total, accName);
                BatchLaunchDetailText = !string.IsNullOrWhiteSpace(progress.Message)
                    ? progress.Message!
                    : LocalizationService.Get("Main.PreparingClientLaunch");
            }
        }

        private void OnLaunchProgressChanged(LaunchProgress progress)
        {
            if (progress == null) return;
            AppDispatcher.Post(() =>
            {
                try
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    UpdateBatchLaunchBanner(progress);
                    if (!string.IsNullOrWhiteSpace(progress.Message))
                        _setStatus(progress.Message);

                    if (progress.Account != null)
                    {
                        var vm = _tryGetAccountVm?.Invoke(progress.Account);
                        if (vm != null)
                        {
                            vm.IsLaunching = !progress.Completed &&
                                progress.Status != LaunchOutcome.Cancelled &&
                                progress.Status != LaunchOutcome.Failed &&
                                progress.Status != LaunchOutcome.TicketError;
                        }
                    }
                    else if (progress.Completed)
                    {
                        _clearLaunchingFlags();
                    }
                }
                catch (Exception ex) { _logger.Debug($"Clearing launching state on queue empty threw: {ex.Message}"); }
            });
        }

        private void ExecuteLaunchSingleHome(AccountViewModel acc)
        {
            if (acc?.Model != null)
                _ = Task.Run(async () => await AccountLauncherService.LaunchAppHomeAsync(acc.Model).ConfigureAwait(false));
        }

        private void ExecuteViewBanForAccount(AccountViewModel? avm)
        {
            if (avm?.Model == null) return;
            string notice = !string.IsNullOrWhiteSpace(avm.BanNoticeText) ? avm.BanNoticeText : LocalizationService.Get("Main.AccountModeratedOrLocked");
            _setStatus(LocalizationService.Format("Main.OpeningModerationBrowser", avm.Username, notice));
            ToastService.ShowWarning(
                LocalizationService.Format("Main.ViewBanTitle", avm.Username),
                LocalizationService.Format("Main.OpeningBanDetailsPrompt", notice),
                4.0);

            if (OnFastReLoginRequested != null) OnFastReLoginRequested.Invoke(avm);
            else AppServices.Dialogs.OpenFastLoginDialog(avm.Model);
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
                    if (closed) ToastService.ShowSuccess(LocalizationService.Format("Main.ClosedRobloxForAccount", username));
                    else ToastService.ShowInfo(LocalizationService.Format("Main.NoRobloxRunningForAccount", username));
                    _refreshLiveProcessStatus();
                });
            });
        }

        private void ExecuteJoinServerForAccounts(IList<AccountViewModel> list)
        {
            if (list == null || list.Count == 0) return;

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
                _setStatus(LocalizationService.Get("Main.EnterPlaceIdOrLink"));
            }
        }

        private void InitializeCommands()
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
                    if (avm.IsBannedOrLocked) { ExecuteViewBanForAccount(avm); return; }
                    if (avm.IsExpired) { AppServices.Dialogs.OpenFastLoginDialog(avm.Model); return; }
                    if (OnLaunchRequested != null) OnLaunchRequested.Invoke(avm);
                    else ExecuteLaunchSingleHome(avm);
                }
            });
            ViewBanAccountCommand = new RelayCommand(param => { if (param is AccountViewModel avm) ExecuteViewBanForAccount(avm); });
            CloseAccountRobloxCommand = new RelayCommand(param => { if (param is AccountViewModel avm) ExecuteCloseRobloxForAccount(avm); });
            JoinServerAccountCommand = new RelayCommand(param => { if (param is AccountViewModel acc) ExecuteJoinServerForAccounts(new[] { acc }); });

            LaunchSelectedCommand = new RelayCommand(() =>
            {
                var selected = _getAccounts().Where(a => a.IsSelected).ToList();
                if (selected.Count == 0)
                {
                    string warnTitle = LocalizationService.Get("Main.NoAccountSelectedTitle");
                    string warnMsg = LocalizationService.Get("Main.SelectAccountBeforeOpenHome");
                    _setStatus(warnMsg);
                    ToastService.ShowWarning(warnTitle, warnMsg);
                    return;
                }
                if (OnMultiLaunchRequested != null) { OnMultiLaunchRequested.Invoke(selected); return; }

                AppServices.SelectionState.SelectedAccounts = selected.Where(a => a.Model != null).Select(a => a.Model).ToList();
                AccountLauncherService.MultiLaunchHome();
            });

            JoinServerSelectedCommand = new RelayCommand(() =>
            {
                var selected = _getAccounts().Where(a => a.IsSelected).ToList();
                if (selected.Count == 0)
                {
                    string warnTitle = LocalizationService.Get("Main.NoAccountSelectedTitle");
                    string warnMsg = LocalizationService.Get("Main.SelectAccountBeforePlayServer");
                    _setStatus(warnMsg);
                    ToastService.ShowWarning(warnTitle, warnMsg);
                    return;
                }

                long placeId = AccountLauncherService.ParsePlaceId(PlaceId?.Trim() ?? string.Empty);
                if (placeId <= 0)
                {
                    string pidWarn = LocalizationService.Get("Main.EnterPlaceIdOrLink");
                    _setStatus(pidWarn);
                    ToastService.ShowWarning(LocalizationService.Get("Main.MissingPlaceIdTitle"), pidWarn);
                    return;
                }

                if (OnJoinServerSelectedRequested != null) OnJoinServerSelectedRequested.Invoke(selected);
                else ExecuteJoinServerForAccounts(selected);
            });

            CancelBatchLaunchCommand = new RelayCommand(() =>
            {
                LaunchCoordinator.RequestGlobalCancel();
                IsBatchLaunching = false;
                BatchLaunchProgressPercent = 0;
                _clearLaunchingFlags();
                string stopMsg = LocalizationService.Get("Main.LaunchQueueStopped");
                _setStatus(stopMsg);
                ToastService.ShowWarning(LocalizationService.Get("Main.LaunchQueueStoppedTitle"), stopMsg);
            });

            SelectRecentGameCommand = new RelayCommand(p =>
            {
                if (p is RecentGameItemViewModel item)
                {
                    SetKnownGameTitle(item.PlaceId, item.Name);
                    PlaceId = item.PlaceId.ToString();
                    AppServices.ServerBrowserState.CurrentPlaceId = PlaceId;
                    _settings?.Set("SavedPlaceId", PlaceId);
                    AppServices.HostUiBridge.QueueDeferredSaveSettings();
                    _navigateToPage(NavigationPage.Accounts);
                    _setStatus(LocalizationService.Format("Main.GameSelected", item.Name, item.PlaceId));
                }
            });

            OpenCurrentGameServersCommand = new RelayCommand(() =>
            {
                string raw = PlaceId?.Trim() ?? string.Empty;
                long parsed = AccountLauncherService.ParsePlaceId(raw);
                string effective = parsed > 0 ? parsed.ToString() : raw;
                if (string.IsNullOrWhiteSpace(effective))
                {
                    _setStatus(LocalizationService.Get("Main.EnterPlaceIdOrLink"));
                    return;
                }

                if (parsed > 0 && !string.Equals(PlaceId, effective, StringComparison.Ordinal))
                    PlaceId = effective;

                AppServices.ServerBrowserState.CurrentPlaceId = effective;
                _syncPlaceIdToServerList?.Invoke(effective);
                _navigateToPage(NavigationPage.ServerList);
            });
        }
    }
}
