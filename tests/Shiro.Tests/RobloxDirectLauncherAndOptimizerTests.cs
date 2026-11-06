using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class RobloxDirectLauncherAndOptimizerTests
    {
        [Fact]
        public void RobloxDirectLauncher_EmptyTicketOrJoinUrl_ReturnsFailureWithoutCrashing()
        {
            var res1 = RobloxDirectLauncher.LaunchDirect("", "http://roblox.com", "user");
            Assert.False(res1.Success);
            Assert.Contains("Ticket or JoinUrl is empty", res1.ErrorMessage);

            var res2 = RobloxDirectLauncher.LaunchDirect("ticket", "", "user");
            Assert.False(res2.Success);
            Assert.Contains("Ticket or JoinUrl is empty", res2.ErrorMessage);
        }

        [Fact]
        public void RobloxProcessOptimizer_Constants_HaveExpectedWin32Values()
        {
            Assert.Equal(0x00000040u, RobloxProcessOptimizer.IDLE_PRIORITY_CLASS);
            Assert.Equal(0x00004000u, RobloxProcessOptimizer.BELOW_NORMAL_PRIORITY_CLASS);
            Assert.Equal(0x00000020u, RobloxProcessOptimizer.NORMAL_PRIORITY_CLASS);
            Assert.Equal(0x00000080u, RobloxProcessOptimizer.HIGH_PRIORITY_CLASS);
        }

        [Fact]
        public void RobloxProcessOptimizer_InvalidPid_ReturnsGracefully()
        {
            Assert.False(RobloxProcessOptimizer.SetProcessPriority(-1, RobloxProcessOptimizer.IDLE_PRIORITY_CLASS));
            Assert.False(RobloxProcessOptimizer.SetProcessAffinity(-1, 0x1));
            Assert.Equal(0, RobloxProcessOptimizer.LowerPriorityForPids(new[] { -1, 0 }));
            Assert.Equal(0, RobloxProcessOptimizer.RestorePriorityForPids(new[] { -1, 0 }));
        }

        [Fact]
        public void ApiDocumentationService_GetOpenApiJson_ReturnsValidOpenApiSpec()
        {
            string json = ApiDocumentationService.GetOpenApiJson();
            Assert.False(string.IsNullOrWhiteSpace(json));

            var node = JsonNode.Parse(json) as JsonObject;
            Assert.NotNull(node);
            Assert.Equal("3.0.1", node["openapi"]?.ToString());
            Assert.NotNull(node["paths"]);
            Assert.True(node["paths"] is JsonObject paths && paths.ContainsKey("/Running"));
            Assert.True(node["paths"] is JsonObject paths2 && paths2.ContainsKey("/GetAccounts"));
            Assert.True(node["paths"] is JsonObject paths3 && paths3.ContainsKey("/LaunchAccount"));
            Assert.True(node["paths"] is JsonObject paths4 && paths4.ContainsKey("/KillRoblox"));
            Assert.True(node["paths"] is JsonObject paths5 && paths5.ContainsKey("/AntiAfk/Status"));
        }

        [Fact]
        public void ApiDocumentationService_GetDocsHtml_ReturnsValidHtmlPage()
        {
            string html = ApiDocumentationService.GetDocsHtml();
            Assert.False(string.IsNullOrWhiteSpace(html));
            Assert.Contains("<!DOCTYPE html>", html);
            Assert.Contains("Shiro Account Manager • REST API", html);
            Assert.Contains("/swagger.json", html);
        }

        [Fact]
        public void Account_IsDirty_TracksModificationsAccurately()
        {
            var acc = new Account();
            acc.IsDirty = false;
            Assert.False(acc.IsDirty);

            acc.Alias = "NewAlias123";
            Assert.True(acc.IsDirty);

            acc.IsDirty = false;
            acc.Description = "New Description";
            Assert.True(acc.IsDirty);

            acc.IsDirty = false;
            acc.Password = "NewPassword123";
            Assert.True(acc.IsDirty);

            acc.IsDirty = false;
            acc.SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--token123";
            Assert.True(acc.IsDirty);

            acc.IsDirty = false;
            acc.SetField("CustomField", "Value123");
            Assert.True(acc.IsDirty);

            acc.IsDirty = false;
            acc.RemoveField("CustomField");
            Assert.True(acc.IsDirty);
        }

        [Fact]
        public void RemoveAccountsFromDatabase_WhenEmpty_BypassesCountCheckAndClears()
        {
            AccountManager.ResetAccountsForTests(Array.Empty<Account>());
            var acc = new Account { Username = "TestUserToDelete", UserID = 9999999 };
            AccountManager.AddAccountThreadSafe(acc);
            int removed = AccountManager.RemoveAccountsFromDatabase(new[] { acc });
            Assert.Equal(1, removed);
            Assert.DoesNotContain(acc, AccountManager.GetAccountsSnapshot());
        }

        [Fact]
        public void RobloxDirectLauncher_GenerateRandomTrackerId_ReturnsValidRandomDigits()
        {
            string id1 = RobloxDirectLauncher.GenerateRandomTrackerId();
            string id2 = RobloxDirectLauncher.GenerateRandomTrackerId();

            Assert.False(string.IsNullOrWhiteSpace(id1));
            Assert.False(string.IsNullOrWhiteSpace(id2));
            Assert.True(id1.Length >= 11 && id1.Length <= 13);
            Assert.All(id1, c => Assert.True(char.IsDigit(c)));
            Assert.NotEqual("121002137941", id1);
        }

        [Fact]
        public void RobloxDirectLauncher_InjectAndRestoreCookies_PreservesOriginalBackupAndRestoresAtomically()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ShiroDirectLauncherTest_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string cookiesDatPath = System.IO.Path.Combine(tempDir, "RobloxCookies.dat");
                string origPayload = "{\"CookiesVersion\":\"1\",\"CookiesData\":\"ORIGINAL_ENCRYPTED_USER_SESSION\"}";
                System.IO.File.WriteAllText(cookiesDatPath, origPayload);

                // Inject new account cookie
                string dummyToken = "_|WARNING:-DO-NOT-SHARE-THIS.--testtoken";
                bool injected = RobloxDirectLauncher.InjectAccountCookieToLocalStorage(
                    dummyToken,
                    browserTrackerId: "123456789012",
                    expiryUtc: DateTimeOffset.UtcNow.AddMonths(1),
                    localStorageDirectoryOverride: tempDir);

                Assert.True(injected);
                Assert.True(System.IO.File.Exists(cookiesDatPath));
                string backupPath = System.IO.Path.Combine(tempDir, "RobloxCookies.dat.shiro-orig");
                Assert.True(System.IO.File.Exists(backupPath), "Backup file should be created before overwriting");
                Assert.Equal(origPayload, System.IO.File.ReadAllText(backupPath));

                // Verify injected content is different and contains CookiesVersion 1
                string injectedJson = System.IO.File.ReadAllText(cookiesDatPath);
                Assert.Contains("\"CookiesVersion\":\"1\"", injectedJson);
                Assert.NotEqual(origPayload, injectedJson);

                // Restore original cookies
                bool restored = RobloxDirectLauncher.RestoreOriginalCookies(tempDir);
                Assert.True(restored);
                Assert.False(System.IO.File.Exists(backupPath), "Backup file should be cleaned up after restoration");
                Assert.Equal(origPayload, System.IO.File.ReadAllText(cookiesDatPath));
            }
            finally
            {
                try { System.IO.Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void RobloxDirectLauncher_InjectCookies_RejectsUnsupportedCookiesVersion()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ShiroDirectLauncherVersionTest_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string cookiesDatPath = System.IO.Path.Combine(tempDir, "RobloxCookies.dat");
                string futurePayload = "{\"CookiesVersion\":\"99\",\"CookiesData\":\"FUTURE_INCOMPATIBLE_DATA\"}";
                System.IO.File.WriteAllText(cookiesDatPath, futurePayload);

                bool injected = RobloxDirectLauncher.InjectAccountCookieToLocalStorage(
                    "dummy_token",
                    localStorageDirectoryOverride: tempDir);

                Assert.False(injected, "Should gracefully refuse injection when CookiesVersion is unknown");
                Assert.Equal(futurePayload, System.IO.File.ReadAllText(cookiesDatPath));
            }
            finally
            {
                try { System.IO.Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void RobloxDirectLauncher_BuildGameLaunchArguments_AlwaysIncludesTicketAndJoinUrl()
        {
            string args = RobloxDirectLauncher.BuildGameLaunchArguments(
                "TEST_TICKET_123",
                "https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame&placeId=123",
                "123456789012");

            Assert.Contains("-t \"TEST_TICKET_123\"", args);
            Assert.Contains("-j \"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame&placeId=123\"", args);
            Assert.Contains("-b 123456789012", args);
        }
    }
}
