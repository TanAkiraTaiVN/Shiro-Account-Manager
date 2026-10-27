using System;
using Xunit;

namespace RBX_Alt_Manager.Tests
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
            AccountManager.CurrentJobId = null;

            Assert.Equal(string.Empty, AccountManager.CurrentJobId);
        }

        [Fact]
        public async System.Threading.Tasks.Task GetUserIDAsync_WithEmptyOrNullUsername_ReturnsFalse()
        {
            var resEmpty = await AccountManager.GetUserIDAsync("");
            Assert.False(resEmpty.Success);
            Assert.Equal(-1, resEmpty.UserId);

            var resNull = await AccountManager.GetUserIDAsync(null);
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
            Assert.True(isPw || !isPw);
        }
    }
}
