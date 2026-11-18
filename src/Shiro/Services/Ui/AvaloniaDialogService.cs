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
            var res = MessageBoxDialog.Show(message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, null);
            return res == MessageBoxResult.Yes;
        }

        public bool? ShowYesNoCancel(string message, string title = "Shiro")
        {
            var res = MessageBoxDialog.Show(message, title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information, null);
            return res switch
            {
                MessageBoxResult.Yes => true,
                MessageBoxResult.No => false,
                _ => null
            };
        }

        public string ShowInputPrompt(string message, string title, string defaultValue = "")
            => InputDialog.Prompt(message, title, defaultValue, false, null);

        public Task<string> ShowInputPromptAsync(string message, string title, string defaultValue = "")
            => InputDialog.PromptAsync(message, title, defaultValue, false, null);

        public Task<string> ShowSecretPromptAsync(string message, string title, string defaultValue = "")
            => InputDialog.PromptAsync(message, title, defaultValue, false, null, isSecret: true);

        public Task<Account?> SignInFastLoginAsync(Account? account, System.Threading.CancellationToken cancellationToken = default)
        {
            if (Avalonia.Application.Current?.ApplicationLifetime == null)
            {
                OpenFastLoginDialog(account);
                return Task.FromResult<Account?>(null);
            }

            var tcs = new TaskCompletionSource<Account?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            AppDispatcher.Post(async () =>
            {
                try
                {
                    var dialog = new FastLoginDialog(account);
                    dialog.Closed += (_, _) => tcs.TrySetResult(dialog.CreatedAccount);
                    await ShowModalOrTopLevelAsync(dialog);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Error($"[AvaloniaDialogService.SignInFastLoginAsync] {ex}");
                    tcs.TrySetException(ex);
                }
            });
            _ = tcs.Task.ContinueWith(_ => reg.Dispose(), System.Threading.Tasks.TaskScheduler.Default);
            return tcs.Task;
        }

        public void OpenFastLoginDialog(Account? account = null)
        {
            AppDispatcher.Post(async () =>
            {
                try
                {
                    var dialog = new FastLoginDialog(account);
                    await ShowModalOrTopLevelAsync(dialog);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Error($"[AvaloniaDialogService.OpenFastLoginDialog] {ex}");
                }
            });
        }

        public void OpenCookieImportDialog(Account? account = null)
        {
            AppDispatcher.Post(async () =>
            {
                try
                {
                    var dialog = new FastLoginDialog(account, startInCookieMode: true);
                    await ShowModalOrTopLevelAsync(dialog);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Error($"[AvaloniaDialogService.OpenCookieImportDialog] {ex}");
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
