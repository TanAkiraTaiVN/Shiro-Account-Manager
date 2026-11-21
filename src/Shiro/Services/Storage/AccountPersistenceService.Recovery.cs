#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class AccountPersistenceService
    {
        private static readonly byte[] Entropy = new byte[] { 0x52, 0x4f, 0x42, 0x4c, 0x4f, 0x58, 0x20, 0x41, 0x43, 0x43, 0x4f, 0x55, 0x4e, 0x54, 0x20, 0x4d, 0x41, 0x4e, 0x41, 0x47, 0x45, 0x52, 0x20, 0x7c, 0x20, 0x3a, 0x29, 0x20, 0x7c, 0x20, 0x42, 0x52, 0x4f, 0x55, 0x47, 0x48, 0x54, 0x20, 0x54, 0x4f, 0x20, 0x59, 0x4f, 0x55, 0x20, 0x42, 0x55, 0x59, 0x20, 0x69, 0x63, 0x33, 0x77, 0x30, 0x6c, 0x66 };

        public void QueueDeferredSave()
        {
            try { _deferredSaveTimer.Change(1500, Timeout.Infinite); }
            catch (ObjectDisposedException) { /* timer disposed during shutdown */ }
            catch (Exception ex) { AppServices.Logger?.Warn($"Failed to schedule deferred account save: {ex.Message}"); }
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

        public bool TryRecoverOrRestoreAccounts(out string statusMessage)
        {
            statusMessage = string.Empty;

            if (TryRestoreReadableBackup())
            {
                statusMessage = LocalizationService.Format("AccountManager.BackupRestored", _repository.Count);
                return true;
            }

            return false;
        }

        public bool TryRestoreReadableBackup()
        {
            lock (saveLock)
            {
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var candidates = new List<(string Path, int Priority, DateTime LastWriteUtc)>();

                void AddCandidate(string path, int priority)
                {
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && seenPaths.Add(path))
                    {
                        DateTime lastWriteUtc = DateTime.MinValue;
                        try { lastWriteUtc = File.GetLastWriteTimeUtc(path); }
                        catch (IOException ex) { AppServices.Logger?.Debug($"Could not stat backup candidate '{path}': {ex.Message}"); }
                        catch (UnauthorizedAccessException ex) { AppServices.Logger?.Debug($"Could not stat backup candidate '{path}': {ex.Message}"); }
                        candidates.Add((path, priority, lastWriteUtc));
                    }
                }

                AddCandidate($"{SaveFilePath}.backup", 0);
                AddCandidate($"{SaveFilePath}.previous", 1);
                AddCandidate($"{SaveFilePath}.backup.previous", 2);
                AddCandidate($"{SaveFilePath}.bak", 3);

                try
                {
                    string backupsDir = Store?.BackupsDirectory ?? StoragePaths.BackupsDirectory;
                    if (Directory.Exists(backupsDir))
                    {
                        foreach (string histPath in Directory.GetFiles(backupsDir, "AccountData_*.json.bak"))
                        {
                            AddCandidate(histPath, 4);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"Could not enumerate historical backups: {ex.Message}");
                }

                candidates.Sort((a, b) =>
                {
                    int timeCmp = b.LastWriteUtc.CompareTo(a.LastWriteUtc);
                    if (timeCmp != 0) return timeCmp;
                    int prioCmp = a.Priority.CompareTo(b.Priority);
                    if (prioCmp != 0) return prioCmp;
                    return StringComparer.OrdinalIgnoreCase.Compare(b.Path, a.Path);
                });

                List<Account>? bestRestored = null;
                bool bestNeedsMigration = false;
                string bestPath = string.Empty;
                DateTime bestWriteUtc = DateTime.MinValue;
                int bestPriority = int.MaxValue;

                foreach (var candidate in candidates)
                {
                    if (bestRestored != null)
                    {
                        if (candidate.LastWriteUtc < bestWriteUtc)
                            break;
                        if (candidate.LastWriteUtc == bestWriteUtc && candidate.Priority > bestPriority)
                            break;
                    }

                    string path = candidate.Path;
                    try
                    {
                        byte[] raw = File.ReadAllBytes(path);
                        if (raw.Length == 0)
                            continue;

                        byte[]? decoded = null;
                        try
                        {
                            if (Cryptography.IsEncrypted(raw))
                            {
                                try
                                {
                                    byte[] keyCopy = StoragePaths.GetOrCreateVaultKeyCopy();
                                    try
                                    {
                                        decoded = Cryptography.Decrypt(raw, keyCopy);
                                    }
                                    finally
                                    {
                                        CryptographicOperations.ZeroMemory(keyCopy);
                                    }
                                }
                                catch (Exception decryptEx)
                                {
                                    AppServices.Logger?.Debug($"Primary vault key could not decrypt backup '{path}': {decryptEx.Message}");
                                    byte[]? recoveredKey = VaultKeyRecoveryService.TryRecoverInProcess(raw);
                                    if (recoveredKey == null)
                                        throw;

                                    try
                                    {
                                        decoded = Cryptography.Decrypt(raw, recoveredKey);
                                    }
                                    finally
                                    {
                                        CryptographicOperations.ZeroMemory(recoveredKey);
                                    }
                                }
                            }
                            else
                            {
                                decoded = TryUnprotectAccountData(raw, out _);
                            }

                            if (decoded != null && decoded.Length > 2)
                            {
                                var restored = AccountDataSerializer.Deserialize(decoded, out bool needsMigration);
                                if (restored != null && restored.Count > 0 &&
                                    (bestRestored == null || restored.Count > bestRestored.Count))
                                {
                                    bestRestored = restored;
                                    bestNeedsMigration = needsMigration;
                                    bestPath = path;
                                    bestWriteUtc = candidate.LastWriteUtc;
                                    bestPriority = candidate.Priority;
                                }
                            }
                        }
                        finally
                        {
                            if (decoded != null)
                            {
                                CryptographicOperations.ZeroMemory(decoded);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppServices.Logger?.Debug($"Candidate backup '{path}' could not be loaded: {ex.Message}");
                    }
                }

                if (bestRestored != null && bestRestored.Count > 0)
                {
                    // Move the locked/corrupt primary file out of SaveFilePath before SaveAccounts
                    // so AccountStore.WriteWithBackup does not copy the locked/corrupt file over AccountData.json.backup.
                    try
                    {
                        if (File.Exists(SaveFilePath))
                            File.Move(SaveFilePath, $"{SaveFilePath}.locked-{DateTime.Now:yyyyMMdd_HHmmss}.bak", overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        AppServices.Logger?.Warn($"Could not move locked primary account data: {ex.Message}");
                    }

                    IsVaultLockedPendingMasterPassword = false;
                    ReplaceAccountsList(bestRestored);
                    HydrateRuntimeStateAndSelectionLocked();
                    accountDataNeedsMigration |= bestNeedsMigration;
                    ClearPasswordHash();
                    SaveAccounts(true, true);
                    AppServices.Logger?.Info($"[LoadAccounts] Restored {bestRestored.Count} accounts from '{bestPath}'.");
                    return true;
                }

                return false;
            }
        }

        internal void HydrateRuntimeStateAndSelectionLocked()
        {
            IReadOnlyList<Account> loadedAccounts = _repository.GetAll();
            try { AccountRuntimeStateStore.HydrateAccounts(loadedAccounts); }
            catch (Exception ex) { AppServices.Logger?.Debug($"[LoadAccounts] Runtime state hydration skipped: {ex.Message}"); }

            if (loadedAccounts.Count > 0)
            {
                Account best = loadedAccounts[0];
                for (int i = 1; i < loadedAccounts.Count; i++)
                {
                    if (loadedAccounts[i].LastUse > best.LastUse)
                        best = loadedAccounts[i];
                }
                AppServices.SelectionState.LastValidAccount = best;
            }

            EnableModernDashboard();
        }

        internal async Task<bool> ResolvePendingBackupRestoreAsync()
        {
            byte[]? pending;
            lock (saveLock)
            {
                pending = _pendingBackupRestoreData;
                _pendingBackupRestoreData = null;
                if (pending == null || pending.Length == 0 || _repository.Count > 0)
                    return false;
            }

            BackupLoadResult result = await TryLoadFromBackupAsync(pending);
            return result == BackupLoadResult.Restored;
        }

        internal void SetProtectedPasswordHash(byte[] hash)
        {
            byte[] protectedHash = ProtectedData.Protect(hash, Array.Empty<byte>(), DataProtectionScope.CurrentUser);
            lock (saveLock)
            {
                PasswordHash = new ReadOnlyMemory<byte>(protectedHash);
            }
        }

        internal void ClearPasswordHash()
        {
            lock (saveLock)
            {
                PasswordHash = ReadOnlyMemory<byte>.Empty;
            }
        }

        internal string PromoteBackupToPrimary(string saveFilePath, DateTime now)
        {
            string backupPath = $"{saveFilePath}.backup";
            if (!File.Exists(backupPath))
                return string.Empty;

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

            try { File.Move(backupPath, saveFilePath, overwrite: true); }
            catch (Exception moveEx)
            {
                AppServices.Logger?.Error($"[PromoteBackupToPrimary] Failed to move backup to primary: {moveEx.Message}. Rolling back preserved primary...");
                if (!string.IsNullOrEmpty(preservedPrimaryPath) && File.Exists(preservedPrimaryPath))
                {
                    try { File.Move(preservedPrimaryPath, saveFilePath, overwrite: true); }
                    catch (Exception rollbackEx) { AppServices.Logger?.Error($"[PromoteBackupToPrimary] Rollback failed: {rollbackEx.Message}"); }
                }
                throw;
            }

            return preservedPrimaryPath;
        }
    }
}
