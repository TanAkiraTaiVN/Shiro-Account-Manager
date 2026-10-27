#nullable enable
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.AvaloniaUI.Views
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
            TxtTitle.Text = $"Tiện ích tài khoản • {account.Username}";
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
                SetStatus("⚠️ Không tìm thấy thông tin tài khoản.", true);
                return;
            }

            var res = await MessageBoxDialog.ShowAsync(
                $"Bạn có chắc chắn muốn đăng xuất tất cả các phiên khác của '{_account.Username}' không?",
                "Xác nhận", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, this);

            if (res == MessageBoxResult.Yes)
            {
                _isBusy = true;
                if (sender is Button btn) btn.IsEnabled = false;
                SetStatus("⏳ Đang gửi yêu cầu đăng xuất các phiên...");
                try
                {
                    bool ok = await _account.LogOutOfOtherSessionsAsync().ConfigureAwait(true);
                    SetStatus(ok ? "✅ Đã gửi yêu cầu đăng xuất tất cả các phiên khác thành công." : "⚠️ Đăng xuất các phiên khác không thành công.", !ok);
                }
                catch (Exception ex)
                {
                    SetStatus($"❌ Lỗi: {ex.Message}", true);
                }
                finally
                {
                    if (sender is Button btn2) btn2.IsEnabled = true;
                    _isBusy = false;
                }
            }
        }

        private async void BtnUnlockPin_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (_account == null)
            {
                SetStatus("⚠️ Không tìm thấy thông tin tài khoản.", true);
                return;
            }

            string? pin = PbPin.Text?.Trim();
            if (string.IsNullOrWhiteSpace(pin) || pin.Length != 4)
            {
                SetStatus("⚠️ Vui lòng nhập mã PIN gồm 4 chữ số!", true);
                return;
            }

            _isBusy = true;
            if (sender is Button btn) btn.IsEnabled = false;
            SetStatus("⏳ Đang mở khóa mã PIN...");
            try
            {
                bool ok = await _account.UnlockPinAsync(pin).ConfigureAwait(true);
                SetStatus(ok ? "✅ Đã mở khóa mã PIN thành công!" : "⚠️ Mã PIN không đúng hoặc yêu cầu thất bại.", !ok);
            }
            catch (Exception ex)
            {
                SetStatus($"❌ Lỗi mở khóa PIN: {ex.Message}", true);
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
                SetStatus("⚠️ Không tìm thấy thông tin tài khoản.", true);
                return;
            }

            string newPass = TxtPassword?.Text ?? "";
            _account.Password = newPass;
            _ = AccountManager.SaveAccountsAsync(true);
            SetStatus(string.IsNullOrEmpty(newPass)
                ? "ℹ️ Đã xóa mật khẩu tài khoản."
                : "✅ Đã lưu mật khẩu tài khoản thành công! Lần Re-login tới sẽ tự động điền.");
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
