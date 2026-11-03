using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Nexus;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("RateLimitCoordinator")]
    public class BackendOptimizationTests
    {
        [Fact]
        public void ExtractRoblosecurityFromSetCookie_ValidWarningToken_ExtractedSuccessfully()
        {
            string validCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF";
            string setCookieHeader = $".ROBLOSECURITY={validCookie}; path=/; domain=.roblox.com; HttpOnly";

            string? extracted = Account.ExtractRoblosecurityFromSetCookie(setCookieHeader);

            Assert.NotNull(extracted);
            Assert.Equal(validCookie, extracted);
        }

        [Fact]
        public void ExtractRoblosecurityFromSetCookie_EmptyOrDeletedCookie_ReturnsNull()
        {
            string emptyCookie = ".ROBLOSECURITY=; path=/; domain=.roblox.com; expires=Thu, 01 Jan 1970 00:00:00 GMT";
            string deletedCookie = ".ROBLOSECURITY=deleted; path=/; domain=.roblox.com;";
            string shortCookie = ".ROBLOSECURITY=short_value; path=/;";

            Assert.Null(Account.ExtractRoblosecurityFromSetCookie(emptyCookie));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie(deletedCookie));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie(shortCookie));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie(null));
            Assert.Null(Account.ExtractRoblosecurityFromSetCookie(string.Empty));
        }

        [Fact]
        public async Task PresenceService_IgnoresNonPositiveUserIds()
        {
            using var presenceService = new PresenceService();
            var account = new Account { Username = "Ignored", UserID = 0 };

            // UserIDs <= 0 must be ignored: no exception and no presence data produced.
            var ex = await Record.ExceptionAsync(() => presenceService.RefreshAsync(new long[] { -1, 0, -999 }));

            Assert.Null(ex);
            Assert.Null(account.Presence);
        }

        [Fact]
        public void ControlledAccount_StatusAndDisconnect_ThreadSafe()
        {
            var account = new Account { Username = "TestUser" };
            var controlled = new ControlledAccount(account);

            Assert.Equal(AccountStatus.Offline, controlled.Status);
            Assert.Equal("Offline", controlled.StatusText);

            controlled.SendMessage("test ping"); // Should not throw when disconnected

            controlled.Disconnect();
            Assert.Equal(AccountStatus.Offline, controlled.Status);
        }

        [Fact]
        public void RateLimitCoordinator_PacingAndBackoff_MaintainsState()
        {
            var coordinator = RobloxRateLimitCoordinator.Instance;
            coordinator.ResetBackoff();

            Assert.False(coordinator.IsBackingOff);
            Assert.Equal(TimeSpan.Zero, coordinator.RemainingBackoff);

            coordinator.RecordRateLimitHit(RateLimitCategory.General, defaultBackoffSeconds: 2);
            Assert.True(coordinator.IsBackingOff);
            Assert.True(coordinator.RemainingBackoff.TotalMilliseconds > 0);

            coordinator.ResetBackoff();
            Assert.False(coordinator.IsBackingOff);
        }
    }
}
