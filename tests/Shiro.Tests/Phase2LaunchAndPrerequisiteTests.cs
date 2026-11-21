#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class Phase2LaunchAndPrerequisiteTests
    {
        private static string CreateTempCookiesDatFile(string trackerParams)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroCookieTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string filePath = Path.Combine(tempDir, "RobloxCookies.dat");

            string netscape = $".roblox.com\tTRUE\t/\tTRUE\t0\tRBXEventTrackerV2\tCreateDate=11/21/2026 00:00:00&{trackerParams}\n";
            byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(netscape), null, DataProtectionScope.CurrentUser);
            string json = JsonSerializer.Serialize(new
            {
                CookiesVersion = "1",
                CookiesData = Convert.ToBase64String(encrypted)
            });
            File.WriteAllText(filePath, json);
            return filePath;
        }

        [Fact]
        public void RobloxCookieFileHelper_ExtractsUserIdFromBothRbxIdAndRbxUid()
        {
            string fileWithRbxId = CreateTempCookiesDatFile("rbxid=44556677&browserid=trk-1");
            string fileWithRbxUid = CreateTempCookiesDatFile("rbxuid=88990011&browserid=trk-2");

            try
            {
                bool ok1 = RobloxCookieFileHelper.TryExtractTrackerMetadataFromCookiesDatFile(
                    fileWithRbxId, out long userId1, out string? browserId1);
                Assert.True(ok1);
                Assert.Equal(44556677L, userId1);
                Assert.Equal("trk-1", browserId1);

                bool ok2 = RobloxCookieFileHelper.TryExtractTrackerMetadataFromCookiesDatFile(
                    fileWithRbxUid, out long userId2, out string? browserId2);
                Assert.True(ok2);
                Assert.Equal(88990011L, userId2);
                Assert.Equal("trk-2", browserId2);

                var mismatchedAccount = new Account
                {
                    Username = "MismatchedUser",
                    UserID = 11111L,
                    BrowserTrackerID = "trk-1"
                };

                Assert.False(RobloxCookieFileHelper.CanSyncRotatedCookieForTarget(
                    mismatchedAccount, userId1, browserId1, expectedBrowserId: "trk-1"));
            }
            finally
            {
                string? dir1 = Path.GetDirectoryName(fileWithRbxId);
                if (!string.IsNullOrEmpty(dir1) && Directory.Exists(dir1)) Directory.Delete(dir1, true);
                string? dir2 = Path.GetDirectoryName(fileWithRbxUid);
                if (!string.IsNullOrEmpty(dir2) && Directory.Exists(dir2)) Directory.Delete(dir2, true);
            }
        }

        [Fact]
        public void RobloxDirectLauncher_DeElevation_UsesUnicodeEnvironmentBlockAndDestroysIt()
        {
            Assert.Equal((0x00000400u, new IntPtr(0x1234)), RobloxDirectLauncher.ResolveDeElevationEnvironmentArgs(new IntPtr(0x1234)));
            Assert.Equal((0u, IntPtr.Zero), RobloxDirectLauncher.ResolveDeElevationEnvironmentArgs(IntPtr.Zero));

            string deElevPath = Path.Combine(
                ArchitectureTests.RepoRoot,
                "src", "Shiro", "Services", "Launch", "RobloxDirectLauncher.DeElevation.cs");
            string source = File.ReadAllText(deElevPath);

            int createIdx = source.IndexOf("CreateEnvironmentBlock(", StringComparison.Ordinal);
            Assert.True(createIdx > 0, "Expected TrySpawnDeElevated to invoke CreateEnvironmentBlock.");

            int finallyIdx = source.IndexOf("finally", createIdx, StringComparison.Ordinal);
            Assert.True(finallyIdx > createIdx, "Expected a finally block after CreateEnvironmentBlock.");

            int destroyIdx = source.IndexOf("DestroyEnvironmentBlock(", finallyIdx, StringComparison.Ordinal);
            Assert.True(destroyIdx > finallyIdx, "Expected DestroyEnvironmentBlock cleanup inside finally block.");
        }

        [Fact]
        public async Task PrerequisiteService_PromptAndInstallVCRedistAsync_RequiresExplicitConfirmation()
        {
            var declineSettings = new IniFile().Section("Prompts");
            int declineInstallCalls = 0;
            bool declinedResult = await PrerequisiteService.PromptAndInstallVCRedistAsync(
                declineSettings,
                confirmAsync: (msg, title) =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(msg));
                    Assert.False(string.IsNullOrWhiteSpace(title));
                    return Task.FromResult(false);
                },
                needsVcRedistOverride: () => true,
                installOverride: _ =>
                {
                    declineInstallCalls++;
                    return Task.FromResult(true);
                });

            Assert.False(declinedResult);
            Assert.Equal("true", declineSettings.Get("VCPrompted"));
            Assert.Equal(0, declineInstallCalls);

            var acceptSettings = new IniFile().Section("Prompts");
            int acceptInstallCalls = 0;
            bool acceptedResult = await PrerequisiteService.PromptAndInstallVCRedistAsync(
                acceptSettings,
                confirmAsync: (_, _) => Task.FromResult(true),
                needsVcRedistOverride: () => true,
                installOverride: _ =>
                {
                    acceptInstallCalls++;
                    return Task.FromResult(true);
                });

            Assert.True(acceptedResult);
            Assert.Equal("true", acceptSettings.Get("VCPrompted"));
            Assert.Equal(1, acceptInstallCalls);

            var alreadyPromptedSettings = new IniFile().Section("Prompts");
            alreadyPromptedSettings.Set("VCPrompted", "true");
            int confirmCalls = 0;
            int alreadyPromptedInstallCalls = 0;
            bool alreadyPromptedResult = await PrerequisiteService.PromptAndInstallVCRedistAsync(
                alreadyPromptedSettings,
                confirmAsync: (_, _) =>
                {
                    confirmCalls++;
                    return Task.FromResult(true);
                },
                needsVcRedistOverride: () => true,
                installOverride: _ =>
                {
                    alreadyPromptedInstallCalls++;
                    return Task.FromResult(true);
                });

            Assert.False(alreadyPromptedResult);
            Assert.Equal(0, confirmCalls);
            Assert.Equal(0, alreadyPromptedInstallCalls);

            string accountManagerSource = File.ReadAllText(Path.Combine(ArchitectureTests.RepoRoot, "src", "Shiro", "AccountManager.cs"));
            Assert.DoesNotContain("InstallVCRedistAsync", accountManagerSource, StringComparison.Ordinal);
        }

        [Fact]
        public void Localization_VcRedistPromptKeys_ExistInBothEnglishAndVietnamese()
        {
            LocalizationService.EnsureCoreKeys();

            string enTitle;
            string enMessage;
            using (LocalizationService.OverrideLanguageForTest(AppLanguage.English))
            {
                enTitle = LocalizationService.Get("Prerequisite.VcRedistPromptTitle", "__MISSING__");
                enMessage = LocalizationService.Get("Prerequisite.VcRedistPromptMessage", "__MISSING__");
            }

            string viTitle;
            string viMessage;
            using (LocalizationService.OverrideLanguageForTest(AppLanguage.Vietnamese))
            {
                viTitle = LocalizationService.Get("Prerequisite.VcRedistPromptTitle", "__MISSING__");
                viMessage = LocalizationService.Get("Prerequisite.VcRedistPromptMessage", "__MISSING__");
            }

            Assert.NotEqual("__MISSING__", enTitle);
            Assert.NotEqual("__MISSING__", enMessage);
            Assert.NotEqual("__MISSING__", viTitle);
            Assert.NotEqual("__MISSING__", viMessage);

            Assert.False(string.IsNullOrWhiteSpace(enTitle));
            Assert.False(string.IsNullOrWhiteSpace(enMessage));
            Assert.False(string.IsNullOrWhiteSpace(viTitle));
            Assert.False(string.IsNullOrWhiteSpace(viMessage));

            Assert.NotEqual(enTitle, viTitle);
            Assert.NotEqual(enMessage, viMessage);
        }
    }
}
