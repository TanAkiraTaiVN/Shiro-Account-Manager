using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Shiro;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    public class MemoryAndBatchOptimizationTests
    {
        [Fact]
        public void GetCommandLineByPid_CurrentProcess_ReturnsAndCachesCommandLine()
        {
            int currentPid = Environment.ProcessId;
            string? cmd1 = ProcessExtensions.GetCommandLineByPid(currentPid);
            string? cmd2 = ProcessExtensions.GetCommandLineByPid(currentPid);

            Assert.False(string.IsNullOrWhiteSpace(cmd1));
            Assert.Same(cmd1, cmd2);
            Assert.Null(ProcessExtensions.GetCommandLineByPid(-1));
        }

        [Fact]
        public void AccountStore_WriteWithBackup_CopiesPrimaryAndThrottlesHistoricalSnapshots()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroTest_R16_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string accountFile = Path.Combine(tempDir, "AccountData.json");
                string backupsDir = Path.Combine(tempDir, "Backups");
                var store = new AccountStore(accountFile, backupsDir);

                byte[] v1 = new byte[] { 1, 2, 3, 4 };
                byte[] v2 = new byte[] { 5, 6, 7, 8 };
                byte[] v3 = new byte[] { 9, 10, 11, 12 };

                // First write creates primary file
                store.WriteWithBackup(v1);
                Assert.Equal(v1, store.Read());
                Assert.False(File.Exists(store.BackupFile));

                // Second write backs up v1 to .backup and creates 1st historical backup
                store.WriteWithBackup(v2);
                Assert.Equal(v2, store.Read());
                Assert.Equal(v1, File.ReadAllBytes(store.BackupFile));
                Assert.Single(store.GetHistoricalBackups());

                // Rapid third write updates .backup to v2, but throttles historical backup
                store.WriteWithBackup(v3, forceHistoricalBackup: false);
                Assert.Equal(v3, store.Read());
                Assert.Equal(v2, File.ReadAllBytes(store.BackupFile));
                Assert.Single(store.GetHistoricalBackups());
            }
            finally
            {
                try { Directory.Delete(tempDir, true); }
                catch (IOException) { }
            }
        }

        [Fact]
        public void GetAccountsSnapshot_CachesImmutableArrayUntilListMutates()
        {
            var original = AccountManager.GetAccountsSnapshot();
            try
            {
                var acc1 = (Account)RuntimeHelpers.GetUninitializedObject(typeof(Account));
                var acc2 = (Account)RuntimeHelpers.GetUninitializedObject(typeof(Account));
                AccountManager.ResetAccountsForTests(new[] { acc1 });

                var snap1 = AccountManager.GetAccountsSnapshot();
                var snap2 = AccountManager.GetAccountsSnapshot();
                Assert.Single(snap1);
                Assert.Same(snap1, snap2);

                AccountManager.AddAccountThreadSafe(acc2);
                var snap3 = AccountManager.GetAccountsSnapshot();
                Assert.Equal(2, snap3.Count);
                Assert.NotSame(snap1, snap3);
            }
            finally
            {
                AccountManager.ResetAccountsForTests(original);
            }
        }

        [Fact]
        public void AccountDataSerializer_SinglePassSerialize_RoundTripsCorrectly()
        {
            var acc = (Account)RuntimeHelpers.GetUninitializedObject(typeof(Account));
            acc.Username = "TestUser_R17";
            acc.UserID = 123456;
            acc.Password = "SecretPass123";
            acc.Valid = true;
            acc.Fields = new Dictionary<string, string>();

            byte[] withPass = AccountDataSerializer.Serialize(new[] { acc }, includePasswords: true);
            var restoredWithPass = AccountDataSerializer.Deserialize(withPass, out bool mig1);
            Assert.False(mig1);
            Assert.Single(restoredWithPass);
            Assert.Equal("SecretPass123", restoredWithPass[0].Password);

            byte[] withoutPass = AccountDataSerializer.Serialize(new[] { acc }, includePasswords: false);
            var restoredWithoutPass = AccountDataSerializer.Deserialize(withoutPass, out bool mig2);
            Assert.False(mig2);
            Assert.Single(restoredWithoutPass);
            Assert.True(string.IsNullOrEmpty(restoredWithoutPass[0].Password));
        }

        [Fact]
        public void MainViewModel_BulkSelectAll_FiresSelectedCountChangedOnce()
        {
            var vm = new MainViewModel();
            var list = new List<Account>();
            for (int i = 1; i <= 10; i++)
            {
                var acc = (Account)RuntimeHelpers.GetUninitializedObject(typeof(Account));
                acc.Username = $"Acc{i}";
                acc.UserID = i;
                acc.Valid = true;
                acc.Fields = new Dictionary<string, string>();
                list.Add(acc);
            }

            vm.LoadAccounts(list);
            int selectedCountNotifications = 0;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedCount))
                    selectedCountNotifications++;
            };

            vm.SelectAllCommand.Execute(null);
            Assert.Equal(10, vm.SelectedCount);
            Assert.Equal(1, selectedCountNotifications);

            vm.ClearSelectionCommand.Execute(null);
            Assert.Equal(0, vm.SelectedCount);
            Assert.Equal(2, selectedCountNotifications);
        }
    }
}
