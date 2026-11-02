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
    internal enum BackupLoadResult
    {
        NotAttempted,
        Restored,
        PromotedNeedsPassword,
        Failed
    }

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
                    try { PasswordHash = new ReadOnlyMemory<byte>(ProtectedData.Protect(hash, Array.Empty<byte>(), DataProtectionScope.CurrentUser)); }
                    finally { CryptographicOperations.ZeroMemory(hash); }
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
            if (Instance == null) return false;
            Instance.LoadAccounts(Hash, out bool unlocked);
            return unlocked;
        }

        public void LoadAccounts(byte[]? Hash = null) => LoadAccounts(Hash, out _);

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
                var decodeResult = AccountFileDecoder.Decode(Data, Hash, CreateDecodeKeys(allowRecovery: true));
                switch (decodeResult.Status)
                {
                    case AccountDecodeStatus.Loaded:
                        ReplaceAccountsList(decodeResult.Accounts);
                        accountDataNeedsMigration |= decodeResult.NeedsMigration;
                        legacyPlaintextLoaded = decodeResult.LegacyPlaintext;

                        if (Cryptography.IsEncrypted(Data))
                        {
                            EnteredPassword = true;
                            unlocked = true;

                            if (Hash != null)
                            {
                                PasswordHash = new ReadOnlyMemory<byte>(ProtectedData.Protect(Hash, Array.Empty<byte>(), DataProtectionScope.CurrentUser));
                                Program.Logger?.Info($"[LoadAccounts] Loaded {AccountCount} accounts via Master Password.");
                            }
                            else
                            {
                                Program.Logger?.Info($"[LoadAccounts] Loaded {AccountCount} accounts via vault key.");
                            }
                        }
                        break;

                    case AccountDecodeStatus.NeedsMasterPassword:
                        if (Hash != null)
                            Program.Logger?.Warn($"[LoadAccounts] Master Password decryption failed: {decodeResult.Error?.Message}");
                        else
                            Program.Logger?.Warn($"[LoadAccounts] Vault-key decryption failed: {decodeResult.Error?.Message}");

                        EnableModernDashboard();
                        return;

                    case AccountDecodeStatus.Corrupt:
                        StoragePaths.AtomicWriteBytes(SaveFilePath + ".bak", Data, durable: true);
                        AppServices.Dialogs.ShowError($"Failed to load accounts!\nA backup file was created in case the data can be recovered.\n\n{decodeResult.Error?.Message}");
                        break;

                    case AccountDecodeStatus.Empty:
                        break;
                }
            }

            lock (AccountListLock)
                AccountsList ??= new List<Account>();

            if (!EnteredPassword && AccountCount == 0 && File.Exists($"{SaveFilePath}.backup") && File.ReadAllBytes($"{SaveFilePath}.backup") is byte[] BackupData && BackupData.Length > 0)
            {
                var backupResult = TryLoadFromBackup(BackupData);
                if (backupResult == BackupLoadResult.PromotedNeedsPassword)
                {
                    EnableModernDashboard();
                    return;
                }
            }

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

        private AccountDecodeKeys CreateDecodeKeys(bool allowRecovery)
        {
            return new AccountDecodeKeys(
                GetVaultKeyCopy: StoragePaths.GetOrCreateVaultKeyCopy,
                TryRecoverVaultKey: allowRecovery ? VaultKeyRecoveryService.TryRecoverInProcess : null,
                UnprotectDpapi: data =>
                {
                    byte[] plain = TryUnprotectAccountData(data, out bool legacyScope);
                    return (plain, legacyScope);
                },
                PlaintextOptIn: File.Exists(PlaintextOptInFile));
        }

        internal BackupLoadResult TryLoadFromBackup(byte[] backupData)
        {
            if (backupData == null || backupData.Length == 0)
                return BackupLoadResult.NotAttempted;

            if (Cryptography.IsEncrypted(backupData))
            {
                var vaultResult = AccountFileDecoder.Decode(backupData, null, CreateDecodeKeys(allowRecovery: false));
                if (vaultResult.Status == AccountDecodeStatus.Loaded)
                {
                    ReplaceAccountsList(vaultResult.Accounts);
                    accountDataNeedsMigration |= vaultResult.NeedsMigration;
                    Program.Logger?.Info($"[LoadAccounts] Restored {AccountCount} accounts from backup via vault key.");
                    if (AccountCount > 0)
                        SaveAccounts(true);
                    return BackupLoadResult.Restored;
                }

                Program.Logger?.Debug($"Direct vault key load from backup was not successful: {vaultResult.Error?.Message}");

                if (vaultResult.Status == AccountDecodeStatus.NeedsMasterPassword)
                {
                    if (AppServices.Dialogs.ShowConfirmation("The existing backup file is password-locked, would you like to attempt to load it?"))
                    {
                        PromoteBackupToPrimary(SaveFilePath, DateTime.Now);
                        return BackupLoadResult.PromotedNeedsPassword;
                    }

                    return BackupLoadResult.NotAttempted;
                }

                return BackupLoadResult.Failed;
            }

            if (AppServices.Dialogs.ShowConfirmation("No accounts were loaded but there is a backup file, would you like to load the backup file?"))
            {
                var unencResult = AccountFileDecoder.Decode(backupData, null, CreateDecodeKeys(allowRecovery: false));
                if (unencResult.Status == AccountDecodeStatus.Loaded)
                {
                    ReplaceAccountsList(unencResult.Accounts);
                    accountDataNeedsMigration |= unencResult.NeedsMigration;
                    legacyPlaintextLoaded = unencResult.LegacyPlaintext;

                    if (AccountCount > 0)
                    {
                        Program.Logger?.Info($"[AccountManager] Restored {AccountCount} accounts from backup. Re-persisting primary vault...");
                        SaveAccounts(true);
                    }
                    return BackupLoadResult.Restored;
                }

                Program.Logger?.Error($"Failed to load backup file: {unencResult.Error?.Message}");
                AppServices.Dialogs.ShowError("Failed to load backup file!");
                return BackupLoadResult.Failed;
            }

            return BackupLoadResult.NotAttempted;
        }

        internal static string PromoteBackupToPrimary(string saveFilePath, DateTime now)
        {
            string preservedPrimaryPath = string.Empty;
            if (File.Exists(saveFilePath))
            {
                string baseCandidate = $"{saveFilePath}.old.{now:yyyyMMddHHmmss}";
                preservedPrimaryPath = baseCandidate;
                if (File.Exists(preservedPrimaryPath))
                {
                    int counter = 1;
                    while (File.Exists($"{baseCandidate}.{counter}")) counter++;
                    preservedPrimaryPath = $"{baseCandidate}.{counter}";
                }
                File.Move(saveFilePath, preservedPrimaryPath);
            }

            string backupPath = $"{saveFilePath}.backup";
            if (File.Exists(backupPath))
            {
                try { File.Move(backupPath, saveFilePath); }
                catch (Exception moveEx)
                {
                    Program.Logger?.Error($"[PromoteBackupToPrimary] Failed to move backup to primary: {moveEx.Message}. Rolling back preserved primary...");
                    if (!string.IsNullOrEmpty(preservedPrimaryPath) && File.Exists(preservedPrimaryPath))
                    {
                        try { File.Move(preservedPrimaryPath, saveFilePath); }
                        catch (Exception rollbackEx) { Program.Logger?.Error($"[PromoteBackupToPrimary] Rollback failed: {rollbackEx.Message}"); }
                    }
                    throw;
                }
            }

            return preservedPrimaryPath;
        }

        public enum SaveGate { Proceed, Defer, Skip }

        public static SaveGate EvaluateSaveGate(DateTime now, DateTime startTime, DateTime lastSaveTime,
            bool bypassRateLimit, bool bypassCountCheck, int accountCount, bool anyDirty)
        {
            if (!bypassCountCheck && accountCount == 0) return SaveGate.Skip;
            if (!bypassRateLimit && !bypassCountCheck && !anyDirty) return SaveGate.Skip;
            if (!bypassRateLimit && ((now - startTime).TotalSeconds < 2 || (now - lastSaveTime).TotalSeconds < 1.5)) return SaveGate.Defer;
            return SaveGate.Proceed;
        }

        public static List<Account> TakeDirtyAccounts(IReadOnlyList<Account> accounts)
        {
            var taken = new List<Account>();
            if (accounts == null) return taken;
            for (int i = 0; i < accounts.Count; i++)
            {
                var acc = accounts[i];
                if (acc != null && acc.IsDirty)
                {
                    acc.IsDirty = false;
                    taken.Add(acc);
                }
            }
            return taken;
        }

        public static void RestoreDirty(IEnumerable<Account> accounts)
        {
            if (accounts == null) return;
            foreach (var acc in accounts)
                if (acc != null) acc.IsDirty = true;
        }

        private static byte[]? _lastSavedPlaintextHash;

        public static Task SaveAccountsAsync(bool BypassRateLimit = false, bool BypassCountCheck = false)
            => Task.Run(() => SaveAccounts(BypassRateLimit, BypassCountCheck));

        public static void SaveAccounts(bool BypassRateLimit = false, bool BypassCountCheck = false)
        {
            lock (saveLock)
            {
                IReadOnlyList<Account> accountsSnapshot = GetAccountsSnapshot();
                bool anyDirty = accountsSnapshot.Any(a => a.IsDirty);
                SaveGate gate = EvaluateSaveGate(DateTime.Now, startTime, lastSaveTime, BypassRateLimit, BypassCountCheck, accountsSnapshot.Count, anyDirty);

                if (gate == SaveGate.Defer) { QueueDeferredSave(); return; }
                if (gate == SaveGate.Skip) return;

                lastSaveTime = DateTime.Now;
                var taken = TakeDirtyAccounts(accountsSnapshot);

                try
                {
                    accountStore ??= new AccountStore();
                    bool savePasswords = General == null || !General.Exists("SavePasswords") || General.Get<bool>("SavePasswords");
                    byte[] SaveData = AccountDataSerializer.Serialize(accountsSnapshot, savePasswords);

                    if (SaveData == null || SaveData.Length < 2)
                    {
                        Program.Logger?.Error("[AccountManager.SaveAccounts] Serialization produced empty or corrupted payload. Save aborted to protect data integrity.");
                        RestoreDirty(taken);
                        return;
                    }

                    try
                    {
                        byte[] currentHash = System.Security.Cryptography.SHA256.HashData(SaveData);

                        if (!BypassRateLimit && _lastSavedPlaintextHash != null &&
                            currentHash.AsSpan().SequenceEqual(_lastSavedPlaintextHash) &&
                            File.Exists(accountStore.AccountFile))
                        {
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
                    }
                    finally
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(SaveData);
                    }
                }
                catch (Exception)
                {
                    RestoreDirty(taken);
                    throw;
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
            try { _deferredSaveTimer.Change(1500, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Program.Logger?.Warn($"Failed to schedule deferred account save: {ex.Message}"); }
        }

        private static byte[] TryUnprotectAccountData(byte[] data, out bool usedLegacyScope)
        {
            usedLegacyScope = false;
            try { return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser); }
            catch (CryptographicException)
            {
                byte[] legacy = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.LocalMachine);
                usedLegacyScope = true;
                return legacy;
            }
        }
    }
}
