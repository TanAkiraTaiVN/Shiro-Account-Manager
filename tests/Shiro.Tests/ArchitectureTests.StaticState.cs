#nullable enable
using System.Linq;
using System.Reflection;
using NetArchTest.Rules;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public partial class ArchitectureTests
    {
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
    }
}
