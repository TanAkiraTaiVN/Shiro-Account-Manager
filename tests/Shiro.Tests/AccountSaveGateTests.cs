using System;
using System.Collections.Generic;
using Shiro.Classes;
using Xunit;
using Shiro.Services;
using static Shiro.Services.AccountPersistenceService;

namespace Shiro.Tests
{
    public class AccountSaveGateTests
    {
        [Fact]
        public void Gate_RateLimitedButDirty_Defers()
        {
            DateTime now = new DateTime(2026, 11, 2, 12, 0, 10);
            DateTime start = now.AddSeconds(-10);
            DateTime last = now.AddSeconds(-1); // 1s ago < 1.5s limit

            SaveGate result = EvaluateSaveGate(
                now: now,
                startTime: start,
                lastSaveTime: last,
                bypassRateLimit: false,
                bypassCountCheck: false,
                accountCount: 3,
                anyDirty: true);

            Assert.Equal(SaveGate.Defer, result);
        }

        [Fact]
        public void Gate_StartupWindow_Defers()
        {
            DateTime start = new DateTime(2026, 11, 2, 12, 0, 0);
            DateTime now = start.AddSeconds(1); // 1s since start < 2s limit
            DateTime last = start.AddSeconds(-10);

            SaveGate result = EvaluateSaveGate(
                now: now,
                startTime: start,
                lastSaveTime: last,
                bypassRateLimit: false,
                bypassCountCheck: false,
                accountCount: 3,
                anyDirty: true);

            Assert.Equal(SaveGate.Defer, result);
        }

        [Fact]
        public void Gate_NothingDirty_Skips()
        {
            DateTime start = new DateTime(2026, 11, 2, 12, 0, 0);
            DateTime last = start.AddSeconds(10);
            DateTime now = last.AddSeconds(10);

            SaveGate result = EvaluateSaveGate(
                now: now,
                startTime: start,
                lastSaveTime: last,
                bypassRateLimit: false,
                bypassCountCheck: false,
                accountCount: 3,
                anyDirty: false);

            Assert.Equal(SaveGate.Skip, result);
        }

        [Fact]
        public void Gate_EmptyList_Skips()
        {
            DateTime start = new DateTime(2026, 11, 2, 12, 0, 0);
            DateTime last = start.AddSeconds(10);
            DateTime now = last.AddSeconds(10);

            SaveGate result = EvaluateSaveGate(
                now: now,
                startTime: start,
                lastSaveTime: last,
                bypassRateLimit: false,
                bypassCountCheck: false,
                accountCount: 0,
                anyDirty: true);

            Assert.Equal(SaveGate.Skip, result);
        }

        [Fact]
        public void Gate_Bypass_Proceeds()
        {
            DateTime now = new DateTime(2026, 11, 2, 12, 0, 1);
            DateTime start = now;
            DateTime last = now;

            SaveGate result = EvaluateSaveGate(
                now: now,
                startTime: start,
                lastSaveTime: last,
                bypassRateLimit: true,
                bypassCountCheck: false,
                accountCount: 3,
                anyDirty: true);

            Assert.Equal(SaveGate.Proceed, result);
        }

        [Fact]
        public void TakeDirty_ClearsOnlyDirtyAndReturnsThem()
        {
            var acc1 = new Account { Username = "U1", Group = "G1", IsDirty = true };
            var acc2 = new Account { Username = "U2", Group = "G2", IsDirty = false };
            var acc3 = new Account { Username = "U3", Group = "G3", IsDirty = true };
            var list = new List<Account> { acc1, acc2, acc3 };

            List<Account> taken = TakeDirtyAccounts(list);

            Assert.Equal(2, taken.Count);
            Assert.Contains(taken, a => ReferenceEquals(a, acc1));
            Assert.Contains(taken, a => ReferenceEquals(a, acc3));
            Assert.DoesNotContain(taken, a => ReferenceEquals(a, acc2));
            Assert.False(acc1.IsDirty);
            Assert.False(acc2.IsDirty);
            Assert.False(acc3.IsDirty);
        }

        [Fact]
        public void RestoreDirty_SetsFlagBack()
        {
            var acc1 = new Account { Username = "U1", Group = "G1", IsDirty = true };
            var acc2 = new Account { Username = "U2", Group = "G2", IsDirty = false };
            var acc3 = new Account { Username = "U3", Group = "G3", IsDirty = true };
            var list = new List<Account> { acc1, acc2, acc3 };

            List<Account> taken = TakeDirtyAccounts(list);
            RestoreDirty(taken);

            Assert.True(acc1.IsDirty);
            Assert.False(acc2.IsDirty);
            Assert.True(acc3.IsDirty);
        }
    }
}
