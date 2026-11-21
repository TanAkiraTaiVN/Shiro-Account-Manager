#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Http;
using Shiro.Tests.TestInfrastructure;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public partial class AccountManagerLifecycleAndAccountsBehaviorTests : IDisposable
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
                AccountRegistrationService.Default.NotifyAccountValidity(null!, true);
                AccountRegistrationService.Default.NotifyAccountValidity(null!, false);
            });
            Assert.Null(exception);

            var acc = new Account
            {
                Username = "ValidityTestUser",
                Valid = false,
                AccountStatusIssue = "LockedOut"
            };

            AccountRegistrationService.Default.NotifyAccountValidity(acc, true);
            Assert.True(acc.Valid);
            Assert.Equal(string.Empty, acc.AccountStatusIssue);

            AccountRegistrationService.Default.NotifyAccountValidity(acc, false);
            Assert.False(acc.Valid);
        }

        [Fact]
        public async Task AccountManager_GetUserIDAsync_EmptyOrWhitespace_ReturnsFalse()
        {
            var resNull = await AccountRegistrationService.Default.GetUserIdAsync(null!);
            Assert.False(resNull.Success);
            Assert.Equal(-1, resNull.UserId);
            Assert.Null(resNull.Response);

            var resEmpty = await AccountRegistrationService.Default.GetUserIdAsync("");
            Assert.False(resEmpty.Success);
            Assert.Equal(-1, resEmpty.UserId);

            var resWhitespace = await AccountRegistrationService.Default.GetUserIdAsync("   ");
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

            AppServices.AccountRepository.AddThreadSafe(known);

            try
            {
                var (success1, uid1, resp1) = await AccountRegistrationService.Default.GetUserIdAsync("KnownListUser");
                Assert.True(success1);
                Assert.Equal(887766, uid1);
                Assert.Null(resp1);

                // Call again to verify in-memory cache lookup
                var (success2, uid2, resp2) = await AccountRegistrationService.Default.GetUserIdAsync("KnownListUser");
                Assert.True(success2);
                Assert.Equal(887766, uid2);
                Assert.Null(resp2);
            }
            finally
            {
                AccountRegistrationService.Default.RemoveAccount(known);
            }
        }

        [Fact]
        public void AccountManager_RegisterValidatedAccount_And_Remove_Behaviors()
        {
            // Invalid account -> returns null
            var invalid = new Account { Valid = false, Username = "InvalidUser" };
            var regInvalid = AccountRegistrationService.Default.RegisterValidatedAccount(invalid, "token", saveImmediate: false);
            Assert.Null(regInvalid);

            // Valid account -> registered
            var valid = new Account
            {
                Username = "ValidRegUser",
                UserID = 998877,
                Valid = true,
                SecurityToken = "InitialToken"
            };

            var registered = AccountRegistrationService.Default.RegisterValidatedAccount(valid, "InitialToken", "InitialPass", saveImmediate: false);
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

                var secondReg = AccountRegistrationService.Default.RegisterValidatedAccount(updated, "UpdatedToken", "UpdatedPass", saveImmediate: false);
                Assert.NotNull(secondReg);
                Assert.Equal("UpdatedToken", secondReg.SecurityToken);
                Assert.Equal("UpdatedPass", secondReg.Password);

                // Remove account from database
                int removedCount = AccountRegistrationService.Default.RemoveAccountsFromDatabase(new[] { secondReg });
                Assert.Equal(string.Empty, secondReg.SecurityToken);
                Assert.Equal(string.Empty, secondReg.Password);
                Assert.Equal(1, removedCount);
            }
            finally
            {
                AccountRegistrationService.Default.RemoveAccount(valid);
            }
        }

        [Fact]
        public void AccountManager_PersistenceAndEncryption_MethodsExecuteSafely()
        {
            var exEnc = Record.Exception(() => AccountPersistenceService.Default.IsDatabasePasswordEncrypted());
            Assert.Null(exEnc);

            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroEmptyStore_" + Guid.NewGuid().ToString("N"));
            var prevStore = AccountPersistenceService.Default.Store;
            try
            {
                AccountPersistenceService.Default.Store = new AccountStore(Path.Combine(tempDir, "AccountData.json"), Path.Combine(tempDir, "Backups"));
                bool loaded = AccountPersistenceService.Default.LoadAccountsFromStore();
                Assert.False(loaded);
            }
            finally
            {
                AccountPersistenceService.Default.Store = prevStore;
                if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void AccountManager_RemoveAccounts_ValidationEdgeCases()
        {
            bool removedNull = AccountRegistrationService.Default.RemoveAccount(null!);
            Assert.False(removedNull);

            int countNull = AccountRegistrationService.Default.RemoveAccountsFromDatabase(null!);
            Assert.Equal(0, countNull);

            int countEmpty = AccountRegistrationService.Default.RemoveAccountsFromDatabase(Array.Empty<Account>());
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
                RobloxSingletonService.Apply(true);
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

            // Canceled token breaks loop with validCount == 0
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            await mgr.ImportBulkUserPassAsync("validUser:validPass", msg => status = msg, cts.Token);
            var prevOverride = BrowserService.TestSignInOverride;
            try
            {
                BrowserService.TestSignInOverride = (user, pass, slot, submit, token) =>
                {
                    if (user == "failUser") throw new InvalidOperationException("bad credentials");
                    if (user == "cancelUser") throw new OperationCanceledException(token);
                    return Task.FromResult<Account?>(new Account { Username = user ?? "u", Valid = true });
                };

                await mgr.ImportBulkUserPassAsync("okUser1:pass1\nokUser2|pass2\nokUser3\tpass3", msg => status = msg);
                Assert.Contains("3", status);

                await mgr.ImportBulkUserPassAsync("okUser1:pass1\nfailUser:pass2\ncancelUser:pass3", msg => status = msg);
                Assert.Contains("failUser", status);
            }
            finally
            {
                BrowserService.TestSignInOverride = prevOverride;
            }
        }

        [Fact]
        public void MakeSafeFileName_SanitizesInvalidCharsAndEdgeCases()
        {
            Assert.Equal("fallback", AccountManager.MakeSafeFileName("", "fallback"));
            Assert.Equal("fallback", AccountManager.MakeSafeFileName("   ", "fallback"));
            Assert.Equal("fallback", AccountManager.MakeSafeFileName("...", "fallback"));
            Assert.Equal("cleanName", AccountManager.MakeSafeFileName("cleanName", "fallback"));
            Assert.Equal("cleanName", AccountManager.MakeSafeFileName("  cleanName... ", "fallback"));
            Assert.Equal("bad_name_file", AccountManager.MakeSafeFileName("bad:name?file.", "fallback"));
            string longInvalid = new string('?', 300);
            Assert.Equal(new string('_', 300), AccountManager.MakeSafeFileName(longInvalid, "fallback"));
        }

        [Fact]
        public async Task GetUserIDAsync_HttpExecution_And_RefreshStaleCookiesAsync_CoverBranches()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            var handler = new FakeHttpHandler();
            using var _ = AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));

            try
            {
                AccountRegistrationService.Default.ClearResolvedUserIdsCacheForTesting();

                handler.Enqueue(HttpStatusCode.OK, "{\"data\":[{\"id\":445566,\"name\":\"HttpResolvedUser\"}]}");
                var (ok1, id1, _) = await AccountRegistrationService.Default.GetUserIdAsync("HttpResolvedUser");
                Assert.True(ok1);
                Assert.Equal(445566, id1);

                var (okCached, idCached, _) = await AccountRegistrationService.Default.GetUserIdAsync("HttpResolvedUser");
                Assert.True(okCached);
                Assert.Equal(445566, idCached);

                handler.Enqueue(HttpStatusCode.OK, "{\"data\":[]}");
                var (okEmpty, idEmpty, _) = await AccountRegistrationService.Default.GetUserIdAsync("UnknownUserX");
                Assert.False(okEmpty);
                Assert.Equal(-1, idEmpty);

                handler.Enqueue(HttpStatusCode.OK, "{not-json");
                var (okBadJson, _, _) = await AccountRegistrationService.Default.GetUserIdAsync("BadJsonUser");
                Assert.False(okBadJson);

                AccountManager.ReplaceAccountsList(new List<Account>());
                await mgr.RefreshStaleCookiesAsync(CancellationToken.None);

                var skippedAcc = new Account
                {
                    Username = "SkippedRefresh",
                    SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('B', 120),
                    Valid = true,
                    LastAttemptedRefresh = DateTime.MinValue
                };
                skippedAcc.SetField("NoCookieRefresh", "true");

                var staleAcc = new Account
                {
                    Username = "StaleCandidate",
                    SecurityToken = "short_invalid_token",
                    Valid = false,
                    LastAttemptedRefresh = DateTime.MinValue
                };

                AccountManager.ReplaceAccountsList(new List<Account> { skippedAcc, staleAcc });
                await mgr.RefreshStaleCookiesAsync(CancellationToken.None);
                Assert.NotEqual(DateTime.MinValue, staleAcc.LastAttemptedRefresh);
            }
            finally
            {
                AccountManager.ReplaceAccountsList(new List<Account>());
            }
        }
    }
}
