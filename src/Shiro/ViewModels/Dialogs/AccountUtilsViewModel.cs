#nullable enable
using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs
{
    public sealed class AccountUtilsViewModel : DialogViewModelBase
    {
        private readonly Account _account;
        private readonly IAccountRepository _repo;
        private readonly IUserPrompt _prompt;
        private readonly Func<Account, Task<bool>> _logoutOthers;
        private readonly Func<Account, string, Task<bool>> _unlockPin;

        private string _password;
        private string _pin = string.Empty;

        public AccountUtilsViewModel(
            Account account,
            IAccountRepository? repo = null,
            IUserPrompt? prompt = null,
            Func<Account, Task<bool>>? logoutOthers = null,
            Func<Account, string, Task<bool>>? unlockPin = null)
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
            _repo = repo ?? AppServices.AccountRepository;
            _prompt = prompt ?? AppServices.Prompt;
            _logoutOthers = logoutOthers ?? (a => a.LogOutOfOtherSessionsAsync());
            _unlockPin = unlockPin ?? ((a, p) => a.UnlockPinAsync(p));

            Title = LocalizationService.Format("AccountUtils.TitleFormat", account.Username);
            DisplayName = account.Username;
            _password = account.Password ?? string.Empty;

            LogOutOtherSessionsCommand = TrackCommand(new AsyncRelayCommand(LogOutOtherSessionsAsync, () => !IsBusy));
            UnlockPinCommand = TrackCommand(new AsyncRelayCommand(UnlockPinAsync, () => !IsBusy));
            SavePasswordCommand = new RelayCommand(SavePassword);
            CloseCommand = new RelayCommand(() => RequestClose(true));
        }

        public string Title { get; }
        public string DisplayName { get; }

        public string Password
        {
            get => _password;
            set => SetProperty(ref _password, value);
        }

        public string Pin
        {
            get => _pin;
            set => SetProperty(ref _pin, value);
        }

        public IAsyncRelayCommand LogOutOtherSessionsCommand { get; }
        public IAsyncRelayCommand UnlockPinCommand { get; }
        public ICommand SavePasswordCommand { get; }
        public ICommand CloseCommand { get; }

        public static bool IsValidPin(string? pin)
        {
            if (string.IsNullOrWhiteSpace(pin))
                return false;

            ReadOnlySpan<char> trimmed = pin.AsSpan().Trim();
            if (trimmed.Length != 4)
                return false;

            for (int i = 0; i < 4; i++)
            {
                char c = trimmed[i];
                if (c < '0' || c > '9')
                    return false;
            }
            return true;
        }

        public async Task LogOutOtherSessionsAsync()
        {
            if (IsBusy)
                return;

            try
            {
                bool confirmed = await _prompt.ConfirmAsync(
                    LocalizationService.Format("AccountUtils.LogoutOtherSessionsPrompt", _account.Username),
                    LocalizationService.Get("AccountUtils.ConfirmTitle")).ConfigureAwait(false);

                if (!confirmed)
                    return;

                IsBusy = true;
                SetStatus(LocalizationService.Get("AccountUtils.SendingLogoutRequest"), StatusKind.Info);
                try
                {
                    bool ok = await _logoutOthers(_account).ConfigureAwait(false);
                    SetStatus(
                        ok ? LocalizationService.Get("AccountUtils.LogoutOtherSessionsSuccess") : LocalizationService.Get("AccountUtils.LogoutOtherSessionsFailed"),
                        ok ? StatusKind.Success : StatusKind.Error);
                }
                catch (Exception ex)
                {
                    SetStatus(LocalizationService.Format("Common.ErrorFormat", ex.Message), StatusKind.Error);
                }
                finally
                {
                    IsBusy = false;
                }
            }
            catch (Exception ex)
            {
                SetStatus(LocalizationService.Format("Common.ErrorFormat", ex.Message), StatusKind.Error);
            }
        }

        public async Task UnlockPinAsync()
        {
            if (IsBusy)
                return;

            if (!IsValidPin(Pin))
            {
                SetStatus(LocalizationService.Get("AccountUtils.PinMustBeFourDigits"), StatusKind.Error);
                return;
            }

            string trimmedPin = Pin.Trim();
            IsBusy = true;
            SetStatus(LocalizationService.Get("AccountUtils.UnlockingPin"), StatusKind.Info);
            try
            {
                bool ok = await _unlockPin(_account, trimmedPin).ConfigureAwait(false);
                SetStatus(
                    ok ? LocalizationService.Get("AccountUtils.UnlockPinSuccess") : LocalizationService.Get("AccountUtils.UnlockPinFailed"),
                    ok ? StatusKind.Success : StatusKind.Error);
            }
            catch (Exception ex)
            {
                SetStatus(LocalizationService.Format("AccountUtils.UnlockPinError", ex.Message), StatusKind.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void SavePassword()
        {
            string newPass = Password ?? string.Empty;
            _account.Password = newPass;
            _repo.SaveAsync(true).Forget();
            SetStatus(
                string.IsNullOrEmpty(newPass)
                    ? LocalizationService.Get("AccountUtils.PasswordCleared")
                    : LocalizationService.Get("AccountUtils.PasswordSaved"),
                StatusKind.Success);
        }
    }
}
