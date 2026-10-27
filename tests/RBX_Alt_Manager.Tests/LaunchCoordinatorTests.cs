using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;
using Xunit;

namespace RBX_Alt_Manager.Tests
{
    public class LaunchCoordinatorTests
    {
        [Fact]
        public void LaunchRequest_InitializesPropertiesCorrectly()
        {
            var account = new Account();
            account.Username = "TestPlayer";
            account.UserID = 12345678;

            var request = new LaunchRequest(
                account: account,
                placeId: 189707,
                jobId: "job-abc-123",
                followUser: true,
                vipServer: false,
                openHome: false,
                preferChromeHome: false);

            Assert.Same(account, request.Account);
            Assert.Equal(189707, request.PlaceId);
            Assert.Equal("job-abc-123", request.JobId);
            Assert.True(request.FollowUser);
            Assert.False(request.VipServer);
            Assert.False(request.OpenHome);
            Assert.False(request.PreferChromeHome);
        }

        [Fact]
        public void LaunchRequest_HandlesNullJobIdGracefully()
        {
            var account = new Account();
            var request = new LaunchRequest(account, 123, null, false, false);

            Assert.NotNull(request.JobId);
            Assert.Empty(request.JobId);
        }

        [Fact]
        public void LaunchProgress_RecordsStateCorrectly()
        {
            var account = new Account { Username = "TestUser" };
            var progress = new LaunchProgress
            {
                Index = 1,
                Total = 5,
                Account = account,
                Status = LaunchOutcome.Success,
                Message = "Launched successfully",
                Completed = true
            };

            Assert.Equal(1, progress.Index);
            Assert.Equal(5, progress.Total);
            Assert.Same(account, progress.Account);
            Assert.Equal(LaunchOutcome.Success, progress.Status);
            Assert.Equal("Launched successfully", progress.Message);
            Assert.True(progress.Completed);
        }
    }
}
