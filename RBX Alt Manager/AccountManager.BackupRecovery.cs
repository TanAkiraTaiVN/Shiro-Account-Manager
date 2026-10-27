#nullable enable
using System;
using System.IO;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager
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
                    statusMessage = $"Đã khôi phục thành công khóa vault.key và tải lại {AccountCount} tài khoản!";
                    return true;
                }
            }

            if (Instance.TryRestoreReadableBackup())
            {
                statusMessage = $"Đã khôi phục {AccountCount} tài khoản từ bản sao lưu hợp lệ gần nhất.";
                return true;
            }

            return false;
        }

        public bool TryRestoreReadableBackup()
        {
            string[] candidates =
            {
                $"{SaveFilePath}.backup",
                $"{SaveFilePath}.previous",
                $"{SaveFilePath}.backup.previous",
                $"{SaveFilePath}.bak"
            };

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
                        if (restored != null && restored.Count > 0)
                        {
                            // Preserve the locked primary file before replacing it
                            try
                            {
                                if (File.Exists(SaveFilePath))
                                    File.Copy(SaveFilePath, $"{SaveFilePath}.locked-{DateTime.Now:yyyyMMdd_HHmmss}.bak", overwrite: true);
                            }
                            catch (Exception ex)
                            {
                                Program.Logger?.Warn($"Could not create copy of locked primary account data: {ex.Message}");
                            }

                            ReplaceAccountsList(restored);
                            accountDataNeedsMigration |= needsMigration;
                            PasswordHash = Array.Empty<byte>();
                            SaveAccounts(true, true);
                            RefreshView();
                            EnableModernDashboard();
                            Program.Logger?.Info($"[LoadAccounts] Restored {restored.Count} accounts from '{path}'.");
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Candidate backup '{path}' could not be loaded: {ex.Message}");
                }
            }

            return false;
        }
    }
}
