#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

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
            var candidates = new List<string>
            {
                $"{SaveFilePath}.backup",
                $"{SaveFilePath}.previous",
                $"{SaveFilePath}.backup.previous",
                $"{SaveFilePath}.bak"
            };

            try
            {
                string backupsDir = Store?.BackupsDirectory ?? StoragePaths.BackupsDirectory;
                if (Directory.Exists(backupsDir))
                {
                    candidates.AddRange(Directory.GetFiles(backupsDir, "AccountData_*.json.bak"));
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Could not enumerate historical backups: {ex.Message}");
            }

            List<Account>? bestRestored = null;
            bool bestNeedsMigration = false;
            string bestPath = string.Empty;

            foreach (string path in candidates)
            {
                if (!File.Exists(path))
                    continue;

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
                        else
                        {
                            decoded = TryUnprotectAccountData(raw, out _);
                        }

                        if (decoded != null && decoded.Length > 2)
                        {
                            var restored = AccountDataSerializer.Deserialize(decoded, out bool needsMigration);
                            if (restored != null && restored.Count > (bestRestored?.Count ?? 0))
                            {
                                bestRestored = restored;
                                bestNeedsMigration = needsMigration;
                                bestPath = path;
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
                accountDataNeedsMigration |= bestNeedsMigration;
                ClearPasswordHash();
                SaveAccounts(true, true);
                RefreshView();
                EnableModernDashboard();
                AppServices.Logger?.Info($"[LoadAccounts] Restored {bestRestored.Count} accounts from '{bestPath}'.");
                return true;
            }

            return false;
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
            }

            return preservedPrimaryPath;
        }
    }
}
