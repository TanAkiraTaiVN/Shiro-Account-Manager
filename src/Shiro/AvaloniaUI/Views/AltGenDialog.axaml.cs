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
                TxtStatus.Text = Shiro.Services.LocalizationService.Get("Auto_AltGenDialogaxam_050");
                return;
            }

            BloxGenService.Default.SavedApiKey = key;

            string genType = GetSelectedTag(CmbGenType, "unverified");
            string continent = GetSelectedTag(CmbContinent, "RANDOM");
            string group = !string.IsNullOrWhiteSpace(TxtGroup.Text) ? TxtGroup.Text.Trim() : "AltGen";

            BloxGenService.Default.DefaultGenType = genType;
            BloxGenService.Default.DefaultContinent = continent;

            BtnGenerate.IsEnabled = false;
            TxtStatus.Text = Shiro.Services.LocalizationService.Get("Auto_AltGenDialogaxam_051");
            BrdResult.IsVisible = false;

            try
            {
                BloxGenAccountResult result = await BloxGenService.Default.GenerateAccountAsync(key, genType, continent).ConfigureAwait(true);
                if (result.Success && result.Account != null)
                {
                    bool imported = BloxGenService.Default.ImportAccountToShiro(result.Account, group);

                    TxtResultUser.Text = result.Account.Username;
                    TxtResultId.Text = result.Account.UserId > 0 ? result.Account.UserId.ToString() : Shiro.Services.LocalizationService.Get("Auto_AltGenDialogaxam_052");
                    TxtResultPass.Text = !string.IsNullOrWhiteSpace(result.Account.Password) ? result.Account.Password : Shiro.Services.LocalizationService.Get("Auto_AltGenDialogaxam_053");
                    BrdResult.IsVisible = true;

                    TxtStatus.Text = imported
                        ? Shiro.Services.LocalizationService.Format("Auto_AltGenDialogaxam_054", group)
                        : Shiro.Services.LocalizationService.Get("Auto_AltGenDialogaxam_055");

                    ToastService.ShowSuccess(Shiro.Services.LocalizationService.Get("Auto_AltGenDialogaxam_056"), Shiro.Services.LocalizationService.Format("Auto_AltGenDialogaxam_057", result.Account.Username));
                }
                else
                {
                    TxtStatus.Text = Shiro.Services.LocalizationService.Format("Auto_AltGenDialogaxam_059", result.ErrorMessage ?? Shiro.Services.LocalizationService.Get("Auto_AltGenDialogaxam_058"));
                    ToastService.ShowError(Shiro.Services.LocalizationService.Get("Auto_AltGenDialogaxam_061"), result.ErrorMessage ?? Shiro.Services.LocalizationService.Get("Auto_AltGenDialogaxam_060"));
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = Shiro.Services.LocalizationService.Format("Proxy_Dead", ex.Message);
                ToastService.ShowError(Shiro.Services.LocalizationService.Get("Auto_AltGenDialogaxam_062"), ex.Message);
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
                    TxtStatus.Text = Shiro.Services.LocalizationService.Get("Auto_AltGenDialogaxam_063");
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = Shiro.Services.LocalizationService.Format("Auto_AltGenDialogaxam_064", ex.Message);
            }
        }

        private void OnCloseClicked(object? sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
