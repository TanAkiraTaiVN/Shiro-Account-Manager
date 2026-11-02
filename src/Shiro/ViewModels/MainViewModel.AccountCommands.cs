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
        // Add callbacks
        public Action? OnAddAccountRequested { get; set; }
        public Action? OnAddAccountChromeRequested { get; set; }
        public Action? OnAddAccountFastLoginRequested { get; set; }
        public Action? OnAddAccountCookieRequested { get; set; }
        public Action? OnBulkImportRequested { get; set; }

        // Card Level Callbacks
        public Action<AccountViewModel>? OnFastReLoginRequested { get; set; }
        public Action<AccountViewModel>? OnReLoginRequested { get; set; }
        public Action<AccountViewModel>? OnReLoginWithCookieRequested { get; set; }
        public Action<AccountViewModel>? OnValidateSessionRequested { get; set; }
        public Action<AccountViewModel>? OnOpenProfileRequested { get; set; }
        public Action<AccountViewModel>? OnSetAliasRequested { get; set; }
        public Action<AccountViewModel>? OnSetGroupRequested { get; set; }
        public Action<AccountViewModel>? OnSetDescriptionRequested { get; set; }
        public Action<AccountViewModel>? OnTogglePinRequested { get; set; }
        public Action<AccountViewModel>? OnCopyUsernameRequested { get; set; }
        public Action<AccountViewModel>? OnCopyUserPassRequested { get; set; }
        public Action<AccountViewModel>? OnCopyUserIdRequested { get; set; }
        public Action<AccountViewModel>? OnCopyProfileUrlRequested { get; set; }
        public Action<AccountViewModel>? OnCopySecurityTokenRequested { get; set; }
        public Action<AccountViewModel>? OnSavePlaceIdToAccountRequested { get; set; }
        public Action<AccountViewModel>? OnViewFieldsRequested { get; set; }
        public Action<AccountViewModel>? OnOpenUtilsRequested { get; set; }
        public Action? OnOpenAboutRequested { get; set; }
        public Action<AccountViewModel>? OnRemoveAccountRequested { get; set; }

        // Batch Level Callbacks
        public Action<IList<AccountViewModel>>? OnReLoginSelectedRequested { get; set; }
        public Action<IList<AccountViewModel>>? OnValidateSessionSelectedRequested { get; set; }
        public Action? OnSetGroupSelectedRequested { get; set; }
        public Action? OnSavePlaceIdSelectedRequested { get; set; }
        public Action? OnCopyUserPassSelectedRequested { get; set; }
        public Action? OnRemoveSelectedRequested { get; set; }

        // Add Commands
        public ICommand AddAccountCommand { get; private set; } = null!;
        public ICommand AddAccountChromeCommand { get; private set; } = null!;
        public ICommand AddAccountFastLoginCommand { get; private set; } = null!;
        public ICommand AddAccountCookieCommand { get; private set; } = null!;
        public ICommand BulkImportCommand { get; private set; } = null!;
        public ICommand AltGenCommand { get; private set; } = null!;

        // Card Level Commands
        public ICommand FastReLoginAccountCommand { get; private set; } = null!;
        public ICommand ReLoginAccountCommand { get; private set; } = null!;
        public ICommand ReLoginWithCookieCommand { get; private set; } = null!;
        public ICommand ValidateSessionCommand { get; private set; } = null!;
        public ICommand OpenProfileCommand { get; private set; } = null!;
        public ICommand SetAliasCommand { get; private set; } = null!;
        public ICommand SetGroupCommand { get; private set; } = null!;
        public ICommand SetDescriptionCommand { get; private set; } = null!;
        public ICommand TogglePinCommand { get; private set; } = null!;
        public ICommand CopyUsernameCommand { get; private set; } = null!;
        public ICommand CopyUserPassCommand { get; private set; } = null!;
        public ICommand CopyUserIdCommand { get; private set; } = null!;
        public ICommand CopyProfileUrlCommand { get; private set; } = null!;
        public ICommand CopySecurityTokenCommand { get; private set; } = null!;
        public ICommand SavePlaceIdToAccountCommand { get; private set; } = null!;
        public ICommand ViewFieldsCommand { get; private set; } = null!;
        public ICommand OpenUtilsCommand { get; private set; } = null!;
        public ICommand OpenAboutDialogCommand { get; private set; } = null!;
        public ICommand RemoveAccountCommand { get; private set; } = null!;

        // Batch Level Commands
        public ICommand SetGroupSelectedCommand { get; private set; } = null!;
        public ICommand SavePlaceIdSelectedCommand { get; private set; } = null!;
        public ICommand CopyUserPassSelectedCommand { get; private set; } = null!;
        public ICommand RemoveSelectedCommand { get; private set; } = null!;
        public ICommand ReLoginSelectedCommand { get; private set; } = null!;
        public ICommand ValidateSessionSelectedCommand { get; private set; } = null!;
        public ICommand ClearRobloxClientSessionCommand { get; private set; } = null!;

        private void InitializeAccountCommands()
        {
            AddAccountCommand = new RelayCommand(() =>
            {
                if (OnAddAccountFastLoginRequested != null) OnAddAccountFastLoginRequested.Invoke();
                else AppServices.Dialogs.OpenFastLoginDialog(null);
            });
            AddAccountFastLoginCommand = AddAccountCommand;
            AddAccountChromeCommand = new RelayCommand(() =>
            {
                if (OnAddAccountChromeRequested != null)
                {
                    OnAddAccountChromeRequested.Invoke();
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await BrowserService.Default.SignInAsync().ConfigureAwait(false);
                        AppDispatcher.Post(() => LoadAccounts(AccountManager.GetAccountsSnapshot()));
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Error($"[AddAccountChromeCommand] {ex}");
                    }
                });
            });
            AddAccountCookieCommand = new RelayCommand(() =>
            {
                if (OnAddAccountCookieRequested != null)
                {
                    OnAddAccountCookieRequested.Invoke();
                    return;
                }

                string cookie = AppServices.Dialogs.ShowInputPrompt("Dán chuỗi Cookie .ROBLOSECURITY vào ô bên dưới:", "Nhập Cookie");
                if (!string.IsNullOrWhiteSpace(cookie) && cookie != "/UC")
                {
                    _ = Task.Run(async () =>
                    {
                        var acc = await AccountManager.AddAccountAsync(cookie.Trim()).ConfigureAwait(false);
                        AppDispatcher.Post(() =>
                        {
                            if (acc != null)
                            {
                                LoadAccounts(AccountManager.GetAccountsSnapshot());
                                StatusMessage = $"Đã thêm tài khoản {acc.Username} qua Cookie thành công!";
                            }
                            else
                            {
                                StatusMessage = "Cookie không hợp lệ hoặc đã hết hạn.";
                            }
                        });
                    });
                }
            });
            ClearRobloxClientSessionCommand = new RelayCommand(() =>
            {
                bool ok = RobloxProcessService.ClearRobloxClientSession();
                StatusMessage = ok
                    ? "Đã xóa sạch session đăng nhập của Roblox Client."
                    : "Không thể xóa session Roblox Client (có thể Roblox đang chạy).";
            });
            BulkImportCommand = new RelayCommand(() =>
            {
                if (OnBulkImportRequested != null) OnBulkImportRequested.Invoke();
                else AppServices.Dialogs.OpenBulkImportDialog();
            });
            AltGenCommand = new RelayCommand(() =>
            {
                AppServices.Dialogs.OpenAltGenDialog();
            });

            // Card Commands
            FastReLoginAccountCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnFastReLoginRequested != null) OnFastReLoginRequested.Invoke(a);
                    else if (a.Model != null) AppServices.Dialogs.OpenFastLoginDialog(a.Model);
                }
            });
            ReLoginAccountCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnReLoginRequested != null) OnReLoginRequested.Invoke(a);
                    else if (OnFastReLoginRequested != null) OnFastReLoginRequested.Invoke(a);
                    else if (a.Model != null) AppServices.Dialogs.OpenFastLoginDialog(a.Model);
                }
            });
            ReLoginWithCookieCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnReLoginWithCookieRequested != null) OnReLoginWithCookieRequested.Invoke(a);
                    else _ = Task.Run(async () => await AccountWorkflowService.Default.ReLoginWithCookieAsync(a, this, PostStatusMessage).ConfigureAwait(false));
                }
            });
            ValidateSessionCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnValidateSessionRequested != null)
                    {
                        OnValidateSessionRequested.Invoke(a);
                        return;
                    }
                    if (a.Model == null) return;
                    _ = Task.Run(async () =>
                    {
                        bool ok = await a.Model.ValidateSessionAsync().ConfigureAwait(false);
                        AccountManager.SaveAccounts(true);
                        UpdateAccountValidity(a.Model, ok);
                        PostStatusMessage(ok ? $"{a.Username}: Phiên đăng nhập hợp lệ!" : (a.Model.IsBannedOrLocked ? $"{a.Username}: {a.Model.AccountStatusIssue}!" : $"{a.Username}: Phiên đã hết hạn!"));
                    });
                }
            });
            OpenProfileCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnOpenProfileRequested != null) OnOpenProfileRequested.Invoke(a);
                    else if (a.Model != null && a.UserId > 0) Utilities.OpenUrl($"https://www.roblox.com/users/{a.UserId}/profile");
                }
            });
            SetAliasCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnSetAliasRequested != null) OnSetAliasRequested.Invoke(a);
                    else AccountWorkflowService.Default.SetAlias(a, msg => StatusMessage = msg);
                }
            });
            SetGroupCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnSetGroupRequested != null) OnSetGroupRequested.Invoke(a);
                    else AccountWorkflowService.Default.SetGroup(a, this, msg => StatusMessage = msg);
                }
            });
            SetDescriptionCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnSetDescriptionRequested != null) OnSetDescriptionRequested.Invoke(a);
                    else AccountWorkflowService.Default.SetDescription(a, msg => StatusMessage = msg);
                }
            });
            TogglePinCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnTogglePinRequested != null) OnTogglePinRequested.Invoke(a);
                    else AccountWorkflowService.Default.TogglePin(a, this, msg => StatusMessage = msg);
                }
            });
            CopyUsernameCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnCopyUsernameRequested != null) OnCopyUsernameRequested.Invoke(a);
                    else AccountWorkflowService.Default.CopyUsername(a, msg => StatusMessage = msg);
                }
            });
            CopyUserPassCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnCopyUserPassRequested != null) OnCopyUserPassRequested.Invoke(a);
                    else AccountWorkflowService.Default.CopyUserPass(a, msg => StatusMessage = msg);
                }
            });
            CopyUserIdCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnCopyUserIdRequested != null) OnCopyUserIdRequested.Invoke(a);
                    else AccountWorkflowService.Default.CopyUserId(a, msg => StatusMessage = msg);
                }
            });
            CopyProfileUrlCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnCopyProfileUrlRequested != null) OnCopyProfileUrlRequested.Invoke(a);
                    else AccountWorkflowService.Default.CopyProfileUrl(a, msg => StatusMessage = msg);
                }
            });
            CopySecurityTokenCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnCopySecurityTokenRequested != null) OnCopySecurityTokenRequested.Invoke(a);
                    else AccountWorkflowService.Default.CopySecurityToken(a, msg => StatusMessage = msg);
                }
            });
            SavePlaceIdToAccountCommand = new RelayCommand(p => { if (p is AccountViewModel a) OnSavePlaceIdToAccountRequested?.Invoke(a); });
            ViewFieldsCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnViewFieldsRequested != null) OnViewFieldsRequested.Invoke(a);
                    else if (a.Model != null) AppServices.Dialogs.OpenAccountFieldsDialog(a.Model);
                }
            });
            OpenUtilsCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnOpenUtilsRequested != null) OnOpenUtilsRequested.Invoke(a);
                    else if (a.Model != null) AppServices.Dialogs.OpenAccountUtilsDialog(a.Model);
                }
            });
            OpenAboutDialogCommand = new RelayCommand(() =>
            {
                if (OnOpenAboutRequested != null) OnOpenAboutRequested.Invoke();
                else AppServices.Dialogs.OpenAboutDialog();
            });
            RemoveAccountCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                {
                    if (OnRemoveAccountRequested != null) OnRemoveAccountRequested.Invoke(a);
                    else AccountWorkflowService.Default.RemoveAccount(a, this, msg => StatusMessage = msg);
                }
            });

            // Batch Commands
            SetGroupSelectedCommand = new RelayCommand(() =>
            {
                if (OnSetGroupSelectedRequested != null) OnSetGroupSelectedRequested.Invoke();
                else AccountWorkflowService.Default.SetGroupSelected(this, PostStatusMessage);
            });
            SavePlaceIdSelectedCommand = new RelayCommand(() =>
            {
                if (OnSavePlaceIdSelectedRequested != null) OnSavePlaceIdSelectedRequested.Invoke();
                else AccountWorkflowService.Default.SavePlaceIdSelected(this, PostStatusMessage);
            });
            CopyUserPassSelectedCommand = new RelayCommand(() =>
            {
                if (OnCopyUserPassSelectedRequested != null) OnCopyUserPassSelectedRequested.Invoke();
                else AccountWorkflowService.Default.CopyUserPassSelected(this, PostStatusMessage);
            });
            RemoveSelectedCommand = new RelayCommand(() =>
            {
                if (OnRemoveSelectedRequested != null) OnRemoveSelectedRequested.Invoke();
                else AccountWorkflowService.Default.RemoveSelectedAccounts(this, PostStatusMessage);
            });
            ReLoginSelectedCommand = new RelayCommand(() =>
            {
                var selected = _accounts.Where(a => a.IsSelected).ToList();
                if (selected.Count == 0)
                {
                    StatusMessage = "Vui lòng tick chọn ít nhất một tài khoản trước khi Re-login!";
                    return;
                }
                if (OnReLoginSelectedRequested != null)
                {
                    OnReLoginSelectedRequested.Invoke(selected);
                    return;
                }
                var models = selected.Where(a => a.Model != null).Select(a => a.Model).ToList();
                if (models.Count == 1)
                    AppServices.Dialogs.OpenFastLoginDialog(models[0]);
                else if (models.Count > 1)
                    AppServices.Dialogs.OpenFastLoginDialogBatch(models);
            });
            ValidateSessionSelectedCommand = new RelayCommand(() =>
            {
                var selected = _accounts.Where(a => a.IsSelected).ToList();
                if (selected.Count == 0)
                {
                    StatusMessage = "Vui lòng tick chọn ít nhất một tài khoản trước khi kiểm tra phiên!";
                    return;
                }
                if (OnValidateSessionSelectedRequested != null) OnValidateSessionSelectedRequested.Invoke(selected);
                else AccountWorkflowService.Default.ValidateSessionSelected(selected, this, PostStatusMessage);
            });
        }
    }
}
