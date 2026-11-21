#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Shiro.AvaloniaUI.Views;

namespace Shiro.Services
{
    public class AvaloniaDialogService : IDialogService, IUserPrompt
    {
        private static readonly Lazy<AvaloniaDialogService> _defaultInstance = new(() => new AvaloniaDialogService());
        private static readonly AsyncLocal<Window?> _testOwnerOverride = new();

        public static AvaloniaDialogService Default => _defaultInstance.Value;

        internal static IDisposable OverrideOwnerForTest(Window? owner)
        {
            var previous = _testOwnerOverride.Value;
            _testOwnerOverride.Value = owner;
            return new OwnerOverrideScope(previous);
        }

        private sealed class OwnerOverrideScope(Window? previous) : IDisposable
        {
            public void Dispose() => _testOwnerOverride.Value = previous;
        }

        private static Window? GetActiveOwner() => _testOwnerOverride.Value ?? MessageBoxDialog.GetActiveWindow();

        private static async Task ShowModalOrTopLevelAsync(Window dialog)
        {
            var owner = GetActiveOwner();
            if (MessageBoxDialog.IsValidDialogOwner(owner))
                await dialog.ShowDialog(owner!);
            else
                dialog.Show();
        }

        private static Task OpenModalDialogAsync<T>(Func<T> factory) where T : Window
            => ShowModalOrTopLevelAsync(factory());

        public async Task<bool> ConfirmAsync(string message, string title)
        {
            if (!MessageBoxDialog.IsAvaloniaUiReady())
                return await MessageBoxDialog.ShowAsync(message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, null) == MessageBoxResult.Yes;

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
            var icon = severity switch
            {
                PromptSeverity.Warning => MessageBoxIcon.Warning,
                PromptSeverity.Error => MessageBoxIcon.Error,
                _ => MessageBoxIcon.Information
            };

            if (!MessageBoxDialog.IsAvaloniaUiReady())
            {
                await MessageBoxDialog.ShowAsync(message, title, MessageBoxButtons.OK, icon, null);
                return;
            }

            if (!Dispatcher.UIThread.CheckAccess())
            {
                await Dispatcher.UIThread.InvokeAsync(() => AlertAsync(message, title, severity));
                return;
            }
            var owner = GetActiveOwner();
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

        public async Task<MessageBoxResult> ShowMessageBoxAsync(
            string message,
            string title = "Shiro",
            MessageBoxButtons buttons = MessageBoxButtons.OK,
            MessageBoxIcon icon = MessageBoxIcon.Information)
        {
            if (!MessageBoxDialog.IsAvaloniaUiReady())
                return await MessageBoxDialog.ShowAsync(message, title, buttons, icon, null);

            if (!Dispatcher.UIThread.CheckAccess())
                return await Dispatcher.UIThread.InvokeAsync(() => ShowMessageBoxAsync(message, title, buttons, icon));

            var owner = GetActiveOwner();
            return await MessageBoxDialog.ShowAsync(message, title, buttons, icon, owner);
        }

        public async Task<bool> ShowConfirmationAsync(string message, string title = "Shiro")
        {
            var res = await ShowMessageBoxAsync(message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            return res == MessageBoxResult.Yes;
        }

        public async Task<bool?> ShowYesNoCancelAsync(string message, string title = "Shiro")
        {
            var res = await ShowMessageBoxAsync(message, title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
            return res switch
            {
                MessageBoxResult.Yes => true,
                MessageBoxResult.No => false,
                _ => null
            };
        }

        public Task<string> ShowInputPromptAsync(string message, string title, string defaultValue = "")
            => InputDialog.PromptAsync(message, title, defaultValue, false, null);

        public Task<string> ShowSecretPromptAsync(string message, string title, string defaultValue = "")
            => InputDialog.PromptAsync(message, title, defaultValue, false, null, isSecret: true);

        public Task<Account?> SignInFastLoginAsync(Account? account, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<Account?>(cancellationToken);

            if (Avalonia.Application.Current?.ApplicationLifetime == null && _testOwnerOverride.Value == null)
            {
                OpenFastLoginDialog(account);
                return Task.FromResult<Account?>(null);
            }

            var tcs = new TaskCompletionSource<Account?>(TaskCreationOptions.RunContinuationsAsynchronously);
            FastLoginDialog? activeDialog = null;
            bool dialogClosed = false;

            void SafeCloseActiveDialog()
            {
                var dialogToClose = Volatile.Read(ref activeDialog);
                if (dialogToClose == null || Volatile.Read(ref dialogClosed))
                    return;

                try
                {
                    dialogToClose.Close(false);
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"[AvaloniaDialogService.SignInFastLoginAsync] Close on cancel threw: {ex.Message}");
                }
            }

            var reg = cancellationToken.Register(() =>
            {
                tcs.TrySetCanceled(cancellationToken);
                AppDispatcher.Post(SafeCloseActiveDialog);
            });

            AppDispatcher.PostTask(async () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    var dialog = new FastLoginDialog(account);
                    Volatile.Write(ref activeDialog, dialog);
                    dialog.Closed += (_, _) =>
                    {
                        Volatile.Write(ref dialogClosed, true);
                        tcs.TrySetResult(dialog.CreatedAccount);
                    };

                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                        SafeCloseActiveDialog();
                        return;
                    }

                    await ShowModalOrTopLevelAsync(dialog);
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Error($"[AvaloniaDialogService.SignInFastLoginAsync] {ex}");
                    tcs.TrySetException(ex);
                }
            }, nameof(SignInFastLoginAsync));

            _ = tcs.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                reg,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return tcs.Task;
        }

        public void OpenFastLoginDialog(Account? account = null)
            => AppDispatcher.PostTask(() => OpenModalDialogAsync(() => new FastLoginDialog(account)), nameof(OpenFastLoginDialog));

        public void OpenCookieImportDialog(Account? account = null)
            => AppDispatcher.PostTask(() => OpenModalDialogAsync(() => new FastLoginDialog(account, startInCookieMode: true)), nameof(OpenCookieImportDialog));

        public void OpenFastLoginDialogBatch(IReadOnlyList<Account> accounts)
            => AppDispatcher.PostTask(() => OpenModalDialogAsync(() => new FastLoginDialog(accounts)), nameof(OpenFastLoginDialogBatch));

        public void OpenBulkImportDialog()
            => AppDispatcher.PostTask(() => OpenModalDialogAsync(() => new BulkImportDialog()), nameof(OpenBulkImportDialog));

        public void OpenProxyManagerDialog()
            => AppDispatcher.PostTask(() => OpenModalDialogAsync(() => new ProxyManagerDialog()), nameof(OpenProxyManagerDialog));

        public void OpenAccountFieldsDialog(Account account)
        {
            if (account == null) return;
            AppDispatcher.PostTask(() => OpenModalDialogAsync(() => new AccountFieldsDialog(account)), nameof(OpenAccountFieldsDialog));
        }

        public void OpenAccountUtilsDialog(Account account)
        {
            if (account == null) return;
            AppDispatcher.PostTask(() => OpenModalDialogAsync(() => new AccountUtilsDialog(account)), nameof(OpenAccountUtilsDialog));
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
