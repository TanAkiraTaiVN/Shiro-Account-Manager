#nullable enable
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class LocalizationIntegrityTests
    {
        [Fact]
        public void LocalizationService_Format_HandlesMissingKeysAndMismatchedPlaceholdersSafely()
        {
            LocalizationService.EnsureCoreKeys();
            string missing = LocalizationService.Format("NonExistent_Key_999", "fallback {0}", "A", "B");
            Assert.Contains("A", missing);

            string malformed = LocalizationService.Format("NonExistent_Key_999", "bad {0} {5}", "onlyOne");
            Assert.False(string.IsNullOrWhiteSpace(malformed));
        }
    }
}
