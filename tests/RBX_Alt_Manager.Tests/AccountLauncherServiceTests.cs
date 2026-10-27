using System.Collections.Generic;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;
using Xunit;

namespace RBX_Alt_Manager.Tests
{
    public class AccountLauncherServiceTests
    {
        [Theory]
        [InlineData("1818", 1818)]
        [InlineData("https://www.roblox.com/games/920587237/Adopt-Me", 920587237)]
        [InlineData("https://roblox.com/games/5315046213", 5315046213)]
        [InlineData("/games/123456789/", 123456789)]
        [InlineData("abc", 0)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        public void ParsePlaceId_ParsesVariousFormatsCorrectly(string? input, long expected)
        {
            long result = AccountLauncherService.ParsePlaceId(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        public async Task LaunchAppHomeAsync_NullAccount_DoesNotThrow()
        {
            var exception = await Record.ExceptionAsync(() => AccountLauncherService.LaunchAppHomeAsync(null));
            Assert.Null(exception);
        }

        [Fact]
        public async Task JoinServerAsync_EmptyAccounts_DoesNotThrow()
        {
            var exception = await Record.ExceptionAsync(() => AccountLauncherService.JoinServerAsync(new List<Account>(), 12345, ""));
            Assert.Null(exception);
        }

        [Fact]
        public void MultiLaunchHome_WhenInstanceNotReady_DoesNotThrow()
        {
            var exception = Record.Exception(() => AccountLauncherService.MultiLaunchHome());
            Assert.Null(exception);
        }
    }
}
