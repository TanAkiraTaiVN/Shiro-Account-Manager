#nullable enable
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Shiro.AvaloniaUI.Views;

namespace Shiro.Services
{
    public class AvaloniaDialogService : IDialogService, IUserPrompt
    {
        private static readonly Lazy<AvaloniaDialogService> _defaultInstance = new(() => new AvaloniaDialogService());
        public static AvaloniaDialogService Default => _defaultInstance.Value;

        private static Window? GetActiveOwner() => MessageBoxDialog.GetActiveWindow();

        private static async Task ShowModalOrTopLevelAsync(Window dialog)
        {
            var owner = GetActiveOwner();
            if (MessageBoxDialog.IsValidDialogOwner(owner))
                await dialog.ShowDialog(owner!);
            else
                dialog.Show();
        }

        public async Task<bool> ConfirmAsync(string message, string title)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                return await Dispatcher.UIThread.InvokeAsync(() => ConfirmAsync(message, title));
            }
            var owner = GetActiveOwner();
            var res = await MessageBoxDialog.ShowAsync(message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, owner);
            return res == MessageBoxResult.Yes;
        }

        public async Task AlertAsync(string message, string title, PromptSeverity severity)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                await Dispatcher.UIThread.InvokeAsync(() => AlertAsync(message, title, severity));
                return;
            }
            var owner = GetActiveOwner();
            var icon = severity switch
            {
                PromptSeverity.Warning => MessageBoxIcon.Warning,
                PromptSeverity.Error => MessageBoxIcon.Error,
                _ => MessageBoxIcon.Information
            };
            await MessageBoxDialog.ShowAsync(message, title, MessageBoxButtons.OK, icon, owner);
        }

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

        public Task<string> ShowInputPromptAsync(string message, string title, string defaultValue = "")
        {
            var owner = GetActiveOwner();
            return InputDialog.PromptAsync(message, title, defaultValue, false, owner);
        }

        public void OpenFastLoginDialog(Account? account = null)
        {
            AppDispatcher.Post(async () =>
            {
                try
                {
                    var dialog = new FastLoginDialog(account!);
                    await ShowModalOrTopLevelAsync(dialog);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Error($"[AvaloniaDialogService.OpenFastLoginDialog] {ex}");
                }
            });
        }

        public void OpenFastLoginDialogBatch(System.Collections.Generic.IReadOnlyList<Account> accounts)
        {
            AppDispatcher.Post(async () =>
            {
                try
                {
                    var dialog = new FastLoginDialog(accounts);
                    await ShowModalOrTopLevelAsync(dialog);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Error($"[AvaloniaDialogService.OpenFastLoginDialogBatch] {ex}");
                }
            });
        }

        public void OpenBulkImportDialog()
        {
            AppDispatcher.Post(async () =>
            {
                try
                {
                    var dialog = new BulkImportDialog();
                    await ShowModalOrTopLevelAsync(dialog);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Error($"[AvaloniaDialogService.OpenBulkImportDialog] {ex}");
                }
            });
        }

        public void OpenAltGenDialog()
        {
            AppDispatcher.Post(async () =>
            {
                try
                {
                    var dialog = new AltGenDialog();
                    await ShowModalOrTopLevelAsync(dialog);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Error($"[AvaloniaDialogService.OpenAltGenDialog] {ex}");
                }
            });
        }

        public void OpenProxyManagerDialog()
        {
            AppDispatcher.Post(async () =>
            {
                try
                {
                    var dialog = new ProxyManagerDialog();
                    await ShowModalOrTopLevelAsync(dialog);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Error($"[AvaloniaDialogService.OpenProxyManagerDialog] {ex}");
                }
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

                    Nexus.NexusHost.Current ??= ViewModels.Dialogs.NexusViewModel.Default;
                    var window = new NexusWindow(ViewModels.Dialogs.NexusViewModel.Default);
                    window.Show();
                    window.Activate();
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Error($"Failed to open Nexus: {ex}");
                    ShowError(Shiro.Services.LocalizationService.Format("Dialog.NexusOpenFailed", ex.Message), Shiro.Services.LocalizationService.Get("Dialog.NexusErrorTitle"));
                }
            });
        }

        public void OpenAboutDialog()
        {
            AppDispatcher.Post(async () =>
            {
                try
                {
                    var dialog = new AboutDialog();
                    await ShowModalOrTopLevelAsync(dialog);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Error($"[AvaloniaDialogService.OpenAboutDialog] {ex}");
                }
            });
        }

        public void OpenAccountFieldsDialog(Account account)
        {
            if (account == null) return;
            AppDispatcher.Post(async () =>
            {
                try
                {
                    var dialog = new AccountFieldsDialog(account);
                    await ShowModalOrTopLevelAsync(dialog);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Error($"[AvaloniaDialogService.OpenAccountFieldsDialog] {ex}");
                }
            });
        }

        public void OpenAccountUtilsDialog(Account account)
        {
            if (account == null) return;
            AppDispatcher.Post(async () =>
            {
                try
                {
                    var dialog = new AccountUtilsDialog(account);
                    await ShowModalOrTopLevelAsync(dialog);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Error($"[AvaloniaDialogService.OpenAccountUtilsDialog] {ex}");
                }
            });
        }

        public void OpenMissingAssetsDialog(Account account, long[] assetIds)
        {
            if (account == null) return;
            AppDispatcher.Post(() =>
            {
                try
                {
                    var dialog = new MissingAssetsDialog(account, assetIds);
                    var owner = GetActiveOwner();
                    if (MessageBoxDialog.IsValidDialogOwner(owner))
                        dialog.Show(owner!);
                    else
                        dialog.Show();
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Error($"[AvaloniaDialogService.OpenMissingAssetsDialog] {ex}");
                }
            });
        }
    }
}
