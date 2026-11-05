using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class FastLoginStructuredClassificationTests
    {
        [Theory]
        [InlineData("{\"errors\":[{\"code\":1,\"message\":\"Incorrect username or password.\"}]}", 403, "WRONG_PASSWORD")]
        [InlineData("{\"errors\":[{\"code\":4,\"message\":\"Account has been locked.\"}]}", 403, "ACCOUNT_LOCKED")]
        [InlineData("{\"errors\":[{\"code\":9002,\"message\":\"User is not authenticated.\"}]}", 401, "UNAUTHENTICATED_MODERATION")]
        [InlineData("{\"punishmentTypeData\":{\"punishmentType\":\"Ban 7 Days\"}}", 200, "BAN_7_DAYS")]
        [InlineData("{\"punishmentTypeData\":{\"punishmentType\":\"Ban 3 Days\"}}", 200, "BAN_3_DAYS")]
        [InlineData("{\"punishmentTypeData\":{\"punishmentType\":\"Ban 1 Day\"}}", 200, "BAN_1_DAY")]
        [InlineData("{\"punishmentTypeData\":{\"punishmentType\":\"Ban 14 Days\"}}", 200, "BAN_14_DAYS")]
        [InlineData("{\"punishmentTypeData\":{\"punishmentType\":\"Delete\"}}", 200, "TERMINATED")]
        [InlineData("{\"punishmentTypeData\":{\"punishmentType\":\"Warn\"}}", 200, "WARNING")]
        [InlineData("{}", 200, "")]
        public void ClassifyAuthOrModerationApiPayload_ParsesStructuredRobloxApiResponses(
            string json,
            int statusCode,
            string expectedCode)
        {
            string actual = FastLoginCoordinator.ClassifyAuthOrModerationApiPayload(json, statusCode);
            Assert.Equal(expectedCode, actual);
        }

        [Fact]
        public void BuildCredentialFillScript_IncludesMutationObserver()
        {
            string script = FastLoginCoordinator.BuildCredentialFillScript("ShiroUser", "Pass123!", forceClick: true);
            Assert.Contains("MutationObserver", script);
        }
    }
}
