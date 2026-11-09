#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;

namespace Shiro.Services
{
    internal enum BackupLoadResult
    {
        NotAttempted,
        Restored,
        PromotedNeedsPassword,
        Failed
    }

    public sealed partial class AccountPersistenceService
    {
        private static readonly Lazy<AccountPersistenceService> _defaultInstance = new(() => new AccountPersistenceService());
        public static AccountPersistenceService Default => _defaultInstance.Value;

        private readonly AccountRepository _repository;
        private readonly Timer _deferredSaveTimer;

        internal AccountStore Store { get; set; }
        internal bool IsShutdownRequested { get; set; }
        internal DateTime StartTime { get; set; } = DateTime.Now;
        internal DateTime LastSaveTime { get; set; } = DateTime.MinValue;

        private string SaveFilePath => Store?.AccountFile ?? StoragePaths.AccountDataFile;
        private string? _lockedVaultFilePath;

        public bool IsVaultLockedPendingMasterPassword
        {
            get => !string.IsNullOrEmpty(_lockedVaultFilePath) &&
                   string.Equals(_lockedVaultFilePath, SaveFilePath, StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(SaveFilePath);
            internal set => _lockedVaultFilePath = value ? SaveFilePath : null;
        }

        private ReadOnlyMemory<byte> PasswordHash; // Store the hash after the data is successfully decrypted so we can encrypt again.
        private bool legacyPlaintextLoaded;
        private string PlaintextOptInFile => StoragePaths.GetUserFile("NoEncryption.IUnderstandTheRisks.iautamor");
        internal Func<string, string, Task<string?>>? TestPasswordPromptAsync { get; set; }

        private bool accountDataNeedsMigration;
        private byte[]? _lastSavedPlaintextHash;
        private readonly System.Threading.Lock saveLock = new();

        internal System.Threading.Lock AccountListLock => _repository.SyncRoot;

        internal AccountPersistenceService(AccountRepository? repository = null)
        {
            _repository = repository ?? AccountRepository.Default;
            Store = new AccountStore();
            _deferredSaveTimer = new Timer(_ =>
            {
                try { SaveAccounts(bypassRateLimit: false); }
                catch (Exception ex) { AppServices.Logger.Error($"[DeferredSave] Error saving accounts: {ex.Message}"); }
            }, null, Timeout.Infinite, Timeout.Infinite);
        }

        internal void ReplaceAccountsList(List<Account> accounts)
        {
            IReadOnlyList<Account> snapshot = _repository.ReplaceAll(accounts);
            AppServices.HostUiBridge.NotifyAccountsReplaced(snapshot);
        }

        private void RefreshView() =>
            AppServices.HostUiBridge.NotifyAccountsReplaced(_repository.GetAll());

        private void EnableModernDashboard()
        {
            RefreshView();
            if (AppServices.GeneralSettings.Get<bool>("ShowPresence"))
                AppServices.HostUiBridge.SchedulePresenceRefresh(null);
        }

        public bool IsDatabasePasswordEncrypted()
        {
            byte[] data = (Store ?? new AccountStore()).Read();
            if (data == null || data.Length == 0 || !Cryptography.IsEncrypted(data))
                return false;

            try
            {
                byte[] vaultKey = StoragePaths.GetOrCreateVaultKeyCopy();
                try { byte[] p = Cryptography.Decrypt(data, vaultKey); CryptographicOperations.ZeroMemory(p); return false; }
                finally { CryptographicOperations.ZeroMemory(vaultKey); }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[AccountPersistenceService] Primary key decrypt failed, trying recovery", ex);
                return VaultKeyRecoveryService.TryRecoverInProcess(data) == null;
            }
        }

        public async Task ResetEncryptionAsync(bool manualReset = false)
        {
            if (IsVaultLockedPendingMasterPassword)
            {
                AppServices.Dialogs.ShowWarning("Cannot change or reset database encryption while the vault is locked with a Master Password.");
                return;
            }

            if (manualReset)
            {
                string? newPass = TestPasswordPromptAsync != null
                    ? await TestPasswordPromptAsync(
                        LocalizationService.Get("AccountManager.EnterNewPasswordPrompt"),
                        LocalizationService.Get("AccountManager.ChangeDatabasePassword")).ConfigureAwait(false)
                    : await AppServices.Dialogs.ShowInputPromptAsync(
                        LocalizationService.Get("AccountManager.EnterNewPasswordPrompt"),
                        LocalizationService.Get("AccountManager.ChangeDatabasePassword"));

                if (newPass == "/UC" || newPass == null) return;

                if (string.IsNullOrWhiteSpace(newPass))
                {
                    PasswordHash = Array.Empty<byte>();
                    SaveAccounts(true, true);
                    AppServices.Dialogs.ShowInfo(LocalizationService.Get("AccountManager.SwitchedToDefaultVault"));
                }
                else
                {
                    if (newPass.Length < 8)
                    {
                        AppServices.Dialogs.ShowWarning(LocalizationService.Get("AccountManager.MasterPasswordMinLength"));
                        return;
                    }
                    byte[] hash = SHA512.HashData(Encoding.UTF8.GetBytes(newPass));
                    try { PasswordHash = new ReadOnlyMemory<byte>(ProtectedData.Protect(hash, Array.Empty<byte>(), DataProtectionScope.CurrentUser)); }
                    finally { CryptographicOperations.ZeroMemory(hash); }
                    SaveAccounts(true, true);
                    AppServices.Dialogs.ShowInfo(LocalizationService.Get("AccountManager.NewPasswordSet"));
                }
            }
        }

        public void ResetEncryption(bool manualReset = false)
        {
            _ = ResetEncryptionAsync(manualReset);
        }

        public bool LoadAccountsFromStore(byte[]? Hash = null)
        {
            LoadAccounts(Hash, out bool unlocked);
            return unlocked;
        }

        public void LoadAccounts(byte[]? Hash = null) => LoadAccounts(Hash, out _);

        public void LoadAccounts(byte[]? Hash, out bool unlocked)
        {
            unlocked = false;
            legacyPlaintextLoaded = false;
            IsVaultLockedPendingMasterPassword = false;
            bool EnteredPassword = false;
            byte[] Data = Store.Read();

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
                                AppServices.Logger?.Info($"[LoadAccounts] Loaded {_repository.Count} accounts via Master Password.");
                            }
                            else
                            {
                                AppServices.Logger?.Info($"[LoadAccounts] Loaded {_repository.Count} accounts via vault key.");
                            }
                        }
                        break;

                    case AccountDecodeStatus.NeedsMasterPassword:
                        IsVaultLockedPendingMasterPassword = true;
                        if (Hash != null)
                            AppServices.Logger?.Warn($"[LoadAccounts] Master Password decryption failed: {decodeResult.Error?.Message}");
                        else
                            AppServices.Logger?.Warn($"[LoadAccounts] Vault-key decryption failed: {decodeResult.Error?.Message}");

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

            if (!EnteredPassword && _repository.Count == 0 && File.Exists($"{SaveFilePath}.backup") && File.ReadAllBytes($"{SaveFilePath}.backup") is byte[] BackupData && BackupData.Length > 0)
            {
                var backupResult = TryLoadFromBackup(BackupData);
                if (backupResult == BackupLoadResult.PromotedNeedsPassword)
                {
                    IsVaultLockedPendingMasterPassword = true;
                    EnableModernDashboard();
                    return;
                }
            }

            try { AccountRuntimeStateStore.HydrateAccounts(_repository.GetAll()); }
            catch (Exception ex) { AppServices.Logger?.Debug($"[LoadAccounts] Runtime state hydration skipped: {ex.Message}"); }

            RefreshView();

            IReadOnlyList<Account> loadedAccounts = _repository.GetAll();
            if (loadedAccounts.Count > 0)
            {
                AppServices.SelectionState.LastValidAccount = loadedAccounts[0];
                foreach (Account account in loadedAccounts)
                {
                    if (account.LastUse > AppServices.SelectionState.LastValidAccount.LastUse)
                        AppServices.SelectionState.LastValidAccount = account;
                }
            }

            EnableModernDashboard();

            if (accountDataNeedsMigration)
            {
                try
                {
                    SaveAccounts(true, true);
                    accountDataNeedsMigration = false;
                    AppServices.Logger?.Info($"Migrated account data to schema {AccountDataSerializer.CurrentSchemaVersion}.");
                }
                catch (Exception exception) { AppServices.Logger?.Warn($"Account data migration will be retried after the next successful save: {exception.Message}"); }
            }
        }

        private AccountDecodeKeys CreateDecodeKeys(bool allowRecovery) => new(
            GetVaultKeyCopy: StoragePaths.GetOrCreateVaultKeyCopy,
            TryRecoverVaultKey: allowRecovery ? VaultKeyRecoveryService.TryRecoverInProcess : null,
            UnprotectDpapi: data => { byte[] plain = TryUnprotectAccountData(data, out bool legacyScope); return (plain, legacyScope); },
            PlaintextOptIn: File.Exists(PlaintextOptInFile));

        private static void EvictCorruptPrimaryBeforeBackupSave(string saveFilePath)
        {
            if (string.IsNullOrWhiteSpace(saveFilePath) || !File.Exists(saveFilePath)) return;
            try { File.Move(saveFilePath, $"{saveFilePath}.corrupt-{DateTime.Now:yyyyMMdd_HHmmss}.bak", overwrite: true); }
            catch (Exception ex) { AppServices.Logger?.Warn($"[TryLoadFromBackup] Could not move unreadable primary file before saving backup: {ex.Message}"); }
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
                    AppServices.Logger?.Info($"[LoadAccounts] Restored {_repository.Count} accounts from backup via vault key.");
                    if (_repository.Count > 0)
                    {
                        EvictCorruptPrimaryBeforeBackupSave(SaveFilePath);
                        SaveAccounts(true);
                    }
                    return BackupLoadResult.Restored;
                }

                AppServices.Logger?.Debug($"Direct vault key load from backup was not successful: {vaultResult.Error?.Message}");

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

                    if (_repository.Count > 0)
                    {
                        AppServices.Logger?.Info($"[AccountPersistenceService] Restored {_repository.Count} accounts from backup. Re-persisting primary vault...");
                        EvictCorruptPrimaryBeforeBackupSave(SaveFilePath);
                        SaveAccounts(true);
                    }
                    return BackupLoadResult.Restored;
                }

                AppServices.Logger?.Error($"Failed to load backup file: {unencResult.Error?.Message}");
                AppServices.Dialogs.ShowError("Failed to load backup file!");
                return BackupLoadResult.Failed;
            }

            return BackupLoadResult.NotAttempted;
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

        public Task SaveAccountsAsync(bool bypassRateLimit = false, bool bypassCountCheck = false)
            => Task.Run(() => SaveAccounts(bypassRateLimit, bypassCountCheck));

        public void SaveAccounts(bool bypassRateLimit = false, bool bypassCountCheck = false)
        {
            lock (saveLock)
            {
                if (IsVaultLockedPendingMasterPassword)
                {
                    AppServices.Logger?.Warn("[AccountPersistenceService.SaveAccounts] Save blocked because the vault is locked pending Master Password.");
                    return;
                }

                IReadOnlyList<Account> accountsSnapshot = _repository.GetAll();
                bool anyDirty = accountsSnapshot.Any(a => a.IsDirty);
                SaveGate gate = EvaluateSaveGate(DateTime.Now, StartTime, LastSaveTime, bypassRateLimit, bypassCountCheck, accountsSnapshot.Count, anyDirty);

                if (gate == SaveGate.Defer) { QueueDeferredSave(); return; }
                if (gate == SaveGate.Skip) return;

                LastSaveTime = DateTime.Now;
                var taken = TakeDirtyAccounts(accountsSnapshot);

                try
                {
                    Store ??= new AccountStore();
                    bool savePasswords = !AppServices.GeneralSettings.Exists("SavePasswords") || AppServices.GeneralSettings.Get<bool>("SavePasswords");
                    byte[] SaveData = AccountDataSerializer.Serialize(accountsSnapshot, savePasswords);

                    if (SaveData == null || SaveData.Length < 2)
                    {
                        AppServices.Logger?.Error("[AccountPersistenceService.SaveAccounts] Serialization produced empty or corrupted payload. Save aborted to protect data integrity.");
                        RestoreDirty(taken);
                        return;
                    }

                    try
                    {
                        byte[] currentHash = SHA256.HashData(SaveData);

                        if (!bypassRateLimit && _lastSavedPlaintextHash != null &&
                            currentHash.AsSpan().SequenceEqual(_lastSavedPlaintextHash) &&
                            File.Exists(Store.AccountFile))
                        {
                            return;
                        }

                        bool forceHistorical = IsShutdownRequested;

                        if (!PasswordHash.IsEmpty)
                        {
                            byte[] unprotectedHash = ProtectedData.Unprotect(PasswordHash.ToArray(), Array.Empty<byte>(), DataProtectionScope.CurrentUser);
                            try
                            {
                                byte[] encrypted = Cryptography.Encrypt(SaveData, unprotectedHash);
                                Store.WriteWithBackup(encrypted, forceHistorical);
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(unprotectedHash);
                            }
                        }
                        else
                        {
                            byte[] vaultKey = StoragePaths.GetOrCreateVaultKeyCopy();
                            try
                            {
                                byte[] encrypted = vaultKey.Length == 32
                                    ? Cryptography.EncryptWithRandomKey(SaveData, vaultKey)
                                    : Cryptography.Encrypt(SaveData, vaultKey);
                                Store.WriteWithBackup(encrypted, forceHistorical);
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
                                    AppServices.Logger.Warn($"Account data was protected with AES vault, but the legacy plaintext marker could not be removed: {exception.Message}");
                                }
                                legacyPlaintextLoaded = false;
                            }
                        }

                        _lastSavedPlaintextHash = currentHash;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(SaveData);
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Debug("[AccountPersistenceService] SaveAccountsAsync failed, restoring dirty state", ex);
                    RestoreDirty(taken);
                    throw;
                }
            }
        }
    }
}
