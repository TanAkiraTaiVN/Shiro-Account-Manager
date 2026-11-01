#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shiro.Classes;

namespace Shiro.Services
{
    /// <summary>
    /// Persistence boundary providing atomic writes, fast backup recovery,
    /// and rotating historical backups for account data preservation.
    /// </summary>
    internal sealed class AccountStore
    {
        public const int MaxHistoricalBackups = 10;
        private static readonly TimeSpan HistoricalBackupInterval = TimeSpan.FromMinutes(5);

        private readonly string? _accountFile;
        private readonly string? _backupsDirectory;
        private DateTime _lastHistoricalBackupUtc = DateTime.MinValue;

        public AccountStore(string? accountFile = null, string? backupsDirectory = null)
        {
            _accountFile = accountFile;
            _backupsDirectory = backupsDirectory;
        }

        public string AccountFile => _accountFile ?? StoragePaths.AccountDataFile;
        public string BackupFile => AccountFile + ".backup";
        public string BackupsDirectory => _backupsDirectory ?? StoragePaths.BackupsDirectory;

        public byte[] Read()
        {
            if (!File.Exists(AccountFile))
                return Array.Empty<byte>();

            try
            {
                return File.ReadAllBytes(AccountFile);
            }
            catch (IOException exception)
            {
                // A second process, antivirus scanner or an interrupted
                // replacement can briefly hold the primary file. Preserve
                // startup by reading the last known-good backup when the
                // primary cannot be opened at all.
                Program.Logger?.Warn($"Could not read account data; trying backup: {exception.Message}");
                try
                {
                    return File.Exists(BackupFile) ? File.ReadAllBytes(BackupFile) : Array.Empty<byte>();
                }
                catch (Exception backupEx)
                {
                    Program.Logger?.Error($"Could not read backup account data: {backupEx.Message}");
                    return Array.Empty<byte>();
                }
            }
        }

        public void CreateBackup(byte[] previousData)
        {
            if (previousData == null || previousData.Length == 0)
                return;

            // 1. Immediate primary backup for fast fallback recovery
            StoragePaths.AtomicWriteBytes(BackupFile, previousData, durable: true);

            // 2. Rotating historical timestamped backup in Backups/ folder
            CreateHistoricalBackup(previousData);
        }

        public void CreateHistoricalBackup(byte[] previousData)
        {
            if (previousData == null || previousData.Length == 0)
                return;

            try
            {
                string dir = BackupsDirectory;
                Directory.CreateDirectory(dir);

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string backupPath = Path.Combine(dir, $"AccountData_{timestamp}.json.bak");

                StoragePaths.AtomicWriteBytes(backupPath, previousData, durable: true);
                _lastHistoricalBackupUtc = DateTime.UtcNow;

                PruneHistoricalBackups(dir);
            }
            catch (Exception ex)
            {
                try { Program.Logger?.Warn($"[AccountStore] Failed to create historical backup: {ex.Message}"); }
                catch (Exception) { }
            }
        }

        private static void PruneHistoricalBackups(string dir)
        {
            var backupFiles = Directory.GetFiles(dir, "AccountData_*.json.bak")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            if (backupFiles.Count > MaxHistoricalBackups)
            {
                foreach (var oldFile in backupFiles.Skip(MaxHistoricalBackups))
                {
                    try { oldFile.Delete(); }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// Performs an OS-level copy of the existing account file to <see cref="BackupFile"/>
        /// (without reading the previous payload into managed memory), throttles historical
        /// snapshots so rapid saves do not overwrite all <see cref="MaxHistoricalBackups"/> slots,
        /// and atomically writes the new <paramref name="data"/>.
        /// </summary>
        public void WriteWithBackup(byte[] data, bool forceHistoricalBackup = false)
        {
            string primaryPath = AccountFile;
            if (File.Exists(primaryPath))
            {
                try
                {
                    var info = new FileInfo(primaryPath);
                    if (info.Length > 0)
                    {
                        File.Copy(primaryPath, BackupFile, overwrite: true);

                        DateTime nowUtc = DateTime.UtcNow;
                        if (forceHistoricalBackup || (nowUtc - _lastHistoricalBackupUtc) >= HistoricalBackupInterval)
                        {
                            string dir = BackupsDirectory;
                            Directory.CreateDirectory(dir);
                            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                            string backupPath = Path.Combine(dir, $"AccountData_{timestamp}.json.bak");
                            File.Copy(primaryPath, backupPath, overwrite: true);
                            _lastHistoricalBackupUtc = nowUtc;
                            PruneHistoricalBackups(dir);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Warn($"[AccountStore] Pre-write OS backup copy warning: {ex.Message}");
                }
            }

            Write(data);
        }

        public IReadOnlyList<string> GetHistoricalBackups()
        {
            try
            {
                if (!Directory.Exists(BackupsDirectory))
                    return Array.Empty<string>();

                return Directory.GetFiles(BackupsDirectory, "AccountData_*.json.bak")
                    .OrderByDescending(f => new FileInfo(f).CreationTimeUtc)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public bool RestoreHistoricalBackup(string backupFilePath)
        {
            if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
                return false;

            try
            {
                byte[] data = File.ReadAllBytes(backupFilePath);
                if (data.Length == 0)
                    return false;

                // Create a safety backup of current before restoring
                byte[] current = Read();
                if (current.Length > 0)
                {
                    CreateBackup(current);
                }

                Write(data);
                return true;
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[AccountStore.RestoreHistoricalBackup] {ex}");
                return false;
            }
        }

        public void Write(byte[] data)
        {
            StoragePaths.AtomicWriteBytes(AccountFile, data ?? Array.Empty<byte>(), durable: true);
        }

        /// <summary>
        /// Exports account records to an encrypted vault file protected with the given passphrase.
        /// </summary>
        public void ExportVault(IReadOnlyList<Account> accounts, string passphrase, string destinationPath)
        {
            string? dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempFile = destinationPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (var fileStream = File.Create(tempFile))
                {
                    VaultExportService.ExportVault(accounts, passphrase, fileStream);
                }
                File.Move(tempFile, destinationPath, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch
                {
                    // Best-effort cleanup
                }
                throw;
            }
        }

        /// <summary>
        /// Imports account records from an encrypted vault file protected with the given passphrase.
        /// </summary>
        public IReadOnlyList<Account> ImportVault(string sourcePath, string passphrase)
        {
            using var fileStream = File.OpenRead(sourcePath);
            return VaultExportService.ImportVault(fileStream, passphrase);
        }
    }
}
