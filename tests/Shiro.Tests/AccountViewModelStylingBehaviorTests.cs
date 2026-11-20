#nullable enable
using System;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AccountViewModelStylingBehaviorTests
    {
        private const string SampleValidLongToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-infrastructure.|_sample_payload_of_more_than_two_hundred_characters_to_qualify_as_a_normal_long_lived_roblox_security_cookie_value_for_testing_purposes_1234567890_abcdefghijklmnopqrstuvwxyz";

        [Fact]
        public void StatusTooltip_ReflectsAccountState()
        {
            // 1. Banned or locked
            var banned = new AccountViewModel(new Account { Valid = false, AccountStatusIssue = "Banned for 1 day" });
            Assert.Contains("Banned for 1 day", banned.StatusTooltip);

            // 2. Expired
            var expired = new AccountViewModel(new Account { Valid = false });
            Assert.Equal(LocalizationService.Get("Account.SessionExpiredTip"), expired.StatusTooltip);

            // 3. Healthy session with LastUse
            var healthy = new AccountViewModel(new Account
            {
                Valid = true,
                SecurityToken = SampleValidLongToken,
                LastUse = new DateTime(2026, 11, 4, 12, 0, 0)
            });
            Assert.Contains("04/11/2026 12:00", healthy.StatusTooltip);

            // 4. Healthy session with DateTime.MinValue LastUse
            var noLastUse = new AccountViewModel(new Account
            {
                Valid = true,
                SecurityToken = SampleValidLongToken,
                LastUse = DateTime.MinValue
            });
            Assert.Contains(LocalizationService.Get("Account.SessionUnknown"), noLastUse.StatusTooltip);
        }

        [Fact]
        public void StatusText_ReflectsAllStatusVariants()
        {
            // Banned
            var banned = new AccountViewModel(new Account { Valid = false, AccountStatusIssue = "Account Terminated" });
            Assert.Equal("Account Terminated", banned.StatusText);

            // Expired
            var expired = new AccountViewModel(new Account { Valid = false });
            Assert.Equal(LocalizationService.Get("Status_Expired"), expired.StatusText);

            // Launching
            var launching = new AccountViewModel(new Account { Valid = true, SecurityToken = SampleValidLongToken }) { IsLaunching = true };
            Assert.Equal(LocalizationService.Get("Status_Launching"), launching.StatusText);

            // InGame with GameName
            var inGameNamed = new AccountViewModel(new Account
            {
                Valid = true,
                SecurityToken = SampleValidLongToken,
                Presence = new UserPresence { userPresenceType = UserPresenceType.InGame, gameName = "Adopt Me" }
            }) { IsInGame = true };
            Assert.Equal(LocalizationService.Format("Status_InGameNamed", "Adopt Me"), inGameNamed.StatusText);

            // InGame without GameName
            var inGameAnon = new AccountViewModel(new Account
            {
                Valid = true,
                SecurityToken = SampleValidLongToken,
                Presence = new UserPresence { userPresenceType = UserPresenceType.InGame, gameName = "" }
            }) { IsInGame = true };
            Assert.Equal(LocalizationService.Get("Status_InGame"), inGameAnon.StatusText);

            // InStudio
            var studio = new AccountViewModel(new Account
            {
                Valid = true,
                SecurityToken = SampleValidLongToken,
                Presence = new UserPresence { userPresenceType = UserPresenceType.InStudio }
            });
            Assert.Equal("Roblox Studio", studio.StatusText);

            // Online
            var online = new AccountViewModel(new Account
            {
                Valid = true,
                SecurityToken = SampleValidLongToken,
                Presence = new UserPresence { userPresenceType = UserPresenceType.Online }
            });
            Assert.Equal(LocalizationService.Get("Status_Online"), online.StatusText);

            // Ready
            var ready = new AccountViewModel(new Account
            {
                Valid = true,
                SecurityToken = SampleValidLongToken,
                Presence = new UserPresence { userPresenceType = UserPresenceType.Offline }
            });
            Assert.Equal(LocalizationService.Get("Status_Ready"), ready.StatusText);
        }

        [Fact]
        public void BadgesAndBorders_ReturnBrushesForAllStates()
        {
            var avm = new AccountViewModel(new Account { Valid = true, SecurityToken = SampleValidLongToken });

            Assert.NotNull(avm.StatusBadgeBackground);
            Assert.NotNull(avm.StatusBadgeBorder);
            Assert.NotNull(avm.StatusBadgeForeground);
            Assert.NotNull(avm.CardBorderBrush);

            // Selected
            avm.IsSelected = true;
            Assert.Same(AccountViewModel.BrushCardSelected, avm.CardBorderBrush);

            // Command palette
            avm.IsCommandPaletteSelected = true;
            Assert.NotNull(avm.CommandPaletteRowBackground);
            Assert.NotNull(avm.CommandPaletteRowBorder);

            avm.IsCommandPaletteSelected = false;
            Assert.NotNull(avm.CommandPaletteRowBackground);
            Assert.NotNull(avm.CommandPaletteRowBorder);
        }
    }
}
