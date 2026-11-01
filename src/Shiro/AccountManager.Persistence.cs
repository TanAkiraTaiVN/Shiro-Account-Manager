using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager
    {
        private static string SaveFilePath => StoragePaths.AccountDataFile;

        private static ReadOnlyMemory<byte> PasswordHash; // Store the hash after the data is successfully decrypted so we can encrypt again.
        // A legacy install could explicitly opt into plaintext account data
        // with this marker. Once such a file is read successfully, the next
        // save must migrate it to the normal DPAPI CurrentUser protection
        // instead of preserving plaintext indefinitely.
        private static bool legacyPlaintextLoaded;
        private static string PlaintextOptInFile => StoragePaths.GetUserFile("NoEncryption.IUnderstandTheRisks.iautamor");

        public static bool IsDatabasePasswordEncrypted()
        {
            byte[] data = new AccountStore().Read();
            if (data == null || data.Length == 0 || !Cryptography.IsEncrypted(data))
                return false;

            try
            {
                byte[] vaultKey = StoragePaths.GetOrCreateVaultKeyCopy();
                try
                {
                    _ = Cryptography.Decrypt(data, vaultKey);
                    return false;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(vaultKey);
                }
            }
            catch
            {
                byte[]? recovered = VaultKeyRecoveryService.TryRecoverInProcess(data);
                return recovered == null;
            }
        }

        public async Task ResetEncryptionAsync(bool manualReset = false)
        {
            if (manualReset)
            {
                string? newPass = await Shiro.AvaloniaUI.Views.InputDialog.PromptAsync(
                    "Nhập mật khẩu mới (để trống để dùng khóa Vault mặc định AES-256-GCM, được bảo vệ bằng Windows DPAPI):",
                    "Đổi mật khẩu cơ sở dữ liệu");

                if (newPass == "/UC" || newPass == null) return;

                if (string.IsNullOrWhiteSpace(newPass))
                {
                    PasswordHash = Array.Empty<byte>();
                    SaveAccounts(true, true);
                    AppServices.Dialogs.ShowInfo("Đã chuyển sang mã hóa AES-256-GCM với khóa Vault mặc định (không cần mật khẩu khi khởi động).");
                }
                else
                {
                    if (newPass.Length < 8)
                    {
                        AppServices.Dialogs.ShowWarning("Mật khẩu chính (Master Password) phải có ít nhất 8 ký tự để đảm bảo an toàn!");
                        return;
                    }
                    byte[] hash = SHA512.HashData(Encoding.UTF8.GetBytes(newPass));
                    try
                    {
                        PasswordHash = new ReadOnlyMemory<byte>(ProtectedData.Protect(hash, Array.Empty<byte>(), DataProtectionScope.CurrentUser));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(hash);
                    }
                    SaveAccounts(true, true);
                    AppServices.Dialogs.ShowInfo("Đã đặt mật khẩu mới thành công!");
                }
            }
        }

        public void ResetEncryption(bool manualReset = false)
        {
            _ = ResetEncryptionAsync(manualReset);
        }

        public static bool LoadAccountsFromStore(byte[]? Hash = null)
        {
            if (Instance == null)
                return false;

            Instance.LoadAccounts(Hash, out bool unlocked);
            return unlocked;
        }

        public void LoadAccounts(byte[]? Hash = null)
        {
            LoadAccounts(Hash, out _);
        }

        public void LoadAccounts(byte[]? Hash, out bool unlocked)
        {
            unlocked = false;
            legacyPlaintextLoaded = false;
            bool EnteredPassword = false;
            byte[] Data = accountStore.Read();

            lock (AccountListLock)
                AccountsList ??= new List<Account>();

            if (Data.Length > 0)
            {
                if (Cryptography.IsEncrypted(Data))
                {
                    if (Hash == null)
                    {
                        try
                        {
                            byte[] vaultKey = StoragePaths.GetOrCreateVaultKeyCopy();
                            byte[] decrypted;
                            try
                            {
                                decrypted = Cryptography.Decrypt(Data, vaultKey);
                            }
                            catch
                            {
                                byte[]? recoveredKey = VaultKeyRecoveryService.TryRecoverInProcess(Data);
                                if (recoveredKey == null)
                                    throw;
                                decrypted = Cryptography.Decrypt(Data, recoveredKey);
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(vaultKey);
                            }

                            ReplaceAccountsList(AccountDataSerializer.Deserialize(decrypted, out bool vaultNeedsMigration));
                            accountDataNeedsMigration |= vaultNeedsMigration;
                            EnteredPassword = true;
                            unlocked = true;
                            Program.Logger?.Info($"[LoadAccounts] Loaded {AccountCount} accounts via vault key.");
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Warn($"[LoadAccounts] Vault-key decryption failed: {ex.Message}");
                            EnableModernDashboard();
                            // Encrypted with Master Password or desynchronized vault.key
                            return;
                        }
                    }
                    else
                    {
                        try
                        {
                            Data = Cryptography.Decrypt(Data, Hash);
                            ReplaceAccountsList(AccountDataSerializer.Deserialize(Data, out bool passwordDataNeedsMigration));
                            accountDataNeedsMigration |= passwordDataNeedsMigration;
                            PasswordHash = new ReadOnlyMemory<byte>(ProtectedData.Protect(Hash, Array.Empty<byte>(), DataProtectionScope.CurrentUser));

                            EnteredPassword = true;
                            unlocked = true;
                            Program.Logger?.Info($"[LoadAccounts] Loaded {AccountCount} accounts via Master Password.");
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Warn($"[LoadAccounts] Master Password decryption failed: {ex.Message}");
                            EnableModernDashboard();
                            return;
                        }
                    }
                }
                else
                    try
                    {
                        byte[] decrypted = TryUnprotectAccountData(Data, out bool usedLegacyScope);
                        ReplaceAccountsList(AccountDataSerializer.Deserialize(decrypted, out bool dpapiDataNeedsMigration));
                        accountDataNeedsMigration |= dpapiDataNeedsMigration;
                        if (usedLegacyScope)
                        {
                            accountDataNeedsMigration = true;
                            Program.Logger.Info("Loaded account data protected with the legacy LocalMachine DPAPI scope; it will be migrated on the next save.");
                        }
                    }
                    catch (Exception e)
                    {
                        try
                        {
                            if (!File.Exists(PlaintextOptInFile))
                                throw new InvalidDataException("The account file could not be decrypted. Plaintext recovery is disabled unless the legacy opt-in marker is present.");

                            ReplaceAccountsList(AccountDataSerializer.Deserialize(Data, out bool plaintextNeedsMigration));
                            legacyPlaintextLoaded = true;
                            accountDataNeedsMigration = true;
                        }
                        catch
                        {
                            StoragePaths.AtomicWriteBytes(SaveFilePath + ".bak", Data, durable: true);

                            AppServices.Dialogs.ShowError($"Failed to load accounts!\nA backup file was created in case the data can be recovered.\n\n{e.Message}");
                        }
                    }
            }

            lock (AccountListLock)
                AccountsList ??= new List<Account>();

            if (!EnteredPassword && AccountCount == 0 && File.Exists($"{SaveFilePath}.backup") && File.ReadAllBytes($"{SaveFilePath}.backup") is byte[] BackupData && BackupData.Length > 0)
            {
                // Try the default vault key first: the backup is only "password-locked"
                // when it cannot be opened with the local vault key.
                if (Cryptography.IsEncrypted(BackupData))
                {
                    try
                    {
                        byte[] vaultKey = StoragePaths.GetOrCreateVaultKeyCopy();
                        byte[] vaultDecrypted;
                        try
                        {
                            vaultDecrypted = Cryptography.Decrypt(BackupData, vaultKey);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(vaultKey);
                        }
                        ReplaceAccountsList(AccountDataSerializer.Deserialize(vaultDecrypted, out bool vaultBackupNeedsMigration));
                        accountDataNeedsMigration |= vaultBackupNeedsMigration;
                        Program.Logger?.Info($"[LoadAccounts] Restored {AccountCount} accounts from backup via vault key.");
                        if (AccountCount > 0)
                            SaveAccounts(true);
                        goto BackupHandled;
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Direct vault key load from backup was not successful: {ex.Message}");
                    }
                }

                if (Cryptography.IsEncrypted(BackupData) && AppServices.Dialogs.ShowConfirmation("The existing backup file is password-locked, would you like to attempt to load it?"))
                {
                    if (File.Exists(SaveFilePath))
                    {
                        if (File.Exists($"{SaveFilePath}.old")) File.Delete($"{SaveFilePath}.old");

                        File.Move(SaveFilePath, $"{SaveFilePath}.old");
                    }

                    File.Move($"{SaveFilePath}.backup", SaveFilePath);

                    LoadAccounts();

                    return;
                }

                if (AppServices.Dialogs.ShowConfirmation("No accounts were loaded but there is a backup file, would you like to load the backup file?"))
                {
                    try
                    {
                        byte[] decoded = TryUnprotectAccountData(BackupData, out _);
                        ReplaceAccountsList(AccountDataSerializer.Deserialize(decoded, out bool backupNeedsMigration));
                        accountDataNeedsMigration |= backupNeedsMigration;
                    }
                    catch
                    {
                        try
                        {
                            if (!File.Exists(PlaintextOptInFile))
                                throw new InvalidDataException("Plaintext backup recovery is disabled unless the legacy opt-in marker is present.");

                            ReplaceAccountsList(AccountDataSerializer.Deserialize(BackupData, out bool plaintextBackupNeedsMigration));
                            legacyPlaintextLoaded = true;
                            accountDataNeedsMigration = true;
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Error($"Failed to load backup file: {ex.Message}");
                            AppServices.Dialogs.ShowError("Failed to load backup file!");
                        }

                        if (AccountCount > 0)
                        {
                            Program.Logger?.Info($"[AccountManager] Restored {AccountCount} accounts from backup. Re-persisting primary vault...");
                            SaveAccounts(true);
                        }
                    }
                }
            }

        BackupHandled:
            try
            {
                AccountRuntimeStateStore.HydrateAccounts(GetAccountsSnapshot());
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[LoadAccounts] Runtime state hydration skipped: {ex.Message}");
            }

            RefreshView();

            IReadOnlyList<Account> loadedAccounts = GetAccountsSnapshot();
            if (loadedAccounts.Count > 0)
            {
                LastValidAccount = loadedAccounts[0];

                foreach (Account account in loadedAccounts)
                    if (account.LastUse > LastValidAccount.LastUse)
                        LastValidAccount = account;
            }

            EnableModernDashboard();

            if (accountDataNeedsMigration)
            {
                try
                {
                    SaveAccounts(true, true);
                    accountDataNeedsMigration = false;
                    Program.Logger?.Info($"Migrated account data to schema {AccountDataSerializer.CurrentSchemaVersion}.");
                }
                catch (Exception exception)
                {
                    Program.Logger?.Warn($"Account data migration will be retried after the next successful save: {exception.Message}");
                }
            }
        }

        private static byte[]? _lastSavedPlaintextHash;

        public static Task SaveAccountsAsync(bool BypassRateLimit = false, bool BypassCountCheck = false)
            => Task.Run(() => SaveAccounts(BypassRateLimit, BypassCountCheck));

        public static void SaveAccounts(bool BypassRateLimit = false, bool BypassCountCheck = false)
        {
            IReadOnlyList<Account> accountsSnapshot = GetAccountsSnapshot();
            if ((!BypassRateLimit && (DateTime.Now - startTime).TotalSeconds < 2) ||
                (!BypassRateLimit && (DateTime.Now - lastSaveTime).TotalSeconds < 1.5) ||
                (!BypassCountCheck && accountsSnapshot.Count == 0)) return;

            if (!BypassRateLimit && !BypassCountCheck && accountsSnapshot.All(a => !a.IsDirty))
                return;

            accountStore ??= new AccountStore();

            lock (saveLock)
            {
                lastSaveTime = DateTime.Now;
                bool savePasswords = General == null ||
                    !General.Exists("SavePasswords") ||
                    General.Get<bool>("SavePasswords");
                byte[] SaveData = AccountDataSerializer.Serialize(accountsSnapshot, savePasswords);

                if (SaveData == null || SaveData.Length < 2)
                {
                    Program.Logger?.Error("[AccountManager.SaveAccounts] Serialization produced empty or corrupted payload. Save aborted to protect data integrity.");
                    return;
                }

                try
                {
                    byte[] currentHash = System.Security.Cryptography.SHA256.HashData(SaveData);

                    if (!BypassRateLimit &&
                        _lastSavedPlaintextHash != null &&
                        currentHash.AsSpan().SequenceEqual(_lastSavedPlaintextHash) &&
                        File.Exists(accountStore.AccountFile))
                    {
                        foreach (var acc in accountsSnapshot)
                        {
                            acc.IsDirty = false;
                        }
                        return;
                    }

                    bool forceHistorical = Instance?.shutdownRequested == true;

                    if (!PasswordHash.IsEmpty)
                    {
                        byte[] unprotectedHash = ProtectedData.Unprotect(PasswordHash.ToArray(), Array.Empty<byte>(), DataProtectionScope.CurrentUser);
                        try
                        {
                            byte[] encrypted = Cryptography.Encrypt(SaveData, unprotectedHash);
                            accountStore.WriteWithBackup(encrypted, forceHistorical);
                        }
                        finally
                        {
                            System.Security.Cryptography.CryptographicOperations.ZeroMemory(unprotectedHash);
                        }
                    }
                    else
                    {
                        byte[] vaultKey = StoragePaths.GetOrCreateVaultKeyCopy();
                        try
                        {
                            byte[] encrypted = Cryptography.Encrypt(SaveData, vaultKey);
                            accountStore.WriteWithBackup(encrypted, forceHistorical);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(vaultKey);
                        }

                        if (legacyPlaintextLoaded || File.Exists(PlaintextOptInFile))
                        {
                            try { File.Delete(PlaintextOptInFile); }
                            catch (Exception exception)
                            {
                                Program.Logger.Warn($"Account data was protected with AES vault, but the legacy plaintext marker could not be removed: {exception.Message}");
                            }
                            legacyPlaintextLoaded = false;
                        }
                    }

                    _lastSavedPlaintextHash = currentHash;

                    foreach (var acc in accountsSnapshot)
                    {
                        acc.IsDirty = false;
                    }
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(SaveData);
                }
            }
        }

        private static readonly Timer _deferredSaveTimer = new Timer(_ =>
        {
            try { SaveAccounts(BypassRateLimit: false); }
            catch (Exception ex) { Program.Logger.Error($"[DeferredSave] Error saving accounts: {ex.Message}"); }
        }, null, Timeout.Infinite, Timeout.Infinite);

        public static void QueueDeferredSave()
        {
            try
            {
                _deferredSaveTimer.Change(1500, Timeout.Infinite);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"Failed to schedule deferred account save: {ex.Message}");
            }
        }

        private static byte[] TryUnprotectAccountData(byte[] data, out bool usedLegacyScope)
        {
            usedLegacyScope = false;
            try
            {
                return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                byte[] legacy = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.LocalMachine);
                usedLegacyScope = true;
                return legacy;
            }
        }
    }
}
