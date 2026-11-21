#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        public static AccountPersistenceService Default => AppServices.AccountPersistence;

        private readonly AccountRepository _repository;
        private readonly System.Threading.Lock _deferredTimerLock = new();
        private Timer? _deferredSaveTimer;
        private volatile bool _isShutdownRequested;

        internal AccountStore Store { get; set; }
        internal bool IsShutdownRequested
        {
            get => _isShutdownRequested;
            set => _isShutdownRequested = value;
        }
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
        private byte[]? _pendingBackupRestoreData;
        private readonly System.Threading.Lock saveLock = new();

        internal System.Threading.Lock AccountListLock => _repository.SyncRoot;

        internal AccountPersistenceService(AccountRepository? repository = null)
        {
            _repository = repository ?? (AppServices.AccountRepository as AccountRepository)
                ?? throw new InvalidOperationException("AccountRepository is not registered.");
            Store = new AccountStore();
        }

        private void OnDeferredSaveTimer(object? state)
        {
            if (IsShutdownRequested)
                return;

            try { SaveAccounts(bypassRateLimit: false); }
            catch (Exception ex) { AppServices.Logger.Error($"[DeferredSave] Error saving accounts: {ex.Message}"); }
        }

        internal void ReplaceAccountsList(List<Account> accounts)
        {
            _ = _repository.ReplaceAll(accounts);
        }

        private void EnableModernDashboard()
        {
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

        public bool LoadAccountsFromStore(byte[]? Hash = null)
        {
            LoadAccounts(Hash, out bool unlocked);
            return unlocked;
        }

        public void LoadAccounts(byte[]? Hash = null) => LoadAccounts(Hash, out _);

        public void LoadAccounts(byte[]? Hash, out bool unlocked)
        {
            lock (saveLock)
            {
                unlocked = false;
                legacyPlaintextLoaded = false;
                _pendingBackupRestoreData = null;
                bool EnteredPassword = false;
                Store ??= new AccountStore();
                byte[] Data = Store.Read();

                if (Data.Length > 0)
                {
                    var decodeResult = AccountFileDecoder.Decode(Data, Hash, CreateDecodeKeys(allowRecovery: true));
                    switch (decodeResult.Status)
                    {
                        case AccountDecodeStatus.Loaded:
                            IsVaultLockedPendingMasterPassword = false;
                            ReplaceAccountsList(decodeResult.Accounts);
                            accountDataNeedsMigration |= decodeResult.NeedsMigration;
                            legacyPlaintextLoaded = decodeResult.LegacyPlaintext;

                            if (Cryptography.IsEncrypted(Data))
                            {
                                EnteredPassword = true;
                                unlocked = true;

                                if (Hash != null)
                                {
                                    SetProtectedPasswordHash(Hash);
                                    AppServices.Logger?.Info($"[LoadAccounts] Loaded {_repository.Count} accounts via Master Password.");
                                }
                                else
                                {
                                    ClearPasswordHash();
                                    AppServices.Logger?.Info($"[LoadAccounts] Loaded {_repository.Count} accounts via vault key.");
                                }
                            }
                            else
                            {
                                ClearPasswordHash();
                            }
                            break;

                        case AccountDecodeStatus.NeedsMasterPassword:
                            ClearPasswordHash();
                            IsVaultLockedPendingMasterPassword = true;
                            if (Hash != null)
                                AppServices.Logger?.Warn($"[LoadAccounts] Master Password decryption failed: {decodeResult.Error?.Message}");
                            else
                                AppServices.Logger?.Warn($"[LoadAccounts] Vault-key decryption failed: {decodeResult.Error?.Message}");

                            EnableModernDashboard();
                            return;

                        case AccountDecodeStatus.Corrupt:
                            IsVaultLockedPendingMasterPassword = false;
                            string quarantinePath = $"{SaveFilePath}.corrupt-{DateTime.UtcNow:yyyyMMdd_HHmmss}.bak";
                            StoragePaths.AtomicWriteBytes(quarantinePath, Data, durable: true);
                            AppServices.Dialogs.ShowError($"Failed to load accounts!\nA backup file was created in case the data can be recovered.\n\n{decodeResult.Error?.Message}");
                            break;

                        case AccountDecodeStatus.Empty:
                            IsVaultLockedPendingMasterPassword = false;
                            break;
                    }
                }
                else
                {
                    IsVaultLockedPendingMasterPassword = false;
                }

                var backupResult = BackupLoadResult.NotAttempted;
                if (!EnteredPassword && _repository.Count == 0 && File.Exists($"{SaveFilePath}.backup") && File.ReadAllBytes($"{SaveFilePath}.backup") is byte[] BackupData && BackupData.Length > 0)
                {
                    (backupResult, string? promptMsg) = TryRestoreBackupWithoutPromptLocked(BackupData);
                    if (promptMsg != null)
                    {
                        _pendingBackupRestoreData = BackupData;
                    }
                    else if (backupResult == BackupLoadResult.PromotedNeedsPassword)
                    {
                        IsVaultLockedPendingMasterPassword = true;
                        EnableModernDashboard();
                        return;
                    }
                }

                if (backupResult != BackupLoadResult.Restored)
                    HydrateRuntimeStateAndSelectionLocked();

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

        internal async Task<BackupLoadResult> TryLoadFromBackupAsync(byte[] backupData)
        {
            if (backupData == null || backupData.Length == 0)
                return BackupLoadResult.NotAttempted;

            BackupLoadResult initialResult;
            string? promptMessage;
            lock (saveLock)
            {
                (initialResult, promptMessage) = TryRestoreBackupWithoutPromptLocked(backupData);
            }

            if (promptMessage == null)
                return initialResult;

            if (!await AppServices.Dialogs.ShowConfirmationAsync(promptMessage))
                return BackupLoadResult.NotAttempted;

            lock (saveLock)
            {
                return CompleteConfirmedBackupRestoreLocked(backupData);
            }
        }

        private (BackupLoadResult Result, string? PromptMessage) TryRestoreBackupWithoutPromptLocked(byte[] backupData)
        {
            if (backupData == null || backupData.Length == 0)
                return (BackupLoadResult.NotAttempted, null);

            if (Cryptography.IsEncrypted(backupData))
            {
                var vaultResult = AccountFileDecoder.Decode(backupData, null, CreateDecodeKeys(allowRecovery: false));
                if (vaultResult.Status == AccountDecodeStatus.Loaded)
                {
                    RestoreDecodedAccountsLocked(vaultResult, logSource: "backup via vault key");
                    return (BackupLoadResult.Restored, null);
                }

                AppServices.Logger?.Debug($"Direct vault key load from backup was not successful: {vaultResult.Error?.Message}");
                return vaultResult.Status == AccountDecodeStatus.NeedsMasterPassword
                    ? (BackupLoadResult.NotAttempted, "The existing backup file is password-locked, would you like to attempt to load it?")
                    : (BackupLoadResult.Failed, null);
            }

            return (BackupLoadResult.NotAttempted, "No accounts were loaded but there is a backup file, would you like to load the backup file?");
        }

        private BackupLoadResult CompleteConfirmedBackupRestoreLocked(byte[] backupData)
        {
            _pendingBackupRestoreData = null;
            if (Cryptography.IsEncrypted(backupData))
            {
                PromoteBackupToPrimary(SaveFilePath, DateTime.Now);
                IsVaultLockedPendingMasterPassword = true;
                EnableModernDashboard();
                return BackupLoadResult.PromotedNeedsPassword;
            }

            var unencResult = AccountFileDecoder.Decode(backupData, null, CreateDecodeKeys(allowRecovery: false));
            if (unencResult.Status == AccountDecodeStatus.Loaded)
            {
                legacyPlaintextLoaded = unencResult.LegacyPlaintext;
                RestoreDecodedAccountsLocked(unencResult, logSource: "backup");
                return BackupLoadResult.Restored;
            }

            AppServices.Logger?.Error($"Failed to load backup file: {unencResult.Error?.Message}");
            AppServices.Dialogs.ShowError("Failed to load backup file!");
            return BackupLoadResult.Failed;
        }

        private void RestoreDecodedAccountsLocked(AccountDecodeResult result, string logSource)
        {
            IsVaultLockedPendingMasterPassword = false;
            ClearPasswordHash();
            ReplaceAccountsList(result.Accounts);
            HydrateRuntimeStateAndSelectionLocked();
            accountDataNeedsMigration |= result.NeedsMigration;
            AppServices.Logger?.Info($"[LoadAccounts] Restored {_repository.Count} accounts from {logSource}.");
            if (_repository.Count > 0)
            {
                EvictCorruptPrimaryBeforeBackupSave(SaveFilePath);
                SaveAccounts(true);
            }
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

        private static void ClearDirtyAccounts(List<Account> accounts)
        {
            for (int i = 0; i < accounts.Count; i++)
                accounts[i].IsDirty = false;
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

                var dirtyAccounts = accountsSnapshot.Where(a => a.IsDirty).ToList();

                try
                {
                    Store ??= new AccountStore();
                    bool savePasswords = !AppServices.GeneralSettings.Exists("SavePasswords") || AppServices.GeneralSettings.Get<bool>("SavePasswords");
                    byte[] SaveData = AccountDataSerializer.Serialize(accountsSnapshot, savePasswords);

                    if (SaveData == null || SaveData.Length < 2)
                    {
                        AppServices.Logger?.Error("[AccountPersistenceService.SaveAccounts] Serialization produced empty or corrupted payload. Save aborted to protect data integrity.");
                        return;
                    }

                    try
                    {
                        byte[] currentHash = SHA256.HashData(SaveData);

                        if (!bypassRateLimit && _lastSavedPlaintextHash != null &&
                            currentHash.AsSpan().SequenceEqual(_lastSavedPlaintextHash) &&
                            File.Exists(Store.AccountFile))
                        {
                            ClearDirtyAccounts(dirtyAccounts);
                            LastSaveTime = DateTime.Now;
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
                        LastSaveTime = DateTime.Now;
                        ClearDirtyAccounts(dirtyAccounts);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(SaveData);
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Debug("[AccountPersistenceService] SaveAccounts failed, dirty state preserved", ex);
                    throw;
                }
            }
        }
    }
}
