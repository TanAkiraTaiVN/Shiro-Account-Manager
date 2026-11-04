#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountManagerBehaviorTests
    {
        [Theory]
        // Count 0 without bypassCountCheck -> Skip
        [InlineData(0, false, false, false, AccountManager.SaveGate.Skip)]
        // Not dirty without bypass -> Skip
        [InlineData(5, false, false, false, AccountManager.SaveGate.Skip)]
        // Throttled by startTime -> Defer
        [InlineData(5, true, false, false, AccountManager.SaveGate.Defer)]
        // Bypass rate limit -> Proceed
        [InlineData(5, true, true, false, AccountManager.SaveGate.Proceed)]
        // Bypass count check and rate limit -> Proceed
        [InlineData(0, false, true, true, AccountManager.SaveGate.Proceed)]
        public void EvaluateSaveGate_EvaluatesTruthTable(
            int count,
            bool anyDirty,
            bool bypassRateLimit,
            bool bypassCountCheck,
            AccountManager.SaveGate expected)
        {
            DateTime now = DateTime.UtcNow;
            DateTime startTime = bypassRateLimit ? now.AddSeconds(-10) : now.AddSeconds(-0.5);
            DateTime lastSave = now.AddSeconds(-10);

            var gate = AccountManager.EvaluateSaveGate(
                now,
                startTime,
                lastSave,
                bypassRateLimit,
                bypassCountCheck,
                count,
                anyDirty);

            Assert.Equal(expected, gate);
        }

        [Fact]
        public void TakeDirtyAccounts_And_RestoreDirty_RoundTrip()
        {
            var acc1 = new Account { Username = "Acc1", IsDirty = true };
            var acc2 = new Account { Username = "Acc2", IsDirty = false };
            var acc3 = new Account { Username = "Acc3", IsDirty = true };
            var list = new List<Account> { acc1, acc2, acc3 };

            var taken = AccountManager.TakeDirtyAccounts(list);
            Assert.Equal(2, taken.Count);
            Assert.Contains(acc1, taken);
            Assert.Contains(acc3, taken);
            Assert.False(acc1.IsDirty);
            Assert.False(acc3.IsDirty);

            AccountManager.RestoreDirty(taken);
            Assert.True(acc1.IsDirty);
            Assert.True(acc3.IsDirty);
        }

        [Fact]
        public void TakeDirtyAccounts_WithNull_ReturnsEmpty()
        {
            var taken = AccountManager.TakeDirtyAccounts(null!);
            Assert.Empty(taken);
        }

        [Fact]
        public void RestoreDirty_WithNull_DoesNotThrow()
        {
            var ex = Record.Exception(() => AccountManager.RestoreDirty(null!));
            Assert.Null(ex);
        }

        [Theory]
        [InlineData("localhost", true)]
        [InlineData("127.0.0.1", true)]
        [InlineData("::1", true)]
        [InlineData("[::1]", true)]
        [InlineData("google.com", false)]
        [InlineData("192.168.1.1", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsLoopbackHostName_IdentifiesLoopbacks(string? host, bool expected)
        {
            bool isLoopback = AccountManager.IsLoopbackHostName(host!);
            Assert.Equal(expected, isLoopback);
        }

        [Theory]
        [InlineData("http://localhost:5000", true)]
        [InlineData("https://localhost", true)]
        [InlineData("http://127.0.0.1:8080", true)]
        [InlineData("http://attacker.com", false)]
        [InlineData("", true)]
        [InlineData(null, true)]
        public void IsTrustedLocalOrigin_ValidatesOrigins(string? origin, bool expected)
        {
            bool trusted = AccountManager.IsTrustedLocalOrigin(origin!);
            Assert.Equal(expected, trusted);
        }

        [Theory]
        [InlineData("secret123", "secret123", true)]
        [InlineData("secret123", "secret124", false)]
        [InlineData("short", "muchlongerpassword", false)]
        [InlineData("", "", true)]
        [InlineData(null, "pwd", false)]
        public void FixedTimeEqualsPassword_ConstantTimeComparison(string? a, string? b, bool expected)
        {
            bool equal = AccountManager.FixedTimeEqualsPassword(a!, b!);
            Assert.Equal(expected, equal);
        }

        [Fact]
        public void ExtractHeaderPassword_ExtractsFromHeaders()
        {
            string? shiro = AccountManager.ExtractHeaderPassword(key => key == "X-Shiro-Auth" ? "Pass123" : null);
            Assert.Equal("Pass123", shiro);

            string? ram = AccountManager.ExtractHeaderPassword(key => key == "X-RAM-Auth" ? "RamPass" : null);
            Assert.Equal("RamPass", ram);

            string? bearer = AccountManager.ExtractHeaderPassword(key => key == "Authorization" ? "Bearer Token456" : null);
            Assert.Equal("Token456", bearer);

            string? none = AccountManager.ExtractHeaderPassword(_ => null);
            Assert.Null(none);
        }

        [Fact]
        public void ExtractQueryPassword_ExtractsQueryParameter()
        {
            string? pwd = AccountManager.ExtractQueryPassword(key => key == "password" ? "QueryPass" : null);
            Assert.Equal("QueryPass", pwd);

            string? token = AccountManager.ExtractQueryPassword(key => key == "token" ? "TokenPass" : null);
            Assert.Equal("TokenPass", token);

            string? none = AccountManager.ExtractQueryPassword(_ => null);
            Assert.Null(none);
        }

        [Fact]
        public void ExtractRequestPassword_PrefersHeaderOverQuery()
        {
            string pwd = AccountManager.ExtractRequestPassword(
                h => h == "X-Shiro-Auth" ? "HeaderPass" : null,
                q => q == "password" ? "QueryPass" : null);

            Assert.Equal("HeaderPass", pwd);

            string fallback = AccountManager.ExtractRequestPassword(
                _ => null,
                q => q == "password" ? "QueryPass" : null);

            Assert.Equal("QueryPass", fallback);
        }

        [Fact]
        public void NotifyAccountValidity_WithNull_ReturnsSafely()
        {
            var ex = Record.Exception(() => AccountManager.NotifyAccountValidity(null!, true));
            Assert.Null(ex);
        }

        [Fact]
        public void RemoveAccountsFromDatabase_WithNullOrEmpty_ReturnsZero()
        {
            int r1 = AccountManager.RemoveAccountsFromDatabase(null!);
            Assert.Equal(0, r1);

            int r2 = AccountManager.RemoveAccountsFromDatabase(new List<Account>());
            Assert.Equal(0, r2);
        }

        [Fact]
        public void RegisterValidatedAccount_WithInvalidOrNull_ReturnsNull()
        {
            var accNull = AccountManager.RegisterValidatedAccount(null!, "tok");
            Assert.Null(accNull);

            var accInvalid = new Account { Username = "Bad", Valid = false };
            var resInvalid = AccountManager.RegisterValidatedAccount(accInvalid, "tok");
            Assert.Null(resInvalid);
        }

        [Fact]
        public async Task ImportBulkUserPassAsync_WithEmptyOrUC_ReturnsImmediately()
        {
            var mgr = new AccountManager();
            var ex1 = await Record.ExceptionAsync(() => mgr.ImportBulkUserPassAsync(""));
            Assert.Null(ex1);

            var ex2 = await Record.ExceptionAsync(() => mgr.ImportBulkUserPassAsync("/UC"));
            Assert.Null(ex2);
        }

        [Fact]
        public void AccountManager_ValidityAndCache_EdgeCases()
        {
            var acc = new Account { Username = "ValidUser1", AccountStatusIssue = "OldIssue" };
            AccountManager.NotifyAccountValidity(acc, true);
            Assert.True(acc.Valid);
            Assert.Empty(acc.AccountStatusIssue);

            AccountManager.NotifyAccountValidity(acc, false);
            Assert.False(acc.Valid);
        }
    }
}
