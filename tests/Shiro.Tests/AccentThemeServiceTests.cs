using Avalonia.Media;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class AccentThemeServiceTests
    {
        [Fact]
        public void AppSettingsKeys_ContainsAccentThemeKey()
        {
            Assert.Equal("AccentTheme", AppSettingsKeys.General.AccentTheme);
        }

        [Fact]
        public void GetThemeColors_Monochrome_ReturnsExpectedWhiteAndDarkColors()
        {
            var (primary, hover, textOnAccent, subtle, borderCol, glow) = AccentThemeService.GetThemeColors(AccentTheme.Monochrome);

            Assert.Equal(Color.FromRgb(255, 255, 255), primary);
            Assert.Equal(Color.FromRgb(228, 228, 231), hover);
            Assert.Equal(Color.FromRgb(9, 9, 11), textOnAccent);
            Assert.Equal(28, subtle.A);
            Assert.Equal(70, borderCol.A);
        }

        [Fact]
        public void ApplyTheme_UpdatesCurrentTheme_AndRaisesEvent()
        {
            AccentTheme capturedTheme = AccentTheme.Monochrome;
            bool eventFired = false;

            AccentThemeService.ThemeChanged += theme =>
            {
                capturedTheme = theme;
                eventFired = true;
            };

            AccentThemeService.ApplyTheme(AccentTheme.Cyan, save: false);

            Assert.True(eventFired);
            Assert.Equal(AccentTheme.Cyan, capturedTheme);
            Assert.Equal(AccentTheme.Cyan, AccentThemeService.CurrentTheme);

            // Revert back to Monochrome (Default)
            AccentThemeService.ApplyTheme(AccentTheme.Monochrome, save: false);
            Assert.Equal(AccentTheme.Monochrome, AccentThemeService.CurrentTheme);
        }

        [Fact]
        public void AccentTheme_Enum_HasMonochrome()
        {
            Assert.True(System.Enum.IsDefined(typeof(AccentTheme), AccentTheme.Monochrome));
            Assert.True(System.Enum.IsDefined(typeof(AccentTheme), AccentTheme.Violet));
            Assert.True(System.Enum.IsDefined(typeof(AccentTheme), AccentTheme.Cyan));
            Assert.True(System.Enum.IsDefined(typeof(AccentTheme), AccentTheme.Emerald));
            Assert.True(System.Enum.IsDefined(typeof(AccentTheme), AccentTheme.Pink));
        }

        [Theory]
        [InlineData("Monochrome", AccentTheme.Monochrome)]
        [InlineData("Trắng", AccentTheme.Monochrome)]
        [InlineData("trang", AccentTheme.Monochrome)]
        [InlineData("White", AccentTheme.Monochrome)]
        [InlineData("Violet", AccentTheme.Violet)]
        [InlineData("tím", AccentTheme.Violet)]
        [InlineData("Cyan", AccentTheme.Cyan)]
        [InlineData("Emerald", AccentTheme.Emerald)]
        [InlineData("Pink", AccentTheme.Pink)]
        [InlineData("", AccentTheme.Monochrome)]
        [InlineData(null, AccentTheme.Monochrome)]
        public void ParseTheme_ReturnsExpectedTheme(string? input, AccentTheme expected)
        {
            Assert.Equal(expected, AccentThemeService.ParseTheme(input));
        }
    }
}
