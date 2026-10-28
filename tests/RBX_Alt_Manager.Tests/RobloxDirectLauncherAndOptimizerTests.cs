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

        [Theory]
        [InlineData(FastFlagProfile.Potato, 30)]
        [InlineData(FastFlagProfile.UltraFarm, 15)]
        [InlineData(FastFlagProfile.MaxPerformance, 240)]
        [InlineData(FastFlagProfile.HighQuality, 240)]
        public void FastFlagPresetService_GenerateProfileSettings_ContainsExpectedFlags(FastFlagProfile profile, int expectedDefaultFps)
        {
            var settings = FastFlagPresetService.GenerateProfileSettings(profile);
            Assert.NotNull(settings);

            Assert.True(settings.ContainsKey("DFIntTaskSchedulerTargetFps"));
            Assert.Equal(expectedDefaultFps, settings["DFIntTaskSchedulerTargetFps"].GetValue<int>());

            if (profile == FastFlagProfile.UltraFarm)
            {
                Assert.Equal(8, settings["DFIntTextureCompositorLowResFactor"].GetValue<int>());
                Assert.False(settings.ContainsKey("FFlagDebugDisableCrashpad"));
                Assert.True(settings["DFFlagDebugDisableTelemetry"].GetValue<bool>());
            }
            else if (profile == FastFlagProfile.Potato)
            {
                Assert.Equal(4, settings["DFIntTextureCompositorLowResFactor"].GetValue<int>());
                Assert.Equal(0, settings["FIntRenderShadowIntensity"].GetValue<int>());
            }
        }

        [Theory]
        [InlineData("ultrafarm", FastFlagProfile.UltraFarm)]
        [InlineData("farm", FastFlagProfile.UltraFarm)]
        [InlineData("potato", FastFlagProfile.Potato)]
        [InlineData("maxperformance", FastFlagProfile.MaxPerformance)]
        [InlineData("perf", FastFlagProfile.MaxPerformance)]
        [InlineData("highquality", FastFlagProfile.HighQuality)]
        [InlineData("hq", FastFlagProfile.HighQuality)]
        [InlineData("default", FastFlagProfile.Default)]
        [InlineData("unknown", FastFlagProfile.Default)]
        public void FastFlagPresetService_ParseProfile_ResolvesProperly(string input, FastFlagProfile expected)
        {
            var parsed = FastFlagPresetService.ParseProfile(input);
            Assert.Equal(expected, parsed);
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
            AccountManager.AccountsList ??= new System.Collections.Generic.List<Account>();
            var acc = new Account { Username = "TestUserToDelete", UserID = 9999999 };
            AccountManager.AccountsList.Add(acc);
            int removed = AccountManager.RemoveAccountsFromDatabase(new[] { acc });
            Assert.Equal(1, removed);
            Assert.DoesNotContain(acc, AccountManager.AccountsList);
        }
    }
}
