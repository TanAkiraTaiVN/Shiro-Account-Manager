#nullable enable
using System;
using Avalonia.Controls;
using Avalonia.Threading;
using Shiro.AvaloniaUI.Views;
using Shiro.Classes;

namespace Shiro.Services
{
    public class AvaloniaDialogService : IDialogService
    {
        private static readonly Lazy<AvaloniaDialogService> _defaultInstance = new(() => new AvaloniaDialogService());
        public static AvaloniaDialogService Default => _defaultInstance.Value;

        private static Window? GetActiveOwner() => MessageBoxDialog.GetActiveWindow();

        public void ShowInfo(string message, string title = "Shiro")
        {
            AppDispatcher.Post(() =>
            {
                var owner = GetActiveOwner();
                _ = MessageBoxDialog.ShowAsync(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information, owner);
            });
        }

        public void ShowWarning(string message, string title = "Shiro")
        {
            AppDispatcher.Post(() =>
            {
                var owner = GetActiveOwner();
                _ = MessageBoxDialog.ShowAsync(message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning, owner);
            });
        }

        public void ShowError(string message, string title = "Shiro")
        {
            AppDispatcher.Post(() =>
            {
                var owner = GetActiveOwner();
                _ = MessageBoxDialog.ShowAsync(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error, owner);
            });
        }

        public bool ShowConfirmation(string message, string title = "Shiro")
        {
            var owner = GetActiveOwner();
            var res = MessageBoxDialog.Show(message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, owner);
            return res == MessageBoxResult.Yes;
        }

        public bool? ShowYesNoCancel(string message, string title = "Shiro")
        {
            var owner = GetActiveOwner();
            var res = MessageBoxDialog.Show(message, title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information, owner);
            return res switch
            {
                MessageBoxResult.Yes => true,
                MessageBoxResult.No => false,
                _ => null
            };
        }

        public string ShowInputPrompt(string message, string title, string defaultValue = "")
        {
            var owner = GetActiveOwner();
            return InputDialog.Prompt(message, title, defaultValue, false, owner);
        }

        public void OpenFastLoginDialog(Account? account = null)
        {
            AppDispatcher.Post(async () =>
            {
                var owner = GetActiveOwner();
                var dialog = new FastLoginDialog(account!);
                if (owner != null)
                    await dialog.ShowDialog(owner);
                else
                    dialog.Show();
            });
        }

        public void OpenFastLoginDialogBatch(System.Collections.Generic.IReadOnlyList<Account> accounts)
        {
            AppDispatcher.Post(async () =>
            {
                var owner = GetActiveOwner();
                var dialog = new FastLoginDialog(accounts);
                if (owner != null)
                    await dialog.ShowDialog(owner);
                else
                    dialog.Show();
            });
        }

        public void OpenBulkImportDialog()
        {
            AppDispatcher.Post(async () =>
            {
                var owner = GetActiveOwner();
                var dialog = new BulkImportDialog();
                if (owner != null)
                    await dialog.ShowDialog(owner);
                else
                    dialog.Show();
            });
        }

        public void OpenAltGenDialog()
        {
            AppDispatcher.Post(async () =>
            {
                var owner = GetActiveOwner();
                var dialog = new AltGenDialog();
                if (owner != null)
                    await dialog.ShowDialog(owner);
                else
                    dialog.Show();
            });
        }

        public void OpenProxyManagerDialog()
        {
            AppDispatcher.Post(async () =>
            {
                var owner = GetActiveOwner();
                var dialog = new ProxyManagerDialog();
                if (owner != null)
                    await dialog.ShowDialog(owner);
                else
                    dialog.Show();
            });
        }

        public void OpenNexusWindow()
        {
            AppDispatcher.Post(() =>
            {
                try
                {
                    if (NexusWindow.Instance != null)
                    {
                        if (NexusWindow.Instance.WindowState == WindowState.Minimized)
                            NexusWindow.Instance.WindowState = WindowState.Normal;
                        NexusWindow.Instance.Show();
                        NexusWindow.Instance.Activate();
                        return;
                    }

                    var window = new NexusWindow();
                    window.Show();
                    window.Activate();
                }
                catch (Exception ex)
                {
                    Program.Logger?.Error($"Failed to open Nexus: {ex}");
                    ShowError(Shiro.Services.LocalizationService.Format("Dialog.NexusOpenFailed", ex.Message), Shiro.Services.LocalizationService.Get("Dialog.NexusErrorTitle"));
                }
            });
        }

        public void OpenAboutDialog()
        {
            AppDispatcher.Post(async () =>
            {
                var owner = GetActiveOwner();
                var dialog = new AboutDialog();
                if (owner != null)
                    await dialog.ShowDialog(owner);
                else
                    dialog.Show();
            });
        }

        public void OpenAccountFieldsDialog(Account account)
        {
            if (account == null) return;
            AppDispatcher.Post(async () =>
            {
                var owner = GetActiveOwner();
                var dialog = new AccountFieldsDialog(account);
                if (owner != null)
                    await dialog.ShowDialog(owner);
                else
                    dialog.Show();
            });
        }

        public void OpenAccountUtilsDialog(Account account)
        {
            if (account == null) return;
            AppDispatcher.Post(async () =>
            {
                var owner = GetActiveOwner();
                var dialog = new AccountUtilsDialog(account);
                if (owner != null)
                    await dialog.ShowDialog(owner);
                else
                    dialog.Show();
            });
        }

        public void OpenMissingAssetsDialog(Account account, long[] assetIds)
        {
            if (account == null) return;
            AppDispatcher.Post(() =>
            {
                var dialog = new MissingAssetsDialog(account, assetIds);
                var owner = GetActiveOwner();
                if (owner != null)
                    dialog.Show(owner);
                else
                    dialog.Show();
            });
        }
    }
}
