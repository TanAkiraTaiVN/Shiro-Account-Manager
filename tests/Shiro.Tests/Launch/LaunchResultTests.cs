using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class LaunchResultTests
    {
        [Fact]
        public void Success_ReturnsSuccessOutcomeAndDefaultMessage()
        {
            var result = LaunchResult.Success();
            Assert.Equal(LaunchOutcome.Success, result.Outcome);
            Assert.Equal("Success", result.Message);
        }

        [Fact]
        public void BrowserHome_ReturnsSuccessOutcomeAndBrowserHomeResult()
        {
            var result = LaunchResult.BrowserHome();
            Assert.Equal(LaunchOutcome.Success, result.Outcome);
            Assert.Equal(Account.BrowserHomeResult, result.Message);
        }

        [Fact]
        public void Fail_RateLimited_PreservesOutcomeAndMessage()
        {
            var result = LaunchResult.Fail(LaunchOutcome.RateLimited, "ERROR: Rate limited (429)");
            Assert.Equal(LaunchOutcome.RateLimited, result.Outcome);
            Assert.Equal("ERROR: Rate limited (429)", result.Message);
        }

        [Fact]
        public void Fail_TicketError_PreservesOutcomeAndMessage()
        {
            var result = LaunchResult.Fail(LaunchOutcome.TicketError, "ERROR: Auth ticket error");
            Assert.Equal(LaunchOutcome.TicketError, result.Outcome);
            Assert.Equal("ERROR: Auth ticket error", result.Message);
        }

        [Fact]
        public void Fail_Timeout_PreservesOutcomeAndMessage()
        {
            var result = LaunchResult.Fail(LaunchOutcome.Timeout, "ERROR: Process launch timed out");
            Assert.Equal(LaunchOutcome.Timeout, result.Outcome);
            Assert.Equal("ERROR: Process launch timed out", result.Message);
        }

        [Fact]
        public void Fail_Cancelled_PreservesOutcomeAndMessage()
        {
            var result = LaunchResult.Fail(LaunchOutcome.Cancelled, "Launch cancelled");
            Assert.Equal(LaunchOutcome.Cancelled, result.Outcome);
            Assert.Equal("Launch cancelled", result.Message);
        }

        [Fact]
        public void Fail_Failed_PreservesOutcomeAndMessage()
        {
            var result = LaunchResult.Fail(LaunchOutcome.Failed, "ERROR: Unsuccessful join");
            Assert.Equal(LaunchOutcome.Failed, result.Outcome);
            Assert.Equal("ERROR: Unsuccessful join", result.Message);
        }

        [Fact]
        public void LaunchResult_Equality_MatchesSameOutcomeAndMessage()
        {
            var a = LaunchResult.Fail(LaunchOutcome.RateLimited, "retry later");
            var b = LaunchResult.Fail(LaunchOutcome.RateLimited, "retry later");
            var c = LaunchResult.Fail(LaunchOutcome.Timeout, "retry later");

            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
        }
    }
}
