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

        private void ExecuteLaunchSingleHome(AccountViewModel acc)
        {
            if (acc?.Model != null)
            {
                _ = Task.Run(async () => await AccountLauncherService.LaunchAppHomeAsync(acc.Model).ConfigureAwait(false));
            }
        }

        private void ExecuteJoinServerForAccounts(IList<AccountViewModel> list)
        {
            if (list == null || list.Count == 0)
                return;

            string placeIdStr = PlaceId?.Trim() ?? string.Empty;
            long placeId = AccountLauncherService.ParsePlaceId(placeIdStr);
            if (placeId > 0)
            {
                var models = list.Where(a => a?.Model != null).Select(a => a.Model).ToList();
                if (models.Count > 0)
                {
                    string jobId = JobId?.Trim() ?? string.Empty;
                    _ = Task.Run(async () => await AccountLauncherService.JoinServerAsync(models, placeId, jobId).ConfigureAwait(false));
                }
            }
            else
            {
                StatusMessage = "⚠️ Vui lòng nhập Place ID hợp lệ trước khi vào Server!";
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
                    StatusMessage = "⚠️ Vui lòng tick chọn ít nhất một tài khoản (hoặc bấm 'Chọn hết') trước khi mở Roblox Home (Launch Home)!";
                    return;
                }

                if (OnMultiLaunchRequested != null)
                {
                    OnMultiLaunchRequested.Invoke(selected);
                    return;
                }

                var models = selected.Where(a => a.Model != null).Select(a => a.Model).ToList();
                if (models.Count > 0)
                {
                    AccountManager.SelectedAccounts = models;
                    AccountLauncherService.MultiLaunchHome();
                }
            });

            JoinServerSelectedCommand = new RelayCommand(() =>
            {
                var selected = _accounts.Where(a => a.IsSelected).ToList();
                if (selected.Count == 0)
                {
                    StatusMessage = "⚠️ Vui lòng tick chọn ít nhất một tài khoản trước khi Vào Server!";
                    return;
                }

                if (OnJoinServerSelectedRequested != null) OnJoinServerSelectedRequested.Invoke(selected);
                else ExecuteJoinServerForAccounts(selected);
            });
        }
    }
}
