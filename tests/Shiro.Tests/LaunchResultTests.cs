using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class LaunchResultTests
    {
        [Fact]
        public void Legacy_ExactSuccess_IsSuccess()
        {
            var result = LaunchResult.FromLegacy("Success");
            Assert.Equal(LaunchOutcome.Success, result.Outcome);
            Assert.Equal("Success", result.Message);
        }

        [Fact]
        public void Legacy_BrowserHome_IsSuccess()
        {
            var result = LaunchResult.FromLegacy(Account.BrowserHomeResult);
            Assert.Equal(LaunchOutcome.Success, result.Outcome);
            Assert.Equal(Account.BrowserHomeResult, result.Message);
        }

        [Fact]
        public void Legacy_Unsuccessful_IsFailed()
        {
            var result = LaunchResult.FromLegacy("ERROR: Unsuccessful join");
            Assert.Equal(LaunchOutcome.Failed, result.Outcome);
            Assert.Equal("ERROR: Unsuccessful join", result.Message);
        }

        [Fact]
        public void Legacy_UsernameWith429_IsFailed()
        {
            var result = LaunchResult.FromLegacy("ERROR: Failed to launch Roblox for user429");
            Assert.Equal(LaunchOutcome.Failed, result.Outcome);
            Assert.Equal("ERROR: Failed to launch Roblox for user429", result.Message);
        }

        [Fact]
        public void Fail_PreservesMessage()
        {
            var result = LaunchResult.Fail(LaunchOutcome.RateLimited, "ERROR: x");
            Assert.Equal(LaunchOutcome.RateLimited, result.Outcome);
            Assert.Equal("ERROR: x", result.Message);
            Assert.Equal("ERROR: x", result.ToLegacyString());
        }

        [Fact]
        public void Success_ReturnsSuccessOutcomeAndMessage()
        {
            var result = LaunchResult.Success();
            Assert.Equal(LaunchOutcome.Success, result.Outcome);
            Assert.Equal("Success", result.Message);
            Assert.Equal("Success", result.ToLegacyString());
        }

        [Fact]
        public void BrowserHome_ReturnsSuccessOutcomeAndBrowserHomeResult()
        {
            var result = LaunchResult.BrowserHome();
            Assert.Equal(LaunchOutcome.Success, result.Outcome);
            Assert.Equal(Account.BrowserHomeResult, result.Message);
            Assert.Equal(Account.BrowserHomeResult, result.ToLegacyString());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Legacy_NullOrWhitespace_IsFailed(string? legacy)
        {
            var result = LaunchResult.FromLegacy(legacy);
            Assert.Equal(LaunchOutcome.Failed, result.Outcome);
        }
    }
}
