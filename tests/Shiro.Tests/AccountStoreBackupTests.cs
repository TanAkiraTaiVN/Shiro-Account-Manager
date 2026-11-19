using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Shiro.Services;

namespace Shiro.Tests
{
    public class AccountStoreBackupTests : IDisposable
    {
        private readonly string _testDir;
        private readonly string _testAccountFile;
        private readonly string _testBackupsDir;

        public AccountStoreBackupTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "Shiro_Test_Backups_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
            _testAccountFile = Path.Combine(_testDir, "AccountData.json");
            _testBackupsDir = Path.Combine(_testDir, "Backups");
            Directory.CreateDirectory(_testBackupsDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testDir))
                    Directory.Delete(_testDir, recursive: true);
            }
            catch (IOException) { }
        }

        [Fact]
        public void StoragePaths_BackupsDirectory_IsNotNullOrEmpty()
        {
            string dir = StoragePaths.BackupsDirectory;
            Assert.False(string.IsNullOrWhiteSpace(dir));
            Assert.Contains("Backups", dir);
        }

        [Fact]
        public void AccountStore_CreateHistoricalBackup_WritesTimestampedBackup()
        {
            var store = new AccountStore(_testAccountFile, _testBackupsDir);
            byte[] dummyData = Encoding.UTF8.GetBytes("{\"test\":\"data_" + Guid.NewGuid().ToString("N") + "\"}");

            store.CreateHistoricalBackup(dummyData);

            var backups = store.GetHistoricalBackups();
            Assert.NotEmpty(backups);

            string latest = backups.First();
            Assert.True(File.Exists(latest));
            Assert.EndsWith(".json.bak", latest);

            byte[] readBack = File.ReadAllBytes(latest);
            Assert.Equal(dummyData, readBack);
        }

        [Fact]
        public void AccountStore_RestoreHistoricalBackup_RestoresDataCorrectly()
        {
            var store = new AccountStore(_testAccountFile, _testBackupsDir);
            string uniqueToken = Guid.NewGuid().ToString("N");
            byte[] originalData = Encoding.UTF8.GetBytes("{\"restored_token\":\"" + uniqueToken + "\"}");

            string tempBackup = Path.Combine(_testBackupsDir, $"AccountData_test_restore_{uniqueToken}.json.bak");
            StoragePaths.AtomicWriteBytes(tempBackup, originalData, durable: true);

            bool ok = store.RestoreHistoricalBackup(tempBackup);
            Assert.True(ok);

            byte[] current = store.Read();
            Assert.Equal(originalData, current);
        }

        [Fact]
        public void AccountStore_RestoreHistoricalBackup_EdgeCases()
        {
            var store = new AccountStore(_testAccountFile, _testBackupsDir);
            Assert.False(store.RestoreHistoricalBackup(null!));
            Assert.False(store.RestoreHistoricalBackup(string.Empty));
            Assert.False(store.RestoreHistoricalBackup("nonexistent.bak"));

            string emptyFile = Path.Combine(_testBackupsDir, "empty.bak");
            File.WriteAllBytes(emptyFile, Array.Empty<byte>());
            Assert.False(store.RestoreHistoricalBackup(emptyFile));

            // Test restoring when primary already has data (covers safety backup creation branch)
            byte[] initial = Encoding.UTF8.GetBytes("{\"initial\":\"data\"}");
            store.Write(initial);

            byte[] newPayload = Encoding.UTF8.GetBytes("{\"new\":\"payload\"}");
            string validBackup = Path.Combine(_testBackupsDir, "AccountData_valid.json.bak");
            File.WriteAllBytes(validBackup, newPayload);

            Assert.True(store.RestoreHistoricalBackup(validBackup));
            Assert.Equal(newPayload, store.Read());
        }

        [Fact]
        public void AccountStore_WriteWithBackup_WithForceHistoricalBackup()
        {
            var store = new AccountStore(_testAccountFile, _testBackupsDir);
            byte[] firstData = Encoding.UTF8.GetBytes("{\"v\":1}");
            store.Write(firstData);

            byte[] secondData = Encoding.UTF8.GetBytes("{\"v\":2}");
            store.WriteWithBackup(secondData, forceHistoricalBackup: true);

            Assert.Equal(secondData, store.Read());
            Assert.True(File.Exists(store.BackupFile));
            Assert.NotEmpty(store.GetHistoricalBackups());
        }

        [Fact]
        public void AccountStore_ExportAndImportVault_RoundTripsSuccessfully()
        {
            var store = new AccountStore(_testAccountFile, _testBackupsDir);
            string vaultPath = Path.Combine(_testDir, "test_vault.shiro");
            var accounts = new[]
            {
                new Account { Username = "VaultUser1", SecurityToken = "tok1", UserID = 101 },
                new Account { Username = "VaultUser2", SecurityToken = "tok2", UserID = 102 }
            };

            store.ExportVault(accounts, "SuperSecret123!", vaultPath);
            Assert.True(File.Exists(vaultPath));

            var imported = store.ImportVault(vaultPath, "SuperSecret123!");
            Assert.Equal(2, imported.Count);
            Assert.Contains(imported, a => a.Username == "VaultUser1" && a.UserID == 101);
            Assert.Contains(imported, a => a.Username == "VaultUser2" && a.UserID == 102);
        }

        [Fact]
        public void AccountStore_PruneHistoricalBackups_EnforcesMaxLimit()
        {
            var store = new AccountStore(_testAccountFile, _testBackupsDir);

            // Create 15 fake historical backup files
            for (int i = 0; i < 15; i++)
            {
                string path = Path.Combine(_testBackupsDir, $"AccountData_{20260101 + i}_120000.json.bak");
                File.WriteAllText(path, $"data-{i}");
                File.SetCreationTimeUtc(path, DateTime.UtcNow.AddMinutes(i - 30));
            }

            // Creating a new backup triggers PruneHistoricalBackups
            store.CreateHistoricalBackup(Encoding.UTF8.GetBytes("{\"latest\":true}"));

            var remaining = store.GetHistoricalBackups();
            Assert.True(remaining.Count <= 10);
        }

        private static readonly byte[] SmallNewestPayload = Encoding.UTF8.GetBytes("{\"s\":1}");

        [Fact]
        public void AccountStore_PruneHistoricalBackups_PreservesNewestSmallerSnapshotOverOlderLargerOnes()
        {
            var store = new AccountStore(_testAccountFile, _testBackupsDir);
            DateTime baseUtc = DateTime.UtcNow.AddMinutes(-30);

            for (int i = 0; i < 11; i++)
            {
                string path = Path.Combine(_testBackupsDir, $"AccountData_20260101_1200{i:D2}.json.bak");
                byte[] payload = new byte[i == 0 ? 4096 : 256];
                Array.Fill(payload, (byte)(0x41 + i));
                File.WriteAllBytes(path, payload);
                File.SetCreationTimeUtc(path, baseUtc.AddMinutes(i));
                File.SetLastWriteTimeUtc(path, baseUtc.AddMinutes(i));
            }

            store.CreateHistoricalBackup(SmallNewestPayload);

            var remaining = store.GetHistoricalBackups();
            Assert.Equal(AccountStore.MaxHistoricalBackups, remaining.Count);
            Assert.Equal(SmallNewestPayload, File.ReadAllBytes(remaining[0]));
            Assert.Contains(remaining, p => p.EndsWith("AccountData_20260101_120000.json.bak", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(remaining, p => p.EndsWith("AccountData_20260101_120002.json.bak", StringComparison.OrdinalIgnoreCase));
        }
    }
}
