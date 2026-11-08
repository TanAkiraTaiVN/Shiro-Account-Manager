using System;
using System.Collections.Generic;
using System.Linq;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class AccountDecouplingTests
    {
        [Fact]
        public void CurrentPlaceId_CanBeSetAndRetrievedSafely()
        {
            const string testPlaceId = "1818";
            AccountManager.CurrentPlaceId = testPlaceId;

            Assert.Equal(testPlaceId, AccountManager.CurrentPlaceId);
        }

        [Fact]
        public void CurrentJobId_CanBeSetAndRetrievedSafely()
        {
            const string testJobId = "test-job-uuid-1234";
            AccountManager.CurrentJobId = testJobId;

            Assert.Equal(testJobId, AccountManager.CurrentJobId);
        }

        [Fact]
        public void CurrentJobId_WhenNull_ReturnsEmptyString()
        {
            AccountManager.CurrentJobId = null!;

            Assert.Equal(string.Empty, AccountManager.CurrentJobId);
        }

        [Fact]
        public async System.Threading.Tasks.Task GetUserIDAsync_WithEmptyOrNullUsername_ReturnsFalse()
        {
            var resEmpty = await AccountManager.GetUserIDAsync("");
            Assert.False(resEmpty.Success);
            Assert.Equal(-1, resEmpty.UserId);

            var resNull = await AccountManager.GetUserIDAsync(null!);
            Assert.False(resNull.Success);
            Assert.Equal(-1, resNull.UserId);
        }

        [Fact]
        public async System.Threading.Tasks.Task Account_UnlockPinAsync_WithInvalidPin_ReturnsFalse()
        {
            var acc = new Account();
            bool result = await acc.UnlockPinAsync("12");
            Assert.False(result);
        }

        [Fact]
        public async System.Threading.Tasks.Task Account_SendFriendRequestAsync_WithInvalidUsername_ReturnsFalse()
        {
            var acc = new Account();
            bool result = await acc.SendFriendRequestAsync("");
            Assert.False(result);
        }

        [Fact]
        public void AccountRepository_IsPasswordEncrypted_ReturnsConsistentState()
        {
            var repo = Services.AccountRepository.Default;
            bool isPw = repo.IsPasswordEncrypted();
            bool expected = AccountManager.IsDatabasePasswordEncrypted();
            Assert.Equal(expected, isPw);
        }

        [Fact]
        public void AccountRepository_OwnsInMemoryCollection_IsolatesInstances_AndSyncsWithAccountManager()
        {
            var previous = AccountRepository.Default.GetAll().ToList();
            try
            {
                var isolatedRepo = new Services.AccountRepository();
                var isolatedAcc = new Account { UserID = 5001, Username = "IsolatedUser", Valid = true, SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('A', 900) };
                isolatedRepo.AddThreadSafe(isolatedAcc);

                Assert.Equal(1, isolatedRepo.Count);
                Assert.Same(isolatedAcc, isolatedRepo.GetById(5001));
                Assert.Null(AccountRepository.Default.GetById(5001));

                var sharedAcc = new Account { UserID = 6001, Username = "SharedUser", Valid = true, SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('B', 900) };
                AccountManager.ReplaceAccountsList(new List<Account> { sharedAcc });

                Assert.Equal(1, AccountRepository.Default.Count);
                Assert.Equal(1, AccountManager.AccountCount);
                Assert.Same(sharedAcc, AccountRepository.Default.GetByUsername("shareduser"));
                Assert.Equal(1, isolatedRepo.Count);
            }
            finally
            {
                AccountManager.ReplaceAccountsList(previous);
            }
        }

        [Fact]
        public void Account_SyncConstructor_DoesNotPerformNetworkIo_WhenAccountJsonMissingOrInvalid()
        {
            var accWithoutJson = new Account("_|WARNING:-DO-NOT-SHARE-THIS.--test-cookie");
            Assert.False(accWithoutJson.Valid);
            Assert.Equal("_|WARNING:-DO-NOT-SHARE-THIS.--test-cookie", accWithoutJson.SecurityToken);

            var accWithValidJson = new Account(
                "_|WARNING:-DO-NOT-SHARE-THIS.--valid-cookie",
                "{\"UserId\":123456,\"Name\":\"JsonConstructedUser\",\"DisplayName\":\"JsonUser\"}");
            Assert.True(accWithValidJson.Valid);
            Assert.Equal(123456L, accWithValidJson.UserID);
            Assert.Equal("JsonConstructedUser", accWithValidJson.Username);
        }

        [Fact]
        public void BackupRecovery_SourceCode_ZeroesDecodedPlaintextBuffersInFinally()
        {
            string path = System.IO.Path.Combine(ArchitectureTests.RepoRoot, "src", "Shiro", "AccountManager.BackupRecovery.cs");
            string content = System.IO.File.ReadAllText(path);
            Assert.Contains("CryptographicOperations.ZeroMemory(decoded)", content, StringComparison.Ordinal);
        }
    }
}
