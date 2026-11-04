#nullable enable
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public partial class AccountUtilsDialog : Window
    {
        private static readonly IBrush BrushError = new SolidColorBrush(Color.Parse("#EF4444"));
        private static readonly IBrush BrushSuccess = new SolidColorBrush(Color.Parse("#22C55E"));

        private readonly Account? _account;
        private bool _isBusy;

        public AccountUtilsDialog()
        {
            InitializeComponent();
        }

        public AccountUtilsDialog(Account account) : this()
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
            TxtTitle.Text = Shiro.Services.LocalizationService.Format("AccountUtils.TitleFormat", account.Username);
            TxtDisplayName.Text = account.Username;
            if (TxtPassword != null)
            {
                TxtPassword.Text = account.Password ?? "";
            }
        }

        private void SetStatus(string message, bool isError = false)
        {
            TxtStatus.Text = message;
            TxtStatus.Foreground = isError ? BrushError : BrushSuccess;
        }

        private async void BtnLogOutOtherSessions_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (_account == null)
            {
                SetStatus(Shiro.Services.LocalizationService.Get("AccountUtils.AccountNotFound"), true);
                return;
            }

            try
            {
                var res = await MessageBoxDialog.ShowAsync(
                    Shiro.Services.LocalizationService.Format("AccountUtils.LogoutOtherSessionsPrompt", _account.Username),
                    Shiro.Services.LocalizationService.Get("AccountUtils.ConfirmTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, this);

                if (res == MessageBoxResult.Yes)
                {
                    _isBusy = true;
                    if (sender is Button btn) btn.IsEnabled = false;
                    SetStatus(Shiro.Services.LocalizationService.Get("AccountUtils.SendingLogoutRequest"));
                    try
                    {
                        bool ok = await _account.LogOutOfOtherSessionsAsync().ConfigureAwait(true);
                        SetStatus(ok ? Shiro.Services.LocalizationService.Get("AccountUtils.LogoutOtherSessionsSuccess") : Shiro.Services.LocalizationService.Get("AccountUtils.LogoutOtherSessionsFailed"), !ok);
                    }
                    catch (Exception ex)
                    {
                        SetStatus(Shiro.Services.LocalizationService.Format("Common.ErrorFormat", ex.Message), true);
                    }
                    finally
                    {
                        if (sender is Button btn2) btn2.IsEnabled = true;
                        _isBusy = false;
                    }
                }
            }
            catch (Exception ex)
            {
                SetStatus(Shiro.Services.LocalizationService.Format("Common.ErrorFormat", ex.Message), true);
            }
        }

        private async void BtnUnlockPin_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (_account == null)
            {
                SetStatus(Shiro.Services.LocalizationService.Get("AccountUtils.AccountNotFound"), true);
                return;
            }

            string? pin = PbPin.Text?.Trim();
            if (string.IsNullOrWhiteSpace(pin) || pin.Length != 4)
            {
                SetStatus(Shiro.Services.LocalizationService.Get("AccountUtils.PinMustBeFourDigits"), true);
                return;
            }

            _isBusy = true;
            if (sender is Button btn) btn.IsEnabled = false;
            SetStatus(Shiro.Services.LocalizationService.Get("AccountUtils.UnlockingPin"));
            try
            {
                bool ok = await _account.UnlockPinAsync(pin).ConfigureAwait(true);
                SetStatus(ok ? Shiro.Services.LocalizationService.Get("AccountUtils.UnlockPinSuccess") : Shiro.Services.LocalizationService.Get("AccountUtils.UnlockPinFailed"), !ok);
            }
            catch (Exception ex)
            {
                SetStatus(Shiro.Services.LocalizationService.Format("AccountUtils.UnlockPinError", ex.Message), true);
            }
            finally
            {
                if (sender is Button btn2) btn2.IsEnabled = true;
                _isBusy = false;
            }
        }

        private void BtnSavePassword_Click(object sender, RoutedEventArgs e)
        {
            if (_account == null)
            {
                SetStatus(Shiro.Services.LocalizationService.Get("AccountUtils.AccountNotFound"), true);
                return;
            }

            string newPass = TxtPassword?.Text ?? "";
            _account.Password = newPass;
            AppServices.AccountRepository.SaveAsync(true).Forget();
            SetStatus(string.IsNullOrEmpty(newPass)
                ? Shiro.Services.LocalizationService.Get("AccountUtils.PasswordCleared")
                : Shiro.Services.LocalizationService.Get("AccountUtils.PasswordSaved"));
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
