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
    [Collection("StaticSeams")]
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
        public async Task Auth_BloxGenService_OnGenerationException_LogsErrorToInjectedLogger()
        {
            var mockLogger = Substitute.For<IAppLogger>();
            var mockSettings = Substitute.For<ISettingsService>();
            var handler = new FaultingHttpMessageHandler();
            using var httpClient = new System.Net.Http.HttpClient(handler)
            {
                BaseAddress = new Uri("https://bloxgen.net/api/")
            };
            var service = new BloxGenService(httpClient, mockLogger, mockSettings);

            var result = await service.GenerateAccountAsync("test-api-key");

            Assert.False(result.Success);
            mockLogger.Received().Error(Arg.Is<object>(o => o.ToString()!.Contains("[BloxGenService] Generation failed")));
        }

        private sealed class FaultingHttpMessageHandler : System.Net.Http.HttpMessageHandler
        {
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
                System.Net.Http.HttpRequestMessage request,
                System.Threading.CancellationToken cancellationToken)
            {
                throw new System.Net.Http.HttpRequestException("Simulated auth network fault");
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

            using var _ = AppServices.Override<IRobloxApiClient>(mockRobloxApi);
            var vm = new ServerListViewModel(null, mockLogger, null)
            {
                PlaceIdInput = "123456"
            };

            await vm.RefreshServersAsync();

            Assert.Equal(0, vm.TotalServersCount);
            mockLogger.Received().Error(Arg.Is<object>(o => o.ToString()!.Contains("[RefreshServersAsync]")));
        }
    }
}
