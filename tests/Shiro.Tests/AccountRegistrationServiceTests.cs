#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class AccountRegistrationServiceTests
    {
        [Fact]
        public void NotifyAccountValidity_NullAndEdgeCases()
        {
            var service = AccountRegistrationService.Default;
            service.NotifyAccountValidity(null!, true);
            service.NotifyAccountValidity(null!, false);

            var acc = new Account { Username = "TestValUser", AccountStatusIssue = "Issue" };
            service.NotifyAccountValidity(acc, true);
            Assert.True(acc.Valid);
            Assert.Equal(string.Empty, acc.AccountStatusIssue);

            service.NotifyAccountValidity(acc, false);
            Assert.False(acc.Valid);
        }

        [Fact]
        public void AddAccount_Synchronous_CreatesAndRegisters()
        {
            var repo = new AccountRepository();
            var service = new AccountRegistrationService(repo, AccountPersistenceService.Default);

            var acc = service.AddAccount("token_test_sync", "pass123", saveImmediate: false);
            Assert.Null(acc); // default token is invalid without valid format
        }

        [Fact]
        public async Task AddAccountAsync_Overloads_ExecuteSafely()
        {
            var repo = new AccountRepository();
            var service = new AccountRegistrationService(repo, AccountPersistenceService.Default);

            using var cts = new CancellationTokenSource();
            var acc1 = await service.AddAccountAsync("invalid_token_1", "pass", null, false, cts.Token);
            Assert.Null(acc1);

            var acc2 = await service.AddAccountAsync("invalid_token_2", "pass", null, false, cts.Token, null);
            Assert.Null(acc2);
        }

        [Fact]
        public void RegisterValidatedAccount_SaveImmediate_And_FieldMerging_Branches()
        {
            var repo = new AccountRepository();
            var service = new AccountRegistrationService(repo, AccountPersistenceService.Default);

            // 1. Initial valid account added with saveImmediate = true
            var initial = new Account
            {
                Username = "MergeBranchUser",
                UserID = 0,
                Valid = true,
                SecurityToken = "Tok1"
            };
            var registered1 = service.RegisterValidatedAccount(initial, "Tok1", "InitialPass", saveImmediate: true);
            Assert.NotNull(registered1);
            Assert.Same(initial, registered1);

            // 2. Updated account where existing has UserID <= 0 and incoming has UserID > 0
            var updated = new Account
            {
                Username = "MergeBranchUser",
                UserID = 556677,
                Valid = true,
                SecurityToken = "Tok2"
            };
            var registered2 = service.RegisterValidatedAccount(updated, "Tok2", "UpdatedPass", saveImmediate: true);
            Assert.NotNull(registered2);
            Assert.Same(initial, registered2);
            Assert.Equal(556677, initial.UserID);
            Assert.Equal("UpdatedPass", initial.Password);

            // 3. Existing account with empty username updated by incoming with username
            var anonRepo = new AccountRepository();
            var anonService = new AccountRegistrationService(anonRepo, AccountPersistenceService.Default);
            var anonAcc = new Account
            {
                Username = "",
                UserID = 998811,
                Valid = true,
                SecurityToken = "AnonTok"
            };
            anonService.RegisterValidatedAccount(anonAcc, "AnonTok", "AnonPass", saveImmediate: false);

            var nameUpdate = new Account
            {
                Username = "ResolvedAnonName",
                UserID = 998811,
                Valid = true,
                SecurityToken = "AnonTok2"
            };
            var mergedAnon = anonService.RegisterValidatedAccount(nameUpdate, "AnonTok2", "NewPass", saveImmediate: true);
            Assert.NotNull(mergedAnon);
            Assert.Equal("ResolvedAnonName", anonAcc.Username);
        }

        [Fact]
        public void RemoveAccountsFromDatabase_WhenEmpty_DeletesEmptyBackupFile()
        {
            var repo = new AccountRepository();
            var service = new AccountRegistrationService(repo, AccountPersistenceService.Default);

            var acc = new Account { Username = "TempEmptyUser", Valid = true };
            repo.AddThreadSafe(acc);

            string backupFile = StoragePaths.AccountDataFile + ".backup";
            try
            {
                File.WriteAllText(backupFile, "dummy-backup");
                int removed = service.RemoveAccountsFromDatabase(new[] { acc });
                Assert.Equal(1, removed);
                Assert.False(File.Exists(backupFile));
            }
            finally
            {
                if (File.Exists(backupFile)) File.Delete(backupFile);
            }
        }

        [Fact]
        public void RegisterValidatedAccount_SetsLastAttemptedRefresh_ForNewAndUpdatedAccounts()
        {
            var repo = new AccountRepository();
            var service = new AccountRegistrationService(repo, AccountPersistenceService.Default);

            var initial = new Account
            {
                Username = "FreshSessionUser",
                UserID = 771122,
                Valid = true,
                SecurityToken = "Tok1",
                LastAttemptedRefresh = DateTime.MinValue
            };

            DateTime beforeRegister = DateTime.Now.AddSeconds(-2);
            Account? registered = service.RegisterValidatedAccount(initial, "Tok1", "Pass1", saveImmediate: false);
            Assert.NotNull(registered);
            Assert.True(registered!.LastAttemptedRefresh >= beforeRegister);

            // Simulate aging past the 24h refresh window, then re-capturing a fresh session via FastLogin
            registered.LastAttemptedRefresh = DateTime.Now.AddDays(-2);
            var refreshedLogin = new Account
            {
                Username = "FreshSessionUser",
                UserID = 771122,
                Valid = true,
                SecurityToken = "Tok2",
                LastAttemptedRefresh = DateTime.MinValue
            };

            DateTime beforeUpdate = DateTime.Now.AddSeconds(-2);
            Account? updated = service.RegisterValidatedAccount(refreshedLogin, "Tok2", "Pass2", saveImmediate: false);
            Assert.NotNull(updated);
            Assert.Same(registered, updated);
            Assert.True(updated!.LastAttemptedRefresh >= beforeUpdate);
        }
    }
}
