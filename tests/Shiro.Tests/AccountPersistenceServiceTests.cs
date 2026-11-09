#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class AccountPersistenceServiceTests
    {
        [Fact]
        public void SaveGate_BlocksEmptyWriteDuringStartupWindow()
        {
            DateTime t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            DateTime now = t0.AddSeconds(1);

            var emptyGate = AccountPersistenceService.EvaluateSaveGate(
                now: now,
                startTime: t0,
                lastSaveTime: DateTime.MinValue,
                bypassRateLimit: false,
                bypassCountCheck: false,
                accountCount: 0,
                anyDirty: false);

            Assert.Equal(AccountPersistenceService.SaveGate.Skip, emptyGate);

            var startupGate = AccountPersistenceService.EvaluateSaveGate(
                now: now,
                startTime: t0,
                lastSaveTime: DateTime.MinValue,
                bypassRateLimit: false,
                bypassCountCheck: false,
                accountCount: 1,
                anyDirty: true);

            Assert.Equal(AccountPersistenceService.SaveGate.Defer, startupGate);
        }

        [Fact]
        public void Shutdown_FlushesDirtyAccounts_ThroughPersistenceService()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroShutdownDirty_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            bool prevSuppress = AccountManager.SuppressExitOnShutdown;
            var prevInstance = AccountManager.Instance;
            var prevStore = AccountPersistenceService.Default.Store;

            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    AccountManager.SuppressExitOnShutdown = true;
                    AccountPersistenceService.Default.Store = new AccountStore(
                        Path.Combine(tempDir, "AccountData.json"),
                        Path.Combine(tempDir, "Backups"));
                    AccountPersistenceService.Default.IsShutdownRequested = false;

                    var tempMgr = new AccountManager();
                    var account = new Account { Username = "FlushedUser", UserID = 778899, IsDirty = true };
                    AccountRepository.Default.ReplaceAll(new[] { account });

                    Assert.True(account.IsDirty);

                    tempMgr.ShutdownBackend();

                    Assert.True(File.Exists(AccountPersistenceService.Default.Store.AccountFile));

                    var remainingDirty = AccountPersistenceService.TakeDirtyAccounts(AccountRepository.Default.GetAll());
                    Assert.Empty(remainingDirty);
                    Assert.False(account.IsDirty);
                }
            }
            finally
            {
                AccountRepository.Default.ReplaceAll(Array.Empty<Account>());
                AccountManager.SuppressExitOnShutdown = prevSuppress;
                AccountManager.Instance = prevInstance;
                AccountPersistenceService.Default.Store = prevStore;
                AccountPersistenceService.Default.IsShutdownRequested = false;
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
                }
                catch { }
            }
        }

        [Fact]
        public void AccountManager_ShouldNotContain_PersistenceMembers()
        {
            var type = typeof(AccountManager);
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

            string[] forbidden =
            {
                "SaveAccounts",
                "SaveAccountsAsync",
                "LoadAccountsFromStore",
                "IsDatabasePasswordEncrypted",
                "TryRecoverOrRestoreAccounts",
                "QueueDeferredSave"
            };

            var found = new List<string>();
            foreach (var memberName in forbidden)
            {
                var members = type.GetMember(memberName, flags);
                if (members.Length > 0)
                {
                    found.Add(memberName);
                }
            }

            Assert.True(found.Count == 0,
                $"AccountManager should not contain persistence members, but found: {string.Join(", ", found)}");
        }
    }
}
