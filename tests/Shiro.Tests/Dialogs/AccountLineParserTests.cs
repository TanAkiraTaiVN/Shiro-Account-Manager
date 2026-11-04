#nullable enable
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Dialogs
{
    public class AccountLineParserTests
    {
        private static readonly string SampleCookie =
            "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_0123456789ABCDEF0123456789ABCDEF";

        [Theory]
        [InlineData("user:pass", "user", "pass")]
        [InlineData("user|pass", "user", "pass")]
        [InlineData("user\tpass", "user", "pass")]
        [InlineData("ñame:pässword", "ñame", "pässword")]
        public void Classify_UserPassVariants_ExtractsUsernameAndPassword(string input, string expectedUser, string expectedPass)
        {
            var result = AccountLineParser.Classify(input);

            Assert.Empty(result.Cookies);
            Assert.Equal(0, result.Failed);
            var item = Assert.Single(result.UserPass);
            Assert.Equal(expectedUser, item.User);
            Assert.Equal(expectedPass, item.Pass);
        }

        [Fact]
        public void Classify_PureCookieAndUserPassCookie_ExtractsCookies()
        {
            string raw = $"{SampleCookie}\nu:p:{SampleCookie}";
            var result = AccountLineParser.Classify(raw);

            Assert.Empty(result.UserPass);
            Assert.Equal(0, result.Failed);
            Assert.Equal(2, result.Cookies.Count);
            Assert.Null(result.Cookies[0].Password);
            Assert.Equal("p", result.Cookies[1].Password);
        }

        [Theory]
        [InlineData(":", 1)]
        [InlineData("user:", 1)]
        [InlineData("|", 1)]
        [InlineData("   ", 0)]
        public void Classify_MalformedAndBlankLines_CountsFailedWithoutThrowing(string input, int expectedFailed)
        {
            var result = AccountLineParser.Classify(input);

            Assert.Empty(result.Cookies);
            Assert.Empty(result.UserPass);
            Assert.Equal(expectedFailed, result.Failed);
        }
    }
}
