#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public enum MessageBoxButtons
    {
        OK,
        OKCancel,
        YesNo,
        YesNoCancel
    }

    public enum MessageBoxIcon
    {
        None,
        Information,
        Warning,
        Error,
        Question
    }

    public enum MessageBoxResult
    {
        None,
        OK,
        Cancel,
        Yes,
        No
    }

    public interface IDialogService
    {
        void ShowInfo(string message, string title = "Shiro");
        void ShowWarning(string message, string title = "Shiro");
        void ShowError(string message, string title = "Shiro");
        Task<MessageBoxResult> ShowMessageBoxAsync(
            string message,
            string title = "Shiro",
            MessageBoxButtons buttons = MessageBoxButtons.OK,
            MessageBoxIcon icon = MessageBoxIcon.Information);
        Task<bool> ShowConfirmationAsync(string message, string title = "Shiro");
        Task<bool?> ShowYesNoCancelAsync(string message, string title = "Shiro");
        Task<string> ShowInputPromptAsync(string message, string title, string defaultValue = "");
        Task<string> ShowSecretPromptAsync(string message, string title, string defaultValue = "")
            => ShowInputPromptAsync(message, title, defaultValue);
        void OpenFastLoginDialog(Account? account = null);
        Task<Account?> SignInFastLoginAsync(Account? account, CancellationToken cancellationToken = default)
        {
            OpenFastLoginDialog(account);
            return Task.FromResult<Account?>(null);
        }
        void OpenCookieImportDialog(Account? account = null) => OpenFastLoginDialog(account);
        void OpenFastLoginDialogBatch(IReadOnlyList<Account> accounts);
        void OpenBulkImportDialog();
        void OpenProxyManagerDialog();
        void OpenAccountFieldsDialog(Account account);
        void OpenAccountUtilsDialog(Account account);
        void OpenMissingAssetsDialog(Account account, long[] assetIds);
    }
}
