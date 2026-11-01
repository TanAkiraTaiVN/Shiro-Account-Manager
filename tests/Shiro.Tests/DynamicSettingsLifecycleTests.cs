#nullable enable
using System.Threading.Tasks;
using Shiro.Classes;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class DynamicSettingsLifecycleTests
    {
        [Fact]
        public void SetAutoCookieRefreshEnabled_TogglesTaskStateDynamically()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            mgr.SetAutoCookieRefreshEnabled(false);
            Assert.Null(mgr.AutoCookieRefresh);

            mgr.SetAutoCookieRefreshEnabled(true);
            Assert.NotNull(mgr.AutoCookieRefresh);

            mgr.SetAutoCookieRefreshEnabled(false);
            Assert.Null(mgr.AutoCookieRefresh);
        }

        [Fact]
        public async Task StopWebServerAsync_WhenNull_DoesNotThrow()
        {
            var mgr = AccountManager.Instance ?? new AccountManager();
            var ex = await Record.ExceptionAsync(async () => await mgr.StopWebServerAsync());
            Assert.Null(ex);
        }
    }
}
