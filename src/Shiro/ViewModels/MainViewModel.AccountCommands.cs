#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class MainViewModel
    {
        // Card Level Callback (used by test hooks)
        public Action<AccountViewModel>? OnFastReLoginRequested { get; set; }

        // Add Commands
        public ICommand AddAccountFastLoginCommand { get; private set; } = null!;
        public ICommand AddAccountCookieCommand { get; private set; } = null!;
        public ICommand BulkImportCommand { get; private set; } = null!;

        // Card Level Commands
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
            AddAccountFastLoginCommand = new RelayCommand(() => AppServices.Dialogs.OpenFastLoginDialog(null));
            AddAccountCookieCommand = new RelayCommand(() => AppServices.Dialogs.OpenCookieImportDialog(null));
            ClearRobloxClientSessionCommand = new RelayCommand(() =>
            {
                bool ok = RobloxProcessService.ClearRobloxClientSession();
                StatusMessage = ok
                    ? Shiro.Services.LocalizationService.Get("Main.ClientSessionCleared")
                    : Shiro.Services.LocalizationService.Get("Main.CannotClearClientSession");
            });
            BulkImportCommand = new RelayCommand(() => AppServices.Dialogs.OpenBulkImportDialog());

            // Card Commands
            ReLoginWithCookieCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a && a.Model != null)
                    AppServices.Dialogs.OpenCookieImportDialog(a.Model);
            });
            ValidateSessionCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a && a.Model != null)
                {
                    _ = Task.Run(async () =>
                    {
                        bool ok = await a.Model.ValidateSessionAsync().ConfigureAwait(false);
                        AppServices.AccountRepository.Save(true);
                        UpdateAccountValidity(a.Model, ok);
                        PostStatusMessage(ok ? Shiro.Services.LocalizationService.Format("Main.SessionValidToast", a.Username) : (a.Model.IsBannedOrLocked ? $"{a.Username}: {a.Model.AccountStatusIssue}!" : Shiro.Services.LocalizationService.Format("Main.SessionExpiredToast", a.Username)));
                    });
                }
            });
            OpenProfileCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a && a.Model != null && a.UserId > 0)
                    Utilities.OpenUrl($"https://www.roblox.com/users/{a.UserId}/profile");
            });
            SetAliasCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                    AppServices.AccountWorkflowService.SetAlias(a, msg => StatusMessage = msg);
            });
            SetGroupCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                    AppServices.AccountWorkflowService.SetGroup(a, this, msg => StatusMessage = msg);
            });
            SetDescriptionCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                    AppServices.AccountWorkflowService.SetDescription(a, msg => StatusMessage = msg);
            });
            TogglePinCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                    AppServices.AccountWorkflowService.TogglePin(a, this, msg => StatusMessage = msg);
            });
            CopyUsernameCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                    AppServices.AccountWorkflowService.CopyUsername(a, msg => StatusMessage = msg);
            });
            CopyUserPassCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                    AppServices.AccountWorkflowService.CopyUserPass(a, msg => StatusMessage = msg);
            });
            CopyUserIdCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                    AppServices.AccountWorkflowService.CopyUserId(a, msg => StatusMessage = msg);
            });
            CopyProfileUrlCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                    AppServices.AccountWorkflowService.CopyProfileUrl(a, msg => StatusMessage = msg);
            });
            CopySecurityTokenCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                    AppServices.AccountWorkflowService.CopySecurityToken(a, msg => StatusMessage = msg);
            });
            SavePlaceIdToAccountCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a && a.Model != null && long.TryParse(PlaceId?.Trim(), out long pid) && pid > 0)
                {
                    a.Model.SetField("SavedPlaceId", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (!string.IsNullOrWhiteSpace(JobId))
                        a.Model.SetField("SavedJobId", JobId.Trim());
                    AppServices.AccountRepository.SaveAsync().Forget();
                }
            });
            ViewFieldsCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a && a.Model != null)
                    AppServices.Dialogs.OpenAccountFieldsDialog(a.Model);
            });
            OpenUtilsCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a && a.Model != null)
                    AppServices.Dialogs.OpenAccountUtilsDialog(a.Model);
            });
            RemoveAccountCommand = new RelayCommand(p =>
            {
                if (p is AccountViewModel a)
                    AppServices.AccountWorkflowService.RemoveAccount(a, this, msg => StatusMessage = msg);
            });

            // Batch Commands
            SetGroupSelectedCommand = new RelayCommand(() =>
                AppServices.AccountWorkflowService.SetGroupSelected(this, PostStatusMessage));
            SavePlaceIdSelectedCommand = new RelayCommand(() =>
                AppServices.AccountWorkflowService.SavePlaceIdSelected(this, PostStatusMessage));
            CopyUserPassSelectedCommand = new RelayCommand(() =>
                AppServices.AccountWorkflowService.CopyUserPassSelected(this, PostStatusMessage));
            RemoveSelectedCommand = new RelayCommand(() =>
                AppServices.AccountWorkflowService.RemoveSelectedAccounts(this, PostStatusMessage));
            ReLoginSelectedCommand = new RelayCommand(() =>
            {
                var selected = _accounts.Where(a => a.IsSelected).ToList();
                if (selected.Count == 0)
                {
                    StatusMessage = Shiro.Services.LocalizationService.Get("Main.SelectAccountBeforeRelogin");
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
                    StatusMessage = Shiro.Services.LocalizationService.Get("Main.SelectAccountBeforeCheckSession");
                    return;
                }
                AppServices.AccountWorkflowService.ValidateSessionSelected(selected, this, PostStatusMessage);
            });
        }

        IReadOnlyList<IAccountWorkflowItem> IAccountWorkflowHost.GetSelectedWorkflowItems() => GetSelectedAccountViewModels();

        IReadOnlyList<Account> IAccountWorkflowHost.GetSelectedAccounts() => GetSelectedAccounts();

        void IAccountWorkflowHost.RebuildLookupIndex() => RebuildLookupIndex();

        void IAccountWorkflowHost.RemoveWorkflowItems(params IAccountWorkflowItem[] toRemove)
        {
            if (toRemove == null || toRemove.Length == 0) return;
            RemoveAccountViewModels(toRemove.OfType<AccountViewModel>().ToArray());
        }
    }
}
