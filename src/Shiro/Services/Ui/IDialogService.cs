#nullable enable
using System;

namespace Shiro.Services
{
    public interface IDialogService
    {
        void ShowInfo(string message, string title = "Shiro");
        void ShowWarning(string message, string title = "Shiro");
        void ShowError(string message, string title = "Shiro");
        bool ShowConfirmation(string message, string title = "Shiro");
        bool? ShowYesNoCancel(string message, string title = "Shiro");
        string ShowInputPrompt(string message, string title, string defaultValue = "");
        System.Threading.Tasks.Task<string> ShowInputPromptAsync(string message, string title, string defaultValue = "")
            => System.Threading.Tasks.Task.FromResult(ShowInputPrompt(message, title, defaultValue));
        void OpenFastLoginDialog(Account? account = null);
        System.Threading.Tasks.Task<Account?> SignInFastLoginAsync(Account? account, System.Threading.CancellationToken cancellationToken = default)
        {
            OpenFastLoginDialog(account);
            return System.Threading.Tasks.Task.FromResult<Account?>(null);
        }
        void OpenCookieImportDialog(Account? account = null) => OpenFastLoginDialog(account);
        void OpenFastLoginDialogBatch(System.Collections.Generic.IReadOnlyList<Account> accounts);
        void OpenBulkImportDialog();
        void OpenProxyManagerDialog();
        void OpenAboutDialog();
        void OpenAccountFieldsDialog(Account account);
        void OpenAccountUtilsDialog(Account account);
        void OpenMissingAssetsDialog(Account account, long[] assetIds);
    }
}
