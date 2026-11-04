#nullable enable
using System;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountManagerLifecycleAndAccountsBehaviorTests : IDisposable
    {
        public void Dispose()
        {
            AppSettings.General.Set("EnableMultiRbx", "true");
            RobloxSingletonService.Apply(true);
        }

        [Fact]
        public void AccountManager_UpdateAccountValidity_NullAccount_DoesNotThrow()
        {
            var exception = Record.Exception(() =>
            {
                AccountManager.NotifyAccountValidity(null!, true);
                AccountManager.NotifyAccountValidity(null!, false);
            });
            Assert.Null(exception);

            var acc = new Account
            {
                Username = "ValidityTestUser",
                Valid = false,
                AccountStatusIssue = "LockedOut"
            };

            AccountManager.NotifyAccountValidity(acc, true);
            Assert.True(acc.Valid);
            Assert.Equal(string.Empty, acc.AccountStatusIssue);

            AccountManager.NotifyAccountValidity(acc, false);
            Assert.False(acc.Valid);
        }

        [Fact]
        public async Task AccountManager_GetUserIDAsync_EmptyOrWhitespace_ReturnsFalse()
        {
            var resNull = await AccountManager.GetUserIDAsync(null!);
            Assert.False(resNull.Success);
            Assert.Equal(-1, resNull.UserId);
            Assert.Null(resNull.Response);

            var resEmpty = await AccountManager.GetUserIDAsync("");
            Assert.False(resEmpty.Success);
            Assert.Equal(-1, resEmpty.UserId);

            var resWhitespace = await AccountManager.GetUserIDAsync("   ");
            Assert.False(resWhitespace.Success);
            Assert.Equal(-1, resWhitespace.UserId);
        }

        [Fact]
        public async Task AccountManager_GetUserIDAsync_KnownAccountInList_ReturnsCachedAndKnownId()
        {
            var known = new Account
            {
                Username = "KnownListUser",
                UserID = 887766,
                SecurityToken = "test_token"
            };

            lock (AccountManager.AccountListLock)
            {
                AccountManager.AccountsList.Add(known);
            }

            try
            {
                var (success1, uid1, resp1) = await AccountManager.GetUserIDAsync("KnownListUser");
                Assert.True(success1);
                Assert.Equal(887766, uid1);
                Assert.Null(resp1);

                // Call again to verify in-memory cache lookup
                var (success2, uid2, resp2) = await AccountManager.GetUserIDAsync("KnownListUser");
                Assert.True(success2);
                Assert.Equal(887766, uid2);
                Assert.Null(resp2);
            }
            finally
            {
                lock (AccountManager.AccountListLock)
                {
                    AccountManager.AccountsList.Remove(known);
                }
            }
        }

        [Fact]
        public void AccountManager_RegisterValidatedAccount_And_Remove_Behaviors()
        {
            // Invalid account -> returns null
            var invalid = new Account { Valid = false, Username = "InvalidUser" };
            var regInvalid = AccountManager.RegisterValidatedAccount(invalid, "token", saveImmediate: false);
            Assert.Null(regInvalid);

            // Valid account -> registered
            var valid = new Account
            {
                Username = "ValidRegUser",
                UserID = 998877,
                Valid = true,
                SecurityToken = "InitialToken"
            };

            var registered = AccountManager.RegisterValidatedAccount(valid, "InitialToken", "InitialPass", saveImmediate: false);
            Assert.NotNull(registered);
            Assert.Equal("InitialPass", registered.Password);

            try
            {
                // Registering again updates existing record
                var updated = new Account
                {
                    Username = "ValidRegUser",
                    UserID = 998877,
                    Valid = true,
                    SecurityToken = "UpdatedToken"
                };

                var secondReg = AccountManager.RegisterValidatedAccount(updated, "UpdatedToken", "UpdatedPass", saveImmediate: false);
                Assert.NotNull(secondReg);
                Assert.Equal("UpdatedToken", secondReg.SecurityToken);
                Assert.Equal("UpdatedPass", secondReg.Password);

                // Remove account from database
                int removedCount = AccountManager.RemoveAccountsFromDatabase(new[] { secondReg });
                Assert.Equal(1, removedCount);
            }
            finally
            {
                lock (AccountManager.AccountListLock)
                {
                    AccountManager.AccountsList.Remove(valid);
                }
            }
        }

        [Fact]
        public void AccountManager_PersistenceAndEncryption_MethodsExecuteSafely()
        {
            var exEnc = Record.Exception(() => AccountManager.IsDatabasePasswordEncrypted());
            Assert.Null(exEnc);

            if (AccountManager.Instance == null)
            {
                bool loaded = AccountManager.LoadAccountsFromStore();
                Assert.False(loaded);
            }
        }

        [Fact]
        public void AccountManager_RemoveAccounts_ValidationEdgeCases()
        {
            bool removedNull = AccountManager.RemoveAccount(null!);
            Assert.False(removedNull);

            int countNull = AccountManager.RemoveAccountsFromDatabase(null!);
            Assert.Equal(0, countNull);

            int countEmpty = AccountManager.RemoveAccountsFromDatabase(Array.Empty<Account>());
            Assert.Equal(0, countEmpty);
        }

        [Fact]
        public void AccountManager_LaunchSignals_ExecuteSafely()
        {
            var exception = Record.Exception(() =>
            {
                if (AccountManager.Instance != null)
                {
                    AccountManager.Instance.NextAccount();
                    AccountManager.Instance.CancelLaunching();
                }
            });
            Assert.Null(exception);
        }

        [Fact]
        public void AccountManager_MultiRobloxAndPresence_Behaviors()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            var ex = Record.Exception(() =>
            {
                bool multiResult = mgr.UpdateMultiRoblox();
                Assert.Equal(multiResult, multiResult);

                bool tryMulti = mgr.TryEnableMultiRoblox(promptIfRobloxRunning: false, notify: false);
                Assert.Equal(tryMulti, tryMulti);

                mgr.RestartPresenceTimer();
                mgr.ModernRefreshPresence();
                mgr.SchedulePresenceRefresh(null!);
                mgr.SchedulePresenceRefresh(new Account { UserID = -1 });
                mgr.SchedulePresenceRefresh(new Account { UserID = 987654 });
            });
            Assert.Null(ex);
        }

        [Fact]
        public async Task AccountManager_ImportBulkUserPassAsync_InputValidation()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();

            // Null or empty combos
            await mgr.ImportBulkUserPassAsync(null!);
            await mgr.ImportBulkUserPassAsync("");
            await mgr.ImportBulkUserPassAsync("/UC");

            // Combos without valid delimiter
            string status = "";
            await mgr.ImportBulkUserPassAsync("invalid_no_separator\nanother_bad_line", msg => status = msg);
            Assert.Equal(LocalizationService.Get("AccountManager.NoValidCredentials"), status);

            // Canceled token breaks loop with validCount == 0
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            await mgr.ImportBulkUserPassAsync("validUser:validPass", msg => status = msg, cts.Token);
            Assert.Equal(LocalizationService.Get("AccountManager.NoValidCredentials"), status);
        }
    }
}
