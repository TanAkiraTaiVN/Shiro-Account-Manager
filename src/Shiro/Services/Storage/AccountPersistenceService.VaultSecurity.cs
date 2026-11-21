#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class AccountPersistenceService
    {
        public async Task ResetEncryptionAsync(bool manualReset = false)
        {
            if (IsVaultLockedPendingMasterPassword)
            {
                AppServices.Dialogs.ShowWarning("Cannot change or reset database encryption while the vault is locked with a Master Password.");
                return;
            }

            if (!manualReset)
                return;

            string? newPass = TestPasswordPromptAsync != null
                ? await TestPasswordPromptAsync(
                    LocalizationService.Get("AccountManager.EnterNewPasswordPrompt"),
                    LocalizationService.Get("AccountManager.ChangeDatabasePassword")).ConfigureAwait(false)
                : await AppServices.Dialogs.ShowSecretPromptAsync(
                    LocalizationService.Get("AccountManager.EnterNewPasswordPrompt"),
                    LocalizationService.Get("AccountManager.ChangeDatabasePassword"));

            if (newPass == "/UC" || newPass == null)
                return;

            if (newPass.Length == 0)
            {
                SwitchToDefaultVaultEncryption();
                AppServices.Dialogs.ShowInfo(LocalizationService.Get("AccountManager.SwitchedToDefaultVault"));
                return;
            }

            if (string.IsNullOrWhiteSpace(newPass) || newPass.Length < 8)
            {
                AppServices.Dialogs.ShowWarning(LocalizationService.Get("AccountManager.MasterPasswordMinLength"));
                return;
            }

            ApplyNewMasterPassword(newPass);
            AppServices.Dialogs.ShowInfo(LocalizationService.Get("AccountManager.NewPasswordSet"));
        }

        public void ResetEncryption(bool manualReset = false)
        {
            _ = ObserveResetEncryptionAsync(manualReset);
        }

        private async Task ObserveResetEncryptionAsync(bool manualReset)
        {
            try
            {
                await ResetEncryptionAsync(manualReset).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Error($"[AccountPersistenceService] ResetEncryption failed: {ex.Message}");
                try
                {
                    AppServices.Dialogs?.ShowError(ex.Message);
                }
                catch (Exception dialogEx)
                {
                    AppServices.Logger?.Debug($"[AccountPersistenceService] Failed to show ResetEncryption error dialog: {dialogEx.Message}");
                }
            }
        }

        private void SwitchToDefaultVaultEncryption()
        {
            byte[]? provisionedKey = null;
            try
            {
                ExecutePasswordTransition("switch vault to default key", () =>
                {
                    provisionedKey = StoragePaths.EnsureVaultKeyForUnlockedVaultCopy();
                    ClearPasswordHash();
                    SaveAccounts(true, true);
                });
            }
            finally
            {
                if (provisionedKey != null)
                {
                    CryptographicOperations.ZeroMemory(provisionedKey);
                }
            }
        }

        private void ApplyNewMasterPassword(string newPass)
        {
            byte[] passBytes = Encoding.UTF8.GetBytes(newPass);
            byte[] hash = SHA512.HashData(passBytes);
            try
            {
                ExecutePasswordTransition("apply new Master Password", () =>
                {
                    SetProtectedPasswordHash(hash);
                    SaveAccounts(true, true);
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passBytes);
                CryptographicOperations.ZeroMemory(hash);
            }
        }

        private void ExecutePasswordTransition(string operationName, Action applyAndSave)
        {
            ReadOnlyMemory<byte> previousHash = SnapshotProtectedPasswordHash();
            try
            {
                applyAndSave();
            }
            catch (Exception ex)
            {
                RestoreProtectedPasswordHash(previousHash);
                AppServices.Logger?.Error($"[AccountPersistenceService] Failed to {operationName}: {ex.Message}");
                throw;
            }
        }

        private ReadOnlyMemory<byte> SnapshotProtectedPasswordHash()
        {
            lock (saveLock)
            {
                return PasswordHash;
            }
        }

        private void RestoreProtectedPasswordHash(ReadOnlyMemory<byte> previousHash)
        {
            lock (saveLock)
            {
                PasswordHash = previousHash;
            }
        }
    }
}
