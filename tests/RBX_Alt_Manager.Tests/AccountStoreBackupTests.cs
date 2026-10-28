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
    }
}
