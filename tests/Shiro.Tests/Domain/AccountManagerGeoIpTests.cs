#nullable enable
using System;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public sealed class AccountManagerGeoIpTests
    {
        [Fact]
        public void Constants_DefineExpectedDefaultAndLegacyEndpoints()
        {
            Assert.Equal("https://ipwho.is/<ip>", AccountManager.DefaultGeoIpLink);
            Assert.Equal("http://ip-api.com/json/<ip>", AccountManager.LegacyGeoIpLink);
        }

        [Fact]
        public void EnsureModernGeoIpLink_WhenKeyMissing_SetsModernHttpsDefault()
        {
            var section = new IniSection("General");
            Assert.False(section.Exists("IPApiLink"));

            AccountManager.EnsureModernGeoIpLink(section);

            Assert.True(section.Exists("IPApiLink"));
            Assert.Equal("https://ipwho.is/<ip>", section.Get("IPApiLink"));
        }

        [Theory]
        [InlineData("http://ip-api.com/json/<ip>")]
        [InlineData("HTTP://IP-API.COM/JSON/<IP>")]
        [InlineData("  http://ip-api.com/json/<ip>  ")]
        public void EnsureModernGeoIpLink_WhenLegacyHttpDefault_UpgradesToModernHttps(string legacyUrl)
        {
            var section = new IniSection("General");
            section.Set("IPApiLink", legacyUrl);

            AccountManager.EnsureModernGeoIpLink(section);

            Assert.Equal("https://ipwho.is/<ip>", section.Get("IPApiLink"));
        }

        [Fact]
        public void EnsureModernGeoIpLink_WhenCustomUrl_PreservesUserSetting()
        {
            var section = new IniSection("General");
            const string customEndpoint = "https://my-custom-geoip.example.org/lookup/<ip>";
            section.Set("IPApiLink", customEndpoint);

            AccountManager.EnsureModernGeoIpLink(section);

            Assert.Equal(customEndpoint, section.Get("IPApiLink"));
        }

        [Fact]
        public void EnsureModernGeoIpLink_WhenNonLegacyUrl_PreservesEntryWithoutOverwriting()
        {
            var section = new IniSection("General");
            const string customEndpoint = "http://localhost:8080/geoip/<ip>";
            section.Set("IPApiLink", customEndpoint);

            AccountManager.EnsureModernGeoIpLink(section);

            Assert.Equal(customEndpoint, section.Get("IPApiLink"));
        }

        [Fact]
        public void EnsureModernGeoIpLink_WhenKeyExistsWithNullValue_DoesNotThrowOrOverwrite()
        {
            var mockSettings = NSubstitute.Substitute.For<Shiro.Services.ISettingsService>();
            mockSettings.Exists("IPApiLink").Returns(true);
            mockSettings.Get("IPApiLink").Returns((string?)null);

            var ex = Record.Exception(() => AccountManager.EnsureModernGeoIpLink(mockSettings));

            Assert.Null(ex);
            mockSettings.DidNotReceive().Set("IPApiLink", NSubstitute.Arg.Any<string>());
            Assert.Equal(AccountManager.DefaultGeoIpLink, "https://ipwho.is/<ip>");
        }
    }
}
