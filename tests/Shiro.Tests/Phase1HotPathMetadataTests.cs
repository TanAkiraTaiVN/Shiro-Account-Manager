#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class Phase1HotPathMetadataTests : IDisposable
    {
        private readonly IReadOnlyList<Account> _prevAccounts;

        public Phase1HotPathMetadataTests()
        {
            _prevAccounts = AppServices.AccountRepository.GetAll();
        }

        public void Dispose()
        {
            AccountManager.ResetAccountsForTests(_prevAccounts);
        }

        private static string BuildHealthyToken() =>
            "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow.|_H." + new string('P', 939) + ".S";

        [Fact]
        public void AccountViewModel_HotPathProperties_PerformZeroSecretDecryptions()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pkcs8 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
            var acc = new Account
            {
                Username = "PerfUser",
                UserID = 7001,
                SecurityToken = BuildHealthyToken(),
                HbaPrivateKey = pkcs8,
                Valid = true,
                LastUse = DateTime.Now
            };
            var vm = new AccountViewModel(acc);

            using var _ = InMemorySecretProtector.BeginScopedUnprotectCount(out Func<long> getDecryptions);
            bool valid = false;
            SessionHealth health = SessionHealth.Expired;
            bool shortLived = false;
            bool hasHba = false;
            string statusText = string.Empty;
            string tooltip = string.Empty;

            for (int i = 0; i < 50; i++)
            {
                valid = vm.Valid;
                health = vm.SessionHealth;
                shortLived = vm.IsShortLivedSession;
                statusText = vm.StatusText;
                tooltip = vm.StatusTooltip;
                hasHba = acc.HasHbaBinding;
            }

            Assert.Equal(0, getDecryptions());
            Assert.True(valid);
            Assert.Equal(SessionHealth.Healthy, health);
            Assert.False(shortLived);
            Assert.True(hasHba);
            Assert.False(string.IsNullOrEmpty(statusText));
            Assert.Contains("939", tooltip, StringComparison.Ordinal);
        }

        [Fact]
        public void Account_HasHbaBinding_MigratesLegacyFieldOnceThenZeroDecryptions()
        {
            var acc = new Account { Username = "LegacyHbaUser", UserID = 7002 };
            acc.Fields["HbaPrivateKey"] = "legacy-pkcs8-placeholder";

            using var _ = InMemorySecretProtector.BeginScopedUnprotectCount(out Func<long> getDecryptions);
            Assert.True(acc.HasHbaBinding);
            Assert.False(acc.Fields.ContainsKey("HbaPrivateKey"));
            Assert.Equal(0, getDecryptions());

            long beforeLoop = getDecryptions();
            for (int i = 0; i < 20; i++)
            {
                Assert.True(acc.HasHbaBinding);
            }

            Assert.Equal(0, getDecryptions() - beforeLoop);

            acc.ClearSensitiveMemory();
            Assert.False(acc.HasHbaBinding);
        }

        [Fact]
        public async Task RefreshStaleCookiesAsync_FreshAccounts_PerformZeroSecretDecryptions()
        {
            var accounts = new List<Account>(20);
            string token = BuildHealthyToken();
            for (int i = 0; i < 20; i++)
            {
                accounts.Add(new Account
                {
                    Username = $"FreshUser_{i}",
                    UserID = 8000 + i,
                    SecurityToken = token,
                    Valid = true,
                    LastAttemptedRefresh = DateTime.Now
                });
            }
            AccountManager.ResetAccountsForTests(accounts);

            using var gate = new SemaphoreSlim(1, 1);
            using var _ = InMemorySecretProtector.BeginScopedUnprotectCount(out Func<long> getDecryptions);
            await AccountSessionMaintenanceService.Default.RefreshStaleCookiesAsync(gate, CancellationToken.None);

            Assert.Equal(0, getDecryptions());

            long globalBefore = InMemorySecretProtector.UnprotectCallCount;
            Assert.Equal(token, accounts[0].SecurityToken);
            Assert.Equal(1, getDecryptions());
            Assert.True(InMemorySecretProtector.UnprotectCallCount > globalBefore);
        }
    }
}
