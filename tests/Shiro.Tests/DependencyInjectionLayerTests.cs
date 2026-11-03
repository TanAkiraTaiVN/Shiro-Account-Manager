#nullable enable
using System;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Classes;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    public class DependencyInjectionLayerTests
    {
        [Fact]
        public async Task Network_RobloxSettingsApiService_WhenSessionInvalid_LogsWarningToInjectedLogger()
        {
            var mockLogger = Substitute.For<IAppLogger>();
            var service = new RobloxSettingsApiService(mockLogger);
            var account = new Account { Username = "TestUser", SecurityToken = "" };

            var result = await service.SetFollowPrivacyAsync(account, 0);

            Assert.False(result);
            mockLogger.Received().Warn(Arg.Is<object>(o => o.ToString()!.Contains("Invalid Account Session")));
        }

        [Fact]
        public async Task Auth_SessionAuditorService_OnAuditException_LogsWarningToInjectedLogger()
        {
            var mockLogger = Substitute.For<IAppLogger>();
            SessionAuditorService.Logger = mockLogger;
            SessionAuditorService.TestHttpClientFactory = _ => throw new InvalidOperationException("Simulated audit network fault");
            var account = new Account { Username = "TestUser", SecurityToken = "valid_security_token" };

            try
            {
                var result = await SessionAuditorService.AuditAccountAsync(account);

                Assert.Equal(AccountSessionStatus.Unknown, result.Status);
                mockLogger.Received().Warn(Arg.Is<object>(o => o.ToString()!.Contains("Audit exception")));
            }
            finally
            {
                SessionAuditorService.TestHttpClientFactory = null;
                SessionAuditorService.Logger = AppServices.Logger;
            }
        }

        [Fact]
        public void Storage_AccountStore_WhenPrimaryFileLocked_LogsWarningToInjectedLogger()
        {
            var mockLogger = Substitute.For<IAppLogger>();
            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);
            var primary = System.IO.Path.Combine(tempDir, "AccountData.json");
            System.IO.File.WriteAllText(primary, "dummy content");

            try
            {
                using var stream = new System.IO.FileStream(primary, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
                var store = new AccountStore(primary, tempDir, mockLogger);
                var data = store.Read();

                Assert.Equal(Array.Empty<byte>(), data);
                mockLogger.Received().Warn(Arg.Is<object>(o => o.ToString()!.Contains("Could not read account data")));
            }
            finally
            {
                if (System.IO.Directory.Exists(tempDir))
                    System.IO.Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task ViewModels_ServerListViewModel_WhenRefreshServersFails_LogsErrorToInjectedLogger()
        {
            var mockLogger = Substitute.For<IAppLogger>();
            var mockRobloxApi = Substitute.For<IRobloxApiClient>();
            mockRobloxApi.Games.Returns(_ => throw new InvalidOperationException("Simulated network outage"));

            var originalApi = AppServices.RobloxApi;
            try
            {
                AppServices.RobloxApi = mockRobloxApi;
                var vm = new ServerListViewModel(null, mockLogger, null)
                {
                    PlaceIdInput = "123456"
                };

                await vm.RefreshServersAsync();

                Assert.Equal(0, vm.TotalServersCount);
                mockLogger.Received().Error(Arg.Is<object>(o => o.ToString()!.Contains("[RefreshServersAsync]")));
            }
            finally
            {
                AppServices.RobloxApi = originalApi;
            }
        }
    }
}
