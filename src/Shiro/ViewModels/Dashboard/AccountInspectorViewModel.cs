#nullable enable
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Shiro.Services;

namespace Shiro.ViewModels
{
    /// <summary>
    /// ViewModel for the non-modal right-hand Account Inspector drawer, managing inline edits,
    /// session health diagnostics, quick actions, and Undo-backed account removal.
    /// </summary>
    public sealed class AccountInspectorViewModel : ObservableObject, IDisposable
    {
        private static readonly ImmutableSolidColorBrush s_healthyBg = new(Color.Parse("#11261B")), s_healthyBorder = new(Color.Parse("#1E4931")), s_healthyFg = new(Color.Parse("#4ADE80"));
        private static readonly ImmutableSolidColorBrush s_warnBg = new(Color.Parse("#261D0E")), s_warnBorder = new(Color.Parse("#593C14")), s_warnFg = new(Color.Parse("#FBBF24"));
        private static readonly ImmutableSolidColorBrush s_dangerBg = new(Color.Parse("#271217")), s_dangerBorder = new(Color.Parse("#541D28")), s_dangerFg = new(Color.Parse("#F87171"));
        private static readonly ImmutableSolidColorBrush s_defaultPinBrush = new(Color.Parse("#A1A1B2"));

        private readonly IAccountRepository _accountRepository;
        private readonly AccountCollectionViewModel? _accountsVm;
        private readonly LaunchBarViewModel? _launchBar;
        private readonly Action<string> _setStatus;
        private readonly IAppLogger _logger;
        private AccountViewModel? _inspectedAccount;
        private PropertyChangedEventHandler? _inspectedPropHandler;
        private NotifyCollectionChangedEventHandler? _accountsCollectionHandler;
        private bool _isOpen;
        private string _editAlias = string.Empty, _editGroup = "Default", _editDescription = string.Empty, _editSavedPlaceId = string.Empty, _validationError = string.Empty;
        private int _disposed;

        public AccountInspectorViewModel(
            IAccountRepository? accountRepository = null,
            AccountCollectionViewModel? accountsVm = null,
            LaunchBarViewModel? launchBar = null,
            Action<string>? setStatus = null,
            IAppLogger? logger = null)
        {
            _accountRepository = accountRepository ?? AppServices.AccountRepository;
            _accountsVm = accountsVm;
            _launchBar = launchBar;
            _setStatus = setStatus ?? (_ => { });
            _logger = logger ?? AppServices.Logger;

            SaveInlineChangesCommand = new RelayCommand(SaveInlineChanges);
            ValidateSessionCommand = new RelayCommand(ValidateSession);
            ReLoginCommand = new RelayCommand(ReLogin);
            RefreshRobuxCommand = new RelayCommand(RefreshRobux);
            CopyUserIdCommand = new RelayCommand(CopyUserId);
            DeleteWithUndoCommand = new RelayCommand(DeleteWithUndo);
            CloseCommand = new RelayCommand(Close);
            ResetEditsCommand = new RelayCommand(ResetEdits);
            TogglePinCommand = new RelayCommand(TogglePin);
            UseCurrentPlaceIdCommand = new RelayCommand(UseCurrentPlaceId);
            PrimaryActionCommand = new RelayCommand(ExecutePrimaryAction);

            if (_accountsVm != null)
            {
                _accountsCollectionHandler = OnAccountsCollectionChanged;
                _accountsVm.Accounts.CollectionChanged += _accountsCollectionHandler;
            }
        }

        public bool IsOpen { get => _isOpen; set { if (SetProperty(ref _isOpen, value) && !value) ValidationError = string.Empty; } }
        internal Task LastSaveTask { get; private set; } = Task.CompletedTask;
        public double PinOpacity => _inspectedAccount?.PinOpacity ?? 0.45;
        public IBrush PinIconStroke => _inspectedAccount?.PinIconStroke ?? s_defaultPinBrush;

        public AccountViewModel? InspectedAccount
        {
            get => _inspectedAccount;
            private set
            {
                if (ReferenceEquals(_inspectedAccount, value)) return;
                DetachInspectedAccount();
                _inspectedAccount = value;
                if (_inspectedAccount != null) { _inspectedPropHandler = OnInspectedAccountPropertyChanged; _inspectedAccount.PropertyChanged += _inspectedPropHandler; }
                OnPropertyChanged();
                NotifyInspectedMetadataChanged();
            }
        }

        public string EditAlias { get => _editAlias; set { if (SetProperty(ref _editAlias, value ?? string.Empty)) NotifyEditStateChanged(); } }
        public string EditGroup { get => _editGroup; set { if (SetProperty(ref _editGroup, value ?? string.Empty)) NotifyEditStateChanged(); } }
        public string EditDescription { get => _editDescription; set { if (SetProperty(ref _editDescription, value ?? string.Empty)) NotifyEditStateChanged(); } }
        public string EditSavedPlaceId { get => _editSavedPlaceId; set { if (SetProperty(ref _editSavedPlaceId, value ?? string.Empty)) NotifyEditStateChanged(); } }
        public string ValidationError { get => _validationError; private set { if (SetProperty(ref _validationError, value)) OnPropertyChanged(nameof(HasValidationError)); } }
        public bool HasValidationError => !string.IsNullOrEmpty(_validationError);

        public bool HasUnsavedChanges
        {
            get
            {
                if (_inspectedAccount == null) return false;
                string curGroup = string.IsNullOrWhiteSpace(_inspectedAccount.Group) ? "Default" : _inspectedAccount.Group;
                string nextGroup = string.IsNullOrWhiteSpace(_editGroup) ? "Default" : _editGroup.Trim();
                string curPlace = _inspectedAccount.Model?.GetField("SavedPlaceId") ?? string.Empty;
                return !string.Equals(_inspectedAccount.Alias ?? string.Empty, _editAlias.Trim(), StringComparison.Ordinal) ||
                       !string.Equals(curGroup, nextGroup, StringComparison.Ordinal) ||
                       !string.Equals(_inspectedAccount.Description ?? string.Empty, _editDescription, StringComparison.Ordinal) ||
                       !string.Equals(curPlace, _editSavedPlaceId.Trim(), StringComparison.Ordinal);
            }
        }

        public string SessionHealthLabel => _inspectedAccount?.SessionHealth switch
        {
            SessionHealth.Healthy => LocalizationService.Get("Inspector_HealthHealthy"),
            SessionHealth.ShortLived => LocalizationService.Get("Inspector_HealthShortLived"),
            SessionHealth.Locked => LocalizationService.Get("Inspector_HealthLocked"),
            _ => LocalizationService.Get("Inspector_HealthExpired")
        };

        public string SessionDetailText => _inspectedAccount?.Model == null ? string.Empty :
            LocalizationService.Format("Inspector_LastUsedFormat",
                _inspectedAccount.Model.LastUse != DateTime.MinValue
                    ? _inspectedAccount.Model.LastUse.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
                    : LocalizationService.Get("Account.SessionUnknown"));

        public IBrush SessionHealthBackground => _inspectedAccount?.SessionHealth switch { SessionHealth.Healthy => s_healthyBg, SessionHealth.ShortLived => s_warnBg, _ => s_dangerBg };
        public IBrush SessionHealthBorder => _inspectedAccount?.SessionHealth switch { SessionHealth.Healthy => s_healthyBorder, SessionHealth.ShortLived => s_warnBorder, _ => s_dangerBorder };
        public IBrush SessionHealthForeground => _inspectedAccount?.SessionHealth switch { SessionHealth.Healthy => s_healthyFg, SessionHealth.ShortLived => s_warnFg, _ => s_dangerFg };

        public string PrimaryActionLabel => _inspectedAccount switch
        {
            { IsLocalClientRunning: true } => LocalizationService.Get("Inspector_PrimaryFocus"),
            { IsExpiredAndNotBanned: true } => LocalizationService.Get("Inspector_ReLoginBtn"),
            { IsBannedOrLocked: true } => LocalizationService.Get("Account.ViewBan"),
            _ => LocalizationService.Get("Inspector_PrimaryLaunch")
        };

        public ICommand SaveInlineChangesCommand { get; }
        public ICommand ValidateSessionCommand { get; }
        public ICommand ReLoginCommand { get; }
        public ICommand RefreshRobuxCommand { get; }
        public ICommand CopyUserIdCommand { get; }
        public ICommand DeleteWithUndoCommand { get; }
        public ICommand CloseCommand { get; }
        public ICommand ResetEditsCommand { get; }
        public ICommand TogglePinCommand { get; }
        public ICommand UseCurrentPlaceIdCommand { get; }
        public ICommand PrimaryActionCommand { get; }

        public void Open(AccountViewModel? account) { if (account == null || Volatile.Read(ref _disposed) != 0) { Close(); return; } InspectedAccount = account; LoadEditFields(account); IsOpen = true; }
        public void Close() { IsOpen = false; InspectedAccount = null; ValidationError = string.Empty; OnPropertyChanged(nameof(HasUnsavedChanges)); }
        public void NotifyLanguageChanged() => NotifyInspectedMetadataChanged();

        private void LoadEditFields(AccountViewModel account)
        {
            ValidationError = string.Empty;
            _editAlias = account.Alias ?? string.Empty;
            _editGroup = string.IsNullOrWhiteSpace(account.Group) ? "Default" : account.Group;
            _editDescription = account.Description ?? string.Empty;
            _editSavedPlaceId = account.Model?.GetField("SavedPlaceId") ?? string.Empty;
            NotifyEditPropertiesChanged();
        }

        private void ResetEdits() { if (_inspectedAccount != null) LoadEditFields(_inspectedAccount); }

        private void SaveInlineChanges()
        {
            if (_inspectedAccount?.Model == null) return;
            ValidationError = string.Empty;
            string nextAlias = _editAlias.Trim();
            if (nextAlias.Length > Account.MaxAliasLength) { ValidationError = LocalizationService.Format("Inspector_ErrAliasTooLong", Account.MaxAliasLength); return; }
            if (_editDescription.Length > Account.MaxDescriptionLength) { ValidationError = LocalizationService.Format("Inspector_ErrNotesTooLong", Account.MaxDescriptionLength); return; }

            long parsedPid = 0;
            string rawPid = _editSavedPlaceId.Trim();
            if (!string.IsNullOrEmpty(rawPid))
            {
                if (!long.TryParse(rawPid, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedPid) && rawPid.Contains("roblox.com", StringComparison.OrdinalIgnoreCase))
                    parsedPid = AccountLauncherService.ParsePlaceId(rawPid);
                if (parsedPid <= 0) { ValidationError = LocalizationService.Get("Inspector_ErrInvalidPlaceId"); return; }
            }

            string nextGroup = string.IsNullOrWhiteSpace(_editGroup) ? "Default" : _editGroup.Trim();
            using (Account.BeginSuppressDeferredSaveScope())
            {
                _inspectedAccount.Alias = nextAlias;
                _inspectedAccount.Group = nextGroup;
                _inspectedAccount.Description = _editDescription;
                if (parsedPid > 0) _inspectedAccount.Model.SetField("SavedPlaceId", parsedPid.ToString(CultureInfo.InvariantCulture));
                else if (!string.IsNullOrEmpty(_inspectedAccount.Model.GetField("SavedPlaceId"))) _inspectedAccount.Model.RemoveField("SavedPlaceId");
                _inspectedAccount.Model.IsDirty = true;
            }

            _editAlias = nextAlias;
            _editGroup = nextGroup;
            _editSavedPlaceId = parsedPid > 0 ? parsedPid.ToString(CultureInfo.InvariantCulture) : string.Empty;
            NotifyEditPropertiesChanged();
            _accountsVm?.RebuildGroupList(applyFilterOnReset: false);
            _accountsVm?.ApplyFilter();
            _accountsVm?.NotifyCounts();
            PersistRepositoryAsync();
            _setStatus(LocalizationService.Format("Inspector_SavedToast", _inspectedAccount.ShownUsername));
        }

        private void ValidateSession()
        {
            if (_inspectedAccount?.Model == null) return;
            if (_accountsVm != null) _accountsVm.ValidateSessionCommand.Execute(_inspectedAccount);
            else AccountCollectionCommandActions.ValidateSingleSession(_inspectedAccount, _accountRepository,
                (acc, ok, _) => { if (ReferenceEquals(_inspectedAccount?.Model, acc)) { _inspectedAccount.Valid = ok; _inspectedAccount.NotifyValidityChanged(); } },
                msg => AppDispatcher.Post(() => _setStatus(msg)));
        }

        private void ReLogin() { if (_inspectedAccount?.Model != null) AppServices.Dialogs.OpenFastLoginDialog(_inspectedAccount.Model); }
        private void RefreshRobux() { if (_inspectedAccount?.Model != null) { if (_accountsVm != null) _accountsVm.CheckRobuxAccountCommand.Execute(_inspectedAccount); else AccountCollectionCommandActions.CheckRobuxSingleAsync(_inspectedAccount, _accountRepository, _setStatus, () => { }).Forget(); } }
        private void CopyUserId() { if (_inspectedAccount?.Model != null) { if (_accountsVm != null) _accountsVm.CopyUserIdCommand.Execute(_inspectedAccount); else AppServices.AccountWorkflowService.CopyUserId(_inspectedAccount, _setStatus); } }
        private void TogglePin() { if (_inspectedAccount?.Model != null) { if (_accountsVm != null) _accountsVm.TogglePinCommand.Execute(_inspectedAccount); else _inspectedAccount.Pinned = !_inspectedAccount.Pinned; } }
        private void UseCurrentPlaceId() { long pid = AccountLauncherService.ParsePlaceId(_launchBar?.PlaceId ?? AppServices.ServerBrowserState.CurrentPlaceId); if (pid > 0) EditSavedPlaceId = pid.ToString(CultureInfo.InvariantCulture); }
        private void ExecutePrimaryAction() { if (_inspectedAccount != null) _launchBar?.PrimaryCardActionCommand.Execute(_inspectedAccount); }

        private void DeleteWithUndo()
        {
            AccountViewModel? target = _inspectedAccount;
            if (target?.Model == null) return;
            Account backup = target.Model.CloneSnapshotForUndo();
            string shownName = target.ShownUsername;
            Close();
            _accountRepository.Remove(target.Model);
            if (_accountsVm != null) { _accountsVm.RemoveAccountViewModels(target); _accountsVm.NotifyCounts(); _accountsVm.RebuildGroupList(); _accountsVm.ApplyFilter(); }

            int restoredFlag = 0;
            string deletedMsg = LocalizationService.Format("Inspector_DeletedMessage", shownName);
            _setStatus(deletedMsg);
            ToastService.ShowWarningWithAction(LocalizationService.Get("Inspector_DeletedTitle"), deletedMsg, LocalizationService.Get("Inspector_UndoBtn"), () =>
            {
                if (Interlocked.Exchange(ref restoredFlag, 1) != 0 || Volatile.Read(ref _disposed) != 0) return;
                RestoreAccount(backup, shownName);
            }, durationSeconds: 8.0, isOneShot: true);
        }

        private void RestoreAccount(Account backup, string shownName)
        {
            try { _accountRepository.AddThreadSafe(backup); PersistRepositoryAsync(); _setStatus(LocalizationService.Format("Inspector_RestoredStatus", shownName)); }
            catch (Exception ex) { _logger.Error($"[AccountInspectorViewModel.RestoreAccount] {ex.Message}"); }
        }

        private void PersistRepositoryAsync() { LastSaveTask = _accountRepository.SaveAsync(true) ?? Task.CompletedTask; LastSaveTask.Forget(); }

        private void NotifyEditPropertiesChanged() { OnPropertyChanged(nameof(EditAlias)); OnPropertyChanged(nameof(EditGroup)); OnPropertyChanged(nameof(EditDescription)); OnPropertyChanged(nameof(EditSavedPlaceId)); OnPropertyChanged(nameof(HasUnsavedChanges)); }
        private void NotifyEditStateChanged() { if (!string.IsNullOrEmpty(_validationError)) ValidationError = string.Empty; OnPropertyChanged(nameof(HasUnsavedChanges)); }
        private void NotifyInspectedMetadataChanged() { OnPropertyChanged(nameof(PinOpacity)); OnPropertyChanged(nameof(PinIconStroke)); OnPropertyChanged(nameof(SessionHealthLabel)); OnPropertyChanged(nameof(SessionDetailText)); OnPropertyChanged(nameof(SessionHealthBackground)); OnPropertyChanged(nameof(SessionHealthBorder)); OnPropertyChanged(nameof(SessionHealthForeground)); OnPropertyChanged(nameof(PrimaryActionLabel)); }

        private void OnInspectedAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            NotifyInspectedMetadataChanged();
            if (e.PropertyName is nameof(AccountViewModel.Alias) or nameof(AccountViewModel.Group) or nameof(AccountViewModel.Description)) OnPropertyChanged(nameof(HasUnsavedChanges));
        }

        private void OnAccountsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (Volatile.Read(ref _disposed) == 0 && _isOpen && _inspectedAccount != null &&
                (e.Action == NotifyCollectionChangedAction.Reset || (e.OldItems != null && e.OldItems.Contains(_inspectedAccount))) &&
                _accountsVm != null && !_accountsVm.Accounts.Contains(_inspectedAccount)) Close();
        }

        private void DetachInspectedAccount() { if (_inspectedAccount != null && _inspectedPropHandler != null) _inspectedAccount.PropertyChanged -= _inspectedPropHandler; _inspectedPropHandler = null; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            DetachInspectedAccount();
            if (_accountsVm != null && _accountsCollectionHandler != null) _accountsVm.Accounts.CollectionChanged -= _accountsCollectionHandler;
            _accountsCollectionHandler = null;
            _inspectedAccount = null;
        }
    }
}
