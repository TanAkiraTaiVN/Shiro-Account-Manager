#nullable enable
using System;
using Shiro.Classes;

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
        void OpenFastLoginDialogBatch(System.Collections.Generic.IReadOnlyList<Account> accounts);
        void OpenBulkImportDialog();
        void OpenAltGenDialog();
        void OpenProxyManagerDialog();
        void OpenNexusWindow();
        void OpenAboutDialog();
        void OpenAccountFieldsDialog(Account account);
        void OpenAccountUtilsDialog(Account account);
        void OpenMissingAssetsDialog(Account account, long[] assetIds);
    }
}
