#nullable enable
using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public partial class AboutDialog : Window
    {
        public AboutDialog()
        {
            InitializeComponent();
            LoadMetrics();
        }

        private void LoadMetrics()
        {
            try
            {
                TxtRuntime.Text = $".NET {Environment.Version.Major}.0 (x64)";

                using var proc = Process.GetCurrentProcess();
                proc.Refresh();
                long memMb = proc.WorkingSet64 / (1024 * 1024);
                TxtMemory.Text = $"{memMb} MB (Working Set)";

                int count = AppServices.AccountRepository.Count;
                TxtAccountCount.Text = $"{count} tài khoản";
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"LoadMetrics threw: {ex.Message}");
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
