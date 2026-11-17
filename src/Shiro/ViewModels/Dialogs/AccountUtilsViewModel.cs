#nullable enable
using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs
{
    public sealed class AccountUtilsViewModel : DialogViewModelBase
    {
        private readonly Account _account;
        private readonly IAccountRepository _repo;
        private readonly IUserPrompt _prompt;
        private readonly Func<Account, Task<bool>> _logoutOthers;

        private string _password;

        public AccountUtilsViewModel(
            Account account,
            IAccountRepository? repo = null,
            IUserPrompt? prompt = null,
            Func<Account, Task<bool>>? logoutOthers = null)
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
            _repo = repo ?? AppServices.AccountRepository;
            _prompt = prompt ?? AppServices.Prompt;
            _logoutOthers = logoutOthers ?? (a => a.LogOutOfOtherSessionsAsync());

            Title = LocalizationService.Format("AccountUtils.TitleFormat", account.Username);
            DisplayName = account.Username;
            _password = account.Password ?? string.Empty;

            LogOutOtherSessionsCommand = TrackCommand(new AsyncRelayCommand(LogOutOtherSessionsAsync, () => !IsBusy));
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

        public IAsyncRelayCommand LogOutOtherSessionsCommand { get; }
        public ICommand SavePasswordCommand { get; }
        public ICommand CloseCommand { get; }

        public async Task LogOutOtherSessionsAsync()
        {
            if (IsBusy)
                return;

            try
            {
                bool confirmed = await _prompt.ConfirmAsync(
                    LocalizationService.Format("AccountUtils.LogoutOtherSessionsPrompt", _account.Username),
                    LocalizationService.Get("AccountUtils.ConfirmTitle"));

                if (!confirmed)
                    return;

                IsBusy = true;
                SetStatus(LocalizationService.Get("AccountUtils.SendingLogoutRequest"), StatusKind.Info);
                try
                {
                    bool ok = await _logoutOthers(_account);
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
