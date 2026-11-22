#nullable enable
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class UpdateCheckServiceTests
    {
        [Theory]
        [InlineData("4.2.0", "v4.3.0", true)]
        [InlineData("4.2.0", "4.2.1", true)]
        [InlineData("4.2.0", "v4.2.0", false)]
        [InlineData("4.3.0", "v4.2.0", false)]
        [InlineData("4.2.0.0", "4.2.0.1", true)]
        [InlineData("1.0.0", "invalid", false)]
        public void IsRemoteVersionNewer_DetectsNewerReleasesAccurately(string currentVer, string releaseTag, bool expectedNewer)
        {
            bool isNewer = UpdateCheckService.IsRemoteVersionNewer(currentVer, releaseTag);
            Assert.Equal(expectedNewer, isNewer);
        }

        [Fact]
        public void DefaultFallbackVersion_MatchesVersionFive()
        {
            Assert.Equal(new System.Version(5, 0, 0), UpdateCheckService.DefaultFallbackVersion);
        }
    }
}
