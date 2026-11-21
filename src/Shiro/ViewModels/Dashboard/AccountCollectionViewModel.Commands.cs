#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public sealed partial class AccountCollectionViewModel
    {
        public ICommand AddAccountFastLoginCommand { get; private set; } = null!;
        public ICommand AddAccountCookieCommand { get; private set; } = null!;
        public ICommand BulkImportCommand { get; private set; } = null!;
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
        public ICommand SetGroupSelectedCommand { get; private set; } = null!;
        public ICommand SavePlaceIdSelectedCommand { get; private set; } = null!;
        public ICommand CopyUserPassSelectedCommand { get; private set; } = null!;
        public ICommand RemoveSelectedCommand { get; private set; } = null!;
        public ICommand ReLoginSelectedCommand { get; private set; } = null!;
        public ICommand ValidateSessionSelectedCommand { get; private set; } = null!;
        public ICommand ClearRobloxClientSessionCommand { get; private set; } = null!;
        public ICommand SelectAllCommand { get; private set; } = null!;
        public ICommand ClearSelectionCommand { get; private set; } = null!;
        public ICommand ClearSearchCommand { get; private set; } = null!;
        public ICommand CheckSessionsCommand { get; private set; } = null!;
        public ICommand ReLoginAllExpiredCommand { get; private set; } = null!;
        public ICommand SetSortModeCommand { get; private set; } = null!;
        public ICommand RefreshAllRobuxCommand { get; private set; } = null!;
        public ICommand CheckRobuxAccountCommand { get; private set; } = null!;
        public IAsyncRelayCommand AuditAllAccountsCommand { get; private set; } = null!;
        public ICommand SelectReadyBatchCommand { get; private set; } = null!;
        public ICommand InvertSelectionCommand { get; private set; } = null!;
        public ICommand SelectByRangePromptCommand { get; private set; } = null!;
        public ICommand MetricCardInGameCommand { get; private set; } = null!;
        public ICommand MetricCardReadyCommand { get; private set; } = null!;
        public ICommand MetricCardExpiredCommand { get; private set; } = null!;
        public ICommand MetricCardRobuxCommand { get; private set; } = null!;

        private void InitializeCommands()
        {
            InitializeCardAndBatchCommands();
            InitializeSelectionAndAuditCommands();
        }

        private void InitializeCardAndBatchCommands()
        {
            AddAccountFastLoginCommand = new RelayCommand(() => AppServices.Dialogs.OpenFastLoginDialog(null));
            AddAccountCookieCommand = new RelayCommand(() => AppServices.Dialogs.OpenCookieImportDialog(null));
            BulkImportCommand = new RelayCommand(() => AppServices.Dialogs.OpenBulkImportDialog());
            ClearRobloxClientSessionCommand = new RelayCommand(() =>
            {
                bool ok = RobloxProcessService.ClearRobloxClientSession();
                _setStatus(ok
                    ? LocalizationService.Get("Main.ClientSessionCleared")
                    : LocalizationService.Get("Main.CannotClearClientSession"));
            });

            ReLoginWithCookieCommand = new RelayCommand(p => { if (p is AccountViewModel a && a.Model != null) AppServices.Dialogs.OpenCookieImportDialog(a.Model); });
            ValidateSessionCommand = new RelayCommand(p => AccountCollectionCommandActions.ValidateSingleSession(p as AccountViewModel, _accountRepository, UpdateAccountValidity, PostStatusMessage));
            OpenProfileCommand = new RelayCommand(p => { if (p is AccountViewModel a && a.Model != null && a.UserId > 0) Utilities.OpenUrl($"https://www.roblox.com/users/{a.UserId}/profile"); });
            SetAliasCommand = new AsyncRelayCommand(p => p is AccountViewModel a ? AppServices.AccountWorkflowService.SetAliasAsync(a, _setStatus) : Task.CompletedTask);
            SetGroupCommand = new AsyncRelayCommand(p => p is AccountViewModel a ? AppServices.AccountWorkflowService.SetGroupAsync(a, this, _setStatus) : Task.CompletedTask);
            SetDescriptionCommand = new AsyncRelayCommand(p => p is AccountViewModel a ? AppServices.AccountWorkflowService.SetDescriptionAsync(a, _setStatus) : Task.CompletedTask);
            TogglePinCommand = new RelayCommand(p => { if (p is AccountViewModel a) AppServices.AccountWorkflowService.TogglePin(a, this, _setStatus); });
            CopyUsernameCommand = new RelayCommand(p => { if (p is AccountViewModel a) AppServices.AccountWorkflowService.CopyUsername(a, _setStatus); });
            CopyUserPassCommand = new RelayCommand(p => { if (p is AccountViewModel a) AppServices.AccountWorkflowService.CopyUserPass(a, _setStatus); });
            CopyUserIdCommand = new RelayCommand(p => { if (p is AccountViewModel a) AppServices.AccountWorkflowService.CopyUserId(a, _setStatus); });
            CopyProfileUrlCommand = new RelayCommand(p => { if (p is AccountViewModel a) AppServices.AccountWorkflowService.CopyProfileUrl(a, _setStatus); });
            CopySecurityTokenCommand = new RelayCommand(p => { if (p is AccountViewModel a) AppServices.AccountWorkflowService.CopySecurityToken(a, _setStatus); });
            SavePlaceIdToAccountCommand = new RelayCommand(p => AccountCollectionCommandActions.SavePlaceIdToAccount(p as AccountViewModel, _accountRepository, _getPlaceId(), _getJobId()));
            ViewFieldsCommand = new RelayCommand(p => { if (p is AccountViewModel a && a.Model != null) AppServices.Dialogs.OpenAccountFieldsDialog(a.Model); });
            OpenUtilsCommand = new RelayCommand(p => { if (p is AccountViewModel a && a.Model != null) AppServices.Dialogs.OpenAccountUtilsDialog(a.Model); });
            RemoveAccountCommand = new AsyncRelayCommand(p => p is AccountViewModel a ? AppServices.AccountWorkflowService.RemoveAccountAsync(a, this, _setStatus) : Task.CompletedTask);

            SetGroupSelectedCommand = new AsyncRelayCommand(() => AppServices.AccountWorkflowService.SetGroupSelectedAsync(this, PostStatusMessage));
            SavePlaceIdSelectedCommand = new RelayCommand(() => AppServices.AccountWorkflowService.SavePlaceIdSelected(this, PostStatusMessage));
            CopyUserPassSelectedCommand = new RelayCommand(() => AppServices.AccountWorkflowService.CopyUserPassSelected(this, PostStatusMessage));
            RemoveSelectedCommand = new AsyncRelayCommand(() => AppServices.AccountWorkflowService.RemoveSelectedAccountsAsync(this, PostStatusMessage));
            ReLoginSelectedCommand = new RelayCommand(() => AccountCollectionCommandActions.ReLoginSelected(_accounts.Where(a => a.IsSelected).ToList(), _setStatus));
            ValidateSessionSelectedCommand = new RelayCommand(() => AccountCollectionCommandActions.ValidateSessionSelected(_accounts.Where(a => a.IsSelected).ToList(), this, _setStatus, PostStatusMessage));
        }

        private void InitializeSelectionAndAuditCommands()
        {
            AuditAllAccountsCommand = new AsyncRelayCommand(async () =>
            {
                _setStatus(LocalizationService.Get("Main.AuditAllSessionsDescription"));
                var audited = await AuditAccountSessionsAsync();
                _setStatus(LocalizationService.Format("Main.AuditAllSessionsCompleted", audited.Count));
            });

            RefreshAllRobuxCommand = new AsyncRelayCommand(() =>
                AccountCollectionCommandActions.RefreshAllRobuxAsync(_accounts, _accountRepository, _setStatus, () =>
                {
                    NotifyCounts();
                    if (SortMode == AccountSortMode.RobuxDescending) ApplyFilter();
                }));

            CheckRobuxAccountCommand = new AsyncRelayCommand(p =>
                AccountCollectionCommandActions.CheckRobuxSingleAsync(p as AccountViewModel, _accountRepository, _setStatus, () =>
                {
                    NotifyCounts();
                    if (SortMode == AccountSortMode.RobuxDescending) ApplyFilter();
                }));

            SelectAllCommand = new RelayCommand(() =>
            {
                bool allSelected = _filteredAccounts.Count > 0 && _filteredAccounts.All(a => a.IsSelected);
                RunSuppressedSelection(() => { foreach (var acc in _filteredAccounts) acc.IsSelected = !allSelected; });
            });

            ClearSelectionCommand = new RelayCommand(() =>
                RunSuppressedSelection(() => { foreach (var acc in _accounts) acc.IsSelected = false; }));

            ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);

            CheckSessionsCommand = new RelayCommand(() =>
            {
                try
                {
                    _ = Task.Run(async () =>
                        await AppServices.AccountWorkflowService.CheckAllSessionsAsync(this, PostStatusMessage).ConfigureAwait(false));
                }
                catch (Exception ex) { _setStatus(LocalizationService.Get("Main.SessionCheckErrorPrefix") + ex.Message); }
            });

            ReLoginAllExpiredCommand = new RelayCommand(() => AccountCollectionCommandActions.ReLoginAllExpired(_accounts, _setStatus));

            SetSortModeCommand = new RelayCommand(param =>
            {
                if (param is AccountSortMode mode) SortMode = mode;
                else if (param is string modeStr && Enum.TryParse<AccountSortMode>(modeStr, out var m)) SortMode = m;
            });

            SelectReadyBatchCommand = new RelayCommand(param =>
            {
                int targetCount = 10;
                if (param is int n && n > 0) targetCount = n;
                else if (param is string s && int.TryParse(s, out int parsed) && parsed > 0) targetCount = parsed;

                int picked = SelectFirstReadyAccounts(targetCount);
                _setStatus(LocalizationService.Format("Main.QuickSelectedReady", picked));
                if (picked == 0)
                    ToastService.ShowWarning(LocalizationService.Get("Dash_QuickSelect"), LocalizationService.Get("Main.NoReadyAccountsInList"));
            });

            InvertSelectionCommand = new RelayCommand(() =>
                _setStatus(LocalizationService.Format("Main.InvertedSelectionResult", InvertFilteredSelection())));

            SelectByRangePromptCommand = new AsyncRelayCommand(() =>
                AccountCollectionCommandActions.SelectByRangePromptAsync(SelectRangeExpression, _setStatus));

            MetricCardInGameCommand = new RelayCommand(() =>
                ActiveTab = ActiveTab == AccountFilterTab.InGame ? AccountFilterTab.All : AccountFilterTab.InGame);
            MetricCardReadyCommand = new RelayCommand(() =>
                ActiveTab = ActiveTab == AccountFilterTab.Ready ? AccountFilterTab.All : AccountFilterTab.Ready);
            MetricCardExpiredCommand = new RelayCommand(() =>
                ActiveTab = ActiveTab == AccountFilterTab.Expired ? AccountFilterTab.All : AccountFilterTab.Expired);
            MetricCardRobuxCommand = new RelayCommand(() =>
            {
                ActiveTab = AccountFilterTab.All;
                SortMode = SortMode == AccountSortMode.RobuxDescending ? AccountSortMode.NaturalAscending : AccountSortMode.RobuxDescending;
            });
        }
    }
}
