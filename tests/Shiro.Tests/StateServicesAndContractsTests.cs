#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class StateServicesAndContractsTests
    {
        [Fact]
        public void AppServices_Resolves_Selection_ServerBrowser_And_HostUiBridge_Services()
        {
            var selection = AppServices.GetRequiredService<ISelectionStateService>();
            var serverState = AppServices.GetRequiredService<IServerBrowserStateService>();
            var uiBridge = AppServices.GetRequiredService<IHostUiBridgeService>();

            Assert.NotNull(selection);
            Assert.NotNull(serverState);
            Assert.NotNull(uiBridge);

            serverState.CurrentPlaceId = "";
            Assert.Equal("5315046213", serverState.CurrentPlaceId);

            serverState.CurrentPlaceId = "1818";
            Assert.Equal("1818", serverState.CurrentPlaceId);
        }

        [Fact]
        public void SelectionStateService_SetSelectedAccounts_IsThreadSafeAndReplacesContents()
        {
            var selection = new SelectionStateService();
            var acc = new Account { Username = "test_user", UserID = 123 };

            Parallel.For(0, 20, _ => selection.SetSelectedAccounts(new[] { acc }));

            Assert.Single(selection.SelectedAccounts);
            Assert.Equal("test_user", selection.SelectedAccounts[0].Username);
        }

        [Fact]
        public void ShiroAssembly_ContainsAtLeast25Interfaces()
        {
            var interfaceCount = typeof(AccountManager).Assembly
                .GetTypes()
                .Count(t => t.IsInterface);

            Assert.True(interfaceCount >= 25, $"Expected >= 25 interfaces, found {interfaceCount}");
        }
    }
}
