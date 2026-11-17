#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SequentialStateTests")]
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

            var isolatedState = new ServerBrowserStateService();
            isolatedState.CurrentPlaceId = "";
            Assert.False(string.IsNullOrWhiteSpace(isolatedState.CurrentPlaceId));

            isolatedState.CurrentPlaceId = "1818";
            Assert.Equal("1818", isolatedState.CurrentPlaceId);
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
        public void ShiroAssembly_ContainsActiveServiceInterfaces()
        {
            var interfaces = typeof(AccountManager).Assembly
                .GetTypes()
                .Where(t => t.IsInterface)
                .ToList();

            Assert.True(interfaces.Count >= 18, $"Expected >= 18 active service interfaces, found {interfaces.Count}");
            Assert.Contains(interfaces, t => t == typeof(ISettingsService));
            Assert.Contains(interfaces, t => t == typeof(IAccountRepository));
            Assert.Contains(interfaces, t => t == typeof(IRobloxApiClient));
            Assert.Contains(interfaces, t => t == typeof(IDialogService));
            Assert.Contains(interfaces, t => t == typeof(ISettingsSideEffects));
        }
    }
}
