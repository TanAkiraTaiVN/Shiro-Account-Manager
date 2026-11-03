using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Shiro.Services;

namespace Shiro.Tests
{
    public class RefactoredServicesTests
    {
        [Fact]
        public void FastJson_SerializeAndDeserialize_WorksCorrectly()
        {
            var testObj = new TestPayload { Name = "Shiro", Value = 12345 };
            string json = FastJson.Serialize(testObj);

            Assert.NotNull(json);
            Assert.Contains("Shiro", json);

            bool ok = FastJson.TryDeserialize<TestPayload>(json, out var deserialized);
            Assert.True(ok);
            Assert.NotNull(deserialized);
            Assert.Equal("Shiro", deserialized.Name);
            Assert.Equal(12345, deserialized.Value);
        }

        [Fact]
        public void JsonExtensions_TryParseJson_ReturnsFalseForInvalidJson()
        {
            string invalidJson = "{ invalid json content: 123 ";
            bool ok = invalidJson.TryParseJson<TestPayload>(out var result);

            Assert.False(ok);
            Assert.Null(result);
        }

        [Fact]
        public void CryptoHelper_SHA256_ComputesCorrectHash()
        {
            string input = "hello world";
            string sha256 = CryptoHelper.SHA256(input);

            // SHA256 of "hello world" in hex
            Assert.Equal("B94D27B9934D3E08A52E52D7DA7DABFAC484EFE37A5380EE9088F7ACE2EFCDE9", sha256.ToUpperInvariant());
        }



        [Fact]
        public void CryptoHelper_FileSHA256_ReturnsDefaultHashForMissingFile()
        {
            string nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string hash = CryptoHelper.FileSHA256(nonExistentPath);

            // Empty string SHA256 default marker
            Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", hash.ToUpperInvariant());
        }

        [Fact]
        public void RobloxPlaceHelper_MapValue_CalculatesAccurately()
        {
            double mapped = RobloxPlaceHelper.MapValue(50, 0, 100, 0, 1000);
            Assert.Equal(500, mapped);
        }

        [Fact]
        public void RobloxPlaceHelper_ToRobloxTick_ReturnsZeroForUnixEpochOrEarlier()
        {
            DateTime epoch = DateTime.UnixEpoch;
            double tick = RobloxPlaceHelper.ToRobloxTick(epoch);
            Assert.Equal(0, tick);
        }

        [Fact]
        public void AppServices_Defaults_AreNotNull()
        {
            Assert.NotNull(AppServices.AccountRepository);
            Assert.NotNull(AppServices.RobloxSessionService);
            Assert.NotNull(AppServices.RobloxLauncherService);
            Assert.NotNull(AppServices.DialogService);
            Assert.NotNull(AppServices.AccountWorkflowService);
            Assert.NotNull(AppServices.RobloxServerQueryService);
            Assert.NotNull(AppServices.Settings);
        }

        [Fact]
        public void AccountRepository_GetById_ReturnsNullWhenNotFound()
        {
            var repo = AppServices.AccountRepository;
            var acc = repo.GetById(999999999999L);
            Assert.Null(acc);
        }

        [Fact]
        public void AccountRepository_GetByUsername_ReturnsNullWhenNotFound()
        {
            var repo = AppServices.AccountRepository;
            var acc = repo.GetByUsername("NonExistentUser12345");
            Assert.Null(acc);
        }

        [Fact]
        public void ChromeLaunchHelper_BuildLaunchArgs_ContainsRequiredFlags()
        {
            var args = ChromeLaunchHelper.BuildLaunchArgs("C:\\FakeProfile", "http://127.0.0.1:8080", headless: true);

            Assert.Contains("--no-first-run", args);
            Assert.Contains("--user-data-dir=C:\\FakeProfile", args);
            Assert.Contains("--proxy-server=http://127.0.0.1:8080", args);
            Assert.Contains("--headless=new", args);
        }

        [Fact]
        public async Task RobloxServerQueryService_QueryPublicServersAsync_WithZeroPlaceId_ReturnsNotSuccess()
        {
            var service = AppServices.RobloxServerQueryService;
            var result = await service.QueryPublicServersAsync(0);

            Assert.NotNull(result);
            Assert.False(result.Success);
            Assert.Empty(result.Servers);
        }

        [Fact]
        public void PrerequisiteService_NeedsVCRedist_DoesNotThrow()
        {
            var ex = Record.Exception(() => PrerequisiteService.NeedsVCRedist());
            Assert.Null(ex);
        }

        [Fact]
        public void AppSettings_Properties_ReturnExpectedDefaults()
        {
            var settings = AppSettings.Current;
            Assert.NotNull(settings);

            // Verify default bounds and fallbacks
            Assert.True(settings.AccountJoinDelay >= 1);
            Assert.True(settings.PresenceUpdateRate >= 1);
            Assert.False(string.IsNullOrEmpty(settings.SavedPlaceId));
        }

        private class TestPayload
        {
            public string Name { get; set; } = string.Empty;
            public int Value { get; set; }
        }
    }
}
