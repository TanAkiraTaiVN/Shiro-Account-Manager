#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class RealFeatureIntegrationTests
    {
        [Fact]
        public void DatacenterService_LoadsDataSuccessfully()
        {
            var service = RobloxDatacenterService.Default;
            Assert.True(service.IsLoaded, "Datacenter database should be loaded successfully.");
            Assert.True(service.TotalDatacenters >= 20, $"Expected at least 20 datacenters, got {service.TotalDatacenters}");
        }

        [Theory]
        [InlineData("128.116.46.33", "Singapore", "SG")]
        [InlineData("128.116.53.33", "Leesburg", "US")]
        [InlineData("128.116.33.33", "London", "GB")]
        [InlineData("128.116.55.33", "Tokyo", "JP")]
        [InlineData("128.116.44.33", "Frankfurt", "DE")]
        public void DatacenterService_ResolvesKnownIps(string ip, string expectedCity, string expectedCountry)
        {
            var service = RobloxDatacenterService.Default;
            bool resolved = service.TryResolve(ip, null, out var resolution);

            Assert.True(resolved, $"Failed to resolve IP {ip}");
            Assert.NotNull(resolution);
            Assert.Equal(expectedCity, resolution.City);
            Assert.Equal(expectedCountry, resolution.CountryCode);
            Assert.False(string.IsNullOrEmpty(resolution.FlagEmoji));
        }

        [Fact]
        public void DatacenterService_ResolvesKnownDataCenterId()
        {
            var service = RobloxDatacenterService.Default;
            // Datacenter ID 295 belongs to Singapore
            bool resolved = service.TryResolve(null, 295, out var resolution);

            Assert.True(resolved, "Failed to resolve DataCenterId 295");
            Assert.NotNull(resolution);
            Assert.Equal("Singapore", resolution.City);
            Assert.Equal("SG", resolution.CountryCode);
        }

        [Theory]
        [InlineData("SG", "🇸🇬")]
        [InlineData("US", "🇺🇸")]
        [InlineData("VN", "🇻🇳")]
        [InlineData("JP", "🇯🇵")]
        [InlineData("DE", "🇩🇪")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("INVALID", "")]
        public void DatacenterService_GeneratesCorrectFlagEmoji(string? code, string expectedEmoji)
        {
            string flag = RobloxDatacenterService.GetCountryFlagEmoji(code);
            Assert.Equal(expectedEmoji, flag);
        }

        [Fact]
        public void StreamerModeService_MaskingUtilitiesWorkProperly()
        {
            string maskedUser = StreamerModeService.MaskUsername("RobloxMaster");
            Assert.StartsWith("R", maskedUser);
            Assert.EndsWith("r", maskedUser);
            Assert.Equal(12, maskedUser.Length);
            Assert.Contains("*", maskedUser);

            Assert.Equal("**", StreamerModeService.MaskUsername("ab"));
            Assert.Equal(string.Empty, StreamerModeService.MaskUsername(string.Empty));

            Assert.Equal("*** R$", StreamerModeService.MaskRobux(50000));
            Assert.Equal("***-***", StreamerModeService.MaskJobId("job-1234-5678"));
        }

        [Fact]
        public void StreamerModeService_StateChangedEventFires()
        {
            bool eventFired = false;
            bool lastState = false;

            Action<bool> handler = active =>
            {
                eventFired = true;
                lastState = active;
            };

            StreamerModeService.StateChanged += handler;
            try
            {
                bool initial = StreamerModeService.IsEnabled;
                StreamerModeService.IsEnabled = !initial;

                Assert.True(eventFired, "StateChanged event should have fired when IsEnabled was toggled.");
                Assert.Equal(StreamerModeService.IsActive, lastState);

                // Revert
                StreamerModeService.IsEnabled = initial;
            }
            finally
            {
                StreamerModeService.StateChanged -= handler;
            }
        }

        [Fact]
        public async Task BloxGenService_GeneratesAccountSuccessfully()
        {
            var mockResponseJson = JsonSerializer.Serialize(new
            {
                success = true,
                remaining_balance = 49.5,
                data = new
                {
                    user_id = 123456789,
                    username = "BloxAltUser",
                    password = "SafeAltPassword123!",
                    cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-to-your-account-and-to-steal-your-ROBUX-and-all-of-your-items.|_SAMPLECOOKIE123"
                }
            });

            var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, mockResponseJson);

            using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://core.bloxgen.net/api/") };
            var service = new BloxGenService(httpClient);

            var result = await service.GenerateAccountAsync("BLOX-TEST-API-KEY", "unverified", "AS");

            Assert.True(result.Success);
            Assert.NotNull(result.Account);
            Assert.Equal("BloxAltUser", result.Account.Username);
            Assert.Equal(123456789, result.Account.UserId);
            Assert.Equal("SafeAltPassword123!", result.Account.Password);
            Assert.StartsWith("_|WARNING", result.Account.Cookie);
            Assert.Equal("AS", result.Account.Region);
            Assert.Equal(49.5, result.RemainingBalance);
        }

        [Fact]
        public async Task BloxGenService_FailsGracefullyOnMissingKey()
        {
            using var httpClient = new HttpClient { BaseAddress = new Uri("https://core.bloxgen.net/api/") };
            var service = new BloxGenService(httpClient);

            var result = await service.GenerateAccountAsync(string.Empty);
            Assert.False(result.Success);
            Assert.Contains("API Key", result.ErrorMessage);
        }

        [Fact]
        public void DiscordRpcService_InitializesCleanly()
        {
            var service = DiscordRpcService.Default;
            Assert.NotNull(service);

            // Calling UpdatePresence when disconnected should not throw
            var ex = Record.Exception(() =>
            {
                service.UpdatePresence("Blox Fruits", 2753915549, "test-job-id", 5, 2);
            });
            Assert.Null(ex);
        }

        [Fact]
        public void RobloxDirectLauncher_ElevationCheckReturnsBool()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            bool expected = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

            bool isElevated = RobloxDirectLauncher.IsCurrentProcessElevated();
            Assert.Equal(expected, isElevated);
        }
    }
}
