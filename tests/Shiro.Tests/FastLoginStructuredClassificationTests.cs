using Microsoft.Web.WebView2.Core;
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

        [Theory]
        [InlineData(CoreWebView2WebResourceContext.Fetch, true)]
        [InlineData(CoreWebView2WebResourceContext.XmlHttpRequest, true)]
        [InlineData(CoreWebView2WebResourceContext.Document, false)]
        [InlineData(CoreWebView2WebResourceContext.Script, false)]
        [InlineData(CoreWebView2WebResourceContext.Image, false)]
        [InlineData(CoreWebView2WebResourceContext.Stylesheet, false)]
        [InlineData(CoreWebView2WebResourceContext.Font, false)]
        [InlineData(CoreWebView2WebResourceContext.All, false)]
        public void ShouldSignWebViewResourceContext_OnlySignsFetchAndXhr(
            CoreWebView2WebResourceContext context,
            bool expected)
        {
            Assert.Equal(expected, FastLoginCoordinator.ShouldSignWebViewResourceContext(context));
        }

        [Theory]
        [InlineData("{\"errors\":[{\"code\":0,\"message\":\"\"}]}", true)]
        [InlineData("Pretty-print\n{\"errors\":[{\"code\":9002,\"subcode\":3,\"message\":\"User is not authenticated\"}]}", true)]
        [InlineData("{\"errors\":[]}", false)]
        [InlineData("{}", false)]
        [InlineData("<html><body><h1>403 Forbidden</h1></body></html>", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsRobloxJsonErrorPayload_IdentifiesRobloxErrorEnvelopesOnly(string? text, bool expected)
        {
            Assert.Equal(expected, FastLoginCoordinator.IsRobloxJsonErrorPayload(text));
        }

        [Theory]
        [InlineData(0, true, false, "", WebViewNavigationFailureAction.ShowNetworkError)]
        [InlineData(0, false, false, null, WebViewNavigationFailureAction.ShowNetworkError)]
        [InlineData(403, true, false, "{\"errors\":[{\"code\":0,\"message\":\"\"}]}", WebViewNavigationFailureAction.FallbackModerationToLogin)]
        [InlineData(401, true, false, "{\"errors\":[{\"code\":9002,\"message\":\"User is not authenticated\"}]}", WebViewNavigationFailureAction.FallbackModerationToLogin)]
        [InlineData(401, true, false, "", WebViewNavigationFailureAction.FallbackModerationToLogin)]
        [InlineData(403, true, true, "{\"errors\":[{\"code\":0,\"message\":\"\"}]}", WebViewNavigationFailureAction.ShowHttpError)]
        [InlineData(403, true, false, "<html>Just a moment...</html>", WebViewNavigationFailureAction.ShowHttpError)]
        [InlineData(403, false, false, "{\"errors\":[{\"code\":0,\"message\":\"\"}]}", WebViewNavigationFailureAction.ShowHttpError)]
        [InlineData(503, false, false, "", WebViewNavigationFailureAction.ShowHttpError)]
        public void ClassifyNavigationFailure_RoutesNetworkHttpAndModerationRejectionsCorrectly(
            int statusCode,
            bool hasRestoredModeration,
            bool alreadyRedirected,
            string? body,
            WebViewNavigationFailureAction expected)
        {
            var actual = FastLoginCoordinator.ClassifyNavigationFailure(
                statusCode,
                hasRestoredModeration,
                alreadyRedirected,
                body);
            Assert.Equal(expected, actual);
        }
    }
}
