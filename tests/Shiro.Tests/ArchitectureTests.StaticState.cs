#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Reflection;
using NetArchTest.Rules;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public partial class ArchitectureTests
    {
        [Fact]
        public void AccountManager_AccountList_IsNotAccessibleOutsideClass()
        {
            var field = typeof(AccountManager).GetField("AccountsList",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.True(field is null || field.IsPrivate);
        }

        [Fact]
        public void AccountManager_ShouldNotExpose_StaticRestClients()
        {
            var offenders = typeof(AccountManager)
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(Shiro.Http.RestClient))
                .Select(f => f.Name).ToList();
            Assert.Empty(offenders);
        }

        [Fact]
        public void RobloxApiClient_ShouldNotDependOn_AccountManager()
        {
            var result = Types.InAssembly(CoreAssembly)
                .That().HaveName("RobloxApiClient")
                .ShouldNot().HaveDependencyOnAny("Shiro.AccountManager")
                .GetResult();
            Assert.True(result.IsSuccessful);
        }

        [Fact]
        public void RobloxApi_IsSingleInstance_AcrossStartup()
        {
            var before = AppServices.RobloxApi;
            _ = AccountManager.Instance ?? new AccountManager();
            Assert.Same(before, AppServices.RobloxApi);
            Assert.Same(before.Games, AppServices.RobloxApi.Games);
        }

        [Fact]
        public void AccountManager_ShouldNotExpose_StaticStateFacade()
        {
            string[] forbidden = { "SelectedAccounts", "RecentGames", "SelectedAccount", "LastValidAccount", "CurrentPlaceId",
                "CurrentJobId", "CachedServers", "ShuffleJobID", "CurrentVersion", "WSPassword", "IniSettings", "General",
                "Developer", "WebServer", "AccountControl", "Watcher", "Prompts", "Accounts", "AccountCount", "AddAccountThreadSafe" };
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var present = typeof(AccountManager).GetMembers(flags).Select(m => m.Name).Intersect(forbidden).ToList();
            Assert.Empty(present);
        }

        [Fact]
        public void AccountManager_Instance_IsPropertyWithNonPublicSetter()
        {
            var p = typeof(AccountManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
            Assert.NotNull(p);
            Assert.False(p!.SetMethod!.IsPublic);
        }

        [Fact]
        public void Services_ShouldNotDependOn_AccountManager()
        {
            var result = Types.InAssembly(CoreAssembly)
                .That().ResideInNamespace("Shiro.Services")
                .ShouldNot().HaveDependencyOnAny("Shiro.AccountManager")
                .GetResult();
            var failingNames = result.FailingTypes?.Select(t => t.FullName) ?? Enumerable.Empty<string>();
            Assert.True(result.IsSuccessful, string.Join(", ", failingNames));
        }

        [Fact]
        public void StateServiceDefaults_ShouldOnlyBeReferenced_ByCompositionRoot()
        {
            var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "AppServices.cs", "SelectionStateService.cs", "ServerBrowserStateService.cs", "AccountWorkflowService.cs",
                "HostUiBridgeService.cs", "AccountRepository.cs", "AccountPersistenceService.cs",
                "AccountPersistenceService.Recovery.cs", "AccountRegistrationService.cs", "AccountSessionMaintenanceService.cs" };
            var offenders = SourceScan.AppSourceFiles(RepoRoot)
                .Where(f => !allow.Contains(Path.GetFileName(f)))
                .Where(f => Regex.IsMatch(SourceScan.StripStringLiterals(File.ReadAllText(f)),
                    @"\b(SelectionStateService|ServerBrowserStateService|AccountWorkflowService|HostUiBridgeService|AccountRepository|AccountSessionMaintenanceService)\.Default\b"))
                .Select(Path.GetFileName).ToList();
            Assert.Empty(offenders);
        }
    }
}
