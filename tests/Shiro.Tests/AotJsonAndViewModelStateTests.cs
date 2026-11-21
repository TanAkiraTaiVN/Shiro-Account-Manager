using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AotJsonAndViewModelStateTests
    {
        [Fact]
        public void ResolveEffectivePlaceIdSource_WhenPlaceIdExplicitlyCleared_PrefersAccountSavedPlaceIdBeforeDefaultFallback()
        {
            var acc = new Account { Username = "PlaceUser", Valid = true };
            acc.SetField("SavedPlaceId", "999888777");

            ServerBrowserStateService.Default.CurrentPlaceId = string.Empty;
            Assert.True(ServerBrowserStateService.Default.IsPlaceIdExplicitlyCleared);

            string? resolved = AccountManager.ResolveEffectivePlaceIdSource(
                viewModelPlaceId: string.Empty,
                targetAccounts: new[] { acc },
                fallbackCurrentPlaceId: ServerBrowserStateService.Default.IsPlaceIdExplicitlyCleared ? string.Empty : ServerBrowserStateService.Default.CurrentPlaceId);

            Assert.Equal("999888777", resolved);
        }
    }
}
