#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager
    {
        public static bool TryRecoverOrRestoreAccounts(out string statusMessage)
        {
            statusMessage = string.Empty;
            if (Instance == null)
                return false;

            if (VaultKeyRecoveryService.RunElevatedRecovery())
            {
                Instance.LoadAccounts(null, out bool unlocked);
                if (unlocked)
                {
                    statusMessage = Shiro.Services.LocalizationService.Format("AccountManager.VaultRestored", AccountCount);
                    return true;
                }
            }

            if (Instance.TryRestoreReadableBackup())
            {
                statusMessage = Shiro.Services.LocalizationService.Format("AccountManager.BackupRestored", AccountCount);
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
                string backupsDir = accountStore?.BackupsDirectory ?? StoragePaths.BackupsDirectory;
                if (Directory.Exists(backupsDir))
                {
                    candidates.AddRange(Directory.GetFiles(backupsDir, "AccountData_*.json.bak"));
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Could not enumerate historical backups: {ex.Message}");
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
                            decoded = Cryptography.Decrypt(raw, StoragePaths.GetOrCreateVaultKey());
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
                    Program.Logger?.Debug($"Candidate backup '{path}' could not be loaded: {ex.Message}");
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
                    Program.Logger?.Warn($"Could not move locked primary account data: {ex.Message}");
                }

                IsVaultLockedPendingMasterPassword = false;
                ReplaceAccountsList(bestRestored);
                accountDataNeedsMigration |= bestNeedsMigration;
                PasswordHash = Array.Empty<byte>();
                SaveAccounts(true, true);
                RefreshView();
                EnableModernDashboard();
                Program.Logger?.Info($"[LoadAccounts] Restored {bestRestored.Count} accounts from '{bestPath}'.");
                return true;
            }

            return false;
        }
    }
}
