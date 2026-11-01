#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public partial class AltGenDialog : Window
    {
        public AltGenDialog()
        {
            InitializeComponent();
            WindowBackdropService.ApplyDarkAndBackdrop(this);

            TxtApiKey.Text = BloxGenService.Default.SavedApiKey;
            SelectOption(CmbContinent, BloxGenService.Default.DefaultContinent);
            SelectOption(CmbGenType, BloxGenService.Default.DefaultGenType);
        }

        private static void SelectOption(ComboBox combo, string tag)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    break;
                }
            }
        }

        private static string GetSelectedTag(ComboBox combo, string fallback)
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                return item.Tag.ToString() ?? fallback;
            }
            return fallback;
        }

        private async void OnGenerateClicked(object? sender, RoutedEventArgs e)
        {
            string key = TxtApiKey.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                TxtStatus.Text = "Vui lòng nhập API Key từ BloxGen.";
                return;
            }

            BloxGenService.Default.SavedApiKey = key;

            string genType = GetSelectedTag(CmbGenType, "unverified");
            string continent = GetSelectedTag(CmbContinent, "RANDOM");
            string group = !string.IsNullOrWhiteSpace(TxtGroup.Text) ? TxtGroup.Text.Trim() : "AltGen";

            BloxGenService.Default.DefaultGenType = genType;
            BloxGenService.Default.DefaultContinent = continent;

            BtnGenerate.IsEnabled = false;
            TxtStatus.Text = "Đang kết nối tới BloxGen API để tạo tài khoản...";
            BrdResult.IsVisible = false;

            try
            {
                BloxGenAccountResult result = await BloxGenService.Default.GenerateAccountAsync(key, genType, continent).ConfigureAwait(true);
                if (result.Success && result.Account != null)
                {
                    bool imported = BloxGenService.Default.ImportAccountToShiro(result.Account, group);

                    TxtResultUser.Text = result.Account.Username;
                    TxtResultId.Text = result.Account.UserId > 0 ? result.Account.UserId.ToString() : "(Đang nạp)";
                    TxtResultPass.Text = !string.IsNullOrWhiteSpace(result.Account.Password) ? result.Account.Password : "(Không có)";
                    BrdResult.IsVisible = true;

                    TxtStatus.Text = imported
                        ? $"Tạo thành công! Đã tự động thêm vào nhóm '{group}' trong Shiro."
                        : "Tạo thành công từ BloxGen nhưng không thể lưu vào Shiro.";

                    ToastService.ShowSuccess("Tạo Alt thành công", $"Tài khoản {result.Account.Username} đã được thêm vào Shiro!");
                }
                else
                {
                    TxtStatus.Text = $"Thất bại: {result.ErrorMessage ?? "Lỗi không xác định"}";
                    ToastService.ShowError("Tạo Alt thất bại", result.ErrorMessage ?? "Không thể tạo tài khoản từ BloxGen.");
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Lỗi: {ex.Message}";
                ToastService.ShowError("Lỗi hệ thống", ex.Message);
            }
            finally
            {
                BtnGenerate.IsEnabled = true;
            }
        }

        private void OnOpenHistoryClicked(object? sender, RoutedEventArgs e)
        {
            try
            {
                string csvPath = Path.Combine(StoragePaths.DataDirectory, "altgen_history.csv");
                if (File.Exists(csvPath))
                {
                    Process.Start(new ProcessStartInfo(csvPath) { UseShellExecute = true });
                }
                else
                {
                    TxtStatus.Text = "Chưa có lịch sử tạo tài khoản.";
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Không thể mở file CSV: {ex.Message}";
            }
        }

        private void OnCloseClicked(object? sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
