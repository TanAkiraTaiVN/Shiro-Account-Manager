using Shiro.Classes;
using Shiro.Nexus;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class AotJsonAndViewModelStateTests
    {
        [Fact]
        public void FastJson_TryDeserializeCommand_WithNumericAndBooleanPayloadValues_SucceedsViaShiroAppJson()
        {
            string raw = "{\"Name\":\"ping\",\"Payload\":{\"fps\":60,\"ping\":42,\"active\":true}}";
            bool ok = FastJson.TryDeserialize<Command>(raw, out var cmd);

            Assert.True(ok);
            Assert.NotNull(cmd);
            Assert.NotNull(cmd!.Payload);
            Assert.Equal("60", cmd.Payload!["fps"]);
            Assert.Equal("42", cmd.Payload["ping"]);
            Assert.Equal("true", cmd.Payload["active"]);
        }

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
