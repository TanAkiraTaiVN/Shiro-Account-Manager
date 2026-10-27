using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RBX_Alt_Manager.AvaloniaUI.Views
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

                int count = AccountManager.AccountCount;
                TxtAccountCount.Text = $"{count} tài khoản";
            }
            catch { }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
